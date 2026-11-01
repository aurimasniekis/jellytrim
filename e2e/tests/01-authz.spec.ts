import { expect, test } from '@playwright/test';
import { auth, state } from './helpers';

const ID = '00000000000000000000000000000001';
const ROUTES: [string, string][] = [
    ['GET', 'Status'], ['GET', 'Overview'], ['GET', 'Trends'], ['GET', 'Users'], ['POST', 'Items/Query'], ['GET', `Items/${ID}`],
    ['GET', 'Policies'], ['POST', 'Policies'], ['PUT', `Policies/${ID}`], ['DELETE', `Policies/${ID}`], ['POST', `Policies/${ID}/Evaluate`],
    ['POST', 'Recommendations'], ['GET', 'Protections'], ['POST', 'Protections'], ['DELETE', `Protections/${ID}`],
    ['GET', 'Settings'], ['PUT', 'Settings'], ['POST', 'Scans'], ['GET', 'Scans/Current'], ['POST', 'Plans'], ['GET', `Plans/${ID}`],
    ['POST', `Plans/${ID}/Execute`], ['GET', 'Jobs'], ['GET', `Jobs/${ID}`], ['POST', `Jobs/${ID}/Cancel`], ['GET', 'Audit'],
];

test.describe('authorization on the real server', () => {
    for (const [method, route] of ROUTES) {
        test(`${method} ${route}: anonymous 401, non-admin 403`, async ({ request }) => {
            const options = (headers: Record<string, string>) => ({ method, headers: { ...headers, 'Content-Type': 'application/json' }, data: method === 'GET' || method === 'DELETE' ? undefined : '{}' });
            expect((await request.fetch(`${state.base}/Jellytrim/${route}`, options({}))).status()).toBe(401);
            expect((await request.fetch(`${state.base}/Jellytrim/${route}`, options(auth(state.nonAdminToken)))).status()).toBe(403);
        });
    }

    test('an invalid token is rejected', async ({ request }) => {
        expect((await request.get(`${state.base}/Jellytrim/Status`, { headers: auth('not-a-real-token') })).status()).toBe(401);
    });

    test('an API key can read but cannot enable deletion or execute', async ({ request }) => {
        expect((await request.get(`${state.base}/Jellytrim/Status`, { headers: auth(state.apiKey) })).status()).toBe(200);
        const settings = await request.put(`${state.base}/Jellytrim/Settings`, { headers: auth(state.apiKey), data: { settings: { deletionEnabled: true }, expectedRevision: 0 } });
        expect(settings.status()).toBe(403);
        expect((await settings.json()).code).toBe('API_KEY_NOT_ALLOWED');
        const execute = await request.post(`${state.base}/Jellytrim/Plans/${ID}/Execute`, { headers: auth(state.apiKey), data: { planHash: 'x', idempotencyKey: 'k', acknowledged: true } });
        expect(execute.status()).toBe(403);
        expect((await execute.json()).code).toBe('API_KEY_NOT_ALLOWED');
    });

    test('an API key cannot weaken protections or policies', async ({ request }) => {
        for (const [method, route] of [['DELETE', `Protections/${ID}`], ['DELETE', `Policies/${ID}`], ['PUT', `Policies/${ID}`]]) {
            const response = await request.fetch(`${state.base}/Jellytrim/${route}`, { method, headers: { ...auth(state.apiKey), 'Content-Type': 'application/json' }, data: method === 'PUT' ? { policy: { name: 'x' }, expectedRevision: 1 } : undefined });
            expect(response.status(), route).toBe(403);
        }
    });

    test('the plugin leaves the server OpenAPI document working', async ({ request }) => {
        const document = await request.get(`${state.base}/api-docs/openapi.json`);
        expect(document.status()).toBe(200);
        expect((await document.json()).paths['/Users']).toBeTruthy();
    });

    test('errors use the stable shape with a correlation id, and bounds are enforced', async ({ request }) => {
        const tooBig = await request.post(`${state.base}/Jellytrim/Items/Query`, { headers: auth(), data: { limit: 100000 } });
        expect(tooBig.status()).toBe(400);
        const body = await tooBig.json();
        expect(body.code).toBe('VALIDATION_FAILED');
        expect(body.correlationId).toBeTruthy();
        const notFound = await request.get(`${state.base}/Jellytrim/Plans/${ID}`, { headers: { ...auth(), 'X-Correlation-Id': 'abc-123' } });
        expect(notFound.status()).toBe(404);
        expect((await notFound.json()).correlationId).toBe('abc-123');
        expect(notFound.headers()['x-correlation-id']).toBe('abc-123');
    });

    test('plugin assets are public but hold no data', async ({ request }) => {
        const script = await request.get(`${state.base}/web/ConfigurationPage?name=jellytrim.js`);
        expect(script.status()).toBe(200);
        const text = await script.text();
        expect(text).not.toContain(state.token);
        expect(text).not.toContain('/media/');
    });

    test('the non-admin dashboard route shows no jellytrim content', async ({ request }) => {
        const pages = await request.get(`${state.base}/web/ConfigurationPages`, { headers: auth(state.nonAdminToken) });
        expect(pages.status()).toBe(403);
    });
});
