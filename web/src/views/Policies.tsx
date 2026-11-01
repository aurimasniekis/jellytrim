import { useState } from 'preact/hooks';
import { ApiError, api } from '../api';
import type { Shared } from '../app';
import { Alert, Badge, Button, Card, CheckboxList, Dialog, Empty, ErrorState, Field, Loading, NumberInput, Size, useAsync } from '../components';
import { dateTime, gibToBytes, bytesToGib, ruleLabel } from '../format';
import type { CleanupPolicy, CleanupUnit, MediaKind, PolicyDto, UserProtectionScope, WatchedRequirement } from '../types';

const blank = (shared: Shared): CleanupPolicy => ({
    id: '00000000-0000-0000-0000-000000000000', name: '', revision: 1,
    participatingUserIds: shared.users.filter(u => !u.isDisabled).map(u => u.id),
    excludedUserIds: [], watched: 'AllSelected', minDaysSinceLastWatched: 30, minDaysSinceAdded: null, minSizeBytes: null, unit: 'Item',
    mediaKinds: ['Movie', 'Episode'], includedLibraryIds: [], excludedLibraryIds: [], excludedPathPrefixes: [], excludedTags: [],
    favoriteProtection: 'AllKnownUsers', inProgressProtection: 'AllKnownUsers', keepAddedWithinDays: 30, keepNewestEpisodesPerShow: null,
});

export function PoliciesView({ shared }: { shared: Shared }) {
    const [editing, setEditing] = useState<CleanupPolicy | null>(null);
    const [error, setError] = useState<Error | null>(null);
    const remove = async (p: PolicyDto) => {
        setError(null);
        try {
            await api.deletePolicy(p.policy.id);
            if (shared.policyId === p.policy.id) shared.setPolicyId(null);
            shared.reloadPolicies();
        } catch (e) { setError(e as Error); }
    };
    const evaluate = async (p: PolicyDto) => {
        setError(null);
        try {
            await api.evaluatePolicy(p.policy.id);
            shared.reloadPolicies();
        } catch (e) { setError(e as Error); }
    };
    return (
        <div>
            {error && <ErrorState error={error} />}
            <Card title="Cleanup policies" note="A policy only decides what is eligible. It never deletes anything by itself: every deletion goes through a reviewed plan." actions={<Button variant="primary" onClick={() => setEditing(blank(shared))}>New policy</Button>}>
                {shared.policies.length === 0 && <Empty title="No policies yet">Create one to find watched content that could go.</Empty>}
                <ul class="jt-list">
                    {shared.policies.map(p => (
                        <li key={p.policy.id}>
                            <div class="jt-card-head" style={{ marginBottom: 0 }}>
                                <div>
                                    <strong>{p.policy.name}</strong> <span class="jt-muted jt-small">revision {p.policy.revision}</span>
                                    <div class="jt-small">{describe(p.policy, shared)}</div>
                                    {p.unavailableUsers.length > 0 && <Alert kind="warning">This policy references {p.unavailableUsers.length} user(s) the server no longer has. Their state is unknown, so affected items stay ineligible. Edit the policy to remove them.</Alert>}
                                    {p.run && (
                                        <div class="jt-small">
                                            <strong>{p.run.eligibleCount.toLocaleString()}</strong> of {p.run.consideredCount.toLocaleString()} eligible · est. reclaimable <Size value={p.run.eligibleReclaimableBytes} /> · evaluated {dateTime(p.run.evaluatedAtUtc)}{p.runStale && <> <Badge tone="warning">Stale</Badge></>}
                                            {Object.keys(p.run.blockedByRule).length > 0 && (
                                                <details><summary>Why items were held back</summary>
                                                    <ul class="jt-list">{Object.entries(p.run.blockedByRule).sort((a, b) => b[1] - a[1]).map(([rule, count]) => <li key={rule}>{ruleLabel(rule)}: {count.toLocaleString()}</li>)}</ul>
                                                </details>
                                            )}
                                        </div>
                                    )}
                                </div>
                                <div class="jt-card-actions">
                                    <Button small onClick={() => evaluate(p)}>Re-evaluate</Button>
                                    <Button small onClick={() => setEditing(p.policy)}>Edit</Button>
                                    <Button small onClick={() => remove(p)} label={`Delete policy ${p.policy.name}`}>Delete</Button>
                                </div>
                            </div>
                        </li>
                    ))}
                </ul>
            </Card>
            <Protections shared={shared} />
            {editing && <PolicyEditor policy={editing} shared={shared} onClose={() => setEditing(null)} onSaved={saved => { setEditing(null); shared.setPolicyId(saved.policy.id); shared.reloadPolicies(); }} />}
        </div>
    );
}

