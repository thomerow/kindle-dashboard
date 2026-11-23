// Kindle dashboard: weather from Bright Sky (data of the Deutscher Wetterdienst, no API key needed).
// Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>

using System.Globalization;
using System.Text.Json;

namespace Dashboard;

/// <param name="Icon">Bright Sky icon name: clear-day, clear-night, partly-cloudy-day, partly-cloudy-night, cloudy, fog, wind, rain, sleet, snow, hail, thunderstorm</param>
public sealed record WeatherNow(double? Temperature, string? Icon, double? WindKmh, int? Humidity);
/// <summary>Today as a whole. The probability of rain is the highest of the hours that have one.</summary>
public sealed record WeatherToday(double? Min, double? Max, double Precipitation, int? PrecipitationProbability);
/// <param name="Time">Start of the hour</param>
/// <param name="PrecipitationProbability">Only for hours of the forecast, null for hours that are already over</param>
public sealed record WeatherHour(DateTimeOffset Time, int? PrecipitationProbability);
/// <summary>A warning of the Deutscher Wetterdienst for the place.</summary>
/// <param name="Event">e.g. "GLÄTTE", "FROST", "STURMBÖEN"</param>
/// <param name="Severity">minor, moderate, severe or extreme</param>
/// <param name="Expires">Null: until further notice</param>
public sealed record WeatherAlert(string Event, string Severity, DateTimeOffset Onset, DateTimeOffset? Expires);
public sealed record WeatherData(DateTimeOffset FetchedAt, WeatherNow Now, WeatherToday? Today, List<WeatherHour> Hours, List<WeatherAlert> Alerts);

/// <summary>Fetches the weather every 30 minutes and whenever the place changes.</summary>
public sealed class WeatherService(IHttpClientFactory httpClients, Store store, ILogger<WeatherService> logger) : BackgroundService {
   /// <summary>Hours from the current one on. A day for the chart and some more, because the data is fetched only every 30 minutes.</summary>
   public const int HoursAhead = 30;
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

      // Today from midnight on and the hours ahead, which reach into tomorrow
      var range = $"date={today:yyyy-MM-dd}&last_date={today.AddDays(3):yyyy-MM-dd}&tz={Uri.EscapeDataString(settings.TimeZone)}";
      using var forecast = JsonDocument.Parse(await client.GetStringAsync($"weather?{place}&{range}", cancellation));
      var all = new List<(DateTimeOffset Time, double? Temperature, double? Precipitation, int? Probability)>();
      foreach (var hour in forecast.RootElement.GetProperty("weather").EnumerateArray()) {
         all.Add((
            DateTimeOffset.Parse(hour.GetProperty("timestamp").GetString()!, CultureInfo.InvariantCulture),
            Number(hour, "temperature"),
            Number(hour, "precipitation"),
            (int?)Number(hour, "precipitation_probability")));
      }
      var hours = all.Where(hour => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(hour.Time, zone).DateTime) == today).ToList();

      WeatherToday? summary = null;
      if (hours.Count > 0) {
         var temperatures = hours.Where(hour => hour.Temperature is not null).Select(hour => hour.Temperature!.Value).ToList();
         summary = new WeatherToday(
            temperatures.Count > 0 ? temperatures.Min() : null,
            temperatures.Count > 0 ? temperatures.Max() : null,
            hours.Sum(hour => hour.Precipitation ?? 0),
            hours.Max(hour => hour.Probability));
      }
      var nowUtc = DateTimeOffset.UtcNow;
      var currentHour = new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, nowUtc.Hour, 0, 0, TimeSpan.Zero);
      var ahead = all
         .Where(hour => hour.Time >= currentHour && hour.Time < currentHour.AddHours(HoursAhead))
         .Select(hour => new WeatherHour(hour.Time, hour.Probability))
         .ToList();

      return new WeatherData(nowUtc, weatherNow, summary, ahead, await FetchAlertsAsync(client, place, cancellation));
   }

   /// <summary>Warnings for the place. Without them the weather is still shown, so a failure only gets logged.</summary>
   private async Task<List<WeatherAlert>> FetchAlertsAsync(HttpClient client, string place, CancellationToken cancellation) {
      try {
         using var document = JsonDocument.Parse(await client.GetStringAsync($"alerts?{place}", cancellation));
         var alerts = new List<WeatherAlert>();
         foreach (var alert in document.RootElement.GetProperty("alerts").EnumerateArray()) {
            if (Text(alert, "event_de") is not { } name || Text(alert, "onset") is not { } onset)
               continue;
            var expires = Text(alert, "expires");
            alerts.Add(new WeatherAlert(
               name,
               Text(alert, "severity") ?? "minor",
               DateTimeOffset.Parse(onset, CultureInfo.InvariantCulture),
               expires is null ? null : DateTimeOffset.Parse(expires, CultureInfo.InvariantCulture)));
         }
         return alerts;
      } catch (Exception exception) when (exception is not OperationCanceledException) {
         logger.LogWarning(exception, "Fetching the weather warnings failed");
         return [];
      }
   }

   private static double? Number(JsonElement element, string name) =>
      element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

   private static string? Text(JsonElement element, string name) =>
      element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
