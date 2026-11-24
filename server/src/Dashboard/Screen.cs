// Kindle dashboard: the current picture, the state of the Kindle and the handling of taps.
// Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dashboard;

/// <summary>A drawn picture. Rev is derived from the content, so it survives restarts of the server.</summary>
/// <param name="Png">Landscape, as people see it (web page)</param>
/// <param name="DevicePng">Turned into the portrait orientation of the display, with the zones in touch coordinates</param>
public sealed record Frame(string Rev, byte[] Png, List<Zone> Zones, byte[] DevicePng, List<Zone> DeviceZones, DateTimeOffset RenderedAt);

/// <summary>What the server knows about the Kindle from its last contact.</summary>
public sealed record DeviceState(DateTimeOffset? LastSeen, int? Battery, bool Charging, string? Rev, DateTimeOffset? NextWakeAt);

/// <summary>
/// Keeps the picture up to date: it is drawn again whenever its content changes (items, settings, weather,
/// date, battery). Waiting requests of the Kindle are released as soon as there is a new picture.
/// </summary>
public sealed class Screen : IHostedService, IDisposable {
   /// <summary>Checked-off notes disappear after this time.</summary>
   private static readonly TimeSpan s_keepDone = TimeSpan.FromHours(12);
   private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

   private readonly Store m_store;
   private readonly WeatherService m_weather;
   private readonly Renderer m_renderer;
   private readonly ILogger<Screen> m_logger;
   private readonly Lock m_lock = new();
   private Frame? m_frame;
   private string? m_hash;
   private DeviceState m_device = new(null, null, false, null, null);
   private DateTimeOffset m_lastBatteryLog;
   private TaskCompletionSource m_changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
   private Timer? m_timer;

   public Screen(Store store, WeatherService weather, Renderer renderer, ILogger<Screen> logger) {
      m_store = store;
      m_weather = weather;
      m_renderer = renderer;
      m_logger = logger;
      m_store.Changed += Update;
      m_weather.Changed += Update;
   }

   public Frame Frame {
      get {
         lock (m_lock) {
            if (m_frame is null)
               Update();
            return m_frame!;
         }
      }
   }

   public DeviceState Device {
      get {
         lock (m_lock)
            return m_device;
      }
   }

