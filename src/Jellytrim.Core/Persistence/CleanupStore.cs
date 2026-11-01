using Jellytrim.Core.Cleanup;
using Microsoft.Data.Sqlite;

namespace Jellytrim.Core.Persistence;

/// <summary>Durable plan and job state. Every status change commits before the action it describes, so a crash leaves evidence.</summary>
public sealed class CleanupStore(JellytrimDb db)
{
    public const string PlanReady = "Ready";
    public const string PlanExecuting = "Executing";
    public const string PlanExecuted = "Executed";
    public const string PlanRejected = "Rejected";

    public (PlanSummary Summary, string Document) SavePlan(CleanupPlan plan)
    {
        var document = Json.Write(plan);
        var hash = CleanupPlan.ComputeHash(document);
        var summary = new PlanSummary(plan.Id, plan.CreatedAtUtc, plan.ExpiresAtUtc, plan.CreatedBy, PlanReady, hash, plan.Totals.Items, plan.Totals.Files, plan.Totals.EstimatedReclaimableBytes);
        db.Write((c, tx) => c.Cmd("INSERT INTO plans(plan_id,created_at,expires_at,created_by,status,hash,item_count,file_count,reclaim_bytes,document) VALUES($i,$c,$e,$b,$s,$h,$n,$f,$r,$d)", tx)
            .With("$i", Db.Id(plan.Id)).With("$c", Db.Ms(plan.CreatedAtUtc)).With("$e", Db.Ms(plan.ExpiresAtUtc)).With("$b", plan.CreatedBy).With("$s", PlanReady).With("$h", hash)
            .With("$n", plan.Totals.Items).With("$f", plan.Totals.Files).With("$r", plan.Totals.EstimatedReclaimableBytes).With("$d", document).Exec());
        return (summary, document);
    }

    public PlanSummary? GetPlanSummary(Guid id) => db.Read(c =>
        c.Cmd("SELECT plan_id,created_at,expires_at,created_by,status,hash,item_count,file_count,reclaim_bytes FROM plans WHERE plan_id=$i").With("$i", Db.Id(id)).Rows(MapPlan).FirstOrDefault());

    public IReadOnlyList<PlanSummary> ListPlans(int limit) => db.Read(c =>
        c.Cmd("SELECT plan_id,created_at,expires_at,created_by,status,hash,item_count,file_count,reclaim_bytes FROM plans ORDER BY created_at DESC LIMIT $l").With("$l", limit).Rows(MapPlan));

    public string? GetPlanDocument(Guid id) => db.Read(c =>
        c.Cmd("SELECT document FROM plans WHERE plan_id=$i").With("$i", Db.Id(id)).Rows(r => r.GetString(0)).FirstOrDefault());

    public void SetPlanStatus(Guid id, string status) => db.Write((c, tx) =>
        c.Cmd("UPDATE plans SET status=$s WHERE plan_id=$i", tx).With("$s", status).With("$i", Db.Id(id)).Exec());

    private static PlanSummary MapPlan(SqliteDataReader r) =>
        new(Guid.Parse(r.GetString(0)), Db.Date(r, 1)!.Value, Db.Date(r, 2)!.Value, Db.Str(r, 3), r.GetString(4), r.GetString(5), r.GetInt32(6), r.GetInt32(7), r.GetInt64(8));

    public JobRow? FindJobByIdempotencyKey(string key) => db.Read(c =>
        c.Cmd(JobSelect + " WHERE idempotency_key=$k").With("$k", key).Rows(MapJob).FirstOrDefault());

    public JobRow? FindJobByPlan(Guid planId) => db.Read(c =>
        c.Cmd(JobSelect + " WHERE plan_id=$p").With("$p", Db.Id(planId)).Rows(MapJob).FirstOrDefault());

    public JobRow? GetJob(Guid id) => db.Read(c =>
        c.Cmd(JobSelect + " WHERE job_id=$i").With("$i", Db.Id(id)).Rows(MapJob).FirstOrDefault());

    public JobRow? ActiveJob() => db.Read(c =>
        c.Cmd(JobSelect + " WHERE status IN ('Queued','Preflight','Running') LIMIT 1").Rows(MapJob).FirstOrDefault());

