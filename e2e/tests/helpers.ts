import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { expect, type APIRequestContext, type Page } from '@playwright/test';

export const state = JSON.parse(readFileSync(resolve(import.meta.dirname, '../../.e2e/state.json'), 'utf8')) as {
    base: string; token: string; nonAdminToken: string; apiKey: string; users: Record<string, string>; items: Record<string, string>; admin: { username: string; password: string }; xssName: string;
};

export const auth = (token: string = state.token) => ({ Authorization: `MediaBrowser Token="${token}"` });

export async function login(page: Page, username = state.admin.username, password = state.admin.password) {
    await page.goto(`${state.base}/web/#/login`);
    const manual = page.getByRole('button', { name: 'Manual Login' });
    await page.getByLabel('User').or(manual).first().waitFor();
    if (await manual.isVisible()) await manual.click();
    await page.getByLabel('User').fill(username);
    await page.getByLabel('Password').fill(password);
    await page.getByRole('button', { name: 'Sign In' }).click();
    await page.waitForURL(/home/, { timeout: 30_000 });
}

export async function openJellytrim(page: Page) {
    await page.goto(`${state.base}/web/#/configurationpage?name=jellytrim`);
    await expect(page.getByRole('heading', { name: 'jellytrim', exact: true })).toBeVisible();
}

export async function scan(request: APIRequestContext) {
    const started = await request.post(`${state.base}/Jellytrim/Scans`, { headers: auth() });
    expect([202, 409]).toContain(started.status());
    await expect.poll(async () => (await (await request.get(`${state.base}/Jellytrim/Scans/Current`, { headers: auth() })).json()).running, { timeout: 60_000 }).toBe(false);
}
