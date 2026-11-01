import type { AuditEntry, ExplorerPage, ExplorerQuery, ItemDetail, JobDetail, JobRow, Overview, Paged, PlanDto, PlanRequest, PolicyDto, CleanupPolicy, Protection, RecommendationSet, ScanStatus, Settings, SettingsDto, Status, Trends, UserDto } from './types';

/** The surface of Jellyfin Web's global client that jellytrim uses. The token is read per request and never stored. */
interface JellyfinApiClient {
    accessToken(): string;
    getUrl(path: string): string;
}

export class ApiError extends Error {
    constructor(
        public readonly status: number,
        public readonly code: string,
        message: string,
        public readonly correlationId?: string,
        public readonly details?: unknown,
    ) {
        super(message);
    }

    get isPermission(): boolean { return this.status === 401 || this.status === 403; }

    get isOffline(): boolean { return this.status === 0; }
}

function client(): JellyfinApiClient {
    return (globalThis as unknown as { ApiClient: JellyfinApiClient }).ApiClient;
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
    const api = client();
    const headers: Record<string, string> = { Authorization: `MediaBrowser Token="${api.accessToken()}"` };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    let response: Response;
    try {
        response = await fetch(api.getUrl(`Jellytrim/${path}`), { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    } catch {
        throw new ApiError(0, 'NETWORK', 'The server could not be reached. Check your connection and try again.');
    }

    if (response.ok) {
        const text = await response.text();
        return (text ? JSON.parse(text) : undefined) as T;
    }

    let parsed: { code?: string; message?: string; correlationId?: string; details?: unknown } = {};
    try {
        parsed = await response.json();
    } catch {
        // Jellyfin's own auth layer answers 401 and 403 with no JSON body.
    }

    const fallback = response.status === 401 || response.status === 403
        ? 'Your account does not have administrator access to jellytrim.'
        : `The request failed (${response.status}).`;
    throw new ApiError(response.status, parsed.code ?? 'HTTP_' + response.status, parsed.message ?? fallback, parsed.correlationId, parsed.details);
}

const repeat = (name: string, values: string[]) => values.map(v => `${name}=${encodeURIComponent(v)}`);

function query(params: Array<string | null | undefined>): string {
    const present = params.filter(Boolean);
    return present.length ? `?${present.join('&')}` : '';
}

export const api = {
    status: () => request<Status>('GET', 'Status'),
    overview: (userIds: string[], policyId: string | null) => request<Overview>('GET', 'Overview' + query([...repeat('userIds', userIds), policyId ? `policyId=${policyId}` : null])),
    trends: (days: number) => request<Trends>('GET', `Trends?days=${days}`),
    users: () => request<UserDto[]>('GET', 'Users'),
    queryItems: (q: ExplorerQuery) => request<ExplorerPage>('POST', 'Items/Query', q),
    item: (id: string, policyId: string | null) => request<ItemDetail>('GET', `Items/${id}` + query([policyId ? `policyId=${policyId}` : null])),
    policies: () => request<PolicyDto[]>('GET', 'Policies'),
    createPolicy: (p: CleanupPolicy) => request<PolicyDto>('POST', 'Policies', p),
    updatePolicy: (p: CleanupPolicy) => request<PolicyDto>('PUT', `Policies/${p.id}`, { policy: p, expectedRevision: p.revision }),
    deletePolicy: (id: string) => request<void>('DELETE', `Policies/${id}`),
    evaluatePolicy: (id: string) => request<PolicyDto>('POST', `Policies/${id}/Evaluate`),
    recommend: (policyId: string, targetBytes: number) => request<RecommendationSet>('POST', 'Recommendations', { policyId, targetBytes }),
    protections: () => request<Protection[]>('GET', 'Protections'),
    protect: (targetId: string, targetType: 'Item' | 'Library', note: string | null) => request<Protection>('POST', 'Protections', { targetId, targetType, note }),
    unprotect: (targetId: string) => request<void>('DELETE', `Protections/${targetId}`),
    settings: () => request<SettingsDto>('GET', 'Settings'),
    saveSettings: (settings: Settings, expectedRevision: number, enableConfirmation: string | null) => request<SettingsDto>('PUT', 'Settings', { settings, expectedRevision, enableConfirmation }),
    startScan: () => request<ScanStatus>('POST', 'Scans'),
    scan: () => request<ScanStatus>('GET', 'Scans/Current'),
    createPlan: (r: PlanRequest) => request<PlanDto>('POST', 'Plans', r),
    plan: (id: string, itemOffset: number, exclusionOffset: number) => request<PlanDto>('GET', `Plans/${id}?itemOffset=${itemOffset}&exclusionOffset=${exclusionOffset}`),
    execute: (id: string, planHash: string, idempotencyKey: string, acknowledged: boolean, confirmationPhrase: string | null) => request<JobRow>('POST', `Plans/${id}/Execute`, { planHash, idempotencyKey, acknowledged, confirmationPhrase }),
    jobs: (offset: number) => request<Paged<JobRow>>('GET', `Jobs?offset=${offset}&limit=25`),
    job: (id: string, offset: number, status: string | null) => request<JobDetail>('GET', `Jobs/${id}?offset=${offset}&limit=50` + (status ? `&status=${status}` : '')),
    cancelJob: (id: string) => request<JobRow>('POST', `Jobs/${id}/Cancel`),
    audit: (offset: number) => request<Paged<AuditEntry>>('GET', `Audit?offset=${offset}&limit=50`),
};