    public (IReadOnlyList<JobRow> Rows, long Total) ListJobs(int offset, int limit) => db.Read(c =>
    {
        var total = c.Cmd("SELECT COUNT(*) FROM jobs").Scalar();
        var rows = c.Cmd(JobSelect + " ORDER BY created_at DESC LIMIT $l OFFSET $o").With("$l", limit).With("$o", offset).Rows(MapJob);
        return ((IReadOnlyList<JobRow>)rows, total);
    });

    /// <summary>Creates the job with all its items and files in one transaction. The database's unique index rejects a second active job.</summary>
    public void InsertJob(JobRow job, string idempotencyKey, IReadOnlyList<(PlanJobEntry Entry, IReadOnlyList<PlanFile> Files)> entries) => db.Write((c, tx) =>
    {
        c.Cmd("""
            INSERT INTO jobs(job_id,plan_id,idempotency_key,correlation_id,status,created_by,created_at,total_items,reclaim_estimate)
            VALUES($i,$p,$k,$c,$s,$b,$t,$n,$r)
            """, tx).With("$i", Db.Id(job.Id)).With("$p", Db.Id(job.PlanId)).With("$k", idempotencyKey).With("$c", job.CorrelationId).With("$s", job.Status).With("$b", job.CreatedBy)
            .With("$t", Db.Ms(job.CreatedAtUtc)).With("$n", job.TotalItems).With("$r", job.ReclaimEstimateBytes).Exec();
        c.Cmd("UPDATE plans SET status=$s WHERE plan_id=$p", tx).With("$s", PlanExecuting).With("$p", Db.Id(job.PlanId)).Exec();
        for (var seq = 0; seq < entries.Count; seq++)
        {
            var (entry, files) = entries[seq];
            c.Cmd("INSERT INTO job_items(job_id,seq,item_id,name,status) VALUES($j,$s,$i,$n,$st)", tx)
                .With("$j", Db.Id(job.Id)).With("$s", seq).With("$i", Db.Id(entry.ItemId)).With("$n", entry.Name).With("$st", ItemStatus.Pending).Exec();
            for (var f = 0; f < files.Count; f++)
            {
                c.Cmd("INSERT INTO job_files(job_id,seq,file_seq,path,role,status,bytes) VALUES($j,$s,$f,$p,$r,$st,$b)", tx)
                    .With("$j", Db.Id(job.Id)).With("$s", seq).With("$f", f).With("$p", files[f].Path).With("$r", (int)files[f].Role).With("$st", FileStatus.Pending).With("$b", files[f].Size).Exec();
            }
        }
    });

    public void SetJobStatus(Guid id, string status, DateTime nowUtc, string? message = null, bool started = false, bool finished = false) => db.Write((c, tx) =>
        c.Cmd("UPDATE jobs SET status=$s,message=COALESCE($m,message),started_at=CASE WHEN $st THEN $t ELSE started_at END,finished_at=CASE WHEN $f THEN $t ELSE finished_at END WHERE job_id=$i", tx)
            .With("$s", status).With("$m", message).With("$st", started).With("$f", finished).With("$t", Db.Ms(nowUtc)).With("$i", Db.Id(id)).Exec());

    public void SetJobVolumes(Guid id, IReadOnlyList<VolumeFree>? before, IReadOnlyList<VolumeFree>? after) => db.Write((c, tx) =>
        c.Cmd("UPDATE jobs SET free_before=COALESCE($b,free_before),free_after=COALESCE($a,free_after) WHERE job_id=$i", tx)
            .With("$b", before is null ? null : Json.Write(before)).With("$a", after is null ? null : Json.Write(after)).With("$i", Db.Id(id)).Exec());

    public void SetJobProblems(Guid id, IReadOnlyList<JobProblem> problems) => db.Write((c, tx) =>
        c.Cmd("UPDATE jobs SET problems=$p WHERE job_id=$i", tx).With("$p", Json.Write(problems)).With("$i", Db.Id(id)).Exec());

    public bool RequestCancel(Guid id) => db.Write((c, tx) =>
        c.Cmd("UPDATE jobs SET cancel_requested=1 WHERE job_id=$i AND status IN ('Queued','Preflight','Running')", tx).With("$i", Db.Id(id)).Exec() > 0);

