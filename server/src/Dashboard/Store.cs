// Kindle dashboard: permanent data in SQLite (notes, settings, battery log).
// Author: Thorsten Kummerow <thorsten.kummerow (at) gmail (dot) com>

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Dashboard;

/// <summary>A note on the list, like a Post-it on the door.</summary>
/// <param name="Done">Time when it was checked off</param>
public sealed record Item(long Id, string Text, DateTimeOffset Created, DateTimeOffset? Done);

/// <summary>Settings that can be changed on the web page.</summary>
public sealed record Settings {
   public string PlaceName { get; init; } = "Berlin";
   public double Latitude { get; init; } = 52.52;
   public double Longitude { get; init; } = 13.405;
   public string TimeZone { get; init; } = "Europe/Berlin";
   /// <summary>Minutes between two wake-ups of the Kindle on battery</summary>
   public int IntervalMinutes { get; init; } = 30;
   /// <summary>No wake-ups between QuietFrom and QuietTo ("HH:mm", local time). Equal times: no quiet hours.</summary>
   public string QuietFrom { get; init; } = "23:00";
   public string QuietTo { get; init; } = "06:00";
}

public sealed record BatteryEntry(DateTimeOffset Time, int Level, bool Charging);

public sealed class Store {
   public const int MaxTextLength = 200;
   /// <summary>
   /// Kind of new items. The table still has the columns kind and due of the former dashboard (shopping list, notes,
   /// reminders); their items show up as notes as well, the due date is ignored.
   /// </summary>
   private const string Kind = "todo";

   private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
   private readonly string m_connectionString;
   private long m_version;

   /// <summary>Raised after every change of items or settings.</summary>
   public event Action? Changed;

   public Store(StateDirectory state) {
      m_connectionString = new SqliteConnectionStringBuilder {
         DataSource = Path.Combine(state.Path, "dashboard.db"),
         Pooling = true
      }.ToString();
      using var connection = Open();
      Execute(connection, """
         PRAGMA journal_mode = WAL;
         CREATE TABLE IF NOT EXISTS items (
            id      INTEGER PRIMARY KEY AUTOINCREMENT,
            kind    TEXT NOT NULL,
            text    TEXT NOT NULL,
            created TEXT NOT NULL,
            due     TEXT NULL,
            done    TEXT NULL
         );
         CREATE TABLE IF NOT EXISTS kv (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
         );
         CREATE TABLE IF NOT EXISTS battery (
            time     TEXT NOT NULL,
            level    INTEGER NOT NULL,
            charging INTEGER NOT NULL
         );
         """);
   }

   /// <summary>Goes up with every change, so that callers can tell whether something changed.</summary>
   public long Version => Interlocked.Read(ref m_version);

   // --- Items ---