function describe(p: CleanupPolicy, shared: Shared): string {
    const names = p.participatingUserIds.filter(u => !p.excludedUserIds.includes(u)).map(u => shared.users.find(x => x.id === u)?.name ?? 'unknown user');
    const parts = [`Played by ${p.watched === 'AllSelected' ? 'all of' : 'any of'}: ${names.join(', ') || 'nobody'}`];
    if (p.minDaysSinceLastWatched !== null) parts.push(`last played over ${p.minDaysSinceLastWatched} days ago`);
    if (p.minDaysSinceAdded !== null) parts.push(`added over ${p.minDaysSinceAdded} days ago`);
    if (p.keepAddedWithinDays !== null) parts.push(`keeps items added in the last ${p.keepAddedWithinDays} days`);
    if (p.keepNewestEpisodesPerShow !== null) parts.push(`keeps newest ${p.keepNewestEpisodesPerShow} episodes per show`);
    if (p.unit !== 'Item') parts.push(p.unit === 'CompleteSeason' ? 'whole seasons only' : 'whole shows only');
    return parts.join('; ');
}

function PolicyEditor({ policy, shared, onClose, onSaved }: { policy: CleanupPolicy; shared: Shared; onClose: () => void; onSaved: (p: PolicyDto) => void }) {
    const [p, setP] = useState<CleanupPolicy>(policy);
    const [error, setError] = useState<Error | null>(null);
    const [busy, setBusy] = useState(false);
    const libraries = useAsync(() => api.overview([], null).then(o => o.libraries.map(l => l.library)), []);
    const set = (patch: Partial<CleanupPolicy>) => setP(x => ({ ...x, ...patch }));
    const isNew = policy.id === '00000000-0000-0000-0000-000000000000';
    const effective = p.participatingUserIds.filter(u => !p.excludedUserIds.includes(u));
    const userOptions = [
        ...shared.users.map(u => ({ value: u.id, label: u.name, note: u.isDisabled ? 'disabled' : undefined })),
        ...p.participatingUserIds.filter(u => !shared.users.some(x => x.id === u)).map(u => ({ value: u, label: 'Deleted user', note: 'state unknown' })),
    ];
    const libraryOptions = (libraries.data ?? []).map(l => ({ value: l.id, label: l.name }));
    const save = async () => {
        setBusy(true);
        setError(null);
        try {
            onSaved(isNew ? await api.createPolicy(p) : await api.updatePolicy(p));
        } catch (e) {
            setError(e as Error);
        } finally {
            setBusy(false);
        }
    };
    const lines = (value: string[]) => value.join('\n');
    const parse = (text: string) => text.split('\n').map(s => s.trim()).filter(Boolean);
    return (
        <Dialog wide title={isNew ? 'New policy' : `Edit ${policy.name}`} onClose={onClose}>
            {error && <ErrorState error={error} />}
            {error instanceof ApiError && error.code === 'REVISION_CONFLICT' && <Alert kind="warning">Someone else changed this policy. Close this editor and open it again to continue.</Alert>}
            <div class="jt-form">
                <Field label="Name"><input data-autofocus class="jt-input" value={p.name} maxLength={120} onInput={e => set({ name: (e.target as HTMLInputElement).value })} /></Field>
                <Field label="Requires played by" hint="Played is Jellyfin's flag, which users can set manually."><select class="jt-input" value={p.watched} onChange={e => set({ watched: (e.target as HTMLSelectElement).value as WatchedRequirement })}><option value="AllSelected">Everyone selected</option><option value="AnySelected">Someone selected</option></select></Field>
                <CheckboxList legend="Participating users" options={userOptions} selected={p.participatingUserIds} onChange={v => set({ participatingUserIds: v })} />
                <CheckboxList legend="Excluded users (subtracted)" options={userOptions} selected={p.excludedUserIds} onChange={v => set({ excludedUserIds: v })} />
                <Field label="Last played over (days)" hint="Most recent time across selected users. A missing time keeps the item."><NumberInput value={p.minDaysSinceLastWatched} min={0} onChange={v => set({ minDaysSinceLastWatched: v })} /></Field>
                <Field label="Added over (days)"><NumberInput value={p.minDaysSinceAdded} min={0} onChange={v => set({ minDaysSinceAdded: v })} /></Field>
                <Field label="Minimum size (GiB)"><NumberInput value={p.minSizeBytes === null ? null : bytesToGib(p.minSizeBytes)} step="0.1" min={0} onChange={v => set({ minSizeBytes: v === null ? null : gibToBytes(v) })} /></Field>
                <Field label="Unit" hint="Whole units qualify only if every file-backed episode qualifies."><select class="jt-input" value={p.unit} onChange={e => set({ unit: (e.target as HTMLSelectElement).value as CleanupUnit })}><option value="Item">Individual movies and episodes</option><option value="CompleteSeason">Complete seasons</option><option value="CompleteShow">Complete shows</option></select></Field>
                <CheckboxList legend="Media types" options={[{ value: 'Movie', label: 'Movies' }, { value: 'Episode', label: 'Episodes' }]} selected={p.mediaKinds} onChange={v => set({ mediaKinds: v as MediaKind[] })} />
                <CheckboxList legend="Only these libraries (none means all)" options={libraryOptions} selected={p.includedLibraryIds} onChange={v => set({ includedLibraryIds: v })} />
                <CheckboxList legend="Excluded libraries" options={libraryOptions} selected={p.excludedLibraryIds} onChange={v => set({ excludedLibraryIds: v })} />
                <Field label="Protect favorites of"><select class="jt-input" value={p.favoriteProtection} onChange={e => set({ favoriteProtection: (e.target as HTMLSelectElement).value as UserProtectionScope })}><option value="AllKnownUsers">Any user on the server</option><option value="SelectedUsers">Selected users only</option><option value="None">Nobody (favorites not protected)</option></select></Field>
                <Field label="Protect in-progress for"><select class="jt-input" value={p.inProgressProtection} onChange={e => set({ inProgressProtection: (e.target as HTMLSelectElement).value as UserProtectionScope })}><option value="AllKnownUsers">Any user on the server</option><option value="SelectedUsers">Selected users only</option></select></Field>
                <Field label="Keep items added in the last (days)"><NumberInput value={p.keepAddedWithinDays} min={0} onChange={v => set({ keepAddedWithinDays: v })} /></Field>
                <Field label="Keep newest episodes per show" hint="Specials are neither counted nor protected."><NumberInput value={p.keepNewestEpisodesPerShow} min={0} onChange={v => set({ keepNewestEpisodesPerShow: v })} /></Field>
                <Field label="Excluded path prefixes (one per line)"><textarea class="jt-input" rows={2} value={lines(p.excludedPathPrefixes)} onChange={e => set({ excludedPathPrefixes: parse((e.target as HTMLTextAreaElement).value) })} /></Field>
                <Field label="Excluded tags (one per line)"><textarea class="jt-input" rows={2} value={lines(p.excludedTags)} onChange={e => set({ excludedTags: parse((e.target as HTMLTextAreaElement).value) })} /></Field>
            </div>
            <div class="jt-actions">
                <Button variant="primary" onClick={save} disabled={busy} why={!p.name.trim() ? 'Enter a name.' : effective.length === 0 ? 'Select at least one user who is not excluded. An empty set can never mean “watched by everyone”.' : null}>Save policy</Button>
                <Button onClick={onClose}>Cancel</Button>
            </div>
        </Dialog>
    );
}

