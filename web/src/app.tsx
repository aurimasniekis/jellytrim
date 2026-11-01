import { useEffect, useState } from 'preact/hooks';
import { api } from './api';
import { Badge, ErrorState, Loading, useAsync, usePoll } from './components';
import { ago, dateTime } from './format';
import type { PlanRequest, PolicyDto, UserDto } from './types';
import { CleanupView } from './views/Cleanup';
import { ExplorerView } from './views/Explorer';
import { HistoryView } from './views/History';
import { OverviewView } from './views/Overview';
import { PoliciesView } from './views/Policies';
import { SettingsView } from './views/Settings';

export type Tab = 'overview' | 'explorer' | 'policies' | 'cleanup' | 'history' | 'settings';

const TABS: { id: Tab; label: string }[] = [
    { id: 'overview', label: 'Overview' },
    { id: 'explorer', label: 'Media explorer' },
    { id: 'policies', label: 'Policies & protection' },
    { id: 'cleanup', label: 'Cleanup' },
    { id: 'history', label: 'History' },
    { id: 'settings', label: 'Settings' },
];

/** State shared between views. Nothing here persists and no credentials are held. */
export interface Shared {
    users: UserDto[];
    policies: PolicyDto[];
    reloadPolicies: () => void;
    selectedUsers: string[];
    setSelectedUsers: (ids: string[]) => void;
    policyId: string | null;
    setPolicyId: (id: string | null) => void;
    deletionEnabled: boolean;
    refreshStatus: () => void;
    go: (tab: Tab) => void;
    startCleanup: (request: PlanRequest, description: string) => void;
    openJob: (id: string) => void;
}

export function App() {
    const [tab, setTab] = useState<Tab>('overview');
    const status = useAsync(() => api.status(), []);
    const users = useAsync(() => api.users(), []);
    const policies = useAsync(() => api.policies(), []);
    const [selectedUsers, setSelectedUsers] = useState<string[]>([]);
    const [policyId, setPolicyId] = useState<string | null>(null);
    const [pending, setPending] = useState<{ request: PlanRequest; description: string; nonce: number } | null>(null);
    const [jobId, setJobId] = useState<string | null>(null);

    usePoll(status.reload, status.data?.scan.running || status.data?.activeJobId ? 3000 : 30000);

    useEffect(() => {
        if (selectedUsers.length === 0 && users.data) {
            setSelectedUsers(users.data.filter(u => !u.isDisabled).map(u => u.id).slice(0, 25));
        }
    }, [users.data]);

    useEffect(() => {
        if (policyId === null && policies.data?.length) setPolicyId(policies.data[0].policy.id);
    }, [policies.data]);

    if (status.error && !status.data) return <div><h1>jellytrim</h1><ErrorState error={status.error} onRetry={status.reload} /></div>;
    if (!status.data) return <Loading label="Loading jellytrim" />;

    const shared: Shared = {
        users: users.data ?? [],
        policies: policies.data ?? [],
        reloadPolicies: policies.reload,
        selectedUsers,
        setSelectedUsers,
        policyId,
        setPolicyId,
        deletionEnabled: status.data.deletionEnabled,
        refreshStatus: status.reload,
        go: setTab,
        startCleanup: (request, description) => {
            setPending({ request, description, nonce: Date.now() });
            setTab('cleanup');
        },
        openJob: id => {
            setJobId(id);
            setTab('history');
        },
    };
    const scan = status.data.scan;
    return (
        <div>
            <header class="jt-head">
                <div>
                    <h1>jellytrim</h1>
                    <div class="jt-muted jt-small">Keep what matters. Reclaim the rest. · Independent plugin, not affiliated with the Jellyfin project · v{status.data.version}</div>
                </div>
                <div class="jt-mode">
                    {status.data.deletionEnabled
                        ? <Badge tone="critical" title="Plans can permanently delete files inside approved roots">Deletion enabled</Badge>
                        : <Badge tone="good" title="Nothing can be deleted">Read-only</Badge>}
                    {status.data.activeJobId && <Badge tone="serious">Cleanup running</Badge>}
                    {scan.running
                        ? <Badge tone="info">Scanning, {scan.itemsSeen.toLocaleString()} items</Badge>
                        : <span class="jt-small jt-muted" title={dateTime(scan.lastSuccessful?.finishedUtc)}>Last scan {ago(scan.lastSuccessful?.finishedUtc)}{scan.stale && scan.lastSuccessful ? ' (stale)' : ''}</span>}
                </div>
            </header>
            <div class="jt-tabs" role="tablist" aria-label="jellytrim sections">
                {TABS.map(t => (
                    <button key={t.id} type="button" role="tab" id={`jt-tab-${t.id}`} class="jt-tab" aria-selected={tab === t.id} aria-controls="jt-panel" tabIndex={tab === t.id ? 0 : -1} onClick={() => setTab(t.id)}
                        onKeyDown={e => {
                            const at = TABS.findIndex(x => x.id === t.id);
                            const next = e.key === 'ArrowRight' ? at + 1 : e.key === 'ArrowLeft' ? at - 1 : null;
                            if (next !== null) {
                                const target = TABS[(next + TABS.length) % TABS.length].id;
                                setTab(target);
                                document.getElementById(`jt-tab-${target}`)?.focus();
                            }
                        }}>{t.label}</button>
                ))}
            </div>
            <main id="jt-panel" role="tabpanel" aria-labelledby={`jt-tab-${tab}`}>
                {tab === 'overview' && <OverviewView shared={shared} status={status.data} />}
                {tab === 'explorer' && <ExplorerView shared={shared} />}
                {tab === 'policies' && <PoliciesView shared={shared} />}
                {tab === 'cleanup' && <CleanupView shared={shared} pending={pending} clearPending={() => setPending(null)} />}
                {tab === 'history' && <HistoryView jobId={jobId} setJobId={setJobId} />}
                {tab === 'settings' && <SettingsView shared={shared} />}
            </main>
        </div>
    );
}
