import { defineConfig } from '@playwright/test';

// Point at a locally running, isolated Concierge.Web instance. The browser
// tests never start a host against a person's real Concierge data directory.
export default defineConfig({
  testDir: '.',
  testMatch: '*.spec.mjs',
  timeout: 30_000,
  use: {
    baseURL: process.env.CONCIERGE_WEB_TEST_URL || 'http://127.0.0.1:5317',
    browserName: 'chromium',
    channel: 'chrome',
    trace: 'retain-on-failure',
  },
  reporter: 'list',
});
