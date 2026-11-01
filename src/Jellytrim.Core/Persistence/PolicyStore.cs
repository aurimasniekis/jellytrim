using Jellytrim.Core.Domain;
using Jellytrim.Core.Policies;

namespace Jellytrim.Core.Persistence;

public sealed record PolicyRun(Guid PolicyId, int Revision, long ScanGeneration, long ProtectionRevision, DateTime EvaluatedAtUtc, int EligibleCount, int ConsideredCount, long EligibleLogicalBytes, long EligibleReclaimableBytes, IReadOnlyDictionary<string, int> BlockedByRule);

public sealed class PolicyStore(JellytrimDb db)
{
    public IReadOnlyList<CleanupPolicy> List() => db.Read(c =>
        c.Cmd("SELECT document FROM policies ORDER BY name COLLATE NOCASE").Rows(r => Json.Read<CleanupPolicy>(r.GetString(0))));

    public CleanupPolicy? Get(Guid id) => db.Read(c =>
        c.Cmd("SELECT document FROM policies WHERE policy_id=$i").With("$i", Db.Id(id)).Rows(r => Json.Read<CleanupPolicy>(r.GetString(0))).FirstOrDefault());

    public CleanupPolicy Create(CleanupPolicy policy, DateTime nowUtc)
    {
        policy.Id = Guid.NewGuid();
        policy.Revision = 1;
        db.Write((c, tx) => c.Cmd("INSERT INTO policies(policy_id,revision,name,document,created_at,updated_at) VALUES($i,1,$n,$d,$t,$t)", tx)
            .With("$i", Db.Id(policy.Id)).With("$n", policy.Name).With("$d", Json.Write(policy)).With("$t", Db.Ms(nowUtc)).Exec());
        return policy;
    }

    public CleanupPolicy Update(CleanupPolicy policy, int expectedRevision, DateTime nowUtc) => db.Write((c, tx) =>
    {
        var current = (int)c.Cmd("SELECT COALESCE(MAX(revision),-1) FROM policies WHERE policy_id=$i", tx).With("$i", Db.Id(policy.Id)).Scalar();
        if (current < 0)
        {
            throw new KeyNotFoundException("Policy not found.");
        }

        if (current != expectedRevision)
        {
            throw new ConcurrencyConflictException($"Policy changed (revision {current}, expected {expectedRevision}).");
        }

        policy.Revision = current + 1;
        c.Cmd("UPDATE policies SET revision=$r,name=$n,document=$d,updated_at=$t WHERE policy_id=$i", tx)
            .With("$i", Db.Id(policy.Id)).With("$r", policy.Revision).With("$n", policy.Name).With("$d", Json.Write(policy)).With("$t", Db.Ms(nowUtc)).Exec();
        c.Cmd("DELETE FROM policy_eligible WHERE policy_id=$i", tx).With("$i", Db.Id(policy.Id)).Exec();
        c.Cmd("DELETE FROM policy_runs WHERE policy_id=$i", tx).With("$i", Db.Id(policy.Id)).Exec();
        return policy;
    });

    public bool Delete(Guid id) => db.Write((c, tx) => c.Cmd("DELETE FROM policies WHERE policy_id=$i", tx).With("$i", Db.Id(id)).Exec() > 0);

    public PolicyRun? GetRun(Guid id) => db.Read(c =>
        c.Cmd("SELECT policy_id,revision,scan_generation,protection_revision,evaluated_at,eligible_count,considered_count,eligible_logical,eligible_reclaimable,blocked FROM policy_runs WHERE policy_id=$i")
            .With("$i", Db.Id(id))
            .Rows(r => new PolicyRun(Guid.Parse(r.GetString(0)), r.GetInt32(1), r.GetInt64(2), r.GetInt64(3), Db.Date(r, 4)!.Value, r.GetInt32(5), r.GetInt32(6), r.GetInt64(7), r.GetInt64(8), Json.Read<Dictionary<string, int>>(r.GetString(9))))
            .FirstOrDefault());

