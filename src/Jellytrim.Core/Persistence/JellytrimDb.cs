using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellytrim.Core.Persistence;

public sealed class DatabaseTooNewException(int found, int supported)
    : Exception($"The jellytrim database has schema version {found}, but this build supports up to {supported}. Install a newer jellytrim or restore a backup.")
{
    public int Found { get; } = found;
}

/// <summary>
/// Plugin-owned SQLite database: a single file in the plugin data folder, WAL mode, opened per operation.
/// Writers take BEGIN IMMEDIATE so that a conflict surfaces at the start of a transaction, where busy_timeout
/// can absorb it. SQLite is justified over Jellyfin's XML config because plans, jobs and audit must survive
/// crashes transactionally and the explorer needs indexed queries over 100k+ items.
/// </summary>
public sealed class JellytrimDb
{
    private readonly string _connectionString;
    private readonly ILogger _logger;

    public JellytrimDb(string path, ILogger logger)
    {
        Path_ = path;
        _logger = logger;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    public string Path_ { get; }

    /// <summary>Creates or migrates the schema. Refuses a database newer than this build rather than guessing.</summary>
    public void Initialize()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path_)!);
        using var connection = Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
            pragma.ExecuteNonQuery();
        }

        var version = GetUserVersion(connection);
        if (version > Migrations.Latest)
        {
            throw new DatabaseTooNewException(version, Migrations.Latest);
        }

        foreach (var (target, sql) in Migrations.All.Where(m => m.Version > version))
        {
            using var tx = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = tx;
            command.CommandText = sql + $"\nPRAGMA user_version = {target};";
            command.ExecuteNonQuery();
            tx.Commit();
            _logger.LogInformation("jellytrim database migrated to schema {SchemaVersion}", target);
        }
    }

    public int SchemaVersion()
    {
        using var connection = Open();
        return GetUserVersion(connection);
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public T Read<T>(Func<SqliteConnection, T> work)
    {
        using var connection = Open();
        return work(connection);
    }

    public void Write(Action<SqliteConnection, SqliteTransaction> work) => Write<object?>((c, t) =>
    {
        work(c, t);
        return null;
    });

    public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> work)
    {
        using var connection = Open();
        using var tx = connection.BeginTransaction(deferred: false);
        var result = work(connection, tx);
        tx.Commit();
        return result;
    }

    /// <summary>Releases this database's pooled handles so the file is not held open after shutdown.</summary>
    public void ReleaseHandles()
    {
        using var connection = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(connection);
    }

    private static int GetUserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}

public static class Db
{
    public static long? Ms(DateTime? value) => value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : null;

    public static long Ms(DateTime value) => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    public static DateTime? Date(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(i)).UtcDateTime;

    public static string Id(Guid id) => id.ToString("N");

    public static string? Id(Guid? id) => id?.ToString("N");

    public static Guid? GuidOrNull(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : Guid.Parse(r.GetString(i));

    public static string? Str(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static int? Int(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);

    public static SqliteCommand Cmd(this SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        var command = c.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        return command;
    }

    public static SqliteCommand With(this SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    public static long Scalar(this SqliteCommand command)
    {
        using (command)
        {
            var value = command.ExecuteScalar();
            return value is null or DBNull ? 0 : Convert.ToInt64(value);
        }
    }

    public static int Exec(this SqliteCommand command)
    {
        using (command)
        {
            return command.ExecuteNonQuery();
        }
    }

    public static List<T> Rows<T>(this SqliteCommand command, Func<SqliteDataReader, T> map)
    {
        using (command)
        {
            using var reader = command.ExecuteReader();
            var rows = new List<T>();
            while (reader.Read())
            {
                rows.Add(map(reader));
            }

            return rows;
        }
    }
}
