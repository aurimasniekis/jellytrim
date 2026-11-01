namespace Jellytrim.Core.Persistence;

public sealed record AuditEntry(long Id, DateTime AtUtc, string? Actor, string Action, string? Subject, string? CorrelationId, string Outcome, string? Details);

/// <summary>Durable audit trail. Its retention is separate from Jellyfin's diagnostic logs and is set in settings.</summary>
public sealed class AuditStore(JellytrimDb db)
{
    public void Record(DateTime atUtc, string? actor, string action, string? subject, string? correlationId, string outcome, string? details = null) =>
        db.Write((c, tx) => Record(c, tx, atUtc, actor, action, subject, correlationId, outcome, details));

    public static void Record(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx, DateTime atUtc, string? actor, string action, string? subject, string? correlationId, string outcome, string? details = null) =>
        c.Cmd("INSERT INTO audit(at,actor,action,subject,correlation_id,outcome,details) VALUES($a,$b,$c,$d,$e,$f,$g)", tx)
            .With("$a", Db.Ms(atUtc)).With("$b", actor).With("$c", action).With("$d", subject).With("$e", correlationId).With("$f", outcome).With("$g", details).Exec();

    public (IReadOnlyList<AuditEntry> Rows, long Total) List(int offset, int limit) => db.Read(c =>
    {
        var total = c.Cmd("SELECT COUNT(*) FROM audit").Scalar();
        var rows = c.Cmd("SELECT audit_id,at,actor,action,subject,correlation_id,outcome,details FROM audit ORDER BY audit_id DESC LIMIT $l OFFSET $o")
            .With("$l", limit).With("$o", offset)
            .Rows(r => new AuditEntry(r.GetInt64(0), Db.Date(r, 1)!.Value, Db.Str(r, 2), r.GetString(3), Db.Str(r, 4), Db.Str(r, 5), r.GetString(6), Db.Str(r, 7)));
        return ((IReadOnlyList<AuditEntry>)rows, total);
    });

    public int Prune(DateTime olderThanUtc) => db.Write((c, tx) =>
        c.Cmd("DELETE FROM audit WHERE at < $t", tx).With("$t", Db.Ms(olderThanUtc)).Exec());
}
