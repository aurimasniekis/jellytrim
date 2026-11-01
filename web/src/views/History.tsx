import { useState } from 'preact/hooks';
import { api } from '../api';
import { Alert, Badge, Button, Card, Empty, ErrorState, Loading, Pager, Size, Stat, useAsync, usePoll } from '../components';
import { Meter } from '../charts';
import { bytes, dateTime } from '../format';

const TONE: Record<string, 'good' | 'warning' | 'serious' | 'critical' | 'info' | 'neutral'> = {
    Completed: 'good', CompletedWithIssues: 'warning', Cancelled: 'neutral', Rejected: 'warning', Failed: 'critical', Interrupted: 'serious', Running: 'info', Preflight: 'info', Queued: 'info',
    Deleted: 'good', Skipped: 'warning', Uncertain: 'serious', NotRun: 'neutral', Pending: 'neutral', InProgress: 'info', AlreadyGone: 'neutral', Refused: 'warning', NotAttempted: 'neutral', Deleting: 'info',
};
const ACTIVE = new Set(['Queued', 'Preflight', 'Running']);

export function JobReport({ jobId, onNew, live }: { jobId: string; onNew?: () => void; live?: boolean }) {
    const [offset, setOffset] = useState(0);
    const [filter, setFilter] = useState<string | null>(null);
    const [error, setError] = useState<Error | null>(null);
    const detail = useAsync(() => api.job(jobId, offset, filter), [jobId, offset, filter]);
    const active = detail.data ? ACTIVE.has(detail.data.job.status) : Boolean(live);
    usePoll(detail.reload, active ? 1500 : null);
    if (detail.error && !detail.data) return <ErrorState error={detail.error} onRetry={detail.reload} />;
    if (!detail.data) return <Loading />;
    const job = detail.data.job;
    const cancel = async () => {
        setError(null);
        try {
            await api.cancelJob(job.id);
            await detail.reload();
        } catch (e) { setError(e as Error); }
    };
    return (
        <div>
            {error && <ErrorState error={error} />}
            <Card title="Cleanup job" note={<>Reference <code>{job.correlationId}</code> · started {dateTime(job.startedAtUtc ?? job.createdAtUtc)}{job.createdBy ? ` by ${job.createdBy}` : ''}</>}
                actions={<>
                    <Badge tone={TONE[job.status]}>{job.status === 'CompletedWithIssues' ? 'Completed with issues' : job.status}</Badge>
                    {active && <Button onClick={cancel} why={job.cancelRequested ? 'Stopping after the current item.' : null}>Stop after current item</Button>}
                    {!active && onNew && <Button onClick={onNew}>Start another cleanup</Button>}
                </>}>
                {active && <Alert kind="info">Stopping prevents further deletions. Deletions already done stay deleted.</Alert>}
                {job.message && <Alert kind={job.status === 'Completed' ? 'good' : job.status === 'Failed' ? 'error' : 'warning'}>{job.message}</Alert>}
                <Meter used={job.completedItems} total={job.totalItems} tone="good" label="Items processed" />
                <div class="jt-stats" style={{ marginTop: '10px' }}>
                    <Stat label="Processed" value={`${job.completedItems} of ${job.totalItems}`} />
                    <Stat label="Estimated freed" value={<Size value={job.deletedBytes} />} sub={<>Plan estimated <Size value={job.reclaimEstimateBytes} /></>} />
                </div>
                {job.freeBefore && (
                    <div>
                        <h3>Before and after: observed free space</h3>
                        <p class="jt-small jt-muted">Observed on the whole volume. Other activity during the job also affects this difference, so it is not an exact measure of what this job freed.</p>
                        <div class="jt-table-wrap"><table class="jt-table"><thead><tr><th scope="col">Volume</th><th scope="col" class="jt-right">Before</th><th scope="col" class="jt-right">After</th><th scope="col" class="jt-right">Change</th></tr></thead>
                            <tbody>{job.freeBefore.map(b => {
                                const after = job.freeAfter?.find(a => a.volumeId === b.volumeId)?.freeBytes ?? null;
                                return <tr key={b.volumeId}><td class="jt-path">{b.samplePath}</td><td class="jt-right"><Size value={b.freeBytes} /></td><td class="jt-right"><Size value={after} /></td><td class="jt-right">{after !== null && b.freeBytes !== null ? bytes(after - b.freeBytes) : '—'}</td></tr>;
                            })}</tbody></table></div>
                    </div>
                )}
                {job.problems && job.problems.length > 0 && (
                    <div><h3>Why the plan was rejected</h3><ul class="jt-list">{job.problems.slice(0, 100).map(p => <li key={p.itemId + p.code}><strong>{p.name}</strong><div class="jt-small"><code>{p.code}</code> {p.message}</div></li>)}</ul></div>
                )}
            </Card>
            <Card title="Per-item outcome" actions={
                <label class="jt-small">Show <select class="jt-input" style={{ width: 'auto' }} value={filter ?? ''} onChange={e => { setFilter((e.target as HTMLSelectElement).value || null); setOffset(0); }}><option value="">All</option>{['Deleted', 'Failed', 'Skipped', 'Uncertain', 'Cancelled', 'NotRun', 'Pending', 'InProgress'].map(s => <option key={s} value={s}>{s}</option>)}</select></label>}>
                {detail.data.items.items.length === 0 && <Empty title="No items to show" />}
                <ul class="jt-list">
                    {detail.data.items.items.map(item => (
                        <li key={item.seq}>
                            <Badge tone={TONE[item.status]}>{item.status}</Badge> <strong>{item.name}</strong> {item.freedBytes > 0 && <span class="jt-small jt-muted">est. {bytes(item.freedBytes)} freed</span>}
                            {item.message && <div class="jt-small">{item.code && <code>{item.code}</code>} {item.message}</div>}
                            {item.reconcile === 'PendingNextLibraryScan' && <div class="jt-small">Files are gone, but Jellyfin still lists this item. A library scan will remove it.</div>}
                            <details class="jt-small"><summary>{item.files.length} file(s)</summary>
                                <ul class="jt-list">{item.files.map(f => <li key={f.fileSeq}><Badge tone={TONE[f.status]}>{f.status}</Badge> {f.code && <code>{f.code}</code>} <span class="jt-path">{f.path}</span></li>)}</ul>
                            </details>
                        </li>
                    ))}
                </ul>
                <Pager label="Item pages" offset={detail.data.items.offset} limit={detail.data.items.limit} total={detail.data.items.total} onChange={setOffset} />
            </Card>
        </div>
    );
}

