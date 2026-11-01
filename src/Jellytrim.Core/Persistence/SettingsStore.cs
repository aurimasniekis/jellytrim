using Jellytrim.Core.Domain;

namespace Jellytrim.Core.Persistence;

public sealed class ConcurrencyConflictException(string message) : Exception(message);

public sealed class SettingsStore(JellytrimDb db)
{
    private const string Key = "settings";

    public Versioned<JellytrimSettings> Get() => db.Read(c =>
    {
        var rows = c.Cmd("SELECT revision, value FROM settings WHERE key=$k").With("$k", Key)
            .Rows(r => new Versioned<JellytrimSettings>(Json.Read<JellytrimSettings>(r.GetString(1)), r.GetInt32(0)));
        return rows.Count == 0 ? new Versioned<JellytrimSettings>(new JellytrimSettings(), 0) : rows[0];
    });

    /// <summary>Compare-and-swap: fails if the stored revision moved on since the caller read it.</summary>
    public int Save(JellytrimSettings settings, int expectedRevision, DateTime nowUtc) => db.Write((c, tx) =>
    {
        var current = (int)c.Cmd("SELECT COALESCE(MAX(revision),0) FROM settings WHERE key=$k", tx).With("$k", Key).Scalar();
        if (current != expectedRevision)
        {
            throw new ConcurrencyConflictException($"Settings changed (revision {current}, expected {expectedRevision}).");
        }

        var next = current + 1;
        c.Cmd("INSERT INTO settings(key,revision,value,updated_at) VALUES($k,$r,$v,$t) ON CONFLICT(key) DO UPDATE SET revision=$r,value=$v,updated_at=$t", tx)
            .With("$k", Key).With("$r", next).With("$v", Json.Write(settings)).With("$t", Db.Ms(nowUtc)).Exec();
        return next;
    });
}