   public List<Item> GetItems() {
      using var connection = Open();
      using var command = connection.CreateCommand();
      command.CommandText = "SELECT id, text, created, done FROM items ORDER BY id";
      using var reader = command.ExecuteReader();
      var items = new List<Item>();
      while (reader.Read()) {
         items.Add(new Item(
            reader.GetInt64(0),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(2)),
            reader.IsDBNull(3) ? null : DateTimeOffset.Parse(reader.GetString(3))));
      }
      return items;
   }

   public Item? GetItem(long id) => GetItems().FirstOrDefault(item => item.Id == id);

   public Item AddItem(string text) {
      var created = DateTimeOffset.UtcNow;
      long id;
      using (var connection = Open()) {
         using var command = connection.CreateCommand();
         command.CommandText = "INSERT INTO items (kind, text, created) VALUES ($kind, $text, $created) RETURNING id";
         command.Parameters.AddWithValue("$kind", Kind);
         command.Parameters.AddWithValue("$text", text);
         command.Parameters.AddWithValue("$created", created.ToString("O"));
         id = (long)command.ExecuteScalar()!;
      }
      OnChanged();
      return new Item(id, text, created, null);
   }

   /// <summary>Changes text and done state. Null leaves a value as it is.</summary>
   public Item? UpdateItem(long id, string? text, bool? done) {
      var item = GetItem(id);
      if (item is null)
         return null;
      var updated = item with {
         Text = text ?? item.Text,
         Done = done switch {
            null => item.Done,
            true => item.Done ?? DateTimeOffset.UtcNow,
            false => null
         }
      };
      if (updated == item)
         return item;
      using (var connection = Open()) {
         using var command = connection.CreateCommand();
         command.CommandText = "UPDATE items SET text = $text, done = $done WHERE id = $id";
         command.Parameters.AddWithValue("$id", id);
         command.Parameters.AddWithValue("$text", updated.Text);
         command.Parameters.AddWithValue("$done", (object?)updated.Done?.ToString("O") ?? DBNull.Value);
         command.ExecuteNonQuery();
      }
      OnChanged();
      return updated;
   }

   public bool DeleteItem(long id) {
      int count;
      using (var connection = Open()) {
         using var command = connection.CreateCommand();
         command.CommandText = "DELETE FROM items WHERE id = $id";
         command.Parameters.AddWithValue("$id", id);
         count = command.ExecuteNonQuery();
      }
      if (count > 0)
         OnChanged();
      return count > 0;
   }

   /// <summary>Deletes checked-off items, optionally only those checked off before a point in time.</summary>
   public int DeleteDone(DateTimeOffset? before = null) {
      int count;
      using (var connection = Open()) {
         using var command = connection.CreateCommand();
         command.CommandText = "DELETE FROM items WHERE done IS NOT NULL AND ($before IS NULL OR done < $before)";
         command.Parameters.AddWithValue("$before", (object?)before?.ToString("O") ?? DBNull.Value);
         count = command.ExecuteNonQuery();
      }
      if (count > 0)
         OnChanged();
      return count;
   }

   // --- Settings ---

   /// <summary>False as long as the settings were never saved (the place is then only the default).</summary>
   public bool HasSettings => GetValue("settings") is not null;

   public Settings GetSettings() {
      var json = GetValue("settings");
      return json is null ? new Settings() : JsonSerializer.Deserialize<Settings>(json, s_json) ?? new Settings();
   }

   public void SetSettings(Settings settings) {
      SetValue("settings", JsonSerializer.Serialize(settings, s_json));
      OnChanged();
   }

   // --- Battery log ---

   public void AddBattery(BatteryEntry entry) {
      using var connection = Open();
      using var command = connection.CreateCommand();
      command.CommandText = "INSERT INTO battery (time, level, charging) VALUES ($time, $level, $charging)";
      command.Parameters.AddWithValue("$time", entry.Time.ToString("O"));
      command.Parameters.AddWithValue("$level", entry.Level);
      command.Parameters.AddWithValue("$charging", entry.Charging ? 1 : 0);
      command.ExecuteNonQuery();
   }

   public List<BatteryEntry> GetBattery(DateTimeOffset since) {
      using var connection = Open();
      using var command = connection.CreateCommand();
      command.CommandText = "SELECT time, level, charging FROM battery WHERE time >= $since ORDER BY time";
      command.Parameters.AddWithValue("$since", since.ToString("O"));
      using var reader = command.ExecuteReader();
      var entries = new List<BatteryEntry>();
      while (reader.Read())
         entries.Add(new BatteryEntry(DateTimeOffset.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetInt32(2) != 0));
      return entries;
   }

   // --- Helpers ---

   private string? GetValue(string key) {
      using var connection = Open();
      using var command = connection.CreateCommand();
      command.CommandText = "SELECT value FROM kv WHERE key = $key";
      command.Parameters.AddWithValue("$key", key);
      return command.ExecuteScalar() as string;
   }

   private void SetValue(string key, string value) {
      using var connection = Open();
      using var command = connection.CreateCommand();
      command.CommandText = "INSERT INTO kv (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value";
      command.Parameters.AddWithValue("$key", key);
      command.Parameters.AddWithValue("$value", value);
      command.ExecuteNonQuery();
   }

   private SqliteConnection Open() {
      var connection = new SqliteConnection(m_connectionString);
      connection.Open();
      return connection;
   }

   private static void Execute(SqliteConnection connection, string sql) {
      using var command = connection.CreateCommand();
      command.CommandText = sql;
      command.ExecuteNonQuery();
   }

   private void OnChanged() {
      Interlocked.Increment(ref m_version);
      Changed?.Invoke();
   }
}
