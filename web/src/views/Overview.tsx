import { useState } from 'preact/hooks';
import { api } from '../api';
import type { Shared } from '../app';
import { Alert, Badge, Button, Card, Empty, ErrorState, Loading, MultiPick, Size, Stat, useAsync, usePoll } from '../components';
import { Legend, Meter, StackedBar, TrendChart, seriesVar, toSeries } from '../charts';
import { ago, bytes, dateTime } from '../format';
import type { ExplorerRow, LargestFile, Overview, Status } from '../types';

const WATCH_COLORS = { all: seriesVar(0), some: seriesVar(1), none: seriesVar(2), unknown: 'var(--jt-text-2)' };

export function OverviewView({ shared, status }: { shared: Shared; status: Status }) {
    const overview = useAsync(() => api.overview(shared.selectedUsers, shared.policyId), [shared.selectedUsers.join(), shared.policyId]);
    const [days, setDays] = useState(90);
    const trends = useAsync(() => api.trends(days), [days]);
    const [starting, setStarting] = useState<Error | null>(null);
    const scanning = status.scan.running;
    usePoll(() => { void overview.reload(); void trends.reload(); }, scanning ? 4000 : null);

    const scan = async () => {
        setStarting(null);
        try {
            await api.startScan();
            shared.refreshStatus();
        } catch (e) {
            setStarting(e as Error);
        }
    };

    if (overview.error && !overview.data) return <ErrorState error={overview.error} onRetry={overview.reload} />;
    if (!overview.data) return <Loading label="Measuring" />;
    const data = overview.data;
    const none = !data.scan.lastSuccessful;
    const scanButton = <Button variant={none ? 'primary' : undefined} onClick={scan} why={scanning ? 'A scan is running.' : status.activeJobId ? 'A cleanup job is running.' : null}>{scanning ? 'Scanning…' : 'Scan now'}</Button>;
    if (none && data.libraries.length === 0) {
        return (
            <Card title="Get started">
                {starting && <ErrorState error={starting} />}
                <p>jellytrim has not scanned your library yet. A scan only reads: it lists files and viewing state and changes nothing.</p>
                <div class="jt-actions">{scanButton}</div>
                {data.scan.latest?.status === 'Failed' && <Alert kind="error" title="The last scan failed.">{data.scan.latest.message}</Alert>}
            </Card>
        );
    }

    const totalLogical = data.libraries.reduce((s, l) => s + l.library.logicalBytes, 0);
    const volumes = data.volumes.filter(v => v.volume.available && v.volume.freeBytes !== null);
    const free = volumes.reduce((s, v) => s + (v.volume.freeBytes ?? 0), 0);
    const policy = shared.policies.find(p => p.policy.id === shared.policyId);

    return (
        <div>
            {starting && <ErrorState error={starting} />}
            {data.warnings.map((w, i) => <Alert key={i} kind={w.severity === 'error' ? 'error' : 'warning'}>{w.message}</Alert>)}
            <div class="jt-stats">
                <Stat label="Library size (logical)" value={<Size value={totalLogical} />} sub={`${data.libraries.reduce((s, l) => s + l.library.itemCount, 0).toLocaleString()} movies and episodes`} />
                <Stat label="Free space (observed)" value={volumes.length ? <Size value={free} /> : '—'} sub={volumes.length ? `across ${volumes.length} volume(s), ${ago(volumes[0].volume.observedUtc)}` : 'Not measured yet'} />
                <Stat label="Cleanup potential (estimated)" value={data.potential ? <Size value={data.potential.eligibleReclaimableBytes} /> : '—'} sub={data.potential ? `${data.potential.eligibleCount.toLocaleString()} items under “${policy?.policy.name ?? 'policy'}”${data.potentialStale ? ' (stale)' : ''}` : 'Create a policy to estimate this'} />
                <Stat label="Data freshness" value={ago(data.scan.lastSuccessful?.finishedUtc)} sub={<span title={dateTime(data.scan.lastSuccessful?.finishedUtc)}>{data.scan.lastSuccessful?.itemsSeen.toLocaleString() ?? 0} items, {data.scan.lastSuccessful?.filesSeen.toLocaleString() ?? 0} files</span>} />
            </div>
            <Card title="Libraries" actions={<>{shared.policies.length > 0 && <PolicyPicker shared={shared} />}{scanButton}</>}>
                <div class="jt-table-wrap">
                    <table class="jt-table">
                        <thead><tr><th scope="col">Library</th><th scope="col" class="jt-right">Logical</th><th scope="col" class="jt-right">Unique</th><th scope="col" class="jt-right">Est. reclaimable</th><th scope="col" class="jt-right">Items</th><th scope="col">Budget</th><th scope="col">Scanned</th></tr></thead>
                        <tbody>
                            {data.libraries.map(({ library: l, budget }) => (
                                <tr key={l.id}>
                                    <td><strong>{l.name}</strong> {!l.online && <Badge tone="critical">Offline</Badge>}<div class="jt-path jt-muted">{l.locations.join(', ')}</div></td>
                                    <td class="jt-right"><Size value={l.logicalBytes} /></td>
                                    <td class="jt-right"><Size value={l.uniqueBytes} /></td>
                                    <td class="jt-right"><Size value={l.reclaimableBytes} /></td>
                                    <td class="jt-right jt-num">{l.itemCount.toLocaleString()}</td>
                                    <td>{budget
                                        ? <div><Meter used={budget.usedBytes} total={budget.budgetBytes} tone={budget.exceeded ? 'critical' : budget.usedFraction > 0.9 ? 'warning' : 'good'} label={`Budget used for ${l.name}`} /><span class="jt-small">{budget.exceeded && '⚠ over: '}{bytes(budget.usedBytes)} of {bytes(budget.budgetBytes)}</span></div>
                                        : <span class="jt-muted jt-small">No budget</span>}</td>
                                    <td class="jt-small" title={dateTime(l.lastScanUtc)}>{ago(l.lastScanUtc)}</td>
                                </tr>
                            ))}
                        </tbody>
                    </table>
                </div>
                <details class="jt-small"><summary>What do these measurements mean?</summary>
                    <ul>
                        <li><strong>Logical</strong>: file sizes summed over every reference, as Jellyfin's records see them.</li>
                        <li><strong>Unique</strong>: each physical file counted once, even if several records or hard links point to it.</li>
                        <li><strong>Estimated reclaimable</strong>: blocks allocated to files with no hard link outside the item. It is an estimate: snapshots, compression, deduplication and concurrent writes can change what you actually get back.</li>
                        <li><strong>Free space</strong>: observed from the filesystem. It belongs to a volume, not to a library, and libraries sharing a volume share it.</li>
                    </ul>
                </details>
            </Card>
            <div class="jt-grid-2">
                <Card title="Storage volumes" note="Free space is a volume measurement. Libraries on the same volume share it.">
                    {data.volumes.length === 0 ? <Empty title="No volumes observed yet" /> : (
                        <ul class="jt-list">
                            {data.volumes.map(v => (
                                <li key={v.volume.volumeId}>
                                    <div class="jt-path">{v.volume.samplePath}</div>
                                    {v.volume.available && v.volume.totalBytes
                                        ? <><Meter used={v.volume.totalBytes - (v.volume.freeBytes ?? 0)} total={v.volume.totalBytes} tone={v.belowGoal ? 'critical' : 'good'} label={`Used space on ${v.volume.samplePath}`} />
                                            <span class="jt-small"><Size value={v.volume.freeBytes} /> free of <Size value={v.volume.totalBytes} />{v.minimumFreeBytes && <> · goal <Size value={v.minimumFreeBytes} /></>}{v.belowGoal && <strong> · ⚠ below goal</strong>} · {data.libraries.filter(l => v.volume.libraryIds.includes(l.library.id)).map(l => l.library.name).join(', ')}</span></>
                                        : <Badge tone="critical">Unavailable</Badge>}
                                </li>
                            ))}
                        </ul>
                    )}
                </Card>
                <WatchCard shared={shared} data={data} />
            </div>
            <Card title="Storage over time" note="Only what jellytrim has recorded since it was installed. There is no history from before then." actions={
                <label class="jt-small">Range <select class="jt-input" style={{ width: 'auto' }} value={days} onChange={e => setDays(Number((e.target as HTMLSelectElement).value))}><option value={30}>30 days</option><option value={90}>90 days</option><option value={365}>1 year</option><option value={3650}>All</option></select></label>}>
                {trends.error && <ErrorState error={trends.error} onRetry={trends.reload} />}
                {trends.data && trends.data.libraries.length === 0 && <Empty title="No snapshots yet">A snapshot is recorded after each scan.</Empty>}
                {trends.data && trends.data.libraries.length > 0 && (
                    <div class="jt-grid-2">
                        <div><h3>Unique bytes by library</h3><TrendChart series={toSeries(trends.data.libraries, p => p.uniqueBytes)} label="Unique bytes per library over time" unitLabel="unique" /></div>
                        <div><h3>Observed free space by volume</h3><TrendChart series={toSeries(trends.data.volumes, p => p.freeBytes ?? 0)} label="Free space per volume over time" unitLabel="free" /></div>
                    </div>
                )}
            </Card>
            <Largest data={data} shared={shared} />
        </div>
    );
}

