import AxeBuilder from '@axe-core/playwright';
import { existsSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, test } from '@playwright/test';
import { auth, login, openJellytrim, scan, state } from './helpers';

const media = resolve(import.meta.dirname, '../../.e2e/run/media');

test.describe.configure({ mode: 'serial' });

test.beforeAll(async ({ request }) => { await scan(request); });

test.beforeEach(async ({ page }) => {
    await login(page);
    await openJellytrim(page);
});

const axe = async (page: import('@playwright/test').Page) => {
    const results = await new AxeBuilder({ page }).include('#jellytrim-root').withTags(['wcag2a', 'wcag2aa']).analyze();
    expect(results.violations.map(v => `${v.id}: ${v.nodes[0]?.html}`)).toEqual([]);
};

test('starts read-only and reports real sizes, volumes and definitions', async ({ page }) => {
    await expect(page.getByText('Read-only', { exact: true })).toBeVisible();
    await expect(page.getByRole('cell', { name: /Movies/ }).first()).toBeVisible();
    await expect(page.getByText('Free space (observed)')).toBeVisible();
    await page.getByText('What do these measurements mean?').click();
    await expect(page.getByText(/each physical file counted once/i)).toBeVisible();
    await expect(page.getByText(/no history from before then/)).toBeVisible();
    await axe(page);
});

test('malicious metadata renders as text and never executes', async ({ page }) => {
    await page.getByRole('tab', { name: 'Media explorer' }).click();
    await expect(page.getByRole('button', { name: state.xssName })).toBeVisible();
    await page.getByRole('button', { name: state.xssName }).click();
    await expect(page.getByRole('dialog')).toBeVisible();
    expect(await page.evaluate(() => (window as unknown as { __xss?: number }).__xss)).toBeUndefined();
    expect(await page.locator('#jellytrim-root img[src="x"]').count()).toBe(0);
});

