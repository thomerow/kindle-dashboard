// Kindle dashboard: endpoints and configuration of the web server. The API is described in API.md.
// Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>

using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Dashboard;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

// The server keeps the notes (a list to check off, like Post-its on the door), draws the picture for the Kindle and
// tells it when to wake up next. The web page (phone) changes the data, the Kindle fetches the picture.

const int maxWaitSeconds = 50; // Has to stay below proxy_read_timeout of nginx (60 s)
const string userAgent = "kindle-dashboard/1.0";

var builder = WebApplication.CreateBuilder(args);
var state = new StateDirectory(builder.Environment.ContentRootPath);

// --- Command line tools (run as the service user, see README.md) ---

if (args is ["set-password", ..]) {
   var password = ReadSecret("New password for the web page: ");
   if (password.Length < 8) {
      Console.Error.WriteLine("The password needs at least 8 characters.");
      return 1;
   }
   new Secrets(state).SetPassword(password);
   Console.WriteLine($"Password saved in {Path.Combine(state.Path, "secrets.json")}");
   return 0;
}
if (args is ["new-device-token", ..]) {
   Console.WriteLine(new Secrets(state).NewDeviceToken());
   return 0;
}

// --- Services ---

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
// Has to stay below TimeoutStopSec of the systemd unit (15 s), otherwise systemd kills the process
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton(state);
builder.Services.AddSingleton<Secrets>();
builder.Services.AddSingleton<Store>();
// ButtonRight (default) or ButtonLeft: where the power button is when the Kindle hangs in landscape
builder.Services.AddSingleton(new Renderer(builder.Configuration.GetValue("Orientation", Orientation.ButtonRight)));
builder.Services.AddSingleton<WeatherService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<WeatherService>());
builder.Services.AddSingleton<Screen>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<Screen>());
builder.Services.AddSingleton<Geo>();

AddClient("brightsky", WeatherService.BaseUrl);
AddClient("ipwho", "https://ipwho.is/");
AddClient("nominatim", "https://nominatim.openstreetmap.org/");
AddClient("openmeteo", "https://api.open-meteo.com/v1/");

// Keys for the login cookie, kept in the state directory so that a restart does not log everybody out
builder.Services.AddDataProtection()
   .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(state.Path, "keys")))
   .SetApplicationName("kindle-dashboard");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
   .AddCookie(options => {
      options.Cookie.Name = "dashboard_login";
      options.Cookie.HttpOnly = true;
      options.Cookie.SameSite = SameSiteMode.Strict;
      options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
      options.ExpireTimeSpan = TimeSpan.FromDays(365);
      options.SlidingExpiration = true;
      // An API answers 401 instead of redirecting to a login page
      options.Events.OnRedirectToLogin = context => {
         context.Response.StatusCode = StatusCodes.Status401Unauthorized;
         return Task.CompletedTask;
      };
   });
builder.Services.AddAuthorization();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
   options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
builder.Services.AddRateLimiter(options => {
   options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
   options.AddPolicy("login", context => PerClient(context, 10));
   options.AddPolicy("web", context => PerClient(context, 300));
   options.AddPolicy("geo", context => PerClient(context, 30));
   options.AddPolicy("device", context => PerClient(context, 600));
});

var app = builder.Build();

app.UseForwardedHeaders();
var pathBase = app.Configuration["PathBase"];
if (!string.IsNullOrEmpty(pathBase))
   app.UsePathBase(pathBase);

