// Kindle dashboard: local time and the wake-up times of the Kindle.
// Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>

using System.Globalization;

namespace Dashboard;

public static class Schedule {
   /// <summary>The Kindle never sleeps shorter than this, a wake-up costs battery as well.</summary>
   public const int MinSleepSeconds = 120;

   public static TimeZoneInfo GetTimeZone(string id) {
      try {
         return TimeZoneInfo.FindSystemTimeZoneById(id);
      } catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException) {
         return TimeZoneInfo.Utc;
      }
   }

   public static DateTime LocalNow(Settings settings) =>
      TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, GetTimeZone(settings.TimeZone)).DateTime;

   public static bool TryParseClock(string? text, out TimeOnly time) =>
      TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

   /// <summary>
   /// Seconds until the Kindle should wake up next: the next slot of the interval (counted from midnight),
   /// moved to the end of the quiet hours if it falls into them.
   /// </summary>
   public static int NextWakeSeconds(Settings settings, DateTimeOffset nowUtc) {
      var zone = GetTimeZone(settings.TimeZone);
      var now = TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime;
      var earliest = now.AddSeconds(MinSleepSeconds);
      var interval = Math.Clamp(settings.IntervalMinutes, 5, 24 * 60);

      var minutes = (int)Math.Ceiling(earliest.TimeOfDay.TotalMinutes / interval) * interval;
      var next = earliest.Date.AddMinutes(minutes);
      if (TryParseClock(settings.QuietFrom, out var quietFrom) && TryParseClock(settings.QuietTo, out var quietTo)
            && IsQuiet(TimeOnly.FromDateTime(next), quietFrom, quietTo)) {
         next = next.Date.Add(quietTo.ToTimeSpan());
         if (next < earliest)
            next = next.AddDays(1);
      }

      var nextUtc = ToUtc(next, zone);
      return Math.Max(MinSleepSeconds, (int)Math.Ceiling((nextUtc - nowUtc).TotalSeconds));
   }

   /// <summary>Quiet hours may cross midnight (23:00–06:00). Equal times mean no quiet hours.</summary>
   public static bool IsQuiet(TimeOnly time, TimeOnly from, TimeOnly to) {
      if (from == to)
         return false;
      return from < to ? time >= from && time < to : time >= from || time < to;
   }

   private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone) {
      // A time that does not exist (clocks go forward) is moved behind the gap
      while (zone.IsInvalidTime(local))
         local = local.AddMinutes(15);
      return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone));
   }
}
