using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Helm.Core.Sync;

/// <summary>One row of the local replica.</summary>
/// <param name="Version">Server version the row reflects, or that the pending local edit was made on (0 = never synced).</param>
/// <param name="Body">Record JSON (decrypted); null for a tombstone.</param>
/// <param name="Dirty">Has a local edit not yet accepted by the server.</param>
/// <param name="LocalRev">Bumped on every write, so an edit made while a push is in flight is not marked clean.</param>
/// <param name="Reseal">Dirty only to be re-encrypted under a new key epoch, not edited: loses every conflict.</param>
internal sealed record SyncRow(
    string Collection, string Id, long Version, int SchemaVersion, bool Deleted, long UpdatedAtMs, string DeviceId,
    string? Body, bool Dirty, long LocalRev, bool Reseal = false);

internal enum LocalWriteResult
{
    Written,
    NotFound,
    RejectedNewerSchema,
}

/// <summary>The local key does not open this replica (e.g. the key file was lost); it must be rebuilt from the server.</summary>
public sealed class SyncLocalKeyException(string message) : Exception(message);

/// <summary>
/// The local replica: every synced record of every collection in one SQLite file, plus the pull cursor.
/// Record bodies are encrypted at rest with a device-local key (AES-256-GCM, AAD "helm-local/v1|collection|id"),
/// kept apart from the account key and protected by DPAPI, so a copied database file is unreadable elsewhere.
/// One connection guarded by a lock: the data is small and SQLite is fast, so this keeps threading trivial.
/// </summary>
public sealed class SyncDatabase : IDisposable
{
    private const int SchemaVersion = 2;
    private const string KeyCheckMeta = "local_key_check";
    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly byte[] _localKey;
    private SqliteTransaction? _transaction;

    /// <param name="localKey">32 random bytes from <see cref="DpapiLocalKeyStore"/>; the same key on every open.</param>
    /// <exception cref="SyncLocalKeyException">The file was encrypted with another key.</exception>
    public SyncDatabase(string filePath, byte[] localKey)
    {
        if (localKey.Length != SyncKeyring.KeySize) throw new ArgumentException("The local key must be 32 bytes.", nameof(localKey));
        FilePath = filePath;
        _localKey = localKey.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        // No pooling: Dispose must release the file (tests delete it; "reset" must be able to as well).
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();
        try
        {
            // secure_delete: overwritten and deleted content is zeroed rather than left in free pages.
            Execute("PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA secure_delete = ON;");
            EnsureSchema();
            VerifyLocalKey();
            DeviceId = GetMeta("device_id") ?? CreateDeviceId();
        }
        catch
        {
            _connection.Dispose();
            throw;
        }
    }

    public string FilePath { get; }

    /// <summary>Random id of this installation, stamped on every local edit (tie-breaker for last-writer-wins).</summary>
    public string DeviceId { get; }

    public void Dispose()
    {
        lock (_gate) _connection.Dispose();
        CryptographicOperations.ZeroMemory(_localKey);
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
                INSERT INTO records (collection, id, version, schema_version, deleted, updated_at, device_id, body, dirty, local_rev, reseal)
                VALUES ($c, $id, 0, $s, $del, $t, $dev, $b, 1, 1, 0)
                ON CONFLICT (collection, id) DO UPDATE SET
                    schema_version = $s, deleted = $del, updated_at = $t, device_id = $dev, body = $b,
                    dirty = 1, local_rev = local_rev + 1, reseal = 0
                """);
            cmd.Parameters.AddWithValue("$c", collection);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$s", schemaVersion);
            cmd.Parameters.AddWithValue("$del", deleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$t", updatedAtMs);
            cmd.Parameters.AddWithValue("$dev", DeviceId);
            cmd.Parameters.AddWithValue("$b", (object?)EncryptBody(collection, id, body) ?? DBNull.Value);
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
                UPDATE records SET version = $v,
                    dirty = CASE WHEN local_rev = $rev THEN 0 ELSE dirty END,
                    reseal = CASE WHEN local_rev = $rev THEN 0 ELSE reseal END
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
                INSERT INTO records (collection, id, version, schema_version, deleted, updated_at, device_id, body, dirty, local_rev, reseal)
                VALUES ($c, $id, $v, $s, $del, $t, $dev, $b, 0, 1, 0)
                ON CONFLICT (collection, id) DO UPDATE SET
                    version = $v, schema_version = $s, deleted = $del, updated_at = $t, device_id = $dev, body = $b,
                    dirty = 0, local_rev = local_rev + 1, reseal = 0
                """);
            cmd.Parameters.AddWithValue("$c", collection);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$v", version);
            cmd.Parameters.AddWithValue("$s", content.SchemaVersion);
            cmd.Parameters.AddWithValue("$del", deleted ? 1 : 0);
            cmd.Parameters.AddWithValue("$t", content.UpdatedAtMs);
            cmd.Parameters.AddWithValue("$dev", content.DeviceId);
            cmd.Parameters.AddWithValue("$b", deleted ? DBNull.Value : (object?)EncryptBody(collection, id, content.Body) ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// After a key rotation: queue every clean record (tombstones and newer-schema records too) to be uploaded again,
    /// sealed with the new key. Pending real edits are left alone; they are sealed with the new key anyway.
    /// </summary>
    internal int MarkAllForReseal()
    {
        lock (_gate)
        {
            using var cmd = Command("UPDATE records SET dirty = 1, reseal = 1, local_rev = local_rev + 1 WHERE dirty = 0");
            return cmd.ExecuteNonQuery();
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
            Execute("UPDATE records SET version = 0, dirty = 1, reseal = 0, local_rev = local_rev + 1");
            Execute("DELETE FROM cursors");
        });
    }