   public Task StartAsync(CancellationToken cancellationToken) {
      // Once a minute: new day, cleaning up checked-off notes
      m_timer = new Timer(_ => {
         try {
            m_store.DeleteDone(DateTimeOffset.UtcNow - s_keepDone);
            Update();
         } catch (Exception exception) {
            m_logger.LogError(exception, "Updating the picture failed");
         }
      }, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
      return Task.CompletedTask;
   }

   public Task StopAsync(CancellationToken cancellationToken) {
      m_timer?.Change(Timeout.Infinite, Timeout.Infinite);
      return Task.CompletedTask;
   }

   public void Dispose() => m_timer?.Dispose();

   /// <summary>Notes the contact of the Kindle. Battery and charging state are part of the picture.</summary>
   public void DeviceSeen(int? battery, bool charging, string? rev, int? nextWakeSeconds) {
      var now = DateTimeOffset.UtcNow;
      bool batteryChanged;
      lock (m_lock) {
         batteryChanged = battery != m_device.Battery || charging != m_device.Charging;
         m_device = new DeviceState(now, battery ?? m_device.Battery, charging, rev,
            nextWakeSeconds is null ? null : now.AddSeconds(nextWakeSeconds.Value));
      }
      if (battery is not null && (batteryChanged || now - m_lastBatteryLog > TimeSpan.FromMinutes(15))) {
         m_store.AddBattery(new BatteryEntry(now, battery.Value, charging));
         m_lastBatteryLog = now;
      }
      if (batteryChanged)
         Update();
   }

   /// <summary>Waits until the picture differs from rev, at most for the timeout.</summary>
   public async Task WaitForChangeAsync(string? rev, TimeSpan timeout, CancellationToken cancellation) {
      Task changed;
      lock (m_lock) {
         if (m_frame is not null && m_frame.Rev != rev)
            return;
         changed = m_changed.Task;
      }
      try {
         await changed.WaitAsync(timeout, cancellation);
      } catch (TimeoutException) {
      }
   }

   /// <summary>Carries out the action of a tapped zone: "item:12" checks off note 12 (or back). Unknown zones are ignored.</summary>
   public bool Tap(string zone) {
      var parts = zone.Split(':');
      if (parts is ["refresh"])
         return true;
      if (parts is not ["item", var number] || !long.TryParse(number, CultureInfo.InvariantCulture, out var id))
         return false;
      var item = m_store.GetItem(id);
      if (item is null)
         return false;
      m_store.UpdateItem(id, null, item.Done is null);
      return true;
   }

   /// <summary>Draws the picture again if its content changed.</summary>
   public void Update() {
      lock (m_lock) {
         var settings = m_store.GetSettings();
         var zone = Schedule.GetTimeZone(settings.TimeZone);
         var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime;
         var model = BuildModel(settings, now);
         var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(model, s_json))))[..12];
         if (hash == m_hash && m_frame is not null)
            return;
         var rendering = m_renderer.Render(model, now, zone);
         m_frame = new Frame(hash, rendering.Png, rendering.Zones, rendering.DevicePng, rendering.DeviceZones, DateTimeOffset.UtcNow);
         m_hash = hash;
         m_changed.TrySetResult();
         m_changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
         m_logger.LogInformation("New picture {Rev}", hash);
      }
   }

   private ScreenModel BuildModel(Settings settings, DateTime now) {
      // Open notes in their order on the list, checked-off ones below
      var notes = m_store.GetItems()
         .OrderBy(item => item.Done is not null)
         .Select(item => new NoteLine(item.Id, item.Text, item.Done is not null))
         .ToList();

      var device = m_device;
      // The battery only in steps of 5 %, so that not every percent costs a new picture
      var battery = device.Battery is { } level ? (int)Math.Round(level / 5.0) * 5 : (int?)null;
      // The rain chart starts with the current hour, so the picture changes once an hour
      var utc = DateTimeOffset.UtcNow;
      var hour = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
      var zone = Schedule.GetTimeZone(settings.TimeZone);
      var today = DateOnly.FromDateTime(now);
      var sun = Sun.Times(today, settings.Latitude, settings.Longitude) is var (rise, set)
         ? new SunLine(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(rise, zone).DateTime), TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(set, zone).DateTime))
         : null;
      var alerts = AlertLines(m_weather.Current?.Alerts ?? [], utc, zone);
      return new ScreenModel(today, hour, settings.PlaceName, m_weather.Current, notes, sun, alerts, battery, device.Charging);
   }

   private static readonly string[] s_severities = ["extreme", "severe", "moderate", "minor"];

   /// <summary>
   /// Warnings that are in force or begin within the next 24 hours, the most severe first, each kind once, with "bis"
   /// or "ab" and a time.
   /// </summary>
   private static List<AlertLine> AlertLines(List<WeatherAlert> alerts, DateTimeOffset now, TimeZoneInfo zone) {
      var german = CultureInfo.GetCultureInfo("de-DE");
      var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
      string When(DateTimeOffset time) {
         var local = TimeZoneInfo.ConvertTime(time, zone).DateTime;
         var days = DateOnly.FromDateTime(local).DayNumber - today.DayNumber;
         var day = days switch { 0 => "", 1 => "morgen ", _ => local.ToString("ddd ", german) };
         return day + (local.Minute == 0 ? $"{local.Hour} Uhr" : local.ToString("H:mm", german));
      }
      int Rank(string severity) => Array.IndexOf(s_severities, severity) is var rank and >= 0 ? rank : s_severities.Length;
      return alerts
         .Where(alert => alert.Onset <= now.AddHours(24) && (alert.Expires is null || alert.Expires > now))
         .OrderBy(alert => Rank(alert.Severity))
         .ThenBy(alert => alert.Onset)
         .DistinctBy(alert => alert.Event)
         .Select(alert => new AlertLine(
            german.TextInfo.ToTitleCase(alert.Event.ToLower(german)),
            alert.Onset > now ? $"ab {When(alert.Onset)}" : alert.Expires is { } expires ? $"bis {When(expires)}" : ""))
         .ToList();
   }
}
