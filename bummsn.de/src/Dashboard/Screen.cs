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
   public const int MaxReminders = 20;
   /// <summary>Checked-off shopping items disappear after this time.</summary>
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
      // Once a minute: new day, reminders that become overdue, cleaning up checked-off items
      m_timer = new Timer(_ => {
         try {
            m_store.DeleteDone("shopping", DateTimeOffset.UtcNow - s_keepDone);
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

   /// <summary>Carries out the action of a tapped zone. Unknown zones are ignored.</summary>
   public bool Tap(string zone) {
      var parts = zone.Split(':');
      if (parts is ["refresh"])
         return true;
      if (parts.Length != 2 || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var id))
         return false;
      var item = m_store.GetItem(id);
      if (item is null || item.Kind != parts[0])
         return false;
      switch (item.Kind) {
         case "shopping":
            m_store.UpdateItem(id, null, null, false, item.Done is null);
            return true;
         case "reminder":
            m_store.UpdateItem(id, null, null, false, true);
            return true;
         default:
            return false;
      }
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
      var today = DateOnly.FromDateTime(now);
      var items = m_store.GetItems();

      var reminders = items
         .Where(item => item.Kind == "reminder" && item.Done is null)
         .Select(item => {
            var hasDue = Schedule.TryParseDue(item.Due, out var date, out var time);
            var dueAt = hasDue ? date.ToDateTime(time ?? TimeOnly.MaxValue) : DateTime.MaxValue;
            return (Item: item, HasDue: hasDue, Date: date, Time: time, DueAt: dueAt);
         })
         .Where(entry => !entry.HasDue || entry.Date <= today.AddDays(14))
         .OrderBy(entry => entry.DueAt)
         .Take(MaxReminders)
         .Select(entry => new ReminderLine(entry.Item.Id, entry.Item.Text,
            entry.HasDue ? DueLabel(entry.Date, entry.Time, today) : null,
            entry.HasDue && entry.DueAt <= now))
         .ToList();

      var shopping = items
         .Where(item => item.Kind == "shopping")
         .OrderBy(item => item.Done is not null)
         .ThenBy(item => item.Id)
         .Select(item => new ShoppingLine(item.Id, item.Text, item.Done is not null))
         .ToList();

      var notes = items
         .Where(item => item.Kind == "note")
         .OrderByDescending(item => item.Id)
         .Select(item => item.Text)
         .ToList();

      var device = m_device;
      // The battery only in steps of 5 %, so that not every percent costs a new picture
      var battery = device.Battery is { } level ? (int)Math.Round(level / 5.0) * 5 : (int?)null;
      return new ScreenModel(today, settings.PlaceName, m_weather.Current, reminders, shopping, notes, battery, device.Charging);
   }

   private static string DueLabel(DateOnly date, TimeOnly? time, DateOnly today) {
      var german = CultureInfo.GetCultureInfo("de-DE");
      var days = date.DayNumber - today.DayNumber;
      var day = days switch {
         0 => "heute",
         1 => "morgen",
         -1 => "gestern",
         > 1 and < 7 => date.ToDateTime(TimeOnly.MinValue).ToString("dddd", german),
         _ => date.ToString("d.M.", german)
      };
      return time is null ? day : $"{day} {time:HH:mm}";
   }
}
