import { useEffect, useMemo, useRef, useState } from 'preact/hooks';
import { api } from '../api';
import type { Shared } from '../app';
import { Alert, Badge, Button, Card, Dialog, Empty, ErrorState, Field, Loading, MultiPick, NumberInput, Pager, Size, useAsync } from '../components';
import { OUTCOME_LABEL, SHOW_WATCH_LABEL, WATCH_LABEL, bytes, date, dateTime, daysAgoIso, episodeCode, episodeProgress, exactBytes, gibToBytes, resumePercent, ruleLabel } from '../format';
import type { ExplorerQuery, ExplorerRow, ExplorerSort, ItemHealth, MediaKind, UserMatch, WatchState } from '../types';

const PAGE = 50;
const COLUMNS = [
    { id: 'library', label: 'Library' },
    { id: 'reclaim', label: 'Est. reclaimable' },
    { id: 'added', label: 'Added' },
    { id: 'played', label: 'Last played' },
    { id: 'files', label: 'Files' },
] as const;
type ColumnId = (typeof COLUMNS)[number]['id'];

const WATCH_TONE: Record<WatchState, 'good' | 'info' | 'neutral' | 'warning'> = { Played: 'good', Partial: 'info', Unplayed: 'neutral', Unknown: 'warning' };
const HEALTH_LABEL: Partial<Record<ItemHealth, string>> = { Missing: 'File missing', Unavailable: 'Storage unavailable', NotFileBacked: 'No local file' };

interface Filters { search: string; libraries: string[]; kind: MediaKind | ''; minGib: number | null; maxGib: number | null; userMatch: UserMatch; watch: WatchState | ''; playedOlderDays: number | null; addedOlderDays: number | null; protection: '' | 'yes' | 'no'; eligibility: '' | 'yes' | 'no' }

const INITIAL: Filters = { search: '', libraries: [], kind: 'Movie', minGib: null, maxGib: null, userMatch: 'All', watch: '', playedOlderDays: null, addedOlderDays: null, protection: '', eligibility: '' };

/**
 * "Select this page" and "select all matching" are different, visibly different states. An all-matching selection is
 * bound to the query and count it was made from: changing any filter clears it, and the server refuses it if the count moved.
 */
type Selection = { mode: 'ids'; ids: Set<string> } | { mode: 'query'; query: ExplorerQuery; count: number };