function PolicyPicker({ shared }: { shared: Shared }) {
    return (
        <label class="jt-small">Policy{' '}
            <select class="jt-input" style={{ width: 'auto' }} value={shared.policyId ?? ''} onChange={e => shared.setPolicyId((e.target as HTMLSelectElement).value || null)}>
                {shared.policies.map(p => <option key={p.policy.id} value={p.policy.id}>{p.policy.name}</option>)}
            </select>
        </label>
    );
}

function WatchCard({ shared, data }: { shared: Shared; data: Overview }) {
    const options = shared.users.map(u => ({ value: u.id, label: u.name, note: u.isDisabled ? 'disabled' : undefined }));
    const n = shared.selectedUsers.length;
    return (
        <Card title="Watched versus unwatched" note={`Scope: ${n} selected user(s). “Played” is Jellyfin's flag, which users can also set by hand, so it does not prove playback finished.`}
            actions={<MultiPick label="Users" options={options} selected={shared.selectedUsers} onChange={shared.setSelectedUsers} />}>
            {n === 0 ? <Empty title="Select users to compare" /> : data.watchSummaries.length === 0 ? <Empty title="No file-backed items" /> : (
                <>
                    {data.libraries.map(({ library: l }) => {
                        const w = data.watchSummaries.find(s => s.libraryId === l.id);
                        if (!w) return null;
                        return (
                            <div key={l.id}>
                                <div class="jt-small"><strong>{l.name}</strong></div>
                                <StackedBar label={`Watch state in ${l.name}`} segments={[
                                    { label: `Played by all ${n}`, value: w.playedByAllBytes, count: w.playedByAllItems, color: WATCH_COLORS.all },
                                    { label: 'Played by some', value: w.playedBySomeBytes, count: w.playedBySomeItems, color: WATCH_COLORS.some },
                                    { label: 'Played by none', value: w.unplayedBytes, count: w.unplayedItems, color: WATCH_COLORS.none },
                                    { label: 'Unknown', value: w.unknownBytes, count: w.unknownItems, color: WATCH_COLORS.unknown },
                                ]} />
                                <div class="jt-small jt-muted">All: {bytes(w.playedByAllBytes)} · Some: {bytes(w.playedBySomeBytes)} · None: {bytes(w.unplayedBytes)} · Unknown: {bytes(w.unknownBytes)}</div>
                            </div>
                        );
                    })}
                    <Legend items={[{ label: `Played by all ${n}`, color: WATCH_COLORS.all }, { label: 'Played by some', color: WATCH_COLORS.some }, { label: 'Played by none', color: WATCH_COLORS.none }, { label: 'Unknown', color: WATCH_COLORS.unknown }]} />
                </>
            )}
        </Card>
    );
}

