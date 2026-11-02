import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/preact';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api } from '../src/api';
import type { Shared } from '../src/app';
import { Button, Dialog, ErrorState, MultiPick, Size } from '../src/components';
import { CleanupView } from '../src/views/Cleanup';
import { ExplorerView } from '../src/views/Explorer';
import type { ExplorerRow, PlanDto } from '../src/types';

const row = (over: Partial<ExplorerRow>): ExplorerRow => ({
    id: 'id1', kind: 'Movie', name: 'Alpha', seriesName: null, seasonNumber: null, episodeNumber: null, libraryId: 'l', libraryName: 'Movies', seriesId: null, seasonId: null,
    logicalBytes: 2048, uniqueBytes: 2048, reclaimableBytes: 2048, fileCount: 1, versionCount: 1, dateAddedUtc: null, lastPlayedUtc: null, runtimeTicks: 100, health: 'Ok', protected: false,
    eligible: null, eligibleDescendants: null, totalDescendants: null, children: null, path: null, userStates: [], ...over,
});

const shared = (over: Partial<Shared> = {}): Shared => ({
    users: [{ id: 'u1', name: 'ann', isAdministrator: false, isDisabled: false, stateReadable: true }],
    policies: [], reloadPolicies: vi.fn(), selectedUsers: ['u1'], setSelectedUsers: vi.fn(), policyId: null, setPolicyId: vi.fn(), deletionEnabled: true,
    refreshStatus: vi.fn(), go: vi.fn(), startCleanup: vi.fn(), openJob: vi.fn(), ...over,
});

const plan = (over: Partial<PlanDto['plan']> = {}): PlanDto => ({
    summary: { id: 'p1', status: 'Ready', hash: 'abcdef0123456789', expiresAtUtc: new Date(Date.now() + 600000).toISOString() },
    plan: { id: 'p1', createdAtUtc: new Date().toISOString(), expiresAtUtc: new Date(Date.now() + 600000).toISOString(), createdBy: 'a', policyName: 'Explicit selection', previewOnly: false, containers: [], warnings: [], totals: { items: 1, files: 1, logicalBytes: 10, uniqueBytes: 10, estimatedReclaimableBytes: 8, hardLinkedRetained: 0 }, volumes: [], highImpact: false, confirmationPhrase: null, ...over },
    items: { items: [{ itemId: 'i', kind: 'Movie', name: 'Alpha', seriesName: null, seasonNumber: null, episodeNumber: null, files: [{ path: '/m/a.mkv', role: 'Main', identity: '1:1', volumeId: '1', size: 10, allocatedBytes: 8, linkCount: 1, modifiedUnixNanos: 1, estimatedFreedBytes: 8 }], directoriesIfEmpty: [], unplannedNeighbors: [], reasons: [], sharedRecords: [] }], total: 1, offset: 0, limit: 50 },
    exclusions: { items: [], total: 0, offset: 0, limit: 50 },
});

beforeEach(() => {
    vi.spyOn(api, 'overview').mockResolvedValue({ libraries: [], volumes: [] } as never);
});

afterEach(() => {
    cleanup();
    vi.restoreAllMocks();
});

