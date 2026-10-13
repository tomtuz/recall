import { defineConfig } from '@playwright/test';

const baseURL = 'http://localhost:15080';

export default defineConfig({
  testDir: './specs',
  fullyParallel: true,
  workers: 2,
  retries: 0,
  timeout: 30_000,
  reporter: [['list'], ['html', { open: 'never' }]],
  use: { baseURL, trace: 'off' },
  webServer: {
    cwd: __dirname,
    command: 'dotnet run --project ../../Recall.Api --urls http://localhost:15080',
    url: `${baseURL}/api/v1/courses?pageSize=1`,
    timeout: 120_000,
    reuseExistingServer: process.env.RECALL_REUSE_API === '1',
    env: { ASPNETCORE_ENVIRONMENT: 'Development' },
  },
});
