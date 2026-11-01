import { appendFileSync, existsSync, statSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, test, type APIRequestContext, type Page } from '@playwright/test';
import { auth, login, openJellytrim, scan, state } from './helpers';

const media = resolve(import.meta.dirname, '../../.e2e/run/media');
const movies = (rel: string) => resolve(media, 'movies', rel);

test.describe.configure({ mode: 'serial' });

const api = (request: APIRequestContext) => ({
    plan: async (itemIds: string[]) => (await request.post(`${state.base}/Jellytrim/Plans`, { headers: auth(), data: { type: 'Items', itemIds } })),
    run: async (plan: any, key = crypto.randomUUID()) => request.post(`${state.base}/Jellytrim/Plans/${plan.summary.id}/Execute`, { headers: auth(), data: { planHash: plan.summary.hash, idempotencyKey: key, acknowledged: true, confirmationPhrase: plan.plan.confirmationPhrase } }),
    job: async (id: string) => (await request.get(`${state.base}/Jellytrim/Jobs/${id}`, { headers: auth() })).json(),
    finished: async (id: string) => {
        await expect.poll(async () => (await (await request.get(`${state.base}/Jellytrim/Jobs/${id}`, { headers: auth() })).json()).job.status, { timeout: 30_000 }).not.toMatch(/Queued|Preflight|Running/);
        return (await request.get(`${state.base}/Jellytrim/Jobs/${id}`, { headers: auth() })).json();
    },
});

async function explorerSelect(page: Page, names: string[]) {
    await page.getByRole('tab', { name: 'Media explorer' }).click();
    for (const name of names) await page.getByRole('checkbox', { name: `Select ${name}` }).check();
    await page.getByRole('button', { name: 'Review cleanup…' }).click();
    await expect(page.getByText(/Dry-run plan/)).toBeVisible();
}

