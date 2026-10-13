import { test, expect, courseData, registerAndLogin } from '../fixtures';

// Regression cases: they pin failure modes that previously slipped through
// (stale-token logout, NUL text, route variants).

test('logout revokes successors of the presented token', async ({ request }) => {
  const session = await registerAndLogin(request);

  // Rotate once: the presented token now has a live successor.
  const refresh = await request.post('/api/v1/auth/refresh', {
    data: { refreshToken: session.refreshToken },
  });
  expect(refresh.status()).toBe(200);
  const successor = await refresh.json();

  // Logout presents the OLD (already rotated) token.
  const logout = await request.post('/api/v1/auth/logout', {
    headers: session.headers,
    data: { refreshToken: session.refreshToken },
  });
  expect(logout.status()).toBe(204);

  // The successor must be revoked too.
  const retry = await request.post('/api/v1/auth/refresh', {
    data: { refreshToken: successor.refreshToken },
  });
  expect(retry.status()).toBe(401);
});

test('NUL input returns 400 rather than 500', async ({ request, resources }) => {
  const response = await request.post('/api/v1/courses', {
    headers: resources.headers,
    data: { ...courseData(), title: 'A\u0000B' },
  });
  expect(response.status()).toBe(400);
  expect((await response.json()).errors[0].code).toBe('VALIDATION_ERROR');
});

test('course description over 2000 characters returns 400', async ({ request, resources }) => {
  const response = await request.post('/api/v1/courses', {
    headers: resources.headers,
    data: { ...courseData(), description: 'x'.repeat(2001) },
  });
  expect(response.status()).toBe(400);
  expect((await response.json()).errors[0].code).toBe('VALIDATION_ERROR');
});

test('import row with a hint over 500 characters returns 400 ROW_INVALID', async ({ request, resources }) => {
  const course = await resources.create('/api/v1/courses', courseData(), 'courses');
  const topic = await resources.create(
    `/api/v1/courses/${course.id}/topics`, { title: 'Import parity topic' }, 'topics');
  const response = await request.post(`/api/v1/topics/${topic.id}/cards/import`, {
    headers: resources.headers,
    data: { cards: [{ question: 'Q', answer: 'A', hint: 'h'.repeat(501) }] },
  });
  expect(response.status()).toBe(400);
  const body = await response.json();
  expect(body.errors[0].code).toBe('ROW_INVALID');
  expect(body.errors[0].message).toContain('Row 1');
});

test('malformed JSON body returns the sanitized BAD_REQUEST envelope', async ({ request, resources }) => {
  const response = await request.fetch('/api/v1/courses', {
    method: 'POST',
    headers: { ...resources.headers, 'Content-Type': 'application/json' },
    data: '{not json',
  });
  expect(response.status()).toBe(400);
  const body = await response.json();
  expect(body.errors[0].code).toBe('BAD_REQUEST');
});

test('auth route variants retain the origin gate', async ({ request }) => {
  for (const path of [
    '/api/v1/auth/refresh',
    '/api/v1/auth/REFRESH',
    '/api/v1/auth/refresh/',
    '/api/v1/auth/LOGOUT',
  ]) {
    const response = await request.post(path, {
      headers: { Origin: 'https://untrusted.example' },
    });
    expect(response.status()).toBe(403);
    expect((await response.json()).errors[0].code).toBe('FORBIDDEN_ORIGIN');
  }
});