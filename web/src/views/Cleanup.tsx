import { useEffect, useRef, useState } from 'preact/hooks';
import { ApiError, api } from '../api';
import type { Shared } from '../app';
import { Alert, Badge, Button, Card, Dialog, Empty, ErrorState, Field, Loading, NumberInput, Pager, Size, Stat, usePoll } from '../components';
import { bytes, date, dateTime, gibToBytes, ruleLabel } from '../format';
import type { JobRow, PlanDto, PlanRequest, RecommendationSet } from '../types';
import { JobReport } from './History';

const ROLE_LABEL = { Main: 'Video', Part: 'Video part', AlternateVersion: 'Alternate version', Sidecar: 'Sidecar' } as const;

export function CleanupView({ shared, pending, clearPending }: { shared: Shared; pending: { request: PlanRequest; description: string; nonce: number } | null; clearPending: () => void }) {
    const [plan, setPlan] = useState<PlanDto | null>(null);
    const [job, setJob] = useState<JobRow | null>(null);
    const [error, setError] = useState<Error | null>(null);
    const [busy, setBusy] = useState(false);
    const handled = useRef<number | null>(null);

    const generate = async (request: PlanRequest) => {
        setBusy(true);
        setError(null);
        setJob(null);
        try {
            setPlan(await api.createPlan(request));
        } catch (e) {
            setError(e as Error);
        } finally {
            setBusy(false);
        }
    };

    useEffect(() => {
        if (pending && handled.current !== pending.nonce) {
            handled.current = pending.nonce;
            void generate(pending.request);
            clearPending();
        }
    }, [pending]);

    const reset = () => { setPlan(null); setJob(null); setError(null); };
    const step = job ? 3 : plan ? 2 : 1;
    return (
        <div>
            <ol class="jt-steps" aria-label="Cleanup steps">
                {['1. Choose', '2. Review the plan', '3. Confirm and run'].map((s, i) => <li key={s} aria-current={step === i + 1 ? 'step' : undefined}>{s}</li>)}
            </ol>
            {busy && <Loading label="Generating dry-run plan. Nothing is being deleted" />}
            {error && <ErrorState error={error} />}
            {error instanceof ApiError && error.code === 'SELECTION_CHANGED' && <Alert kind="warning">The explorer results changed after you selected them, so nothing was planned. Go back and select again.</Alert>}
            {!plan && !job && !busy && <Choose shared={shared} onPlan={generate} />}
            {plan && !job && <Review plan={plan} setPlan={setPlan} shared={shared} onStarted={setJob} onDiscard={reset} />}
            {job && <JobReport jobId={job.id} onNew={reset} live />}
        </div>
    );
}

function Choose({ shared, onPlan }: { shared: Shared; onPlan: (r: PlanRequest) => void }) {
    const [gib, setGib] = useState<number | null>(100);
    const [recommendations, setRecommendations] = useState<RecommendationSet | null>(null);
    const [error, setError] = useState<Error | null>(null);
    const [sidecars, setSidecars] = useState(true);
    const policyId = shared.policyId;
    const find = async () => {
        setError(null);
        try {
            setRecommendations(await api.recommend(policyId!, gibToBytes(gib ?? 0)));
        } catch (e) { setError(e as Error); }
    };
    const noPolicy = shared.policies.length === 0;
    return (
        <div>
            {!shared.deletionEnabled && <Alert kind="info" title="Read-only mode.">You can preview plans, but executing one requires enabling deletion in Settings. Previews stay previews even if deletion is enabled later.</Alert>}
            <Card title="Reclaim space" note="Find eligible content by policy. Results are ranked by how long ago it was last played, then by reclaimable size, then by age.">
                {noPolicy && <Empty title="No policies yet">Create a policy under Policies & protection, or select items in the Media explorer.</Empty>}
                {!noPolicy && (
                    <div>
                        <div class="jt-form">
                            <Field label="Policy"><select class="jt-input" value={policyId ?? ''} onChange={e => { shared.setPolicyId((e.target as HTMLSelectElement).value || null); setRecommendations(null); }}>{shared.policies.map(p => <option key={p.policy.id} value={p.policy.id}>{p.policy.name}</option>)}</select></Field>
                            <Field label="Target to reclaim (GiB)" hint="Approximate. Uses estimated reclaimable space."><NumberInput value={gib} step="1" min={0} onChange={setGib} /></Field>
                            <label class="jt-check"><input type="checkbox" checked={sidecars} onChange={e => setSidecars((e.target as HTMLInputElement).checked)} /><span>Include sidecars attributable to each item (subtitles, NFO, artwork). The plan lists every one.</span></label>
                        </div>
                        <div class="jt-actions">
                            <Button onClick={find} why={!policyId ? 'Select a policy.' : !gib ? 'Enter a target.' : null}>Find recommendations</Button>
                            <Button onClick={() => onPlan({ type: 'Policy', policyId, targetBytes: gib ? gibToBytes(gib) : null, includeSidecars: sidecars })} why={!policyId ? 'Select a policy.' : null}>Generate plan for this target</Button>
                            <Button onClick={() => onPlan({ type: 'Policy', policyId, targetBytes: null, includeSidecars: sidecars })} why={!policyId ? 'Select a policy.' : null}>Generate plan for all eligible</Button>
                        </div>
                    </div>
                )}
                {error && <ErrorState error={error} />}
                {recommendations && <Recommendations set={recommendations} onUse={ids => onPlan({ type: 'Items', itemIds: ids, policyId, includeSidecars: sidecars })} />}
            </Card>
            <Card title="Or pick by hand"><p>Select movies, seasons, shows or episodes in the <Button small variant="plain" onClick={() => shared.go('explorer')}>Media explorer</Button>. Explicit selections still respect protections, favorites, in-progress and playback, for all users.</p></Card>
        </div>
    );
}

