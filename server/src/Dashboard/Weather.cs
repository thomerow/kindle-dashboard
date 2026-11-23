// Kindle dashboard: weather from Bright Sky (data of the Deutscher Wetterdienst, no API key needed).
// Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>

using System.Globalization;
using System.Text.Json;

namespace Dashboard;

/// <param name="Icon">Bright Sky icon name: clear-day, clear-night, partly-cloudy-day, partly-cloudy-night, cloudy, fog, wind, rain, sleet, snow, hail, thunderstorm</param>
public sealed record WeatherNow(double? Temperature, string? Icon, double? WindKmh, int? Humidity);
/// <summary>Today as a whole. The probability of rain is the highest of the hours that have one.</summary>
public sealed record WeatherToday(double? Min, double? Max, double Precipitation, int? PrecipitationProbability);
/// <param name="Hour">Local hour of the day, 0 to 23</param>
/// <param name="PrecipitationProbability">Only for hours of the forecast, null for hours that are already over</param>
public sealed record WeatherHour(int Hour, int? PrecipitationProbability);
public sealed record WeatherData(DateTimeOffset FetchedAt, WeatherNow Now, WeatherToday? Today, List<WeatherHour> Hours);

/// <summary>Fetches the weather every 30 minutes and whenever the place changes.</summary>
public sealed class WeatherService(IHttpClientFactory httpClients, Store store, ILogger<WeatherService> logger) : BackgroundService {
   public const string BaseUrl = "https://api.brightsky.dev/";
   private static readonly TimeSpan s_interval = TimeSpan.FromMinutes(30);
   private static readonly TimeSpan s_retry = TimeSpan.FromMinutes(5);

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

      // Only today: the hours from midnight to midnight, local time
      var range = $"date={today:yyyy-MM-dd}&last_date={today.AddDays(1):yyyy-MM-dd}&tz={Uri.EscapeDataString(settings.TimeZone)}";
      using var forecast = JsonDocument.Parse(await client.GetStringAsync($"weather?{place}&{range}", cancellation));
      var hours = new List<(DateTime Local, double? Temperature, double? Precipitation, int? Probability)>();
      foreach (var hour in forecast.RootElement.GetProperty("weather").EnumerateArray()) {
         var time = DateTimeOffset.Parse(hour.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture);
         var local = TimeZoneInfo.ConvertTime(time, zone).DateTime;
         if (DateOnly.FromDateTime(local) != today)
            continue;
         hours.Add((local, Number(hour, "temperature"), Number(hour, "precipitation"), (int?)Number(hour, "precipitation_probability")));
      }

      WeatherToday? summary = null;
      if (hours.Count > 0) {
         var temperatures = hours.Where(hour => hour.Temperature is not null).Select(hour => hour.Temperature!.Value).ToList();
         summary = new WeatherToday(
            temperatures.Count > 0 ? temperatures.Min() : null,
            temperatures.Count > 0 ? temperatures.Max() : null,
            hours.Sum(hour => hour.Precipitation ?? 0),
            hours.Max(hour => hour.Probability));
      }
      // One value per hour; the hour that is there twice when the clocks go back keeps the higher one
      var byHour = hours
         .GroupBy(hour => hour.Local.Hour)
         .OrderBy(group => group.Key)
         .Select(group => new WeatherHour(group.Key, group.Max(hour => hour.Probability)))
         .ToList();

      return new WeatherData(DateTimeOffset.UtcNow, weatherNow, summary, byHour);
   }

   private static double? Number(JsonElement element, string name) =>
      element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

   private static string? Text(JsonElement element, string name) =>
      element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