describe('safety of what is displayed', () => {
    it('renders hostile names as text', async () => {
        vi.spyOn(api, 'queryItems').mockResolvedValue({ rows: [row({ name: '<img src=x onerror=alert(1)>' })], total: 1, offset: 0, limit: 50 });
        const { container } = render(<ExplorerView shared={shared()} />);
        await screen.findByText('<img src=x onerror=alert(1)>');
        expect(container.querySelector('img')).toBeNull();
    });

    it('shows Unknown as Unknown, never as unplayed', async () => {
        vi.spyOn(api, 'queryItems').mockResolvedValue({ rows: [row({ userStates: [{ userId: 'u1', state: 'Unknown', positionTicks: 0, playCount: 0, lastPlayedUtc: null, isFavorite: false }] })], total: 1, offset: 0, limit: 50 });
        render(<ExplorerView shared={shared()} />);
        expect(await screen.findByText(/ann: Unknown/)).toBeTruthy();
        expect(screen.queryByText(/ann: Unplayed/)).toBeNull();
    });

    it('shows resume progress for in-progress items', async () => {
        vi.spyOn(api, 'queryItems').mockResolvedValue({ rows: [row({ userStates: [{ userId: 'u1', state: 'Partial', positionTicks: 25, playCount: 0, lastPlayedUtc: null, isFavorite: true }] })], total: 1, offset: 0, limit: 50 });
        render(<ExplorerView shared={shared()} />);
        expect(await screen.findByText(/ann: In progress 25% ★/)).toBeTruthy();
    });

    it('exposes exact bytes beside a rounded size', () => {
        render(<Size value={1234567} />);
        expect(screen.getByTitle('1,234,567 bytes')).toBeTruthy();
    });
});

describe('explorer selection', () => {
    const many = Array.from({ length: 50 }, (_, i) => row({ id: `id${i}`, name: `Movie ${i}` }));

    it('select-page and select-all-matching are distinct, and a filter change clears all-matching', async () => {
        const query = vi.spyOn(api, 'queryItems').mockResolvedValue({ rows: many, total: 120, offset: 0, limit: 50 });
        const s = shared();
        render(<ExplorerView shared={s} />);
        await screen.findByText('Movie 0');
        fireEvent.click(screen.getByLabelText('Select this page'));
        expect(await screen.findByText('50 selected')).toBeTruthy();

        fireEvent.click(screen.getByRole('button', { name: 'Select all 120 matching results' }));
        expect(await screen.findByText('All 120 matching results selected')).toBeTruthy();

        fireEvent.change(screen.getByLabelText('Type'), { target: { value: 'Episode' } });
        await waitFor(() => expect(screen.queryByText('All 120 matching results selected')).toBeNull());
        expect(screen.getByText('0 selected')).toBeTruthy();
        expect(screen.getByText(/selection was cleared/)).toBeTruthy();
        expect(query).toHaveBeenCalled();
    });

    it('sends all-matching with its expected count so the server can refuse a change', async () => {
        vi.spyOn(api, 'queryItems').mockResolvedValue({ rows: many, total: 120, offset: 0, limit: 50 });
        const s = shared();
        render(<ExplorerView shared={s} />);
        await screen.findByText('Movie 0');
        fireEvent.click(screen.getByLabelText('Select this page'));
        fireEvent.click(await screen.findByRole('button', { name: 'Select all 120 matching results' }));
        fireEvent.click(await screen.findByRole('button', { name: 'Review cleanup…' }));
        expect(s.startCleanup).toHaveBeenCalledOnce();
        const request = (s.startCleanup as ReturnType<typeof vi.fn>).mock.calls[0][0];
        expect(request.type).toBe('Query');
        expect(request.expectedCount).toBe(120);
    });

    it('explicit selection persists across paging and sends ids only', async () => {
        vi.spyOn(api, 'queryItems').mockResolvedValue({ rows: many, total: 120, offset: 0, limit: 50 });
        const s = shared();
        render(<ExplorerView shared={s} />);
        await screen.findByText('Movie 0');
        fireEvent.click(screen.getByLabelText('Select Movie 3'));
        fireEvent.click(screen.getByRole('button', { name: 'Next' }));
        fireEvent.click(await screen.findByRole('button', { name: 'Review cleanup…' }));
        const request = (s.startCleanup as ReturnType<typeof vi.fn>).mock.calls[0][0];
        expect(request).toMatchObject({ type: 'Items', itemIds: ['id3'] });
    });

    it('offers no review while nothing is selected', async () => {
        vi.spyOn(api, 'queryItems').mockResolvedValue({ rows: many, total: 120, offset: 0, limit: 50 });
        render(<ExplorerView shared={shared()} />);
        await screen.findByText('Movie 0');
        expect((screen.getByRole('button', { name: 'Review cleanup…' }) as HTMLButtonElement).disabled).toBe(true);
    });

    it('shows an empty state and an error state', async () => {
        const query = vi.spyOn(api, 'queryItems').mockResolvedValue({ rows: [], total: 0, offset: 0, limit: 50 });
        const { unmount } = render(<ExplorerView shared={shared()} />);
        expect(await screen.findByText('Nothing matches these filters')).toBeTruthy();
        unmount();
        query.mockRejectedValue(new ApiError(500, 'INTERNAL_ERROR', 'Boom', 'corr-1'));
        render(<ExplorerView shared={shared()} />);
        expect(await screen.findByText('Boom')).toBeTruthy();
        expect(screen.getByText('corr-1')).toBeTruthy();
    });
});