export function HistoryView({ jobId, setJobId }: { jobId: string | null; setJobId: (id: string | null) => void }) {
    const [offset, setOffset] = useState(0);
    const [auditOffset, setAuditOffset] = useState(0);
    const jobs = useAsync(() => api.jobs(offset), [offset, jobId]);
    const audit = useAsync(() => api.audit(auditOffset), [auditOffset, jobId]);
    if (jobId) return <div><div style={{ marginBottom: '10px' }}><Button small onClick={() => setJobId(null)}>‹ All cleanup jobs</Button></div><JobReport jobId={jobId} /></div>;
    return (
        <div>
            <Card title="Cleanup jobs" note="Deleted content cannot be restored from here. This is a record, not an undo.">
                {jobs.error && <ErrorState error={jobs.error} onRetry={jobs.reload} />}
                {!jobs.data && !jobs.error && <Loading />}
                {jobs.data?.items.length === 0 && <Empty title="No cleanup has run yet" />}
                {jobs.data && jobs.data.items.length > 0 && (
                    <div class="jt-table-wrap"><table class="jt-table"><thead><tr><th scope="col">Started</th><th scope="col">Status</th><th scope="col">By</th><th scope="col" class="jt-right">Items</th><th scope="col" class="jt-right">Est. freed</th><th scope="col"><span class="jt-hide-narrow">Report</span></th></tr></thead>
                        <tbody>{jobs.data.items.map(j => <tr key={j.id}><td class="jt-small">{dateTime(j.createdAtUtc)}</td><td><Badge tone={TONE[j.status]}>{j.status}</Badge></td><td>{j.createdBy ?? '—'}</td><td class="jt-right jt-num">{j.completedItems}/{j.totalItems}</td><td class="jt-right"><Size value={j.deletedBytes} /></td><td><Button small onClick={() => setJobId(j.id)} label={`Open report from ${dateTime(j.createdAtUtc)}`}>Open</Button></td></tr>)}</tbody></table></div>
                )}
                {jobs.data && <Pager label="Job pages" offset={offset} limit={25} total={jobs.data.total} onChange={setOffset} />}
            </Card>
            <Card title="Audit log" note="Who did what. Kept for the audit retention set in Settings, separately from server logs.">
                {audit.error && <ErrorState error={audit.error} onRetry={audit.reload} />}
                {audit.data && (
                    <div class="jt-table-wrap"><table class="jt-table"><thead><tr><th scope="col">Time</th><th scope="col">Actor</th><th scope="col">Action</th><th scope="col">Outcome</th><th scope="col">Reference</th></tr></thead>
                        <tbody>{audit.data.items.map(a => <tr key={a.id}><td class="jt-small">{dateTime(a.atUtc)}</td><td>{a.actor ?? 'system'}</td><td><code>{a.action}</code></td><td>{a.outcome}</td><td class="jt-small"><code>{a.correlationId ?? ''}</code></td></tr>)}</tbody></table></div>
                )}
                {audit.data && <Pager label="Audit pages" offset={auditOffset} limit={50} total={audit.data.total} onChange={setAuditOffset} />}
            </Card>
        </div>
    );
}
