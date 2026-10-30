// Kindle dashboard: local time, due dates of reminders and the wake-up times of the Kindle.
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

   /// <summary>Checks the format of a due date: "yyyy-MM-dd" or "yyyy-MM-ddTHH:mm".</summary>
   public static bool TryParseDue(string? due, out DateOnly date, out TimeOnly? time) {
      time = null;
      date = default;
      if (due is null)
         return false;
      if (DateOnly.TryParseExact(due, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
         return true;
      if (DateTime.TryParseExact(due, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime)) {
         date = DateOnly.FromDateTime(dateTime);
         time = TimeOnly.FromDateTime(dateTime);
         return true;
      }
      return false;
   }

   public static bool TryParseClock(string? text, out TimeOnly time) =>
      TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

   /// <summary>
   /// Seconds until the Kindle should wake up next: the next slot of the interval (counted from midnight),
   /// moved to the end of the quiet hours if it falls into them. A reminder with a time wakes it earlier.
   /// </summary>
   public static int NextWakeSeconds(Settings settings, IEnumerable<Item> items, DateTimeOffset nowUtc) {
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

      foreach (var item in items) {
         if (item.Kind == "reminder" && item.Done is null && TryParseDue(item.Due, out var date, out var time) && time is not null) {
            var due = date.ToDateTime(time.Value);
            if (due >= earliest && due < next)
               next = due;
         }
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
