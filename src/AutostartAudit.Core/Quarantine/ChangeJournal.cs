using Microsoft.Data.Sqlite;

namespace AutostartAudit.Core.Quarantine;

/// <summary>
/// SQLite-backed durable change journal. One file under the per-user app data
/// directory (default <c>%LOCALAPPDATA%\autostart-audit\journal.db</c>);
/// machine-scope elevated helpers open the same file. The Begin/commit
/// semantics are the safety contract: a record row is committed before the
/// caller mutates anything, and every later state transition is durable.
/// Machine-scope processes sharing one user profile DB is acceptable because
/// writes are single-row transactions; WAL mode allows the elevated child to
/// read and update while the parent waits.
/// </summary>
public sealed class ChangeJournal : IJournal, IDisposable
{
    public const int CurrentSchemaVersion = 1;

    private readonly SqliteConnection _connection;

    /// <summary>Default journal path: %LOCALAPPDATA%\autostart-audit\journal.db.</summary>
    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "autostart-audit", "journal.db");

    public ChangeJournal(string? databasePath = null)
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
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS schema_info (
                version INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS journal (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT NOT NULL,
                entry_identity TEXT NOT NULL,
                source_kind TEXT NOT NULL,
                scope TEXT NOT NULL,
                native_key TEXT NOT NULL,
                display_name TEXT NOT NULL,
                strategy TEXT NOT NULL,
                before_state_json TEXT NOT NULL,
                result TEXT NOT NULL,
                error_detail TEXT,
                restore_status TEXT NOT NULL,
                restore_error_detail TEXT,
                restored_at TEXT
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
                $"Journal schema version {version} is newer than this build supports ({CurrentSchemaVersion}). Refusing to touch the data.");
        if (version < CurrentSchemaVersion)
            throw new InvalidOperationException(
                $"No migration path is implemented for journal schema {version} -> {CurrentSchemaVersion}.");
    }

    private long? ReadSchemaVersion()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_info LIMIT 1;";
        var value = command.ExecuteScalar();
        return value is long v ? v : null;
    }

    /// <summary>
    /// Commits a Pending record in its own transaction and returns its id.
    /// Any persistence failure throws, and the caller must then perform no
    /// mutation — this is the refuse-to-act gate.
    /// </summary>
    public long Begin(JournalDraft draft)
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO journal (created_at, entry_identity, source_kind, scope, native_key,
                display_name, strategy, before_state_json, result, restore_status)
            VALUES ($at, $identity, $kind, $scope, $native, $name, $strategy, $before, 'Pending', 'None');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$identity", draft.EntryIdentity);
        command.Parameters.AddWithValue("$kind", draft.SourceKind);
        command.Parameters.AddWithValue("$scope", draft.Scope);
        command.Parameters.AddWithValue("$native", draft.NativeKey);
        command.Parameters.AddWithValue("$name", draft.DisplayName);
        command.Parameters.AddWithValue("$strategy", draft.Strategy);
        command.Parameters.AddWithValue("$before", draft.BeforeStateJson);
        var id = (long)command.ExecuteScalar()!;
        transaction.Commit();
        return id;
    }

    public void MarkExecuted(long id) => Transition(id, "Executed", null);

    public void MarkFailed(long id, string detail) => Transition(id, "Failed", detail);

    public void MarkRestored(long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            UPDATE journal SET restore_status = 'Restored', restored_at = $at, restore_error_detail = NULL
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException($"Journal record {id} does not exist; refusing to mark a restore that has no durable record.");
    }

    public void MarkRestoreFailed(long id, string detail)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE journal SET restore_status = 'Failed', restore_error_detail = $detail WHERE id = $id;";
        command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException($"Journal record {id} does not exist; refusing to mark a restore failure that has no durable record.");
    }

    private void Transition(long id, string result, string? detail)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "UPDATE journal SET result = $result, error_detail = $detail WHERE id = $id;";
        command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException($"Journal record {id} does not exist; refusing to record a transition with no durable record.");
    }

    public JournalRecord? Get(long id)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = SelectSql + " WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadRecord(reader) : null;
    }

    public IReadOnlyList<JournalRecord> ListAll()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = SelectSql + " ORDER BY id DESC;";
        using var reader = command.ExecuteReader();
        var results = new List<JournalRecord>();
        while (reader.Read())
            results.Add(ReadRecord(reader));
        return results;
    }

    private const string SelectSql = """
        SELECT id, created_at, entry_identity, source_kind, scope, native_key, display_name,
               strategy, before_state_json, result, error_detail, restore_status,
               restore_error_detail, restored_at
        FROM journal
        """;

    private static JournalRecord ReadRecord(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        CreatedAtUtc = DateTimeOffset.Parse(reader.GetString(1),
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
        EntryIdentity = reader.GetString(2),
        SourceKind = reader.GetString(3),
        Scope = reader.GetString(4),
        NativeKey = reader.GetString(5),
        DisplayName = reader.GetString(6),
        Strategy = reader.GetString(7),
        BeforeStateJson = reader.GetString(8),
        Result = ParseResult(reader.GetString(9)),
        ErrorDetail = reader.IsDBNull(10) ? null : reader.GetString(10),
        RestoreStatus = ParseRestore(reader.GetString(11)),
        RestoreErrorDetail = reader.IsDBNull(12) ? null : reader.GetString(12),
        RestoredAtUtc = reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13),
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
    };

    private static QuarantineResult ParseResult(string value) => value switch
    {
        "Pending" => QuarantineResult.Pending,
        "Executed" => QuarantineResult.Executed,
        "Failed" => QuarantineResult.Failed,
        _ => throw new InvalidOperationException($"Unknown journal result '{value}'; refusing to guess its meaning."),
    };

    private static JournalRestoreStatus ParseRestore(string value) => value switch
    {
        "None" => JournalRestoreStatus.None,
        "Restored" => JournalRestoreStatus.Restored,
        "Failed" => JournalRestoreStatus.Failed,
        _ => throw new InvalidOperationException($"Unknown restore status '{value}'; refusing to guess its meaning."),
    };

    public void Dispose() => _connection.Dispose();
}
