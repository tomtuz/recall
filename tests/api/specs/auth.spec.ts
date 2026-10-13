import { test, expect, registerAndLogin } from '../fixtures';

test('auth: register, login, rotate refresh cookie and reject token replay', async ({ request }) => {
  const session = await registerAndLogin(request);
  expect((await request.get('/api/v1/me/dashboard', { headers: session.headers })).status()).toBe(200);

  // Login populated this request context's cookie jar; no token body is needed.
  const refresh = await request.post('/api/v1/auth/refresh');
  expect(refresh.status()).toBe(200);
  const rotated = await refresh.json();
  expect(rotated.refreshToken).toBe('');
  const cookie = (await request.storageState()).cookies.find(c => c.name === 'recall_refresh');
  expect(Boolean(cookie?.httpOnly)).toBe(true);
  expect(Boolean(cookie?.value && cookie.value !== session.refreshToken)).toBe(true);
  expect((await request.get('/api/v1/me/dashboard', {
    headers: { Authorization: `Bearer ${rotated.accessToken}` },
  })).status()).toBe(200);

  const replay = await request.post('/api/v1/auth/refresh', { data: { refreshToken: session.refreshToken } });
  expect(replay.status()).toBe(401);
  expect((await replay.json()).errors[0].code).toBe('REFRESH_TOKEN_REUSED');
  expect((await request.post('/api/v1/auth/refresh')).status()).toBe(401);
});

test('auth: invalid registration is 400 and unknown login is 401', async ({ request }) => {
  const invalid = await request.post('/api/v1/auth/register', {
    data: { email: 'invalid', password: '', displayName: '' },
  });
  expect(invalid.status()).toBe(400);
  expect((await invalid.json()).errors[0].code).toBe('VALIDATION_ERROR');
  const login = await request.post('/api/v1/auth/login', {
    data: { email: 'missing@example.test', password: '' },
  });
  expect(login.status()).toBe(401);
  expect((await login.json()).errors[0].code).toBe('INVALID_CREDENTIALS');
});
