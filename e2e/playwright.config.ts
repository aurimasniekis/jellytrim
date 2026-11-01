import { defineConfig } from '@playwright/test';

// Tests share one disposable server and its destructive state moves forward, so they run in file order, serially.
export default defineConfig({
    testDir: './tests',
    workers: 1,
    fullyParallel: false,
    timeout: 90_000,
    expect: { timeout: 15_000 },
    reporter: [['list']],
    use: {
        channel: process.env.CI ? undefined : 'chrome',
        viewport: { width: 1360, height: 900 },
        screenshot: 'only-on-failure',
        trace: 'retain-on-failure',
    },
});
