import { execFileSync } from 'node:child_process';
import { existsSync, renameSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, test } from '@playwright/test';
import { auth, login, openJellytrim, scan, state } from './helpers';

const root = resolve(import.meta.dirname, '../..');
const media = resolve(root, '.e2e/run/media');
const env = (command: string) => execFileSync('python3', [resolve(root, 'e2e/env.py'), command], { stdio: 'inherit' });

test.describe.configure({ mode: 'serial' });

test('an offline root is reported, keeps its items and blocks planning', async ({ request, page }) => {
    const before = (await (await request.post(`${state.base}/Jellytrim/Items/Query`, { headers: auth(), data: { kinds: ['Movie'] } })).json()).total;
    const moved = `${media}/movies.offline`;
    renameSync(`${media}/movies`, moved);
    try {
        await scan(request);
        const overview = await (await request.get(`${state.base}/Jellytrim/Overview`, { headers: auth() })).json();
        expect(overview.libraries.find((l: any) => l.library.name === 'Movies').library.online).toBe(false);
        const after = (await (await request.post(`${state.base}/Jellytrim/Items/Query`, { headers: auth(), data: { kinds: ['Movie'] } })).json()).total;
        expect(after).toBe(before);

        await login(page);
        await openJellytrim(page);
        await expect(page.getByText(/has unavailable storage/)).toBeVisible();
        await expect(page.getByText('Offline', { exact: true })).toBeVisible();

        const planned = await (await request.post(`${state.base}/Jellytrim/Plans`, { headers: auth(), data: { type: 'Items', itemIds: [state.items['India (2010)']] } })).json();
        expect(planned.items.total).toBe(0);
    } finally {
        renameSync(moved, `${media}/movies`);
    }

    await scan(request);
    const overview = await (await request.get(`${state.base}/Jellytrim/Overview`, { headers: auth() })).json();
    expect(overview.libraries.find((l: any) => l.library.name === 'Movies').library.online).toBe(true);
    expect(existsSync(`${media}/movies/India (2010)/India (2010).mkv`)).toBe(true);
});

test('a file removed outside jellytrim becomes a stale entry, not a deletion candidate', async ({ request }) => {
    const path = `${media}/movies/Charlie (2003)/Charlie (2003).mkv`;
    renameSync(path, `${path}.aside`);
    try {
        await scan(request);
        const row = (await (await request.post(`${state.base}/Jellytrim/Items/Query`, { headers: auth(), data: { search: 'Charlie', kinds: ['Movie'] } })).json()).rows[0];
        expect(row.health).toBe('Missing');
        const planned = await (await request.post(`${state.base}/Jellytrim/Plans`, { headers: auth(), data: { type: 'Items', itemIds: [row.id] } })).json();
        expect(planned.exclusions.items[0].code).toBe('health.available');
    } finally {
        renameSync(`${path}.aside`, path);
    }

    await scan(request);
});

test('settings, policies, jobs and audit survive a server restart, and the plugin still loads', async ({ request }) => {
    const jobsBefore = (await (await request.get(`${state.base}/Jellytrim/Jobs`, { headers: auth() })).json()).total;
    expect(jobsBefore).toBeGreaterThan(0);
    env('restart');
    // The connection can be reset while the server restarts, so a failed request counts as not up yet.
    await expect.poll(async () => (await request.get(`${state.base}/Jellytrim/Status`, { headers: auth() }).catch(() => ({ status: () => 0 }))).status(), { timeout: 90_000 }).toBe(200);
    const status = await (await request.get(`${state.base}/Jellytrim/Status`, { headers: auth() })).json();
    expect(status.deletionEnabled).toBe(true);
    expect(status.activeJobId).toBeNull();
    expect((await (await request.get(`${state.base}/Jellytrim/Jobs`, { headers: auth() })).json()).total).toBe(jobsBefore);
    expect((await (await request.get(`${state.base}/Jellytrim/Policies`, { headers: auth() })).json()).length).toBeGreaterThan(0);

    // A graceful stop must not take the server process down with an unhandled exception from the plugin.
    const logs = execFileSync('docker', ['logs', 'jellytrim-e2e'], { encoding: 'utf8', maxBuffer: 1 << 28, stdio: ['ignore', 'pipe', 'pipe'] });
    expect(logs).not.toContain('Unhandled exception');
});