    private const string Columns = "collection, id, version, schema_version, deleted, updated_at, device_id, body, dirty, local_rev, reseal";

    private SyncRow ReadRow(SqliteDataReader r)
    {
        var collection = r.GetString(0);
        var id = r.GetString(1);
        var body = r.IsDBNull(7) ? null : DecryptBody(collection, id, r.GetFieldValue<byte[]>(7));
        return new SyncRow(collection, id, r.GetInt64(2), r.GetInt32(3), r.GetInt64(4) != 0, r.GetInt64(5), r.GetString(6),
            body, r.GetInt64(8) != 0, r.GetInt64(9), r.GetInt64(10) != 0);
    }

    private List<SyncRow> ReadRows(SqliteCommand cmd)
    {
        var rows = new List<SyncRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) rows.Add(ReadRow(reader));
        return rows;
    }

    private byte[]? EncryptBody(string collection, string id, string? body)
    {
        if (body is null) return null;
        var plaintext = Encoding.UTF8.GetBytes(body);
        var output = new byte[12 + 16 + plaintext.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using (var gcm = new AesGcm(_localKey, 16))
            gcm.Encrypt(output.AsSpan(0, 12), plaintext, output.AsSpan(28), output.AsSpan(12, 16), LocalAad(collection, id));
        CryptographicOperations.ZeroMemory(plaintext);
        return output;
    }

    private string? DecryptBody(string collection, string id, byte[] stored)
    {
        if (stored.Length < 28) throw new SyncLocalKeyException($"Damaged local record {collection}/{id}.");
        var plaintext = new byte[stored.Length - 28];
        try
        {
            using var gcm = new AesGcm(_localKey, 16);
            gcm.Decrypt(stored.AsSpan(0, 12), stored.AsSpan(28), stored.AsSpan(12, 16), plaintext, LocalAad(collection, id));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException)
        {
            throw new SyncLocalKeyException($"Local record {collection}/{id} cannot be decrypted with this device's key.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] LocalAad(string collection, string id) => Encoding.UTF8.GetBytes($"helm-local/v1|{collection}|{id}");

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

        if (current == 0)
        {
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
                    body           BLOB,
                    dirty          INTEGER NOT NULL DEFAULT 0,
                    local_rev      INTEGER NOT NULL DEFAULT 0,
                    reseal         INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (collection, id)
                ) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS records_dirty ON records (dirty) WHERE dirty = 1;
                CREATE TABLE IF NOT EXISTS cursors (name TEXT PRIMARY KEY, last_seq INTEGER NOT NULL);
                PRAGMA user_version = {SchemaVersion};
                """);
            return;
        }

        // v1 (Helm 0.5.0): plaintext bodies, no reseal column. Encrypt in place, then rewrite the file so no
        // plaintext survives in free pages or the WAL.
        InTransaction(() =>
        {
            Execute("ALTER TABLE records ADD COLUMN reseal INTEGER NOT NULL DEFAULT 0");
            var plain = new List<(string Collection, string Id, string Body)>();
            using (var select = Command("SELECT collection, id, body FROM records WHERE body IS NOT NULL"))
            using (var reader = select.ExecuteReader())
            {
                while (reader.Read()) plain.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            foreach (var (collection, id, body) in plain)
            {
                using var update = Command("UPDATE records SET body = $b WHERE collection = $c AND id = $id");
                update.Parameters.AddWithValue("$b", EncryptBody(collection, id, body)!);
                update.Parameters.AddWithValue("$c", collection);
                update.Parameters.AddWithValue("$id", id);
                update.ExecuteNonQuery();
            }
            Execute($"PRAGMA user_version = {SchemaVersion}");
        });
        Execute("PRAGMA wal_checkpoint(TRUNCATE);");
        Execute("VACUUM;");
    }

    /// <summary>A known value encrypted with the local key: opening with another key fails fast instead of per row.</summary>
    private void VerifyLocalKey()
    {
        var stored = GetMeta(KeyCheckMeta);
        if (stored is null)
        {
            using var insert = Command("INSERT INTO meta (key, value) VALUES ($k, $v)");
            insert.Parameters.AddWithValue("$k", KeyCheckMeta);
            insert.Parameters.AddWithValue("$v", Convert.ToBase64String(EncryptBody("meta", KeyCheckMeta, "ok")!));
            insert.ExecuteNonQuery();
            return;
        }
        if (DecryptBody("meta", KeyCheckMeta, Convert.FromBase64String(stored)) != "ok")
            throw new SyncLocalKeyException("The local sync key does not match this replica.");
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
