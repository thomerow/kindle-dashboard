// Kindle dashboard: state directory, password of the web page and access token of the Kindle.
// Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Dashboard;

/// <summary>Directory for everything the server keeps permanently.</summary>
public sealed class StateDirectory {
   public string Path { get; }

   /// <summary>systemd passes the directory in STATE_DIRECTORY (StateDirectory= in the unit). Otherwise: data/ next to the project.</summary>
   public StateDirectory(string contentRoot) {
      var fromSystemd = Environment.GetEnvironmentVariable("STATE_DIRECTORY")?.Split(':')[0];
      Path = string.IsNullOrEmpty(fromSystemd) ? System.IO.Path.Combine(contentRoot, "data") : fromSystemd;
      Directory.CreateDirectory(Path);
   }
}

/// <summary>
/// Hash of the web password and hash of the Kindle token in secrets.json of the state directory.
/// Only hashes are stored. The file is read again whenever it changed, so the command line tools
/// (set-password, new-device-token) take effect without a restart.
/// </summary>
public sealed class Secrets(StateDirectory state) {
   private const int Iterations = 600_000;
   private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

   private readonly string m_file = Path.Combine(state.Path, "secrets.json");
   private readonly Lock m_lock = new();
   private DateTime m_loadedAt;
   private Data m_data = new();

   private sealed record Data {
      public string? PasswordHash { get; init; }
      public string? DeviceTokenHash { get; init; }
   }

   public bool HasPassword => Load().PasswordHash is not null;
   public bool HasDeviceToken => Load().DeviceTokenHash is not null;

   public bool VerifyPassword(string password) {
      var stored = Load().PasswordHash?.Split('$');
      if (stored is not ["pbkdf2-sha256", var iterations, var salt, var hash])
         return false;
      var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), Convert.FromBase64String(salt),
         int.Parse(iterations), HashAlgorithmName.SHA256, 32);
      return CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(hash));
   }

   public bool VerifyDeviceToken(string token) {
      var stored = Load().DeviceTokenHash;
      if (stored is null || string.IsNullOrEmpty(token))
         return false;
      return CryptographicOperations.FixedTimeEquals(Sha256(token), Convert.FromHexString(stored));
   }

   public void SetPassword(string password) {
      var salt = RandomNumberGenerator.GetBytes(16);
      var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
      Save(Load() with { PasswordHash = $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}" });
   }

   /// <summary>Creates a new token for the Kindle and returns it. The previous token stops working.</summary>
   public string NewDeviceToken() {
      var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
      Save(Load() with { DeviceTokenHash = Convert.ToHexString(Sha256(token)) });
      return token;
   }

   private Data Load() {
      lock (m_lock) {
         var writtenAt = File.Exists(m_file) ? File.GetLastWriteTimeUtc(m_file) : DateTime.MinValue;
         if (writtenAt != m_loadedAt) {
            m_data = writtenAt == DateTime.MinValue ? new Data() : JsonSerializer.Deserialize<Data>(File.ReadAllText(m_file), s_json) ?? new Data();
            m_loadedAt = writtenAt;
         }
         return m_data;
      }
   }

   private void Save(Data data) {
      lock (m_lock) {
         var temporary = m_file + ".tmp";
         File.WriteAllText(temporary, JsonSerializer.Serialize(data, s_json));
         if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
         File.Move(temporary, m_file, overwrite: true);
         m_loadedAt = DateTime.MinValue;
      }
   }

   private static byte[] Sha256(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));
}