describe('cleanup confirmation', () => {
    const open = async (dto: PlanDto) => {
        vi.spyOn(api, 'createPlan').mockResolvedValue(dto);
        const pending = { request: { type: 'Items' as const, itemIds: ['i'], includeSidecars: true }, description: 'x', nonce: 1 };
        render(<CleanupView shared={shared()} pending={pending} clearPending={vi.fn()} />);
        await screen.findByText(/Dry-run plan/);
    };

    it('lists exact file targets and never offers undo', async () => {
        await open(plan());
        expect(screen.getByText('/m/a.mkv')).toBeTruthy();
        expect(screen.queryByText(/undo|restore/i)).toBeNull();
    });

    it('a preview explains why it cannot run', async () => {
        await open(plan({ previewOnly: true }));
        expect((screen.getByRole('button', { name: 'Permanently delete…' }) as HTMLButtonElement).disabled).toBe(true);
        expect(screen.getByText(/This is a preview/)).toBeTruthy();
    });

    it('an expired plan cannot be confirmed', async () => {
        const dto = plan();
        dto.summary.expiresAtUtc = new Date(Date.now() - 1000).toISOString();
        await open(dto);
        expect((screen.getByRole('button', { name: 'Permanently delete…' }) as HTMLButtonElement).disabled).toBe(true);
        expect(screen.getAllByText(/This plan expired\./).length).toBeGreaterThan(0);
    });

    it('requires the acknowledgement, and sends the plan hash and one idempotency key', async () => {
        const execute = vi.spyOn(api, 'execute').mockRejectedValue(new ApiError(409, 'PLAN_EXPIRED', 'Expired.'));
        await open(plan());
        fireEvent.click(screen.getByRole('button', { name: 'Permanently delete…' }));
        const go = await screen.findByRole('button', { name: 'Delete permanently' }) as HTMLButtonElement;
        expect(go.disabled).toBe(true);
        fireEvent.click(screen.getByRole('checkbox', { name: /permanently deleted/ }));
        await waitFor(() => expect((screen.getByRole('button', { name: 'Delete permanently' }) as HTMLButtonElement).disabled).toBe(false));
        fireEvent.click(screen.getByRole('button', { name: 'Delete permanently' }));
        expect(await screen.findByText('Expired.')).toBeTruthy();
        fireEvent.click(screen.getByRole('button', { name: 'Delete permanently' }));
        await waitFor(() => expect(execute).toHaveBeenCalledTimes(2));
        expect(execute.mock.calls[0][1]).toBe('abcdef0123456789');
        expect(execute.mock.calls[0][2]).toBe(execute.mock.calls[1][2]);
    });

    it('a high-impact plan requires the exact phrase', async () => {
        await open(plan({ highImpact: true, confirmationPhrase: 'DELETE 1 ITEMS' }));
        fireEvent.click(screen.getByRole('button', { name: 'Permanently delete…' }));
        fireEvent.click(await screen.findByRole('checkbox', { name: /permanently deleted/ }));
        const input = screen.getByLabelText(/Type DELETE 1 ITEMS/);
        fireEvent.input(input, { target: { value: 'delete 1 items' } });
        await waitFor(() => expect((screen.getByRole('button', { name: 'Delete permanently' }) as HTMLButtonElement).disabled).toBe(true));
        fireEvent.input(input, { target: { value: 'DELETE 1 ITEMS' } });
        await waitFor(() => expect((screen.getByRole('button', { name: 'Delete permanently' }) as HTMLButtonElement).disabled).toBe(false));
    });
});