function Protections({ shared }: { shared: Shared }) {
    const list = useAsync(() => api.protections(), []);
    const libraries = useAsync(() => api.overview([], null).then(o => o.libraries.map(l => l.library)), []);
    const [error, setError] = useState<Error | null>(null);
    const run = async (action: () => Promise<unknown>) => {
        setError(null);
        try {
            await action();
            await list.reload();
            shared.reloadPolicies();
        } catch (e) { setError(e as Error); }
    };
    const protectedIds = new Set(list.data?.map(p => p.targetId));
    return (
        <Card title="Protections" note="Protections override every policy and every selection. Protect individual items, shows and seasons from their panel in the explorer.">
            {error && <ErrorState error={error} />}
            {list.error && <ErrorState error={list.error} onRetry={list.reload} />}
            {!list.data && !list.error && <Loading />}
            <h3>Libraries</h3>
            <ul class="jt-list">
                {(libraries.data ?? []).map(l => (
                    <li key={l.id} style={{ display: 'flex', justifyContent: 'space-between', gap: '8px' }}>
                        <span>{l.name} {protectedIds.has(l.id) && <Badge tone="good">Protected</Badge>}</span>
                        {protectedIds.has(l.id)
                            ? <Button small onClick={() => run(() => api.unprotect(l.id))} label={`Remove protection from ${l.name}`}>Remove</Button>
                            : <Button small onClick={() => run(() => api.protect(l.id, 'Library', null))} label={`Protect library ${l.name}`}>Protect</Button>}
                    </li>
                ))}
            </ul>
            <h3>Items</h3>
            {list.data && list.data.filter(p => p.targetType === 'Item').length === 0 && <p class="jt-muted">No items are protected.</p>}
            <ul class="jt-list">
                {list.data?.filter(p => p.targetType === 'Item').map(p => (
                    <li key={p.targetId} style={{ display: 'flex', justifyContent: 'space-between', gap: '8px' }}>
                        <span>{p.name}<div class="jt-small jt-muted">{p.createdBy ?? 'unknown'} · {dateTime(p.createdAtUtc)}</div></span>
                        <Button small onClick={() => run(() => api.unprotect(p.targetId))} label={`Remove protection from ${p.name}`}>Remove</Button>
                    </li>
                ))}
            </ul>
        </Card>
    );
}