    public bool IsCancelRequested(Guid id) => db.Read(c =>
        c.Cmd("SELECT cancel_requested FROM jobs WHERE job_id=$i").With("$i", Db.Id(id)).Scalar() != 0);

    public void SetItem(Guid job, int seq, string status, DateTime nowUtc, string? code = null, string? message = null, long freed = 0, string? reconcile = null, bool started = false, bool finished = false) => db.Write((c, tx) =>
    {
        c.Cmd("""
            UPDATE job_items SET status=$s,code=COALESCE($c,code),message=COALESCE($m,message),freed_bytes=freed_bytes+$fb,reconcile=COALESCE($r,reconcile),
                started_at=CASE WHEN $st THEN $t ELSE started_at END,finished_at=CASE WHEN $f THEN $t ELSE finished_at END
            WHERE job_id=$j AND seq=$q
            """, tx).With("$s", status).With("$c", code).With("$m", message).With("$fb", freed).With("$r", reconcile).With("$st", started).With("$f", finished).With("$t", Db.Ms(nowUtc)).With("$j", Db.Id(job)).With("$q", seq).Exec();
        if (finished)
        {
            c.Cmd("UPDATE jobs SET completed_items=completed_items+1,deleted_bytes=deleted_bytes+$b WHERE job_id=$j", tx).With("$b", freed).With("$j", Db.Id(job)).Exec();
        }
    });

    public void SetFile(Guid job, int seq, int fileSeq, string status, DateTime nowUtc, string? code = null) => db.Write((c, tx) =>
        c.Cmd("UPDATE job_files SET status=$s,code=$c,at=$t WHERE job_id=$j AND seq=$q AND file_seq=$f", tx)
            .With("$s", status).With("$c", code).With("$t", Db.Ms(nowUtc)).With("$j", Db.Id(job)).With("$q", seq).With("$f", fileSeq).Exec());

    public void MarkRemainingItems(Guid job, string status, DateTime nowUtc) => db.Write((c, tx) =>
    {
        c.Cmd("UPDATE job_items SET status=$s,finished_at=$t WHERE job_id=$j AND status=$p", tx).With("$s", status).With("$t", Db.Ms(nowUtc)).With("$j", Db.Id(job)).With("$p", ItemStatus.Pending).Exec();
        c.Cmd("UPDATE job_files SET status=$s,at=$t WHERE job_id=$j AND status=$p", tx).With("$s", FileStatus.NotAttempted).With("$t", Db.Ms(nowUtc)).With("$j", Db.Id(job)).With("$p", FileStatus.Pending).Exec();
    });

    public void MarkFilesNotAttempted(Guid job, int seq, DateTime nowUtc) => db.Write((c, tx) =>
        c.Cmd("UPDATE job_files SET status=$s,at=$t WHERE job_id=$j AND seq=$q AND status=$p", tx).With("$s", FileStatus.NotAttempted).With("$t", Db.Ms(nowUtc)).With("$j", Db.Id(job)).With("$q", seq).With("$p", FileStatus.Pending).Exec());

    public (IReadOnlyList<JobItemRow> Rows, long Total) ListJobItems(Guid job, int offset, int limit, string? status) => db.Read(c =>
    {
        var filter = status is null ? string.Empty : "AND status=$st";
        var count = c.Cmd($"SELECT COUNT(*) FROM job_items WHERE job_id=$j {filter}").With("$j", Db.Id(job));
        var cmd = c.Cmd($"SELECT seq,item_id,name,status,code,message,started_at,finished_at,freed_bytes,reconcile FROM job_items WHERE job_id=$j {filter} ORDER BY seq LIMIT $l OFFSET $o").With("$j", Db.Id(job)).With("$l", limit).With("$o", offset);
        if (status is not null)
        {
            count.With("$st", status);
            cmd.With("$st", status);
        }

        var total = count.Scalar();
        var head = cmd.Rows(r => (Seq: r.GetInt32(0), Item: Guid.Parse(r.GetString(1)), Name: r.GetString(2), Status: r.GetString(3), Code: Db.Str(r, 4), Message: Db.Str(r, 5), Started: Db.Date(r, 6), Finished: Db.Date(r, 7), Freed: r.GetInt64(8), Reconcile: Db.Str(r, 9)));
        var rows = head.Select(h =>
        {
            var files = c.Cmd("SELECT file_seq,path,role,status,code,bytes,at FROM job_files WHERE job_id=$j AND seq=$s ORDER BY file_seq").With("$j", Db.Id(job)).With("$s", h.Seq)
                .Rows(r => new JobFileRow(r.GetInt32(0), r.GetString(1), (Domain.FileRole)r.GetInt32(2), r.GetString(3), Db.Str(r, 4), r.GetInt64(5), Db.Date(r, 6)));
            return new JobItemRow(h.Seq, h.Item, h.Name, h.Status, h.Code, h.Message, h.Started, h.Finished, h.Freed, h.Reconcile, files);
        }).ToList();
        return ((IReadOnlyList<JobItemRow>)rows, total);
    });

