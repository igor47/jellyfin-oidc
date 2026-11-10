import { defineConfig } from '@playwright/test';

const phase = process.env.E2E_PHASE || 'adhoc';
const results = process.env.E2E_RESULTS || '/results';

export default defineConfig({
  testDir: './tests',
  // Phases share one server and build on each other's state: run in order,
  // one worker, no retries (a flaky gate is a failing gate).
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 180_000,
  expect: { timeout: 15_000 },
  outputDir: `${results}/artifacts-${phase}`,
  reporter: [
    ['list'],
    ['json', { outputFile: `${results}/${phase}.json` }],
    ['junit', { outputFile: `${results}/${phase}.junit.xml` }],
  ],
  use: {
    ignoreHTTPSErrors: true, // throwaway IdP certificate
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
  },
});
