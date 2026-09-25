using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace QuotaPeek.Services;

public sealed class HistoryStore
{
    private readonly string connectionString;
    public HistoryStore(string directory)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "history.db") }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS snapshots (id INTEGER PRIMARY KEY, provider TEXT NOT NULL, time INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS provider_time ON snapshots(provider, time);
            CREATE TABLE IF NOT EXISTS alerts (provider TEXT PRIMARY KEY, is_low INTEGER NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    public void Save(QuotaSnapshot snapshot)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO snapshots(provider,time,json) VALUES($provider,$time,$json); DELETE FROM snapshots WHERE time < $cutoff;";
        command.Parameters.AddWithValue("$provider", snapshot.ProviderId);
        command.Parameters.AddWithValue("$time", snapshot.AttemptedAt.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot, SettingsStore.Json));
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-90).ToUnixTimeSeconds());
        command.ExecuteNonQuery();
    }
    public List<QuotaSnapshot> Read(string provider, int count = 2016)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT json FROM snapshots WHERE provider=$provider ORDER BY id DESC LIMIT $count";
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$count", count);
        using var reader = command.ExecuteReader();
        var snapshots = new List<QuotaSnapshot>();
        while (reader.Read())
        {
            try { if (JsonSerializer.Deserialize<QuotaSnapshot>(reader.GetString(0), SettingsStore.Json) is { } value) snapshots.Add(value); }
            catch (JsonException) { }
        }
        snapshots.Reverse();
        return snapshots;
    }

    public bool CrossedLowThreshold(string provider, bool low)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT is_low FROM alerts WHERE provider=$provider";
        command.Parameters.AddWithValue("$provider", provider);
        var wasLow = Convert.ToInt32(command.ExecuteScalar() ?? 0) != 0;
        command.CommandText = "INSERT INTO alerts(provider,is_low) VALUES($provider,$low) ON CONFLICT(provider) DO UPDATE SET is_low=$low";
        command.Parameters.AddWithValue("$low", low ? 1 : 0);
        command.ExecuteNonQuery();
        transaction.Commit();
        return low && !wasLow;
    }
}