    /// <summary>Replaces the policy's eligible set and run summary atomically.</summary>
    public void SaveRun(PolicyRun run, IEnumerable<Guid> eligibleItemIds) => db.Write((c, tx) =>
    {
        var policy = Db.Id(run.PolicyId);
        c.Cmd("DELETE FROM policy_eligible WHERE policy_id=$p", tx).With("$p", policy).Exec();
        using var insert = c.Cmd("INSERT OR IGNORE INTO policy_eligible(policy_id,item_key) SELECT $p,item_key FROM items WHERE item_id=$i", tx);
        insert.Parameters.AddWithValue("$p", policy);
        var item = insert.Parameters.Add("$i", Microsoft.Data.Sqlite.SqliteType.Text);
        foreach (var id in eligibleItemIds)
        {
            item.Value = Db.Id(id);
            insert.ExecuteNonQuery();
        }

        c.Cmd("""
            INSERT INTO policy_runs(policy_id,revision,scan_generation,protection_revision,evaluated_at,eligible_count,considered_count,eligible_logical,eligible_reclaimable,blocked)
            VALUES($p,$r,$g,$pr,$e,$ec,$cc,$el,$er,$b)
            ON CONFLICT(policy_id) DO UPDATE SET revision=$r,scan_generation=$g,protection_revision=$pr,evaluated_at=$e,eligible_count=$ec,considered_count=$cc,eligible_logical=$el,eligible_reclaimable=$er,blocked=$b
            """, tx)
            .With("$p", policy).With("$r", run.Revision).With("$g", run.ScanGeneration).With("$pr", run.ProtectionRevision).With("$e", Db.Ms(run.EvaluatedAtUtc))
            .With("$ec", run.EligibleCount).With("$cc", run.ConsideredCount).With("$el", run.EligibleLogicalBytes).With("$er", run.EligibleReclaimableBytes).With("$b", Json.Write(run.BlockedByRule)).Exec();
    });

    public IReadOnlyList<Protection> ListProtections() => db.Read(c =>
        c.Cmd("SELECT target_id,target_type,name,note,created_at,created_by FROM protections ORDER BY created_at DESC")
            .Rows(r => new Protection(Guid.Parse(r.GetString(0)), (ProtectionTarget)r.GetInt32(1), r.GetString(2), Db.Str(r, 3), Db.Date(r, 4)!.Value, Db.Str(r, 5))));

    public IReadOnlySet<Guid> ProtectedIds() => db.Read(c =>
        c.Cmd("SELECT target_id FROM protections").Rows(r => Guid.Parse(r.GetString(0))).ToHashSet());

    public void AddProtection(Protection protection) => db.Write((c, tx) =>
    {
        c.Cmd("INSERT OR REPLACE INTO protections(target_id,target_type,name,note,created_at,created_by) VALUES($i,$t,$n,$o,$a,$b)", tx)
            .With("$i", Db.Id(protection.TargetId)).With("$t", (int)protection.TargetType).With("$n", protection.Name).With("$o", protection.Note).With("$a", Db.Ms(protection.CreatedAtUtc)).With("$b", protection.CreatedBy).Exec();
        Bump(c, tx);
    });

    public bool RemoveProtection(Guid targetId) => db.Write((c, tx) =>
    {
        var removed = c.Cmd("DELETE FROM protections WHERE target_id=$i", tx).With("$i", Db.Id(targetId)).Exec() > 0;
        if (removed)
        {
            Bump(c, tx);
        }

        return removed;
    });

    public long ProtectionRevision() => db.Read(c => c.Cmd("SELECT value FROM counters WHERE key='protection_revision'").Scalar());

    private static void Bump(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction tx) =>
        c.Cmd("UPDATE counters SET value=value+1 WHERE key='protection_revision'", tx).Exec();
}