function Largest({ data, shared }: { data: Overview; shared: Shared }) {
    const [kind, setKind] = useState<'movies' | 'shows' | 'seasons' | 'episodes' | 'files'>('movies');
    const rows: ExplorerRow[] = kind === 'files' ? [] : data.largest[kind];
    const files: LargestFile[] = data.largest.files;
    return (
        <Card title="Largest" actions={
            <>
                {(['movies', 'shows', 'seasons', 'episodes', 'files'] as const).map(k => <Button key={k} small variant={k === kind ? 'primary' : undefined} onClick={() => setKind(k)}>{k[0].toUpperCase() + k.slice(1)}</Button>)}
                <Button small variant="plain" onClick={() => shared.go('explorer')}>Open explorer</Button>
            </>}>
            <div class="jt-table-wrap">
                {kind === 'files'
                    ? <table class="jt-table"><thead><tr><th scope="col">File</th><th scope="col">Item</th><th scope="col" class="jt-right">Size</th></tr></thead>
                        <tbody>{files.map(f => <tr key={f.path}><td class="jt-path">{f.path}{f.linkCount > 1 && <> <Badge tone="info">{f.linkCount} hard links</Badge></>}</td><td>{f.itemName}</td><td class="jt-right"><Size value={f.size} /></td></tr>)}</tbody></table>
                    : <table class="jt-table"><thead><tr><th scope="col">Name</th><th scope="col">Library</th><th scope="col" class="jt-right">Logical</th><th scope="col" class="jt-right">Est. reclaimable</th></tr></thead>
                        <tbody>{rows.map(r => <tr key={r.id}><td>{r.seriesName ? `${r.seriesName} · ` : ''}{r.name}</td><td>{r.libraryName}</td><td class="jt-right"><Size value={r.logicalBytes} /></td><td class="jt-right"><Size value={r.reclaimableBytes} /></td></tr>)}</tbody></table>}
                {kind !== 'files' && rows.length === 0 && <Empty title={`No ${kind}`} />}
            </div>
        </Card>
    );
}