async function confirmDelete(page: Page) {
    await page.getByRole('button', { name: 'Permanently delete…' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText('This cannot be undone.')).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Delete permanently' })).toBeDisabled();
    await dialog.getByRole('checkbox').check();
    await dialog.getByRole('button', { name: 'Delete permanently' }).click();
}

test.beforeEach(async ({ page }) => {
    await login(page);
    await openJellytrim(page);
});

test('enabling deletion needs an approved root and the exact phrase', async ({ page }) => {
    await page.getByRole('tab', { name: 'Settings' }).click();
    await expect(page.getByRole('button', { name: 'Enable permanent deletion…' })).toBeDisabled();
    await expect(page.getByText('Approve at least one root first.')).toBeVisible();
    await page.getByRole('checkbox', { name: '/media/movies' }).check();
    await page.getByRole('checkbox', { name: '/media/tv' }).check();
    await page.getByRole('button', { name: 'Enable permanent deletion…' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('button', { name: 'Enable deletion' })).toBeDisabled();
    await dialog.getByRole('textbox').fill('enable permanent deletion');
    await expect(dialog.getByRole('button', { name: 'Enable deletion' })).toBeDisabled();
    await dialog.getByRole('textbox').fill('ENABLE PERMANENT DELETION');
    await dialog.getByRole('button', { name: 'Enable deletion' }).click();
    await expect(page.getByText('Deletion enabled', { exact: true })).toBeVisible();
});

test('roots that are not library locations are refused by the server', async ({ request }) => {
    const current = await (await request.get(`${state.base}/Jellytrim/Settings`, { headers: auth() })).json();
    const response = await request.put(`${state.base}/Jellytrim/Settings`, { headers: auth(), data: { settings: { ...current.settings, approvedRoots: ['/etc'] }, expectedRevision: current.revision } });
    expect(response.status()).toBe(400);
    expect((await response.json()).code).toBe('ROOT_NOT_LIBRARY_LOCATION');
});

test('settings use optimistic concurrency', async ({ request }) => {
    const current = await (await request.get(`${state.base}/Jellytrim/Settings`, { headers: auth() })).json();
    const stale = await request.put(`${state.base}/Jellytrim/Settings`, { headers: auth(), data: { settings: current.settings, expectedRevision: current.revision - 1 } });
    expect(stale.status()).toBe(409);
    expect((await stale.json()).code).toBe('REVISION_CONFLICT');
});

test('a stale plan is rejected and nothing is deleted', async ({ page }) => {
    await explorerSelect(page, ['Hotel (2009)', 'India (2010)']);
    const hotel = movies('Hotel (2009)/Hotel (2009).mkv');
    appendFileSync(hotel, 'file changed after planning');
    await confirmDelete(page);
    await expect(page.getByText('Rejected', { exact: true }).first()).toBeVisible();
    await expect(page.getByText(/changed since this plan was made. Nothing was deleted/)).toBeVisible();
    await expect(page.getByText('FILE_SIZE_CHANGED')).toBeVisible();
    expect(existsSync(hotel)).toBe(true);
    expect(existsSync(movies('India (2010)/India (2010).mkv'))).toBe(true);
});

test('plan scope equals executed scope: sidecars go, unattributable neighbors and extras stay', async ({ page, request }) => {
    await scan(request);
    await page.reload();
    await expect(page.getByRole('heading', { name: 'jellytrim', exact: true })).toBeVisible();
    await explorerSelect(page, ['Alpha (2001)']);
    for (const name of ['Alpha (2001).mkv', 'Alpha (2001).en.srt', 'poster.jpg']) await expect(page.getByText(`/media/movies/Alpha (2001)/${name}`)).toBeVisible();
    await expect(page.getByText(/neighboring entries stay on disk/)).toBeVisible();
    await page.getByText(/neighboring entries stay on disk/).click();
    await expect(page.getByText(/notes\.txt/)).toBeVisible();
    await confirmDelete(page);
    await expect(page.getByText('Completed', { exact: true }).first()).toBeVisible();
    await expect(page.getByText(/permanently deleted/).first()).toBeVisible();
    await expect(page.getByText('Before and after: observed free space')).toBeVisible();
    await expect(page.getByRole('button', { name: /undo|restore/i })).toHaveCount(0);

    for (const gone of ['Alpha (2001).mkv', 'Alpha (2001).en.srt', 'poster.jpg']) expect(existsSync(movies(`Alpha (2001)/${gone}`)), gone).toBe(false);
    expect(existsSync(movies('Alpha (2001)/notes.txt'))).toBe(true);
    expect(existsSync(movies('Alpha (2001)/extras/Behind the scenes.mkv'))).toBe(true);

    // Reconciled in the real server through the supported API, not just on disk.
    const record = await request.get(`${state.base}/Items/${state.items['Alpha (2001)']}?userId=${state.users.admin}`, { headers: auth() });
    expect(record.status()).toBe(404);
});

test('a movie alone in its folder has the folder removed once empty', async ({ request }) => {
    const client = api(request);
    const planned = await (await client.plan([state.items['Bravo (2002)']])).json();
    const job = await (await client.run(planned)).json();
    const done = await client.finished(job.id);
    expect(done.job.status).toBe('Completed');
    expect(existsSync(movies('Bravo (2002)'))).toBe(false);
    expect(existsSync(resolve(media, 'movies'))).toBe(true);
});

test('all versions of a movie go together and its record is removed', async ({ request }) => {
    const client = api(request);
    const planned = await (await client.plan([state.items['Echo (2005)']])).json();
    const item = planned.items.items[0];
    expect(item.files.filter((f: any) => f.role !== 'Sidecar').length).toBe(2);
    const done = await client.finished((await (await client.run(planned)).json()).id);
    expect(done.job.status).toBe('Completed');
    expect(existsSync(movies('Echo (2005)/Echo (2005) - 1080p.mkv'))).toBe(false);
    expect(existsSync(movies('Echo (2005)/Echo (2005) - 720p.mkv'))).toBe(false);
    expect((await request.get(`${state.base}/Items/${state.items['Echo (2005)']}?userId=${state.users.admin}`, { headers: auth() })).status()).toBe(404);
});

test('a hard-linked file: this path goes, the outside link keeps the data, and no space is claimed', async ({ request }) => {
    const client = api(request);
    const planned = await (await client.plan([state.items['Linked (2012)']])).json();
    expect(planned.plan.totals.estimatedReclaimableBytes).toBe(0);
    expect(planned.plan.warnings.join()).toContain('hard links');
    const seed = resolve(media, 'seed/Linked.mkv');
    const size = statSync(seed).size;
    const done = await client.finished((await (await client.run(planned)).json()).id);
    expect(done.job.status).toBe('Completed');
    expect(existsSync(movies('Linked (2012)/Linked (2012).mkv'))).toBe(false);
    expect(statSync(seed).size).toBe(size);
});

test('a symlink in the library is refused and its target stays', async ({ request }) => {
    const planned = await (await api(request).plan([state.items['Symlinked (2013)']])).json();
    expect(planned.items.total).toBe(0);
    expect(planned.exclusions.items[0].code).toBe('PATH_TargetIsLink');
    expect(existsSync(resolve(media, 'outside/Target.mkv'))).toBe(true);
});

test('favorited and in-progress items are excluded from explicit selections', async ({ request }) => {
    const planned = await (await api(request).plan([state.items['Foxtrot (2007)'], state.items['Golf (2008)']])).json();
    expect(planned.items.total).toBe(0);
    expect(planned.exclusions.items.map((e: any) => e.code).sort()).toEqual(['protect.favorite', 'protect.inProgress']);
});

test('protecting an item in the UI keeps it out of plans, and removing it allows planning again', async ({ page, request }) => {
    await page.getByRole('tab', { name: 'Media explorer' }).click();
    await page.getByRole('button', { name: 'Juliet (2011)' }).click();
    await page.getByRole('dialog').getByRole('button', { name: 'Protect this item' }).click();
    await expect(page.getByRole('dialog').getByRole('button', { name: 'Remove protection' })).toBeVisible();
    await page.keyboard.press('Escape');
    const client = api(request);
    const blocked = await (await client.plan([state.items['Juliet (2011)']])).json();
    expect(blocked.exclusions.items[0].code).toBe('protect.explicit');
    expect((await request.delete(`${state.base}/Jellytrim/Protections/${state.items['Juliet (2011)']}`, { headers: auth() })).status()).toBe(204);
    expect((await (await client.plan([state.items['Juliet (2011)']])).json()).items.total).toBe(1);
});

test('protection added after planning stops execution', async ({ request }) => {
    const client = api(request);
    const planned = await (await client.plan([state.items['Juliet (2011)']])).json();
    await request.post(`${state.base}/Jellytrim/Protections`, { headers: auth(), data: { targetId: state.items['Juliet (2011)'], targetType: 'Item' } });
    const done = await client.finished((await (await client.run(planned)).json()).id);
    expect(done.job.status).toBe('Rejected');
    expect(done.job.problems[0].code).toBe('protect.explicit');
    expect(existsSync(movies('Juliet (2011)/Juliet (2011).mkv'))).toBe(true);
    await request.delete(`${state.base}/Jellytrim/Protections/${state.items['Juliet (2011)']}`, { headers: auth() });
});

test('a user starting to watch after planning stops execution, read live from Jellyfin', async ({ request }) => {
    const client = api(request);
    const planned = await (await client.plan([state.items['Juliet (2011)']])).json();
    await request.post(`${state.base}/UserItems/${state.items['Juliet (2011)']}/UserData?userId=${state.users.ann}`, { headers: auth(), data: { PlaybackPositionTicks: 1000000 } });
    const done = await client.finished((await (await client.run(planned)).json()).id);
    expect(done.job.status).toBe('Rejected');
    expect(done.job.problems[0].code).toBe('protect.inProgress');
    await request.post(`${state.base}/UserItems/${state.items['Juliet (2011)']}/UserData?userId=${state.users.ann}`, { headers: auth(), data: { PlaybackPositionTicks: 0 } });
});

test('a plan cannot be reused, while a retry with the same key returns the same job', async ({ request }) => {
    const client = api(request);
    await scan(request);
    const planned = await (await client.plan([state.items['Juliet (2011)']])).json();
    const key = crypto.randomUUID();
    const first = await (await client.run(planned, key)).json();
    const retry = await (await client.run(planned, key)).json();
    expect(retry.id).toBe(first.id);
    await client.finished(first.id);
    const reuse = await client.run(planned);
    expect(reuse.status()).toBe(409);
    expect((await reuse.json()).code).toBe('PLAN_ALREADY_EXECUTED');
});

test('a wrong confirmation hash is refused', async ({ request }) => {
    const client = api(request);
    const planned = await (await client.plan([state.items['Hotel (2009)']])).json();
    const refused = await request.post(`${state.base}/Jellytrim/Plans/${planned.summary.id}/Execute`, { headers: auth(), data: { planHash: 'deadbeef', idempotencyKey: crypto.randomUUID(), acknowledged: true } });
    expect((await refused.json()).code).toBe('PLAN_HASH_MISMATCH');
});

test('complete-show policy: high-impact phrase, artwork and empty records go, unwatched show stays', async ({ page, request }) => {
    const showTwoEpisodes = ['Show Two S01E01', 'Show Two S01E02'];
    const created = await request.post(`${state.base}/Jellytrim/Policies`, { headers: auth(), data: { name: 'Finished shows', participatingUserIds: [state.users.ann, state.users.bob], watched: 'AllSelected', unit: 'CompleteShow', mediaKinds: ['Episode'] } });
    expect(created.status()).toBe(201);
    await page.reload();
    await page.getByRole('tab', { name: 'Cleanup' }).click();
    await page.getByLabel('Policy').selectOption({ label: 'Finished shows' });
    await page.getByRole('button', { name: 'Generate plan for all eligible' }).click();
    await expect(page.getByText(/Dry-run plan: Finished shows/)).toBeVisible();
    await expect(page.getByText('/media/tv/Show Two/tvshow.nfo')).toBeVisible();
    await expect(page.getByText(/Show One S01E01\.mkv/)).toHaveCount(0);

    await page.getByRole('button', { name: 'Permanently delete…' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('checkbox').check();
    await expect(dialog.getByRole('button', { name: 'Delete permanently' })).toBeDisabled();
    await dialog.getByLabel(/high-impact/).fill('DELETE 2 ITEMS');
    await dialog.getByRole('button', { name: 'Delete permanently' }).click();
    await expect(page.getByText('Completed', { exact: true }).first()).toBeVisible();

    expect(existsSync(resolve(media, 'tv/Show Two'))).toBe(false);
    for (const ep of showTwoEpisodes) expect((await request.get(`${state.base}/Items/${state.items[ep]}?userId=${state.users.admin}`, { headers: auth() })).status()).toBe(404);
    expect((await request.get(`${state.base}/Items/${state.items['Show Two']}?userId=${state.users.admin}`, { headers: auth() })).status()).toBe(404);
    expect(existsSync(resolve(media, 'tv/Show One/Season 01/Show One S01E01.mkv'))).toBe(true);
});

test('an audit trail records who did what', async ({ page }) => {
    await page.getByRole('tab', { name: 'History' }).click();
    await expect(page.getByRole('cell', { name: 'Completed' }).first()).toBeVisible();
    await expect(page.getByRole('cell', { name: 'Rejected' }).first()).toBeVisible();
    await expect(page.getByText('deletion.enabled')).toBeVisible();
    await expect(page.getByText('job.finished').first()).toBeVisible();
    await page.getByRole('button', { name: /Open report/ }).first().click();
    await expect(page.getByText('Per-item outcome')).toBeVisible();
});
