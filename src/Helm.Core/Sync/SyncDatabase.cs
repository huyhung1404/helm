using Microsoft.Data.Sqlite;

namespace Helm.Core.Sync;

/// <summary>One row of the local replica.</summary>
/// <param name="Version">Server version the row reflects, or that the pending local edit was made on (0 = never synced).</param>
/// <param name="Body">Record JSON in plaintext; null for a tombstone.</param>
/// <param name="Dirty">Has a local edit not yet accepted by the server.</param>
/// <param name="LocalRev">Bumped on every write, so an edit made while a push is in flight is not marked clean.</param>
internal sealed record SyncRow(
    string Collection, string Id, long Version, int SchemaVersion, bool Deleted, long UpdatedAtMs, string DeviceId,
    string? Body, bool Dirty, long LocalRev);

internal enum LocalWriteResult
{
    Written,
    NotFound,
    RejectedNewerSchema,
}

/// <summary>
/// The local replica: every synced record of every collection in one SQLite file, plus the pull cursor. Plaintext
/// lives here (it is what modules query); at-rest protection is the user's Windows profile. One connection guarded
/// by a lock: the data is small and SQLite is fast, so this keeps the threading model trivial.
/// </summary>
public sealed class SyncDatabase : IDisposable
{
    private const int SchemaVersion = 1;
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private SqliteTransaction? _transaction;

    public SyncDatabase(string filePath)
    {
        FilePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        // No pooling: Dispose must release the file (tests delete it; "reset" must be able to as well).
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;");
        EnsureSchema();
        DeviceId = GetMeta("device_id") ?? CreateDeviceId();
    }

    public string FilePath { get; }

    /// <summary>Random id of this installation, stamped on every local edit (tie-breaker for last-writer-wins).</summary>
    public string DeviceId { get; }

    public void Dispose()
    {
        lock (_gate) _connection.Dispose();
    }

    internal void InTransaction(Action action)
    {
        lock (_gate)
        {
            if (_transaction is not null)
            {
                action();
                return;
            }
            _transaction = _connection.BeginTransaction();
            try
            {
                action();
                _transaction.Commit();
            }
            catch
            {
                _transaction.Rollback();
                throw;
            }
            finally
            {
                _transaction.Dispose();
                _transaction = null;
            }
        }
    }

    internal SyncRow? Get(string collection, string id)
    {
        lock (_gate)
        {
            using var cmd = Command($"SELECT {Columns} FROM records WHERE collection = $c AND id = $id");
            cmd.Parameters.AddWithValue("$c", collection);
            cmd.Parameters.AddWithValue("$id", id);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRow(reader) : null;
        }
    }

    internal List<SyncRow> List(string collection)
    {
        lock (_gate)
        {
            using var cmd = Command($"SELECT {Columns} FROM records WHERE collection = $c AND deleted = 0 ORDER BY id");
            cmd.Parameters.AddWithValue("$c", collection);
            return ReadRows(cmd);
        }
    }

    internal List<SyncRow> GetDirty(int limit)
    {
        lock (_gate)
        {
            using var cmd = Command($"SELECT {Columns} FROM records WHERE dirty = 1 ORDER BY updated_at LIMIT $n");
            cmd.Parameters.AddWithValue("$n", limit);
            return ReadRows(cmd);
        }
    }