    public IReadOnlyList<(int Seq, int FileSeq, string Path)> FilesInStatus(Guid job, string status) => db.Read(c =>
        c.Cmd("SELECT seq,file_seq,path FROM job_files WHERE job_id=$j AND status=$s").With("$j", Db.Id(job)).With("$s", status).Rows(r => (r.GetInt32(0), r.GetInt32(1), r.GetString(2))));

    public IReadOnlyList<Guid> ActiveJobIds() => db.Read(c =>
        c.Cmd("SELECT job_id FROM jobs WHERE status IN ('Queued','Preflight','Running')").Rows(r => Guid.Parse(r.GetString(0))));

    public IReadOnlyList<int> ItemsInStatus(Guid job, string status) => db.Read(c =>
        c.Cmd("SELECT seq FROM job_items WHERE job_id=$j AND status=$s").With("$j", Db.Id(job)).With("$s", status).Rows(r => r.GetInt32(0)));

    public int PruneOlderThan(DateTime cutoffUtc) => db.Write((c, tx) =>
    {
        var removed = c.Cmd("DELETE FROM jobs WHERE created_at<$t AND status NOT IN ('Queued','Preflight','Running')", tx).With("$t", Db.Ms(cutoffUtc)).Exec();
        c.Cmd("DELETE FROM plans WHERE created_at<$t AND plan_id NOT IN (SELECT plan_id FROM jobs)", tx).With("$t", Db.Ms(cutoffUtc)).Exec();
        return removed;
    });

    /// <summary>Unexecuted plans carry full file lists and expire quickly; keep them only for a short time.</summary>
    public int PruneUnexecutedPlans(DateTime cutoffUtc) => db.Write((c, tx) =>
        c.Cmd("DELETE FROM plans WHERE expires_at<$t AND plan_id NOT IN (SELECT plan_id FROM jobs)", tx).With("$t", Db.Ms(cutoffUtc)).Exec());

    private const string JobSelect = "SELECT job_id,plan_id,correlation_id,status,created_by,created_at,started_at,finished_at,cancel_requested,total_items,completed_items,deleted_bytes,reclaim_estimate,message,free_before,free_after,problems FROM jobs";

    private static JobRow MapJob(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        PlanId = Guid.Parse(r.GetString(1)),
        CorrelationId = r.GetString(2),
        Status = r.GetString(3),
        CreatedBy = Db.Str(r, 4),
        CreatedAtUtc = Db.Date(r, 5)!.Value,
        StartedAtUtc = Db.Date(r, 6),
        FinishedAtUtc = Db.Date(r, 7),
        CancelRequested = r.GetBoolean(8),
        TotalItems = r.GetInt32(9),
        CompletedItems = r.GetInt32(10),
        DeletedBytes = r.GetInt64(11),
        ReclaimEstimateBytes = r.GetInt64(12),
        Message = Db.Str(r, 13),
        FreeBefore = r.IsDBNull(14) ? null : Json.Read<List<VolumeFree>>(r.GetString(14)),
        FreeAfter = r.IsDBNull(15) ? null : Json.Read<List<VolumeFree>>(r.GetString(15)),
        Problems = r.IsDBNull(16) ? null : Json.Read<List<JobProblem>>(r.GetString(16)),
    };
}

public sealed record PlanJobEntry(Guid ItemId, string Name);