describe('components', () => {
    it('a disabled action states why in text linked to the button', () => {
        render(<Button why="Approve a root first.">Enable</Button>);
        const button = screen.getByRole('button', { name: 'Enable' }) as HTMLButtonElement;
        expect(button.disabled).toBe(true);
        expect(document.getElementById(button.getAttribute('aria-describedby')!)?.textContent).toBe('Approve a root first.');
    });

    it('permission errors say so and offer no pointless retry', () => {
        render(<ErrorState error={new ApiError(403, 'HTTP_403', 'Your account does not have administrator access to jellytrim.')} onRetry={vi.fn()} />);
        expect(screen.getByText('Access denied')).toBeTruthy();
        expect(screen.queryByRole('button', { name: 'Try again' })).toBeNull();
    });

    it('dialog focuses inside, closes on Escape and restores focus', async () => {
        const opener = document.createElement('button');
        document.body.appendChild(opener);
        opener.focus();
        const onClose = vi.fn();
        const { unmount } = render(<Dialog title="T" onClose={onClose}><input data-autofocus aria-label="first" /></Dialog>);
        await waitFor(() => expect(document.activeElement).toBe(screen.getByLabelText('first')));
        expect(screen.getByRole('dialog').getAttribute('aria-modal')).toBe('true');
        fireEvent.keyDown(screen.getByRole('dialog'), { key: 'Escape' });
        expect(onClose).toHaveBeenCalled();
        unmount();
        await waitFor(() => expect(document.activeElement).toBe(opener));
        opener.remove();
    });
});

describe('api client', () => {
    beforeEach(() => {
        (globalThis as unknown as { ApiClient: unknown }).ApiClient = { accessToken: () => 'tok', getUrl: (p: string) => `http://x/${p}` };
    });

    it('sends the token in the header, never in the URL', async () => {
        const fetchMock = vi.fn().mockResolvedValue(new Response('{"x":1}', { status: 200 }));
        vi.stubGlobal('fetch', fetchMock);
        await api.status();
        expect(fetchMock.mock.calls[0][0]).toBe('http://x/Jellytrim/Status');
        expect(fetchMock.mock.calls[0][1].headers.Authorization).toBe('MediaBrowser Token="tok"');
        vi.unstubAllGlobals();
    });

    it('maps failures to stable errors', async () => {
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{"code":"PLAN_EXPIRED","message":"Gone","correlationId":"c"}', { status: 410 })));
        await expect(api.status()).rejects.toMatchObject({ code: 'PLAN_EXPIRED', status: 410, correlationId: 'c' });
        vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('', { status: 403 })));
        await expect(api.status()).rejects.toMatchObject({ isPermission: true });
        vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('net')));
        await expect(api.status()).rejects.toMatchObject({ isOffline: true });
        vi.unstubAllGlobals();
    });
});

describe('filter pickers', () => {
    const pick = () => render(<div><MultiPick label="Library" options={[{ value: 'a', label: 'Movies' }]} selected={[]} onChange={vi.fn()} /><button>elsewhere</button></div>);
    const details = () => screen.getByText('Library: all').closest('details') as HTMLDetailsElement;

    it('closes when clicking outside, but not inside', () => {
        pick();
        details().open = true;
        fireEvent.pointerDown(screen.getByText('Movies'));
        expect(details().open).toBe(true);
        fireEvent.pointerDown(screen.getByText('elsewhere'));
        expect(details().open).toBe(false);
    });

    it('closes on Escape and when focus moves away', () => {
        pick();
        details().open = true;
        fireEvent.keyDown(document, { key: 'Escape' });
        expect(details().open).toBe(false);
        details().open = true;
        (screen.getByText('elsewhere') as HTMLButtonElement).focus();
        expect(details().open).toBe(false);
    });
});
