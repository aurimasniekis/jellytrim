import { useEffect, useState } from 'preact/hooks';
import { ApiError, api } from '../api';
import type { Shared } from '../app';
import { Alert, Button, Card, CheckboxList, Dialog, ErrorState, Field, Loading, NumberInput, useAsync } from '../components';
import { bytesToGib, dateTime, gibToBytes } from '../format';
import type { Settings } from '../types';

export function SettingsView({ shared }: { shared: Shared }) {
    const loaded = useAsync(() => api.settings(), []);
    const context = useAsync(() => api.overview([], null), []);
    const [form, setForm] = useState<Settings | null>(null);
    const [error, setError] = useState<Error | null>(null);
    const [saved, setSaved] = useState(false);
    const [enabling, setEnabling] = useState(false);
    useEffect(() => { if (loaded.data) setForm(loaded.data.settings); }, [loaded.data]);
    if (loaded.error && !loaded.data) return <ErrorState error={loaded.error} onRetry={loaded.reload} />;
    if (!form || !loaded.data) return <Loading />;
    const set = (patch: Partial<Settings>) => { setSaved(false); setForm({ ...form, ...patch }); };

    const save = async (next: Settings, confirmation: string | null) => {
        setError(null);
        setSaved(false);
        try {
            const result = await api.saveSettings(next, loaded.data!.revision, confirmation);
            loaded.setData(result);
            setForm(result.settings);
            setSaved(true);
            setEnabling(false);
            shared.refreshStatus();
        } catch (e) {
            setError(e as Error);
        }
    };

    const budget = (id: string) => form.libraryBudgets.find(b => b.libraryId === id)?.budgetBytes ?? null;
    const setBudget = (libraryId: string, gib: number | null) => set({ libraryBudgets: [...form.libraryBudgets.filter(b => b.libraryId !== libraryId), ...(gib ? [{ libraryId, budgetBytes: gibToBytes(gib) }] : [])] });
    const goal = (id: string) => form.volumeGoals.find(b => b.volumeId === id)?.minimumFreeBytes ?? null;
    const setGoal = (volumeId: string, gib: number | null) => set({ volumeGoals: [...form.volumeGoals.filter(b => b.volumeId !== volumeId), ...(gib ? [{ volumeId, minimumFreeBytes: gibToBytes(gib) }] : [])] });
    const live = loaded.data.settings;

    return (
        <div>
            {error && <ErrorState error={error} onRetry={error instanceof ApiError && error.code === 'REVISION_CONFLICT' ? loaded.reload : undefined} />}
            {saved && <Alert kind="good">Settings saved.</Alert>}
            <Card title="Permanent deletion" note="Off by default. While off, jellytrim only reads, and plans are previews that can never run. There is no scheduled deletion: every deletion is a plan confirmed by an administrator.">
                <CheckboxList legend="Approved deletion roots: nothing outside these can ever be deleted" options={loaded.data.libraryRoots.map(r => ({ value: r, label: r }))} selected={form.approvedRoots} onChange={v => set({ approvedRoots: v })} />
                {live.deletionEnabled
                    ? (
                        <div>
                            <Alert kind="warning">Deletion was enabled {dateTime(live.deletionEnabledAtUtc)}{live.deletionEnabledBy ? ` by ${live.deletionEnabledBy}` : ''}. Plans can permanently delete files inside approved roots.</Alert>
                            <Button onClick={() => save({ ...form, deletionEnabled: false }, null)}>Disable deletion</Button>
                        </div>
                    )
                    : (
                        <div class="jt-actions">
                            <Button variant="danger" onClick={() => setEnabling(true)} why={form.approvedRoots.length === 0 ? 'Approve at least one root first.' : null}>Enable permanent deletion…</Button>
                        </div>
                    )}
            </Card>
            <Card title="Size budgets and free-space goals" note="Advisory only. Going over a budget raises a warning. It never triggers deletion and never overrides protection.">
                {context.error && <ErrorState error={context.error} onRetry={context.reload} />}
                <div class="jt-form">
                    {context.data?.libraries.map(({ library: l }) => (
                        <Field key={l.id} label={`Budget for ${l.name} (GiB, unique bytes)`}><NumberInput value={budget(l.id) === null ? null : bytesToGib(budget(l.id)!)} step="1" min={0} placeholder="No budget" onChange={v => setBudget(l.id, v)} /></Field>
                    ))}
                    {context.data?.volumes.map(({ volume: v }) => (
                        <Field key={v.volumeId} label={`Minimum free on ${v.samplePath} (GiB)`} hint="This covers the whole volume, shared by all its libraries."><NumberInput value={goal(v.volumeId) === null ? null : bytesToGib(goal(v.volumeId)!)} step="1" min={0} placeholder="No goal" onChange={g => setGoal(v.volumeId, g)} /></Field>
                    ))}
                </div>
            </Card>
            <Card title="Limits and retention">
                <div class="jt-form">
                    <Field label="Plan lifetime (minutes)"><NumberInput value={form.planTtlMinutes} min={1} onChange={v => set({ planTtlMinutes: v ?? 0 })} /></Field>
                    <Field label="Stale scan after (hours)"><NumberInput value={form.staleScanHours} min={1} onChange={v => set({ staleScanHours: v ?? 0 })} /></Field>
                    <Field label="High-impact: at least this many items" hint="Requires typing a phrase to confirm."><NumberInput value={form.highImpactItems} min={1} onChange={v => set({ highImpactItems: v ?? 0 })} /></Field>
                    <Field label="High-impact: at least this much (GiB)"><NumberInput value={bytesToGib(form.highImpactBytes)} min={0} onChange={v => set({ highImpactBytes: gibToBytes(v ?? 0) })} /></Field>
                    <Field label="Maximum items per plan"><NumberInput value={form.maxPlanItems} min={1} onChange={v => set({ maxPlanItems: v ?? 0 })} /></Field>
                    <Field label="Keep snapshots (days)"><NumberInput value={form.snapshotRetentionDays} min={7} onChange={v => set({ snapshotRetentionDays: v ?? 0 })} /></Field>
                    <Field label="Keep audit and job history (days)" hint="Separate from Jellyfin's server logs."><NumberInput value={form.auditRetentionDays} min={30} onChange={v => set({ auditRetentionDays: v ?? 0 })} /></Field>
                </div>
                <div class="jt-actions"><Button variant="primary" onClick={() => save(form, null)}>Save settings</Button></div>
            </Card>
            {enabling && <EnableDialog phrase={loaded.data.enablePhrase} roots={form.approvedRoots} error={error} onClose={() => setEnabling(false)} onConfirm={typed => save({ ...form, deletionEnabled: true }, typed)} />}
        </div>
    );
}

function EnableDialog({ phrase, roots, error, onClose, onConfirm }: { phrase: string; roots: string[]; error: Error | null; onClose: () => void; onConfirm: (typed: string) => void }) {
    const [typed, setTyped] = useState('');
    return (
        <Dialog title="Enable permanent deletion?" onClose={onClose}>
            {error && <ErrorState error={error} />}
            <Alert kind="warning">This allows confirmed plans to permanently delete files in:</Alert>
            <ul class="jt-path">{roots.map(r => <li key={r}>{r}</li>)}</ul>
            <p>It deletes nothing by itself. Every deletion still needs a reviewed and confirmed plan. Make sure you have backups of anything you might want back.</p>
            <Field label={`Type ${phrase} to continue`}><input data-autofocus class="jt-input" autoComplete="off" value={typed} onInput={e => setTyped((e.target as HTMLInputElement).value)} /></Field>
            <div class="jt-actions">
                <Button onClick={onClose}>Stay read-only</Button>
                <Button variant="danger" onClick={() => onConfirm(typed)} why={typed !== phrase ? 'Type the phrase exactly.' : null}>Enable deletion</Button>
            </div>
        </Dialog>
    );
}
