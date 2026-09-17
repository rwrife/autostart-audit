using System.Text.Json;
using AutostartAudit.Core.Model;
using AutostartAudit.Core.Snapshot;
using Microsoft.Data.Sqlite;

namespace AutostartAudit.Core.Store;

/// <summary>
/// A persisted scan snapshot as loaded from the store.
/// </summary>
public sealed record StoredSnapshot
{
    public required long Id { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required int SchemaVersion { get; init; }
    public required ScanDocument Document { get; init; }
}

/// <summary>
/// Outcome of a capture-and-compare operation. The first capture against an
/// empty store is a <see cref="IsBaseline"/> capture: it establishes the
/// reference snapshot and deliberately yields NO diff, because every entry
/// would otherwise read as "added" — a storm that misrepresents a first run
/// as a mass change event.
/// </summary>
public sealed record CaptureOutcome
{
    public required bool IsBaseline { get; init; }
    public required long SnapshotId { get; init; }
    public long? PreviousSnapshotId { get; init; }

    /// <summary>Null exactly when <see cref="IsBaseline"/> is true.</summary>
    public ScanDiff? Diff { get; init; }
}

/// <summary>
/// SQLite-backed snapshot store. One file under the per-user app data directory
/// (default <c>%LOCALAPPDATA%\autostart-audit\snapshots.db</c>); transactional,
/// local-only, no network. Documents are stored as the canonical JSON wire
/// format (see <see cref="JsonOptions.Default"/>) so the stored evidence is
/// byte-comparable with CLI exports.
/// </summary>
public sealed class SnapshotStore : IDisposable
{
    /// <summary>Schema version written into new databases and stamped on snapshots.</summary>
    public const int CurrentSchemaVersion = 1;

    private readonly SqliteConnection _connection;

    /// <summary>Default store path: %LOCALAPPDATA%\autostart-audit\snapshots.db.</summary>
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "autostart-audit", "snapshots.db");

    public SnapshotStore(string? databasePath = null)
    {
        var path = databasePath ?? DefaultDatabasePath;
        if (!string.Equals(path, ":memory:", StringComparison.Ordinal))
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connection = new SqliteConnection($"Data Source={path}");
        _connection.Open();
        try
        {
            Initialize();
        }
        catch
        {
            // If initialization fails (e.g. unsupported schema version), do
            // not leak the open connection into the shared pool — otherwise
            // the file stays locked and the caller cannot clean it up.
            SqliteConnection.ClearPool(_connection);
            _connection.Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                captured_at TEXT NOT NULL,
                schema_version INTEGER NOT NULL,
                scan_complete INTEGER NOT NULL,
                entry_count INTEGER NOT NULL,
                document_json TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        var version = ReadSchemaVersion();
        if (version is null)
        {
            using var insert = _connection.CreateCommand();
            insert.CommandText = "INSERT INTO schema_info (version) VALUES ($v);";
            insert.Parameters.AddWithValue("$v", CurrentSchemaVersion);
            insert.ExecuteNonQuery();
            return;
        }

        if (version > CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"Snapshot database schema version {version} is newer than this build supports ({CurrentSchemaVersion}). Refusing to touch the data.");

        if (version < CurrentSchemaVersion)
            Migrate((int)version);
    }

    /// <summary>
    /// Forward-only migration path stub. Version 1 is the baseline; unknown
    /// older versions fail closed — the store never silently reinterprets
    /// data written under a different shape. When a later issue changes the
    /// snapshot shape, replace this throw with per-step transformations
    /// (v1 -> v2, etc.) inside a transaction, bumping schema_info per step.
    /// </summary>
    private void Migrate(int fromVersion)
    {
        throw new InvalidOperationException(
            $"No migration path is implemented for snapshot schema {fromVersion} -> {CurrentSchemaVersion}.");
    }

    private long? ReadSchemaVersion()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_info LIMIT 1;";
        var value = command.ExecuteScalar();
        return value is long v ? v : null;
    }

    /// <summary>
    /// Persists a scan snapshot inside a transaction and returns its id.
    /// The capture timestamp is UTC and monotonic-ordered by rowid.
    /// </summary>
    public long Save(ScanDocument document, DateTimeOffset? capturedAtUtc = null)
    {
        var json = document.ToJson();
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO snapshots (captured_at, schema_version, scan_complete, entry_count, document_json)
            VALUES ($at, $schema, $complete, $count, $json);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$at", (capturedAtUtc ?? DateTimeOffset.UtcNow).ToString("O"));
        command.Parameters.AddWithValue("$schema", CurrentSchemaVersion);
        command.Parameters.AddWithValue("$complete", document.ScanComplete ? 1 : 0);
        command.Parameters.AddWithValue("$count", document.Entries.Count);
        command.Parameters.AddWithValue("$json", json);
        var id = (long)command.ExecuteScalar()!;
        transaction.Commit();
        return id;
    }

    /// <summary>Most recently stored snapshot, or null when the store is empty.</summary>
    public StoredSnapshot? GetLatest()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, captured_at, schema_version, document_json
            FROM snapshots ORDER BY id DESC LIMIT 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        return ReadSnapshot(reader);
    }

    public StoredSnapshot? GetById(long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, captured_at, schema_version, document_json FROM snapshots WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSnapshot(reader) : null;
    }

    /// <summary>All snapshots oldest-first. Bounded reads can be added when paging is needed.</summary>
    public IReadOnlyList<StoredSnapshot> ListAll()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT id, captured_at, schema_version, document_json FROM snapshots ORDER BY id ASC;";
        using var reader = command.ExecuteReader();
        var results = new List<StoredSnapshot>();
        while (reader.Read())
            results.Add(ReadSnapshot(reader));
        return results;
    }

    private static StoredSnapshot ReadSnapshot(SqliteDataReader reader)
    {
        var json = reader.GetString(3);
        var document = JsonSerializer.Deserialize<ScanDocument>(json, JsonOptions.Default)
            ?? throw new InvalidOperationException("Stored snapshot JSON failed to deserialize; refusing to guess.");
        return new StoredSnapshot
        {
            Id = reader.GetInt64(0),
            CapturedAtUtc = DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind),
            SchemaVersion = (int)reader.GetInt64(2),
            Document = document,
        };
    }

    public void Dispose() => _connection.Dispose();
}