test('explorer distinguishes watched by someone from watched by everyone', async ({ page }) => {
    await page.getByRole('tab', { name: 'Media explorer' }).click();
    await page.getByText('Users: 3').click();
    await page.getByRole('checkbox', { name: 'e2e-admin' }).uncheck();
    await page.getByText('Users: 2').click();
    await page.getByLabel('Watch state').selectOption('Played');
    await expect(page.getByRole('button', { name: 'Alpha (2001)' })).toBeVisible();
    await expect(page.getByRole('button', { name: 'Charlie (2003)' })).toHaveCount(0);
    await page.getByLabel('Must match').selectOption('Any');
    await expect(page.getByRole('button', { name: 'Charlie (2003)' })).toBeVisible();
    await page.getByLabel('Watch state').selectOption('Partial');
    await expect(page.getByRole('button', { name: 'Golf (2008)' })).toBeVisible();
    await expect(page.getByText(/bob: In progress/)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Alpha (2001)' })).toHaveCount(0);
});

test('hierarchy, sorting and the detail panel with per-user state', async ({ page }) => {
    await page.getByRole('tab', { name: 'Media explorer' }).click();
    await page.getByLabel('Type').selectOption('Series');
    await page.getByRole('button', { name: 'Open Show One' }).click();
    await page.getByRole('button', { name: 'Open Season 1' }).click();
    await expect(page.getByRole('button', { name: /Show One S01E01/ })).toBeVisible();
    await page.getByRole('navigation', { name: 'Location' }).getByRole('button', { name: 'All' }).click();
    await page.getByLabel('Type').selectOption('Movie');
    await page.getByRole('button', { name: 'Foxtrot (2007)' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByText('/media/movies/Foxtrot (2007)/Foxtrot (2007).mkv')).toBeVisible();
    await expect(dialog.getByText(/bytes · allocated/)).toBeVisible();
    await expect(dialog.getByRole('cell', { name: '★ Yes' })).toBeVisible();
    await expect(dialog.getByText(/can set it by hand/)).toBeVisible();
    await axe(page);
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
});

test('select page versus select all matching; changing a filter clears the bulk selection', async ({ page }) => {
    await page.getByRole('tab', { name: 'Media explorer' }).click();
    await page.getByLabel('Type').selectOption('');
    await page.getByLabel('Select this page').check();
    await expect(page.getByText(/^\d+ selected$/)).toBeVisible();
    const button = page.getByRole('button', { name: /Select all \d+ matching results/ });
    if (await button.isVisible()) {
        await button.click();
        await expect(page.getByText(/All \d+ matching results selected/)).toBeVisible();
        await page.getByLabel('Type').selectOption('Movie');
        await expect(page.getByText('0 selected')).toBeVisible();
        await expect(page.getByText(/“all matching” selection was cleared/)).toBeVisible();
    }
});

test('creates a policy through the editor; an empty user set cannot be saved', async ({ page }) => {
    await page.getByRole('tab', { name: 'Policies & protection' }).click();
    await page.getByRole('button', { name: 'New policy' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByLabel('Name').fill('Watched by ann and bob');
    for (const user of ['e2e-admin', 'ann', 'bob']) await dialog.getByRole('group', { name: 'Participating users' }).getByRole('checkbox', { name: user }).uncheck();
    await expect(dialog.getByText(/An empty set can never mean/)).toBeVisible();
    await expect(dialog.getByRole('button', { name: 'Save policy' })).toBeDisabled();
    for (const user of ['ann', 'bob']) await dialog.getByRole('group', { name: 'Participating users' }).getByRole('checkbox', { name: user }).check();
    await dialog.getByLabel(/Last played over/).fill('');
    await dialog.getByLabel(/Keep items added/).fill('');
    await axe(page);
    await dialog.getByRole('button', { name: 'Save policy' }).click();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(page.getByText('Watched by ann and bob')).toBeVisible();
    await expect(page.getByText(/of 19 eligible/)).toBeVisible();
    await page.getByText('Why items were held back').click();
    await expect(page.getByText(/Favorites: 1/)).toBeVisible();
    await expect(page.getByText(/In progress: 1/)).toBeVisible();
});

test('recommendations explain themselves and hard-linked content is not ranked', async ({ page }) => {
    await page.getByRole('tab', { name: 'Cleanup' }).click();
    await page.getByLabel(/Target to reclaim/).fill('0.001');
    await page.getByRole('button', { name: 'Find recommendations' }).click();
    await expect(page.getByText(/Found approximately/)).toBeVisible();
    await expect(page.getByRole('cell', { name: /Marked played by all 2 selected/ }).first()).toBeVisible();
    await expect(page.getByRole('cell', { name: 'Linked (2012)' })).toHaveCount(0);
});

test('read-only: a preview shows exact targets and exclusions but cannot run', async ({ page, request }) => {
    await page.getByRole('tab', { name: 'Cleanup' }).click();
    await page.getByRole('button', { name: 'Generate plan for all eligible' }).click();
    await expect(page.getByText(/Dry-run plan/)).toBeVisible();
    await expect(page.getByText(/this is a preview only/i)).toBeVisible();
    await expect(page.getByRole('button', { name: 'Permanently delete…' })).toBeDisabled();
    await expect(page.getByText('This is a preview. Enable deletion in Settings, then generate a new plan.')).toBeVisible();
    await expect(page.getByText(/Symlinked \(2013\)/)).toBeVisible();
    await expect(page.getByText(/does not delete/)).toBeVisible();
    await axe(page);

    const policies = await (await request.get(`${state.base}/Jellytrim/Policies`, { headers: auth() })).json();
    const planned = await (await request.post(`${state.base}/Jellytrim/Plans`, { headers: auth(), data: { type: 'Policy', policyId: policies[0].policy.id } })).json();
    const refused = await request.post(`${state.base}/Jellytrim/Plans/${planned.summary.id}/Execute`, { headers: auth(), data: { planHash: planned.summary.hash, idempotencyKey: 'read-only', acknowledged: true, confirmationPhrase: planned.plan.confirmationPhrase } });
    expect(refused.status()).toBe(409);
    expect((await refused.json()).code).toBe('DELETION_DISABLED');
    expect(existsSync(resolve(media, 'movies/Alpha (2001)/Alpha (2001).mkv'))).toBe(true);
});

test('budget warning is advisory and appears on the overview', async ({ page }) => {
    await page.getByRole('tab', { name: 'Settings' }).click();
    await page.getByLabel(/Budget for Movies/).fill('0.001');
    await page.getByRole('button', { name: 'Save settings' }).click();
    await expect(page.getByText('Settings saved.')).toBeVisible();
    await axe(page);
    await page.getByRole('tab', { name: 'Overview' }).click();
    await expect(page.getByText(/over its budget/)).toBeVisible();
    await expect(page.getByText(/never trigger deletion/)).toBeVisible();
    await expect(page.getByText('Read-only', { exact: true })).toBeVisible();
});

test('keyboard: tabs move with arrow keys', async ({ page }) => {
    await page.getByRole('tab', { name: 'Overview' }).focus();
    await page.keyboard.press('ArrowRight');
    await expect(page.getByRole('tab', { name: 'Media explorer' })).toBeFocused();
    await expect(page.getByRole('tab', { name: 'Media explorer' })).toHaveAttribute('aria-selected', 'true');
});

test('stays usable at phone width', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 800 });
    const overflow = await page.evaluate(() => {
        const root = document.querySelector('#jellytrim-root')!;
        return root.scrollWidth - root.clientWidth;
    });
    expect(overflow).toBeLessThanOrEqual(1);
    await expect(page.getByRole('tab', { name: 'Overview' })).toBeVisible();
});