export function ExplorerView({ shared }: { shared: Shared }) {
    const [filters, setFilters] = useState<Filters>(INITIAL);
    const [search, setSearch] = useState('');
    const [sort, setSort] = useState<ExplorerSort>('Size');
    const [descending, setDescending] = useState(true);
    const [offset, setOffset] = useState(0);
    const [path, setPath] = useState<{ id: string; name: string }[]>([]);
    const [selection, setSelection] = useState<Selection>({ mode: 'ids', ids: new Set() });
    const [notice, setNotice] = useState<string | null>(null);
    const [detail, setDetail] = useState<string | null>(null);
    const [columns, setColumns] = useState<ColumnId[]>(['library', 'reclaim', 'added', 'played']);
    const libraries = useAsync(() => api.overview([], null).then(o => o.libraries.map(l => l.library)), []);

    useEffect(() => {
        const timer = setTimeout(() => setFilters(f => (f.search === search ? f : { ...f, search })), 300);
        return () => clearTimeout(timer);
    }, [search]);

    const parentId = path.length ? path[path.length - 1].id : null;
    const query = useMemo<ExplorerQuery>(() => ({
        libraryIds: filters.libraries,
        kinds: parentId || !filters.kind ? [] : [filters.kind],
        parentId,
        search: filters.search || null,
        minBytes: filters.minGib === null ? null : gibToBytes(filters.minGib),
        maxBytes: filters.maxGib === null ? null : gibToBytes(filters.maxGib),
        userIds: shared.selectedUsers,
        userMatch: filters.userMatch,
        watchState: filters.watch || null,
        lastPlayedBeforeUtc: filters.playedOlderDays === null ? null : daysAgoIso(filters.playedOlderDays),
        lastPlayedAfterUtc: null,
        addedBeforeUtc: filters.addedOlderDays === null ? null : daysAgoIso(filters.addedOlderDays),
        addedAfterUtc: null,
        protected: filters.protection === '' ? null : filters.protection === 'yes',
        policyId: shared.policyId,
        eligible: filters.eligibility === '' ? null : filters.eligibility === 'yes',
        sort,
        descending,
        offset,
        limit: PAGE,
    }), [filters, parentId, shared.selectedUsers, shared.policyId, sort, descending, offset]);

    const page = useAsync(() => api.queryItems(query), [query]);

    // Filter identity excludes paging and sorting, which do not change which results match.
    const filterKey = JSON.stringify({ ...query, offset: 0, sort: null, descending: null, lastPlayedBeforeUtc: filters.playedOlderDays, addedBeforeUtc: filters.addedOlderDays });
    const previousKey = useRef(filterKey);
    useEffect(() => {
        if (previousKey.current !== filterKey) {
            previousKey.current = filterKey;
            setOffset(0);
            if (selection.mode === 'query') {
                setSelection({ mode: 'ids', ids: new Set() });
                setNotice('Filters changed, so the “all matching” selection was cleared. Nothing is selected.');
            }
        }
    }, [filterKey]);

    const update = (patch: Partial<Filters>) => setFilters(f => ({ ...f, ...patch }));
    const rows = page.data?.rows ?? [];
    const total = page.data?.total ?? 0;
    const ids = selection.mode === 'ids' ? selection.ids : null;
    const pageAllSelected = rows.length > 0 && ids !== null && rows.every(r => ids.has(r.id));

    const toggle = (id: string) => {
        setNotice(null);
        setSelection(s => {
            const next = new Set(s.mode === 'ids' ? s.ids : []);
            if (next.has(id)) next.delete(id);
            else next.add(id);
            return { mode: 'ids', ids: next };
        });
    };
    const togglePage = () => {
        setNotice(null);
        setSelection(s => {
            const next = new Set(s.mode === 'ids' ? s.ids : []);
            for (const r of rows) {
                if (pageAllSelected) next.delete(r.id);
                else next.add(r.id);
            }

            return { mode: 'ids', ids: next };
        });
    };
    const clear = () => { setNotice(null); setSelection({ mode: 'ids', ids: new Set() }); };
    const selectedCount = selection.mode === 'query' ? selection.count : selection.ids.size;

    const review = () => {
        const includeSidecars = true;
        const policyId = null;
        if (selection.mode === 'query') {
            shared.startCleanup({ type: 'Query', query: selection.query, expectedCount: selection.count, policyId, includeSidecars }, `All ${selection.count} matching results`);
        } else {
            shared.startCleanup({ type: 'Items', itemIds: Array.from(selection.ids), policyId, includeSidecars }, `${selection.ids.size} selected item(s)`);
        }
    };

    const open = (row: ExplorerRow) => setPath(p => [...p, { id: row.id, name: row.kind === 'Season' ? row.name : row.name }]);
    const sortButton = (key: ExplorerSort, label: string) => (
        <button type="button" class="jt-sort" onClick={() => { if (sort === key) setDescending(d => !d); else { setSort(key); setDescending(key !== 'Name'); } setOffset(0); }}>
            {label}{sort === key ? (descending ? ' ▼' : ' ▲') : ''}
        </button>
    );
    const ariaSort = (key: ExplorerSort) => (sort === key ? (descending ? 'descending' : 'ascending') : undefined);
    const show = (c: ColumnId) => columns.includes(c);

    return (
        <div>
            <Card>
                <div class="jt-filters">
                    <Field label="Search"><input class="jt-input" type="search" value={search} placeholder="Title…" onInput={e => setSearch((e.target as HTMLInputElement).value)} /></Field>
                    <Field label="Type"><select class="jt-input" value={filters.kind} disabled={Boolean(parentId)} onChange={e => update({ kind: (e.target as HTMLSelectElement).value as MediaKind | '' })}><option value="">All</option><option value="Movie">Movies</option><option value="Series">Shows</option><option value="Season">Seasons</option><option value="Episode">Episodes</option></select></Field>
                    <div class="jt-field"><span class="jt-field-label">Library</span><MultiPick label="Libraries" options={(libraries.data ?? []).map(l => ({ value: l.id, label: l.name }))} selected={filters.libraries} onChange={v => update({ libraries: v })} /></div>
                    <Field label="Min size (GiB)"><NumberInput value={filters.minGib} step="0.1" min={0} onChange={v => update({ minGib: v })} /></Field>
                    <Field label="Max size (GiB)"><NumberInput value={filters.maxGib} step="0.1" min={0} onChange={v => update({ maxGib: v })} /></Field>
                    <div class="jt-field"><span class="jt-field-label">Users in scope</span><MultiPick label="Users" options={shared.users.map(u => ({ value: u.id, label: u.name, note: u.isDisabled ? 'disabled' : undefined }))} selected={shared.selectedUsers} onChange={shared.setSelectedUsers} /></div>
                    <Field label="Watch state"><select class="jt-input" value={filters.watch} onChange={e => update({ watch: (e.target as HTMLSelectElement).value as WatchState | '' })}><option value="">Any</option><option value="Played">Played</option><option value="Partial">In progress</option><option value="Unplayed">Unplayed</option><option value="Unknown">Unknown</option></select></Field>
                    <Field label="Must match"><select class="jt-input" value={filters.userMatch} disabled={!filters.watch} onChange={e => update({ userMatch: (e.target as HTMLSelectElement).value as UserMatch })}><option value="All">Everyone selected</option><option value="Any">Someone selected</option></select></Field>
                    <Field label="Last played over (days ago)"><NumberInput value={filters.playedOlderDays} min={0} onChange={v => update({ playedOlderDays: v })} /></Field>
                    <Field label="Added over (days ago)"><NumberInput value={filters.addedOlderDays} min={0} onChange={v => update({ addedOlderDays: v })} /></Field>
                    <Field label="Protection"><select class="jt-input" value={filters.protection} onChange={e => update({ protection: (e.target as HTMLSelectElement).value as Filters['protection'] })}><option value="">Any</option><option value="yes">Protected</option><option value="no">Not protected</option></select></Field>
                    <Field label="Policy"><select class="jt-input" value={shared.policyId ?? ''} onChange={e => { const value = (e.target as HTMLSelectElement).value || null; shared.setPolicyId(value); if (!value) update({ eligibility: '' }); }}><option value="">None</option>{shared.policies.map(p => <option key={p.policy.id} value={p.policy.id}>{p.policy.name}</option>)}</select></Field>
                    <Field label="Cleanup eligibility"><select class="jt-input" value={filters.eligibility} disabled={!shared.policyId} onChange={e => update({ eligibility: (e.target as HTMLSelectElement).value as Filters['eligibility'] })}><option value="">Any</option><option value="yes">Eligible</option><option value="no">Not eligible</option></select></Field>
                    <div class="jt-field"><span class="jt-field-label">Columns</span><MultiPick label="Columns" options={COLUMNS.map(c => ({ value: c.id, label: c.label }))} selected={columns} onChange={v => setColumns(v as ColumnId[])} /></div>
                </div>
                <div><Button small variant="plain" onClick={() => { setFilters(INITIAL); setSearch(''); setPath([]); }}>Reset filters</Button></div>
            </Card>

            <Card>
                {path.length > 0 && (
                    <nav class="jt-crumbs" aria-label="Location">
                        <Button small variant="plain" onClick={() => setPath([])}>All</Button>
                        {path.map((p, i) => <span key={p.id}>› <Button small variant="plain" onClick={() => setPath(path.slice(0, i + 1))}>{p.name}</Button></span>)}
                    </nav>
                )}
                {notice && <Alert kind="info">{notice}</Alert>}
                <div class="jt-selbar" aria-live="polite">
                    <strong>{selection.mode === 'query' ? `All ${selection.count.toLocaleString()} matching results selected` : `${selectedCount.toLocaleString()} selected`}</strong>
                    {selection.mode === 'ids' && total > rows.length && pageAllSelected && total > selectedCount && (
                        <Button small onClick={() => { setNotice(null); setSelection({ mode: 'query', query: { ...query, offset: 0 }, count: total }); }}>Select all {total.toLocaleString()} matching results</Button>
                    )}
                    {selectedCount > 0 && <Button small onClick={clear}>Clear selection</Button>}
                    <Button small variant="primary" disabled={selectedCount === 0} onClick={review}>Review cleanup…</Button>
                    {selectedCount === 0 && <span class="jt-small jt-muted">Select items to review a cleanup. Selecting never deletes anything.</span>}
                </div>
                {page.error && <ErrorState error={page.error} onRetry={page.reload} />}
                {page.loading && !page.data && <Loading />}
                {page.data && rows.length === 0 && <Empty title="Nothing matches these filters">Change the filters, or run a scan if the library has changed.</Empty>}
                {rows.length > 0 && (
                    <div class="jt-table-wrap" aria-busy={page.loading}>
                        <table class="jt-table">
                            <thead>
                                <tr>
                                    <th scope="col"><input type="checkbox" aria-label="Select this page" checked={pageAllSelected} onChange={togglePage} /></th>
                                    <th scope="col" aria-sort={ariaSort('Name')}>{sortButton('Name', 'Name')}</th>
                                    {show('library') && <th scope="col">Library</th>}
                                    <th scope="col" class="jt-right" aria-sort={ariaSort('Size')}>{sortButton('Size', 'Size')}</th>
                                    {show('reclaim') && <th scope="col" class="jt-right" aria-sort={ariaSort('Reclaimable')}>{sortButton('Reclaimable', 'Est. reclaimable')}</th>}
                                    <th scope="col">State</th>
                                    {show('played') && <th scope="col" aria-sort={ariaSort('LastPlayed')}>{sortButton('LastPlayed', 'Last played')}</th>}
                                    {show('added') && <th scope="col" aria-sort={ariaSort('DateAdded')}>{sortButton('DateAdded', 'Added')}</th>}
                                    {show('files') && <th scope="col" class="jt-right">Files</th>}
                                </tr>
                            </thead>
                            <tbody>
                                {rows.map(row => {
                                    const selected = selection.mode === 'query' || selection.ids.has(row.id);
                                    return (
                                        <tr key={row.id} class={selected ? 'jt-row-selected' : ''}>
                                            <td><input type="checkbox" aria-label={`Select ${row.name}`} checked={selected} disabled={selection.mode === 'query'} onChange={() => toggle(row.id)} /></td>
                                            <td>
                                                <Button small variant="plain" onClick={() => setDetail(row.id)}>{row.kind === 'Episode' && row.seriesName ? `${row.seriesName} ${episodeCode(row.seasonNumber, row.episodeNumber)} · ` : ''}{row.name}</Button>
                                                {row.children !== null && <Button small onClick={() => open(row)} label={`Open ${row.name}`}>{row.kind === 'Series' ? `${row.children} season(s)` : `${row.children} episode(s)`} ›</Button>}
                                                <div class="jt-small">
                                                    {row.versionCount > 1 && <Badge title="Multiple versions are deleted together">{row.versionCount} versions</Badge>}{' '}
                                                    {row.protected && <Badge tone="good">Protected</Badge>}{' '}
                                                    {row.eligible === true && (row.totalDescendants === null ? <Badge tone="info">Eligible</Badge> : <Badge tone="info">{row.eligibleDescendants} of {row.totalDescendants} eligible</Badge>)}{' '}
                                                    {HEALTH_LABEL[row.health] && <Badge tone="warning">{HEALTH_LABEL[row.health]}</Badge>}
                                                </div>
                                            </td>
                                            {show('library') && <td>{row.libraryName}</td>}
                                            <td class="jt-right"><Size value={row.logicalBytes} /></td>
                                            {show('reclaim') && <td class="jt-right"><Size value={row.reclaimableBytes} /></td>}
                                            <td><UserStates row={row} shared={shared} /></td>
                                            {show('played') && <td class="jt-small" title={dateTime(row.lastPlayedUtc)}>{date(row.lastPlayedUtc)}</td>}
                                            {show('added') && <td class="jt-small" title={dateTime(row.dateAddedUtc)}>{date(row.dateAddedUtc)}</td>}
                                            {show('files') && <td class="jt-right jt-num">{row.fileCount}</td>}
                                        </tr>
                                    );
                                })}
                            </tbody>
                        </table>
                    </div>
                )}
                {page.data && <Pager label="Results pages" offset={offset} limit={PAGE} total={total} onChange={setOffset} />}
            </Card>
            {detail && <ItemPanel id={detail} shared={shared} onClose={() => setDetail(null)} onChanged={page.reload} />}
        </div>
    );
}