function Recommendations({ set, onUse }: { set: RecommendationSet; onUse: (ids: string[]) => void }) {
    if (set.items.length === 0) return <Alert kind="info">Nothing eligible with reclaimable space was found. {set.skippedNoReclaim > 0 && `${set.skippedNoReclaim} eligible item(s) would free no space because of hard links elsewhere.`}</Alert>;
    return (
        <div>
            <Alert kind={set.targetMet ? 'good' : 'warning'}>{set.targetMet ? `Found approximately ${bytes(set.selectedBytes)} in ${set.items.length} item(s), estimated.` : `Only approximately ${bytes(set.selectedBytes)} of the requested ${bytes(set.requestedBytes)} is eligible.`}</Alert>
            <div class="jt-table-wrap">
                <table class="jt-table">
                    <thead><tr><th scope="col" class="jt-right">#</th><th scope="col">Item</th><th scope="col" class="jt-right">Est. reclaim</th><th scope="col" class="jt-right">Running total</th><th scope="col">Last played</th><th scope="col">Why</th></tr></thead>
                    <tbody>{set.items.slice(0, 100).map(r => <tr key={r.itemId}><td class="jt-right jt-num">{r.rank}</td><td>{r.name}</td><td class="jt-right"><Size value={r.reclaimableBytes} /></td><td class="jt-right"><Size value={r.cumulativeBytes} /></td><td class="jt-small">{date(r.lastPlayedUtc)}</td><td class="jt-small">{r.reason}</td></tr>)}</tbody>
                </table>
            </div>
            {set.items.length > 100 && <p class="jt-small jt-muted">Showing the first 100 of {set.items.length}.</p>}
            <div class="jt-actions"><Button variant="primary" onClick={() => onUse(set.items.map(i => i.itemId))}>Generate plan from these {set.items.length} item(s)</Button></div>
        </div>
    );
}

