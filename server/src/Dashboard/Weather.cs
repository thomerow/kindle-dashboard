// Kindle dashboard: weather from Bright Sky (data of the Deutscher Wetterdienst, no API key needed).
// Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>

using System.Globalization;
using System.Text.Json;

namespace Dashboard;

/// <param name="Icon">Bright Sky icon name: clear-day, clear-night, partly-cloudy-day, partly-cloudy-night, cloudy, fog, wind, rain, sleet, snow, hail, thunderstorm</param>
public sealed record WeatherNow(double? Temperature, string? Icon, double? WindKmh, int? Humidity);
public sealed record WeatherHour(DateTimeOffset Time, double? Temperature, string? Icon, int? PrecipitationProbability);
public sealed record WeatherDay(DateOnly Date, double? Min, double? Max, string? Icon, double Precipitation, int? PrecipitationProbability);
public sealed record WeatherData(DateTimeOffset FetchedAt, WeatherNow Now, List<WeatherHour> Hours, List<WeatherDay> Days);

/// <summary>Fetches the weather every 30 minutes and whenever the place changes.</summary>
public sealed class WeatherService(IHttpClientFactory httpClients, Store store, ILogger<WeatherService> logger) : BackgroundService {
   public const string BaseUrl = "https://api.brightsky.dev/";
   private static readonly TimeSpan s_interval = TimeSpan.FromMinutes(30);
   private static readonly TimeSpan s_retry = TimeSpan.FromMinutes(5);
   /// <summary>Today and the next five days. The DWD forecast reaches about ten days, but gets vague towards the end.</summary>
   public const int ForecastDays = 6;

   // Order of importance when a day has several kinds of weather
   private static readonly string[] s_severity = [
      "thunderstorm", "hail", "snow", "sleet", "rain", "fog", "wind", "cloudy", "partly-cloudy-day", "clear-day"
   ];

   private readonly SemaphoreSlim m_wake = new(0);
   private (double, double) m_place;

   public WeatherData? Current { get; private set; }

   /// <summary>Raised after new weather data arrived.</summary>
   public event Action? Changed;

   protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
      store.Changed += () => {
         var settings = store.GetSettings();
         if ((settings.Latitude, settings.Longitude) != m_place && m_wake.CurrentCount == 0)
            m_wake.Release();
      };
      while (!stoppingToken.IsCancellationRequested) {
         var settings = store.GetSettings();
         m_place = (settings.Latitude, settings.Longitude);
         var delay = s_interval;
         try {
            Current = await FetchAsync(settings, stoppingToken);
            Changed?.Invoke();
         } catch (Exception exception) when (exception is not OperationCanceledException) {
            logger.LogWarning(exception, "Fetching the weather failed");
            delay = s_retry;
         }
         try {
            await m_wake.WaitAsync(delay, stoppingToken);
         } catch (OperationCanceledException) {
            break;
         }
      }
   }

   private async Task<WeatherData> FetchAsync(Settings settings, CancellationToken cancellation) {
      var client = httpClients.CreateClient("brightsky");
      var zone = Schedule.GetTimeZone(settings.TimeZone);
      var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);
      var place = string.Create(CultureInfo.InvariantCulture, $"lat={settings.Latitude}&lon={settings.Longitude}");

      using var current = JsonDocument.Parse(await client.GetStringAsync($"current_weather?{place}", cancellation));
      var now = current.RootElement.GetProperty("weather");
      var weatherNow = new WeatherNow(
         Number(now, "temperature"),
         Text(now, "icon"),
         Number(now, "wind_speed_10") ?? Number(now, "wind_speed_30") ?? Number(now, "wind_speed_60"),
         (int?)Number(now, "relative_humidity"));

      var range = $"date={today:yyyy-MM-dd}&last_date={today.AddDays(ForecastDays):yyyy-MM-dd}&tz={Uri.EscapeDataString(settings.TimeZone)}";
      using var forecast = JsonDocument.Parse(await client.GetStringAsync($"weather?{place}&{range}", cancellation));
      var hours = new List<(DateTimeOffset Time, double? Temperature, string? Icon, double? Precipitation, int? Probability)>();
      foreach (var hour in forecast.RootElement.GetProperty("weather").EnumerateArray()) {
         hours.Add((
            DateTimeOffset.Parse(hour.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture),
            Number(hour, "temperature"),
            Text(hour, "icon"),
            Number(hour, "precipitation"),
            (int?)Number(hour, "precipitation_probability")));
      }

      var nowUtc = DateTimeOffset.UtcNow;
      var nextHours = hours
         .Where(hour => hour.Time > nowUtc)
         .Where((_, index) => index % 3 == 0)
         .Take(6)
         .Select(hour => new WeatherHour(hour.Time, hour.Temperature, hour.Icon, hour.Probability))
         .ToList();

      var days = new List<WeatherDay>();
      for (var day = today; day < today.AddDays(ForecastDays); day = day.AddDays(1)) {
         var ofDay = hours.Where(hour => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(hour.Time, zone).DateTime) == day).ToList();
         if (ofDay.Count == 0)
            continue;
         var temperatures = ofDay.Where(hour => hour.Temperature is not null).Select(hour => hour.Temperature!.Value).ToList();
         var daytime = ofDay.Where(hour => TimeZoneInfo.ConvertTime(hour.Time, zone).Hour is >= 8 and < 20).ToList();
         days.Add(new WeatherDay(
            day,
            temperatures.Count > 0 ? temperatures.Min() : null,
            temperatures.Count > 0 ? temperatures.Max() : null,
            IconOfDay(daytime.Count > 0 ? daytime.Select(hour => hour.Icon) : ofDay.Select(hour => hour.Icon)),
            ofDay.Sum(hour => hour.Precipitation ?? 0),
            ofDay.Max(hour => hour.Probability)));
      }

      return new WeatherData(nowUtc, weatherNow, nextHours, days);
   }

   /// <summary>The most important kind of weather that lasts at least two hours, otherwise the most frequent one.</summary>
   private static string? IconOfDay(IEnumerable<string?> icons) {
      var counts = icons
         .Where(icon => icon is not null)
         .Select(icon => icon!.Replace("-night", "-day"))
         .GroupBy(icon => icon)
         .ToDictionary(group => group.Key, group => group.Count());
      foreach (var icon in s_severity) {
         if (counts.GetValueOrDefault(icon) >= 2)
            return icon;
      }
      return counts.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).FirstOrDefault();
   }

   private static double? Number(JsonElement element, string name) =>
      element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

   private static string? Text(JsonElement element, string name) =>
      element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