    /// <summary>
    /// Records a local edit (or a tombstone when <paramref name="body"/> is null and <paramref name="deleted"/>).
    /// Refuses to overwrite a record written by a newer schema, whose fields this build would silently drop.
    /// </summary>
    internal LocalWriteResult WriteLocal(string collection, string id, int schemaVersion, string? body, bool deleted, long updatedAtMs)
    {
        lock (_gate)
        {
            using (var check = Command("SELECT schema_version, deleted FROM records WHERE collection = $c AND id = $id"))
            {
                check.Parameters.AddWithValue("$c", collection);
                check.Parameters.AddWithValue("$id", id);
                using var reader = check.ExecuteReader();
                if (reader.Read())
                {
                    if (reader.GetInt32(0) > schemaVersion) return LocalWriteResult.RejectedNewerSchema;
                    if (deleted && reader.GetInt64(1) != 0) return LocalWriteResult.NotFound;
                }
                else if (deleted)
                {
                    return LocalWriteResult.NotFound;
                }
            }

            using var cmd = Command("""
                INSERT INTO records (collection, id, version, schema_version, deleted, updated_at, device_id, body, dirty, local_rev)
                VALUES ($c, $id, 0, $s, $del, $t, $dev, $b, 1, 1)
                ON CONFLICT (collection, id) DO UPDATE SET
                    schema_version = $s, deleted = $del, updated_at = $t, device_id = $dev, body = $b,
                    dirty = 1, local_rev = local_rev + 1
                """);
            cmd.Parameters.AddWithValue("$c", collection);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$s", schemaVersion);
            cmd.Parameters.AddWithValue("$del", deleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$t", updatedAtMs);
            cmd.Parameters.AddWithValue("$dev", DeviceId);
            cmd.Parameters.AddWithValue("$b", (object?)body ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            return LocalWriteResult.Written;
        }
    }

    /// <summary>The server accepted the edit seen at <paramref name="pushedRev"/>; clean unless edited since.</summary>
    internal void MarkPushed(string collection, string id, long pushedRev, long newVersion)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                UPDATE records SET version = $v, dirty = CASE WHEN local_rev = $rev THEN 0 ELSE dirty END
                WHERE collection = $c AND id = $id
                """);
            cmd.Parameters.AddWithValue("$v", newVersion);
            cmd.Parameters.AddWithValue("$rev", pushedRev);
            cmd.Parameters.AddWithValue("$c", collection);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Keeps the local edit but bases it on <paramref name="serverVersion"/>, so the next push wins.</summary>
    internal void Rebase(string collection, string id, long serverVersion)
    {
        lock (_gate)
        {
            using var cmd = Command("UPDATE records SET version = $v WHERE collection = $c AND id = $id");
            cmd.Parameters.AddWithValue("$v", serverVersion);
            cmd.Parameters.AddWithValue("$c", collection);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Replaces the row with the server's version and marks it clean.</summary>
    internal void ApplyRemote(string collection, string id, long version, SealedContent content, bool deleted)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO records (collection, id, version, schema_version, deleted, updated_at, device_id, body, dirty, local_rev)
                VALUES ($c, $id, $v, $s, $del, $t, $dev, $b, 0, 1)
                ON CONFLICT (collection, id) DO UPDATE SET
                    version = $v, schema_version = $s, deleted = $del, updated_at = $t, device_id = $dev, body = $b,
                    dirty = 0, local_rev = local_rev + 1
                """);
            cmd.Parameters.AddWithValue("$c", collection);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$v", version);
            cmd.Parameters.AddWithValue("$s", content.SchemaVersion);
            cmd.Parameters.AddWithValue("$del", deleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$t", content.UpdatedAtMs);
            cmd.Parameters.AddWithValue("$dev", content.DeviceId);
            cmd.Parameters.AddWithValue("$b", deleted ? DBNull.Value : (object?)content.Body ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    internal long GetCursor(string name)
    {
        lock (_gate)
        {
            using var cmd = Command("SELECT last_seq FROM cursors WHERE name = $n");
            cmd.Parameters.AddWithValue("$n", name);
            return cmd.ExecuteScalar() is long seq ? seq : 0;
        }
    }

    internal void SetCursor(string name, long seq)
    {
        lock (_gate)
        {
            using var cmd = Command("""
                INSERT INTO cursors (name, last_seq) VALUES ($n, $s)
                ON CONFLICT (name) DO UPDATE SET last_seq = $s
                """);
            cmd.Parameters.AddWithValue("$n", name);
            cmd.Parameters.AddWithValue("$s", seq);
            cmd.ExecuteNonQuery();
        }
    }

    internal string? GetMetaValue(string key)
    {
        lock (_gate) return GetMeta(key);
    }

    internal void SetMetaValue(string key, string? value)
    {
        lock (_gate)
        {
            using var cmd = Command(value is null
                ? "DELETE FROM meta WHERE key = $k"
                : "INSERT INTO meta (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = $v");
            cmd.Parameters.AddWithValue("$k", key);
            if (value is not null) cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Forgets everything learned from a server (versions, cursors, tombstones) and marks every live record as a
    /// local edit, so the replica uploads cleanly into a different account instead of conflicting with it.
    /// </summary>
    internal void ResetSyncState()
    {
        InTransaction(() =>
        {
            Execute("DELETE FROM records WHERE deleted = 1");
            Execute("UPDATE records SET version = 0, dirty = 1, local_rev = local_rev + 1");
            Execute("DELETE FROM cursors");
        });
    }

    private const string Columns = "collection, id, version, schema_version, deleted, updated_at, device_id, body, dirty, local_rev";

    private static SyncRow ReadRow(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt32(3), r.GetInt64(4) != 0, r.GetInt64(5), r.GetString(6),
        r.IsDBNull(7) ? null : r.GetString(7), r.GetInt64(8) != 0, r.GetInt64(9));

    private static List<SyncRow> ReadRows(SqliteCommand cmd)
    {
        var rows = new List<SyncRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(ReadRow(reader));
        return rows;
    }

    private SqliteCommand Command(string sql)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = _transaction;
        return cmd;
    }

    private void Execute(string sql)
    {
        using var cmd = Command(sql);
        cmd.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using var versionCmd = Command("PRAGMA user_version");
        var current = Convert.ToInt32(versionCmd.ExecuteScalar());
        if (current > SchemaVersion)
            throw new InvalidOperationException($"{FilePath} was written by a newer Helm (schema {current}).");
        if (current == SchemaVersion) return;

        Execute($"""
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS records (
                collection     TEXT    NOT NULL,
                id             TEXT    NOT NULL,
                version        INTEGER NOT NULL DEFAULT 0,
                schema_version INTEGER NOT NULL,
                deleted        INTEGER NOT NULL DEFAULT 0,
                updated_at     INTEGER NOT NULL,
                device_id      TEXT    NOT NULL,
                body           TEXT,
                dirty          INTEGER NOT NULL DEFAULT 0,
                local_rev      INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (collection, id)
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS records_dirty ON records (dirty) WHERE dirty = 1;
            CREATE TABLE IF NOT EXISTS cursors (name TEXT PRIMARY KEY, last_seq INTEGER NOT NULL);
            PRAGMA user_version = {SchemaVersion};
            """);
    }

    private string? GetMeta(string key)
    {
        using var cmd = Command("SELECT value FROM meta WHERE key = $k");
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private string CreateDeviceId()
    {
        var id = SyncIds.NewId();
        using var cmd = Command("INSERT INTO meta (key, value) VALUES ('device_id', $v)");
        cmd.Parameters.AddWithValue("$v", id);
        cmd.ExecuteNonQuery();
        return id;
    }
}