function Review({ plan, setPlan, shared, onStarted, onDiscard }: { plan: PlanDto; setPlan: (p: PlanDto) => void; shared: Shared; onStarted: (j: JobRow) => void; onDiscard: () => void }) {
    const header = plan.plan;
    const totals = header.totals;
    const [confirming, setConfirming] = useState(false);
    const [now, setNow] = useState(Date.now());
    const [error, setError] = useState<Error | null>(null);
    usePoll(() => setNow(Date.now()), 15000);
    const expired = now >= new Date(plan.summary.expiresAtUtc).getTime();
    const minutes = Math.max(0, Math.ceil((new Date(plan.summary.expiresAtUtc).getTime() - now) / 60000));
    const page = async (itemOffset: number, exclusionOffset: number) => {
        try {
            setPlan(await api.plan(plan.summary.id, itemOffset, exclusionOffset));
        } catch (e) { setError(e as Error); }
    };
    const why = header.previewOnly ? 'This is a preview. Enable deletion in Settings, then generate a new plan.'
        : !shared.deletionEnabled ? 'Deletion is disabled.'
        : expired ? 'This plan expired. Generate a new one.'
        : totals.items === 0 ? 'This plan has nothing to delete.' : null;
    return (
        <div>
            {error && <ErrorState error={error} />}
            {expired && <Alert kind="warning" title="This plan expired.">Plans are short-lived so that what you confirm is what is on disk. Generate a new one.</Alert>}
            {header.warnings.map(w => <Alert key={w} kind="warning">{w}</Alert>)}
            <div class="jt-stats">
                <Stat label="Items to delete" value={totals.items.toLocaleString()} sub={`${totals.files.toLocaleString()} files`} />
                <Stat label="Estimated reclaim" value={<Size value={totals.estimatedReclaimableBytes} />} sub="Estimate. See below." />
                <Stat label="Logical / unique" value={<Size value={totals.logicalBytes} />} sub={<><Size value={totals.uniqueBytes} /> unique</>} />
                <Stat label="Excluded" value={plan.exclusions.total.toLocaleString()} sub="Items kept, with reasons" />
                <Stat label="Plan expires" value={expired ? 'Expired' : `${minutes} min`} sub={dateTime(plan.summary.expiresAtUtc)} />
            </div>
            <Card title={`Dry-run plan: ${header.policyName}`} note={<>Immutable plan <code>{plan.summary.hash.slice(0, 12)}</code>. Every file below is exactly what will be removed. Files not listed are not touched.</>}
                actions={<>
                    <Button onClick={onDiscard}>Discard plan</Button>
                    <Button variant="danger" onClick={() => setConfirming(true)} why={why}>Permanently delete…</Button>
                </>}>
                <p class="jt-small jt-muted">Reclaim is estimated from allocated blocks, and hard links outside the plan count as zero. Snapshots, compression, deduplication and other activity on the volume can change what you actually get back.</p>
                {header.volumes.length > 0 && (
                    <div class="jt-table-wrap"><table class="jt-table"><thead><tr><th scope="col">Volume</th><th scope="col" class="jt-right">Free now</th><th scope="col" class="jt-right">Est. freed</th><th scope="col" class="jt-right">Goal</th></tr></thead>
                        <tbody>{header.volumes.map(v => <tr key={v.volumeId}><td class="jt-path">{v.samplePath}</td><td class="jt-right"><Size value={v.freeBytesNow} /></td><td class="jt-right"><Size value={v.estimatedFreed} /></td><td class="jt-right"><Size value={v.minimumFreeGoal} /></td></tr>)}</tbody></table></div>
                )}
                <h3>Targets</h3>
                {plan.items.items.length === 0 && <Empty title="Nothing qualifies">See exclusions below for the reasons.</Empty>}
                <ul class="jt-list">
                    {plan.items.items.map(item => (
                        <li key={item.itemId}>
                            <strong>{item.seriesName ? `${item.seriesName} · ` : ''}{item.name}</strong> <span class="jt-muted jt-small">{item.kind}</span>
                            <ul class="jt-list">
                                {item.files.map(f => (
                                    <li key={f.path} class="jt-small">
                                        <div class="jt-path">{f.path}</div>
                                        <span class="jt-muted">{ROLE_LABEL[f.role]} · {bytes(f.size)} · est. freed {bytes(f.estimatedFreedBytes)}</span>
                                        {f.linkCount > 1 && <> <Badge tone="info">{f.linkCount} hard links</Badge></>}
                                    </li>
                                ))}
                            </ul>
                            {item.directoriesIfEmpty.length > 0 && <div class="jt-small jt-muted">Folders removed only if empty afterwards: <span class="jt-path">{item.directoriesIfEmpty.join(', ')}</span></div>}
                            {item.unplannedNeighbors.length > 0 && <details class="jt-small"><summary>{item.unplannedNeighbors.length} neighboring entries stay on disk</summary><div class="jt-path">{item.unplannedNeighbors.join('\n')}</div></details>}
                            <details class="jt-small"><summary>Why this item qualifies</summary>
                                <ul class="jt-list">{item.reasons.filter(r => r.outcome === 'Pass').map(r => <li key={r.ruleId}><strong>{ruleLabel(r.ruleId)}:</strong> {r.message}</li>)}</ul>
                            </details>
                        </li>
                    ))}
                </ul>
                <Pager label="Plan items" offset={plan.items.offset} limit={plan.items.limit} total={plan.items.total} onChange={o => page(o, plan.exclusions.offset)} />
                {header.containers.length > 0 && (
                    <div>
                        <h3>Complete seasons and shows</h3>
                        <p class="jt-small jt-muted">Every file-backed episode of these is in the plan, so their folder artwork and empty records go too, but only if all their episodes succeed.</p>
                        <ul class="jt-list">{header.containers.map(c => <li key={c.itemId} class="jt-small"><strong>{c.name}</strong> ({c.kind}){c.files.map(f => <div class="jt-path" key={f.path}>{f.path}</div>)}{c.unplannedNeighbors.length > 0 && <div class="jt-muted">{c.unplannedNeighbors.length} other entries stay on disk</div>}</li>)}</ul>
                    </div>
                )}
            </Card>
            {plan.exclusions.total > 0 && (
                <Card title={`Excluded: ${plan.exclusions.total.toLocaleString()} item(s) stay`}>
                    <ul class="jt-list">
                        {plan.exclusions.items.map(e => (
                            <li key={e.itemId}><strong>{e.name}</strong><div class="jt-small">{ruleLabel(e.code)}: {e.message}</div>
                                {e.rules.filter(r => r.outcome === 'Block' || r.outcome === 'Unknown').length > 1 && <details class="jt-small"><summary>All reasons</summary><ul class="jt-list">{e.rules.filter(r => r.outcome === 'Block' || r.outcome === 'Unknown').map(r => <li key={r.ruleId}><strong>{ruleLabel(r.ruleId)}:</strong> {r.message}</li>)}</ul></details>}
                            </li>
                        ))}
                    </ul>
                    <Pager label="Plan exclusions" offset={plan.exclusions.offset} limit={plan.exclusions.limit} total={plan.exclusions.total} onChange={o => page(plan.items.offset, o)} />
                </Card>
            )}
            {confirming && <Confirm plan={plan} onClose={() => setConfirming(false)} onStarted={onStarted} />}
        </div>
    );
}

