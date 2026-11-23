// Kindle dashboard: sunrise and sunset, calculated from the place and the date.
// Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>

namespace Dashboard;

public static class Sun {
   private const double J2000 = 2451545.0;
   private const double UnixEpoch = 2440587.5;

   /// <summary>
   /// Sunrise and sunset of a day (UTC), with the "sunrise equation" (accurate to about a minute). Null when the sun
   /// does not rise or does not set that day (polar night, midnight sun).
   /// </summary>
   public static (DateTimeOffset Rise, DateTimeOffset Set)? Times(DateOnly date, double latitude, double longitude) {
      // Julian day number of the date at noon UTC, then the mean solar noon at the longitude (east positive)
      var julianDay = new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero).ToUnixTimeSeconds() / 86400.0 + UnixEpoch;
      var day = Math.Ceiling(julianDay - J2000 - 0.0009);
      var meanNoon = day - longitude / 360;

      var anomaly = Normalize(357.5291 + 0.98560028 * meanNoon);
      var center = 1.9148 * Sin(anomaly) + 0.0200 * Sin(2 * anomaly) + 0.0003 * Sin(3 * anomaly);
      var eclipticLongitude = Normalize(anomaly + center + 180 + 102.9372);
      var transit = J2000 + meanNoon + 0.0053 * Sin(anomaly) - 0.0069 * Sin(2 * eclipticLongitude);

      var declination = Math.Asin(Sin(eclipticLongitude) * Sin(23.4397));
      // -0.833°: the upper edge of the sun at the horizon, with the refraction of the air
      var cosHourAngle = (Sin(-0.833) - Sin(latitude) * Math.Sin(declination)) / (Cos(latitude) * Math.Cos(declination));
      if (cosHourAngle is < -1 or > 1)
         return null;
      var hourAngle = Math.Acos(cosHourAngle) * 180 / Math.PI;
      return (FromJulian(transit - hourAngle / 360), FromJulian(transit + hourAngle / 360));
   }

   private static DateTimeOffset FromJulian(double julianDay) =>
      DateTimeOffset.FromUnixTimeSeconds((long)Math.Round((julianDay - UnixEpoch) * 86400));

   private static double Sin(double degrees) => Math.Sin(degrees * Math.PI / 180);
   private static double Cos(double degrees) => Math.Cos(degrees * Math.PI / 180);
   private static double Normalize(double degrees) => (degrees % 360 + 360) % 360;
}