app.Use(async (context, next) => {
   context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
   await next();
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions {
   OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});
// Has to come after UsePathBase, otherwise the routes are matched against the path with the prefix
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

var devices = new ConnectionLimit(4);
// Long polls end as soon as the server stops. The shutdown waits for open requests, without this until the timeout.
var stopping = app.Lifetime.ApplicationStopping;

// --- Login ---

app.MapPost("/api/login", async (LoginRequest request, Secrets secrets, HttpContext context) => {
   if (!secrets.HasPassword)
      return Results.Problem("No password set yet, see README.md (set-password).", statusCode: StatusCodes.Status503ServiceUnavailable);
   if (string.IsNullOrEmpty(request.Password) || !secrets.VerifyPassword(request.Password))
      return Results.Unauthorized();
   var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner")], CookieAuthenticationDefaults.AuthenticationScheme);
   await context.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
   return Results.NoContent();
}).RequireRateLimiting("login");

app.MapPost("/api/logout", async (HttpContext context) => {
   await context.SignOutAsync();
   return Results.NoContent();
});

app.MapGet("/api/session", (HttpContext context, Secrets secrets) =>
   new { loggedIn = context.User.Identity?.IsAuthenticated == true, hasPassword = secrets.HasPassword });

// --- Web page (needs the login) ---

var web = app.MapGroup("/api").RequireAuthorization().RequireRateLimiting("web");

web.MapGet("/items", (Store store) => store.GetItems());

web.MapPost("/items", (NewItem request, Store store) => {
   var error = ValidateText(request.Text);
   if (error is not null)
      return Results.BadRequest(new { error });
   return Results.Ok(store.AddItem(request.Text!.Trim()));
});

web.MapPatch("/items/{id:long}", (long id, ItemPatch patch, Store store) => {
   var item = store.GetItem(id);
   if (item is null)
      return Results.NotFound();
   var error = ValidateText(patch.Text ?? item.Text);
   if (error is not null)
      return Results.BadRequest(new { error });
   return Results.Ok(store.UpdateItem(id, patch.Text?.Trim(), patch.Done));
});

web.MapDelete("/items/{id:long}", (long id, Store store) => store.DeleteItem(id) ? Results.NoContent() : Results.NotFound());

web.MapPost("/items/delete-done", (Store store) => new { deleted = store.DeleteDone() });

web.MapGet("/settings", (Store store) => store.GetSettings());

web.MapPut("/settings", (Settings settings, Store store) => {
   if (settings.Latitude is < -90 or > 90 || settings.Longitude is < -180 or > 180)
      return Results.BadRequest(new { error = "Position outside of the valid range" });
   if (settings.IntervalMinutes is < 5 or > 1440)
      return Results.BadRequest(new { error = "Interval must be between 5 and 1440 minutes" });
   if (!Schedule.TryParseClock(settings.QuietFrom, out _) || !Schedule.TryParseClock(settings.QuietTo, out _))
      return Results.BadRequest(new { error = "Quiet hours need the format HH:mm" });
   if (Schedule.GetTimeZone(settings.TimeZone) == TimeZoneInfo.Utc && settings.TimeZone != "UTC")
      return Results.BadRequest(new { error = "Unknown time zone" });
   var cleaned = settings with { PlaceName = Shorten(settings.PlaceName.Trim(), 60) };
   store.SetSettings(cleaned);
   return Results.Ok(cleaned);
});

web.MapGet("/state", (Screen screen, WeatherService weather, Store store) => {
   var frame = screen.Frame;
   var device = screen.Device;
   return new {
      frame = new { frame.Rev, frame.RenderedAt },
      device,
      weather = weather.Current is null ? null : new { weather.Current.FetchedAt },
      settingsSaved = store.HasSettings,
      nextWakeSeconds = Schedule.NextWakeSeconds(store.GetSettings(), DateTimeOffset.UtcNow)
   };
});

web.MapGet("/preview.png", (Screen screen) => Results.File(screen.Frame.Png, "image/png"));

web.MapGet("/battery", (int? days, Store store) =>
   store.GetBattery(DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days ?? 7, 1, 90))));

// Help for choosing the place
web.MapGet("/geo/ip", async (Geo geo, HttpContext context) =>
   await geo.FromIpAsync(context.Connection.RemoteIpAddress, context.RequestAborted) is { } position
      ? Results.Ok(position) : Results.NotFound()).RequireRateLimiting("geo");
web.MapGet("/geo/search", async (string q, Geo geo, HttpContext context) =>
   string.IsNullOrWhiteSpace(q) ? Results.BadRequest() : Results.Ok(await geo.SearchAsync(Shorten(q, 100), context.RequestAborted)))
   .RequireRateLimiting("geo");
web.MapGet("/geo/place", async (double lat, double lon, Geo geo, HttpContext context) =>
   await geo.DescribeAsync(lat, lon, context.RequestAborted)).RequireRateLimiting("geo");

// --- Kindle (needs the device token) ---

var device = app.MapGroup("/api/device").AddEndpointFilter(async (context, next) => {
   var header = context.HttpContext.Request.Headers.Authorization.ToString();
   var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..].Trim() : "";
   var secrets = context.HttpContext.RequestServices.GetRequiredService<Secrets>();
   return secrets.VerifyDeviceToken(token) ? await next(context) : Results.Unauthorized();
}).RequireRateLimiting("device");

