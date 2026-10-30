// Kindle dashboard: help for choosing the place on the map. All services work without an API key:
// ipwho.is (rough position of an IP address), Nominatim of OpenStreetMap (search, names), Open-Meteo (time zone).
// Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>

using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Dashboard;

public sealed record GeoPosition(double Latitude, double Longitude, string? Name);
public sealed record GeoPlace(string Name, string TimeZone);

public sealed class Geo(IHttpClientFactory httpClients) {
   // Nominatim allows at most one request per second
   private static readonly SemaphoreSlim s_nominatim = new(1);
   private static DateTime s_lastNominatim;

   /// <summary>Rough position of an IP address, null for private addresses or when the lookup fails.</summary>
   public async Task<GeoPosition?> FromIpAsync(IPAddress? address, CancellationToken cancellation) {
      // Locally (127.0.0.1, home network) the service looks up the public address of the server instead
      var path = address is null || IsPrivate(address) ? "" : Uri.EscapeDataString(address.ToString());
      using var document = JsonDocument.Parse(await httpClients.CreateClient("ipwho").GetStringAsync(path, cancellation));
      var root = document.RootElement;
      if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
         return null;
      return new GeoPosition(root.GetProperty("latitude").GetDouble(), root.GetProperty("longitude").GetDouble(),
         root.TryGetProperty("city", out var city) ? city.GetString() : null);
   }

   /// <summary>Places that match a search text.</summary>
   public async Task<List<GeoPosition>> SearchAsync(string query, CancellationToken cancellation) {
      var json = await NominatimAsync($"search?format=jsonv2&limit=6&accept-language=de&q={Uri.EscapeDataString(query)}", cancellation);
      using var document = JsonDocument.Parse(json);
      return document.RootElement.EnumerateArray()
         .Select(place => new GeoPosition(
            double.Parse(place.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture),
            double.Parse(place.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture),
            place.GetProperty("display_name").GetString()))
         .ToList();
   }

   /// <summary>Name of the town and time zone of a point.</summary>
   public async Task<GeoPlace> DescribeAsync(double latitude, double longitude, CancellationToken cancellation) {
      var point = string.Create(CultureInfo.InvariantCulture, $"lat={latitude}&lon={longitude}");
      var name = string.Create(CultureInfo.InvariantCulture, $"{latitude:0.00}, {longitude:0.00}");
      try {
         using var reverse = JsonDocument.Parse(await NominatimAsync($"reverse?format=jsonv2&zoom=12&accept-language=de&{point}", cancellation));
         if (reverse.RootElement.TryGetProperty("name", out var found) && !string.IsNullOrWhiteSpace(found.GetString()))
            name = found.GetString()!;
      } catch (HttpRequestException) {
      }

      var timeZone = "Europe/Berlin";
      var forecast = string.Create(CultureInfo.InvariantCulture, $"forecast?latitude={latitude}&longitude={longitude}&timezone=auto&forecast_days=1");
      using (var meteo = JsonDocument.Parse(await httpClients.CreateClient("openmeteo").GetStringAsync(forecast, cancellation))) {
         if (meteo.RootElement.TryGetProperty("timezone", out var zone) && zone.GetString() is { Length: > 0 } id)
            timeZone = id;
      }
      return new GeoPlace(name, timeZone);
   }

   private async Task<string> NominatimAsync(string query, CancellationToken cancellation) {
      await s_nominatim.WaitAsync(cancellation);
      try {
         var wait = s_lastNominatim.AddSeconds(1) - DateTime.UtcNow;
         if (wait > TimeSpan.Zero)
            await Task.Delay(wait, cancellation);
         return await httpClients.CreateClient("nominatim").GetStringAsync(query, cancellation);
      } finally {
         s_lastNominatim = DateTime.UtcNow;
         s_nominatim.Release();
      }
   }

   private static bool IsPrivate(IPAddress address) {
      if (IPAddress.IsLoopback(address))
         return true;
      if (address.IsIPv4MappedToIPv6)
         address = address.MapToIPv4();
      var bytes = address.GetAddressBytes();
      return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
         ? bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and < 32) || (bytes[0] == 192 && bytes[1] == 168)
         : address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
   }
}
