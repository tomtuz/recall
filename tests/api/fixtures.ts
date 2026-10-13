import { randomUUID } from 'node:crypto';
import { test as base, expect, type APIRequestContext } from '@playwright/test';

export { expect };

export async function registerAndLogin(request: APIRequestContext) {
  const credentials = {
    email: `playwright-${randomUUID()}@example.test`,
    password: randomUUID(),
    displayName: 'Playwright API test',
  };
  const registration = await request.post('/api/v1/auth/register', { data: credentials });
  expect(registration.status()).toBe(201);
  const registered = await registration.json();
  expect(registered.user.email).toBe(credentials.email);
  expect(registered.user.role).toBe('Contributor');

  const login = await request.post('/api/v1/auth/login', { data: credentials });
  expect(login.status()).toBe(200);
  const session = await login.json();
  expect(session.user.id).toBe(registered.user.id);
  expect(typeof session.accessToken === 'string' && session.accessToken.length > 0).toBe(true);
  return { ...session, headers: { Authorization: `Bearer ${session.accessToken}` } };
}

type Resources = {
  headers: Record<string, string>;
  create: (path: string, data: object, kind: 'courses' | 'topics' | 'cards') => Promise<any>;
};

export const test = base.extend<{ resources: Resources }>({
  resources: async ({ request }, use) => {
    const { headers } = await registerAndLogin(request);
    const cleanup: string[] = [];
    try {
      await use({
        headers,
        async create(path, data, kind) {
          const response = await request.post(path, { headers, data });
          expect(response.status()).toBe(201);
          const body = await response.json();
          expect(Number.isInteger(body.id) && body.id > 0).toBe(true);
          cleanup.push(`/api/v1/${kind}/${body.id}`);
          expect(response.headers().location).toBe(`/api/v1/${kind}/${body.id}`);
          return body;
        },
      });
    } finally {
      // Children must be removed before parents; attempt every cleanup even on failure.
      const failures: string[] = [];
      for (const path of cleanup.reverse()) {
        try {
          const response = await request.delete(path, { headers });
          if (![204, 404].includes(response.status())) failures.push(`${path}: ${response.status()}`);
        } catch {
          failures.push(`${path}: request failed`);
        }
      }
      expect(failures, 'Test resource cleanup').toEqual([]);
    }
  },
});

export function courseData(title = 'Playwright course') {
  return { code: `PW-${randomUUID().slice(0, 20)}`, title, visibility: 'Private' };
}