// Text instead of JSON, so that a shell script with awk can read it on the Kindle
device.MapGet("/frame", async (string? rev, int? battery, int? charging, int? wait, Screen screen, Store store, HttpContext context) => {
   if (!devices.TryEnter())
      return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
   try {
      var isCharging = charging == 1;
      var seconds = Math.Clamp(wait ?? 0, 0, maxWaitSeconds);
      screen.DeviceSeen(battery is >= 0 and <= 100 ? battery : null, isCharging, rev, null);
      if (seconds > 0) {
         using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopping);
         await screen.WaitForChangeAsync(rev, TimeSpan.FromSeconds(seconds), cancellation.Token);
      }
      var nextWake = Schedule.NextWakeSeconds(store.GetSettings(), DateTimeOffset.UtcNow);
      screen.DeviceSeen(null, isCharging, rev, nextWake);
      return Results.Text(FrameText(screen.Frame, nextWake), "text/plain; charset=utf-8");
   } finally {
      devices.Leave();
   }
});

device.MapGet("/image", (Screen screen, HttpContext context) => {
   var frame = screen.Frame;
   context.Response.Headers["X-Rev"] = frame.Rev;
   return Results.File(frame.DevicePng, "image/png");
});

device.MapPost("/tap", (string zone, Screen screen, Store store) => {
   if (!screen.Tap(zone))
      return Results.NotFound();
   var nextWake = Schedule.NextWakeSeconds(store.GetSettings(), DateTimeOffset.UtcNow);
   return Results.Text(FrameText(screen.Frame, nextWake), "text/plain; charset=utf-8");
});

app.Run();
return 0;

// --- Helpers ---

void AddClient(string name, string baseUrl) =>
   builder.Services.AddHttpClient(name, client => {
      client.BaseAddress = new Uri(baseUrl);
      client.Timeout = TimeSpan.FromSeconds(15);
      client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
   });

// rev <rev> / wake <seconds> / zone <id> <x> <y> <width> <height>, one per line
static string FrameText(Frame frame, int nextWakeSeconds) {
   var text = new StringBuilder();
   text.Append(CultureInfo.InvariantCulture, $"rev {frame.Rev}\nwake {nextWakeSeconds}\n");
   foreach (var zone in frame.DeviceZones)
      text.Append(CultureInfo.InvariantCulture, $"zone {zone.Id} {zone.X} {zone.Y} {zone.Width} {zone.Height}\n");
   return text.ToString();
}

static string? ValidateText(string? text) {
   if (string.IsNullOrWhiteSpace(text))
      return "text is empty";
   if (text.Length > Store.MaxTextLength)
      return $"text is longer than {Store.MaxTextLength} characters";
   return null;
}

static string Shorten(string text, int length) => text.Length <= length ? text : text[..length];

static string ReadSecret(string prompt) {
   if (Console.IsInputRedirected)
      return Console.ReadLine() ?? "";
   Console.Write(prompt);
   var secret = new StringBuilder();
   while (true) {
      var key = Console.ReadKey(intercept: true);
      if (key.Key == ConsoleKey.Enter)
         break;
      if (key.Key == ConsoleKey.Backspace) {
         if (secret.Length > 0)
            secret.Length--;
      } else if (!char.IsControl(key.KeyChar)) {
         secret.Append(key.KeyChar);
      }
   }
   Console.WriteLine();
   return secret.ToString();
}

static RateLimitPartition<string> PerClient(HttpContext context, int perMinute) =>
   RateLimitPartition.GetFixedWindowLimiter(
      context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
      _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1) });

sealed record LoginRequest(string? Password);
sealed record NewItem(string? Text);
/// <summary>Null leaves a field as it is.</summary>
sealed record ItemPatch(string? Text, bool? Done);

/// <summary>Caps the number of requests that are held open at the same time.</summary>
sealed class ConnectionLimit(int max) {
   private int m_count;

   public bool TryEnter() {
      if (Interlocked.Increment(ref m_count) <= max)
         return true;
      Interlocked.Decrement(ref m_count);
      return false;
   }

   public void Leave() => Interlocked.Decrement(ref m_count);
}