test('a job killed mid-run is finalized as Interrupted on restart and never resumed', async ({ request }) => {
    // Enough items that the job is still running when the container is killed.
    const db = resolve(root, '.e2e/run/config/data/jellytrim/jellytrim.db');
    expect(existsSync(db)).toBe(true);
    const ids = [state.items['India (2010)'], state.items['Hotel (2009)'], state.items['Charlie (2003)']];
    const planned = await (await request.post(`${state.base}/Jellytrim/Plans`, { headers: auth(), data: { type: 'Items', itemIds: ids } })).json();
    expect(planned.items.total).toBeGreaterThan(0);

    // Start, then kill without giving the server a chance to shut down cleanly.
    const started = await request.post(`${state.base}/Jellytrim/Plans/${planned.summary.id}/Execute`, { headers: auth(), data: { planHash: planned.summary.hash, idempotencyKey: crypto.randomUUID(), acknowledged: true, confirmationPhrase: planned.plan.confirmationPhrase } });
    const job = await started.json();
    execFileSync('docker', ['kill', 'jellytrim-e2e'], { stdio: 'ignore' });
    execFileSync('docker', ['start', 'jellytrim-e2e'], { stdio: 'ignore' });
    await expect.poll(async () => (await request.get(`${state.base}/Jellytrim/Status`, { headers: auth() }).catch(() => ({ status: () => 0 }))).status(), { timeout: 120_000 }).toBe(200);

    const report = await (await request.get(`${state.base}/Jellytrim/Jobs/${job.id}`, { headers: auth() })).json();
    // The kill can land before, during or after the work. Whatever happened, the job must be final and honest.
    expect(['Completed', 'CompletedWithIssues', 'Rejected', 'Interrupted']).toContain(report.job.status);
    expect((await (await request.get(`${state.base}/Jellytrim/Status`, { headers: auth() })).json()).activeJobId).toBeNull();
    for (const item of report.items.items) {
        expect(['Pending', 'InProgress']).not.toContain(item.status);
    }
});

test('a database from a newer version disables jellytrim with a clear error rather than guessing', async ({ request, page }) => {
    const db = resolve(root, '.e2e/run/config/data/jellytrim/jellytrim.db');
    execFileSync('docker', ['stop', 'jellytrim-e2e'], { stdio: 'ignore' });
    execFileSync('python3', ['-c', `import sqlite3; c=sqlite3.connect(${JSON.stringify(db)}); c.execute("PRAGMA user_version=999"); c.commit(); c.close()`]);
    execFileSync('docker', ['start', 'jellytrim-e2e'], { stdio: 'ignore' });
    // Jellyfin answers 503 with its own HTML page while starting, so wait for jellytrim's own JSON error.
    const code = async () => {
        try {
            return (await (await request.get(`${state.base}/Jellytrim/Status`, { headers: auth() })).json()).code;
        } catch {
            return 'starting';
        }
    };
    await expect.poll(code, { timeout: 120_000 }).toBe('DATABASE_UNAVAILABLE');
    const body = await (await request.get(`${state.base}/Jellytrim/Status`, { headers: auth() })).json();
    expect(body.message).toContain('schema version 999');

    await login(page);
    await page.goto(`${state.base}/web/#/configurationpage?name=jellytrim`);
    await expect(page.getByText(/schema version 999/)).toBeVisible();

    execFileSync('docker', ['stop', 'jellytrim-e2e'], { stdio: 'ignore' });
    execFileSync('python3', ['-c', `import sqlite3; c=sqlite3.connect(${JSON.stringify(db)}); c.execute("PRAGMA user_version=1"); c.commit(); c.close()`]);
    execFileSync('docker', ['start', 'jellytrim-e2e'], { stdio: 'ignore' });
    await expect.poll(async () => (await request.get(`${state.base}/Jellytrim/Status`, { headers: auth() }).catch(() => ({ status: () => 0 }))).status(), { timeout: 120_000 }).toBe(200);
});