function Confirm({ plan, onClose, onStarted }: { plan: PlanDto; onClose: () => void; onStarted: (j: JobRow) => void }) {
    const [acknowledged, setAcknowledged] = useState(false);
    const [phrase, setPhrase] = useState('');
    const [error, setError] = useState<Error | null>(null);
    const [busy, setBusy] = useState(false);
    // One key per opened confirmation, so a double click or a retry after a lost response cannot start a second job.
    const key = useRef(crypto.randomUUID());
    const header = plan.plan;
    const required = header.confirmationPhrase;
    const phraseOk = !required || phrase === required;
    const go = async () => {
        setBusy(true);
        setError(null);
        try {
            onStarted(await api.execute(plan.summary.id, plan.summary.hash, key.current, acknowledged, required ? phrase : null));
        } catch (e) {
            setError(e as Error);
            setBusy(false);
        }
    };
    return (
        <Dialog title={`Permanently delete ${header.totals.items} item(s)?`} onClose={onClose}>
            <Alert kind="error" title="This cannot be undone.">jellytrim does not move files to a trash folder. Once a file is deleted, restoring it needs your own backups.</Alert>
            <p>{header.totals.files.toLocaleString()} files, approximately {bytes(header.totals.estimatedReclaimableBytes)} estimated to be freed.</p>
            <p class="jt-small jt-muted">Each item is re-checked right before deletion. If anything changed since this plan was made, nothing is deleted and you will be asked for a new plan.</p>
            {error && <ErrorState error={error} />}
            <label class="jt-check"><input data-autofocus type="checkbox" checked={acknowledged} onChange={e => setAcknowledged((e.target as HTMLInputElement).checked)} /><span>I reviewed this plan and understand that these files will be permanently deleted.</span></label>
            {required && (
                <Field label={`This is a high-impact plan. Type ${required} to confirm`}>
                    <input class="jt-input" value={phrase} autoComplete="off" onInput={e => setPhrase((e.target as HTMLInputElement).value)} />
                </Field>
            )}
            <div class="jt-actions">
                <Button onClick={onClose}>Keep files</Button>
                <Button variant="danger" onClick={go} disabled={busy} why={!acknowledged ? 'Tick the confirmation first.' : !phraseOk ? `Type ${required} exactly.` : null}>Delete permanently</Button>
            </div>
        </Dialog>
    );
}