function UserStates({ row, shared }: { row: ExplorerRow; shared: Shared }) {
    if (row.userStates.length === 0) return <span class="jt-small jt-muted">No users selected</span>;
    return (
        <div class="jt-small">
            {row.userStates.map(s => {
                const name = shared.users.find(u => u.id === s.userId)?.name ?? 'Unknown user';
                if (s.episodes) {
                    const p = s.episodes;
                    return <span key={s.userId}><Badge tone={WATCH_TONE[s.state]}>{name}: {SHOW_WATCH_LABEL[s.state]}{s.state === 'Unknown' ? '' : ` ${p.played}/${p.total}`}{s.isFavorite ? ' ★' : ''}</Badge>{' '}</span>;
                }
                const percent = resumePercent(s.positionTicks, row.runtimeTicks);
                return <span key={s.userId}><Badge tone={WATCH_TONE[s.state]}>{name}: {WATCH_LABEL[s.state]}{s.state === 'Partial' && percent !== null ? ` ${percent}%` : ''}{s.isFavorite ? ' ★' : ''}</Badge>{' '}</span>;
            })}
        </div>
    );
}

function ItemPanel({ id, shared, onClose, onChanged }: { id: string; shared: Shared; onClose: () => void; onChanged: () => void }) {
    const detail = useAsync(() => api.item(id, shared.policyId), [id, shared.policyId]);
    const [error, setError] = useState<Error | null>(null);
    const d = detail.data;
    const own = d?.protections.find(p => p.targetId === id);
    const inherited = d?.protections.filter(p => p.targetId !== id) ?? [];
    const setProtected = async (on: boolean) => {
        setError(null);
        try {
            if (on) await api.protect(id, 'Item', null);
            else await api.unprotect(id);
            await detail.reload();
            onChanged();
        } catch (e) {
            setError(e as Error);
        }
    };
    return (
        <Dialog side title={d?.item.name ?? 'Item'} onClose={onClose}>
            {detail.error && <ErrorState error={detail.error} onRetry={detail.reload} />}
            {!d && !detail.error && <Loading />}
            {error && <ErrorState error={error} />}
            {d && (
                <div>
                    <p class="jt-muted">{d.item.kind}{d.item.seriesName ? ` · ${d.item.seriesName} ${episodeCode(d.item.seasonNumber, d.item.episodeNumber)}` : ''} · {d.item.libraryName} · added {dateTime(d.item.dateAddedUtc)}</p>
                    <p title={exactBytes(d.item.logicalBytes)}><strong>{bytes(d.item.logicalBytes)}</strong> logical ({exactBytes(d.item.logicalBytes)}) · unique {bytes(d.item.uniqueBytes)} · est. reclaimable {bytes(d.item.reclaimableBytes)}</p>

                    <h3>Protection</h3>
                    {inherited.map(p => <Alert key={p.targetId} kind="good">Protected through {p.targetType === 'Library' ? 'its library' : 'a parent'}: {p.name}</Alert>)}
                    {own ? <Button small onClick={() => setProtected(false)}>Remove protection</Button> : <Button small variant="primary" onClick={() => setProtected(true)}>Protect this item</Button>}
                    <p class="jt-small jt-muted">A protection overrides every policy and also applies to everything below this item. It is keyed to Jellyfin's item id, so it does not survive the item being re-created.</p>

                    <h3>Files</h3>
                    {d.files.length === 0 && <p class="jt-muted">No local files are indexed for this item.</p>}
                    <ul class="jt-list">
                        {d.files.map(f => (
                            <li key={f.path}>
                                <div class="jt-path">{f.path}</div>
                                <div class="jt-small jt-muted"><span title={exactBytes(f.size)}>{exactBytes(f.size)}</span> · allocated {bytes(f.allocated)} · {f.role} · observed {dateTime(f.observedUtc)}</div>
                                <div class="jt-small">
                                    {f.linkCount > 1 && <Badge tone="info">{f.linkCount} hard links: removing this may not free space</Badge>}{' '}
                                    {f.otherItemReferences > 0 && <Badge tone="warning">Also referenced by {f.otherItemReferences} other item(s)</Badge>}{' '}
                                    {f.status !== 'Ok' && <Badge tone="warning">{f.status}</Badge>}
                                </div>
                            </li>
                        ))}
                    </ul>

                    <h3>Per-user state</h3>
                    <div class="jt-table-wrap">
                        <table class="jt-table">
                            <thead><tr><th scope="col">User</th><th scope="col">State</th><th scope="col">Progress</th><th scope="col">Last played</th><th scope="col" class="jt-right">Plays</th><th scope="col">Favorite</th></tr></thead>
                            <tbody>
                                {d.userStates.map(s => (
                                    <tr key={s.userId}>
                                        <td>{s.userName}</td>
                                        <td><Badge tone={WATCH_TONE[s.state]}>{s.episodes ? SHOW_WATCH_LABEL[s.state] : WATCH_LABEL[s.state]}</Badge></td>
                                        <td class="jt-small">{s.episodes ? (s.state === 'Unknown' ? '—' : episodeProgress(s.episodes)) : s.state === 'Partial' ? `${resumePercent(s.positionTicks, d.item.runtimeTicks) ?? '?'}%` : '—'}</td>
                                        <td class="jt-small">{dateTime(s.lastPlayedUtc)}</td>
                                        <td class="jt-right jt-num">{s.state === 'Unknown' ? '—' : s.playCount}</td>
                                        <td>{s.isFavorite ? '★ Yes' : s.state === 'Unknown' ? '—' : 'No'}</td>
                                    </tr>
                                ))}
                            </tbody>
                        </table>
                    </div>
                    <p class="jt-small jt-muted">Played is Jellyfin's flag: a user can set it by hand, and Jellyfin does not record which happened. Last played is one timestamp per user, not a viewing history. As of the last scan.</p>

                    <h3>Policy explanation</h3>
                    {!d.evaluation && <p class="jt-muted">Select a policy to see how its rules apply to this item. Shows and seasons are evaluated through their episodes.</p>}
                    {d.evaluation && (
                        <div>
                            <Alert kind={d.evaluation.eligible ? 'info' : 'good'}>{d.evaluation.eligible ? 'Eligible under this policy.' : 'Not eligible under this policy.'} Evaluated {dateTime(d.policyEvaluatedAtUtc)}.</Alert>
                            {d.evaluation.rules.filter(r => r.outcome !== 'NotApplicable').map(r => (
                                <div class="jt-rule" key={r.ruleId}>
                                    <span class={`jt-outcome-${r.outcome}`}>{r.outcome === 'Pass' ? '✓ ' : '✕ '}{OUTCOME_LABEL[r.outcome]}</span>
                                    <span><strong>{ruleLabel(r.ruleId)}</strong><div class="jt-small jt-muted">{r.message}</div></span>
                                </div>
                            ))}
                        </div>
                    )}
                </div>
            )}
        </Dialog>
    );
}
