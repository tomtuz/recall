import { test, expect, courseData } from '../fixtures';

// Data-integrity and contract cases: each one failed before its fix
// (lost concurrent reviews, position overflow, untrimmed storage, silent filter fallback).

test('concurrent reviews of one card end in the same state as sequential reviews', async ({ request, resources }) => {
  const headers = resources.headers;
  const course = await resources.create('/api/v1/courses', courseData(), 'courses');
  const topic = await resources.create(`/api/v1/courses/${course.id}/topics`, { title: 'Race topic' }, 'topics');
  const cardsPath = `/api/v1/topics/${topic.id}/cards`;
  const sequential = await resources.create(cardsPath, { question: 'Sequential?', answer: 'Yes' }, 'cards');
  const concurrent = await resources.create(cardsPath, { question: 'Concurrent?', answer: 'Yes' }, 'cards');

  for (let i = 0; i < 5; i++) {
    const r = await request.post(`/api/v1/cards/${sequential.id}/review`, { headers, data: { rating: 'good' } });
    expect(r.status()).toBe(200);
  }
  const results = await Promise.all(Array.from({ length: 5 }, () =>
    request.post(`/api/v1/cards/${concurrent.id}/review`, { headers, data: { rating: 'good' } })));
  expect(results.map(r => r.status())).toEqual([200, 200, 200, 200, 200]);

  const pick = ({ state, ease, intervalDays, reps, lapses }) => ({ state, ease, intervalDays, reps, lapses });
  const expected = pick(await (await request.get(`/api/v1/cards/${sequential.id}`, { headers })).json());
  const actual = pick(await (await request.get(`/api/v1/cards/${concurrent.id}`, { headers })).json());
  expect(expected.reps).toBe(5);
  expect(actual).toEqual(expected);
});

test('automatic topic position never overflows', async ({ request, resources }) => {
  const course = await resources.create('/api/v1/courses', courseData(), 'courses');
  const topicsPath = `/api/v1/courses/${course.id}/topics`;
  await resources.create(topicsPath, { title: 'Last slot', position: 2147483647 }, 'topics');
  const automatic = await request.post(topicsPath, { headers: resources.headers, data: { title: 'No slot left' } });
  expect(automatic.status()).toBe(409);
  expect((await automatic.json()).errors[0].code).toBe('CONFLICT');
  const explicit = await resources.create(topicsPath, { title: 'Explicit slot', position: 5 }, 'topics');
  expect(explicit.position).toBe(5);
});

test('text is stored exactly as it was validated (trimmed)', async ({ request, resources }) => {
  const padded = `${' '.repeat(2100)}x  `;
  const course = await resources.create('/api/v1/courses', { ...courseData(), description: padded }, 'courses');
  expect(course.description).toBe('x');
  const topic = await resources.create(`/api/v1/courses/${course.id}/topics`, { title: 'T', summary: padded }, 'topics');
  expect(topic.summary).toBe('x');
  const card = await resources.create(`/api/v1/topics/${topic.id}/cards`, { question: 'Q', answer: 'A', tags: '  http  ' }, 'cards');
  expect(card.tags).toBe('http');
  const patched = await request.patch(`/api/v1/cards/${card.id}`, { headers: resources.headers, data: { tags: ' db ' } });
  expect((await patched.json()).tags).toBe('db');
});

test('unknown filter and sort values are 400, known ones are case-insensitive', async ({ request, resources }) => {
  const course = await resources.create('/api/v1/courses', courseData(), 'courses');
  const topic = await resources.create(`/api/v1/courses/${course.id}/topics`, { title: 'Filters' }, 'topics');
  const headers = resources.headers;
  const cards = `/api/v1/topics/${topic.id}/cards`;
  for (const [path, params] of [
    ['/api/v1/courses', { visibility: 'secret' }],
    ['/api/v1/courses', { sort: 'hacker' }],
    [cards, { state: 'bogus' }],
    [cards, { state: '2' }],
    [cards, { state: '-1' }],
  ] as const) {
    const response = await request.get(path, { headers, params });
    expect(response.status(), `${path} ${JSON.stringify(params)}`).toBe(400);
    expect((await response.json()).errors[0].code).toBe('VALIDATION_ERROR');
  }
  expect((await request.get('/api/v1/courses', { headers, params: { visibility: 'PRIVATE', sort: '-title' } })).status()).toBe(200);
  expect((await request.get(cards, { headers, params: { state: 'review' } })).status()).toBe(200);
});

test('import and password size caps reject oversized input without side effects', async ({ request, resources }) => {
  const course = await resources.create('/api/v1/courses', courseData(), 'courses');
  const topic = await resources.create(`/api/v1/courses/${course.id}/topics`, { title: 'Import cap' }, 'topics');
  const rows = Array.from({ length: 201 }, (_, i) => ({ question: `Q${i}`, answer: 'A' }));
  const tooMany = await request.post(`/api/v1/topics/${topic.id}/cards/import`, { headers: resources.headers, data: { cards: rows } });
  expect(tooMany.status()).toBe(400);
  const listed = await request.get(`/api/v1/topics/${topic.id}/cards`, { headers: resources.headers });
  expect((await listed.json()).meta.totalItems).toBe(0);

  const longPassword = await request.post('/api/v1/auth/register', {
    data: { email: `long-${Date.now()}@example.test`, password: 'p'.repeat(129), displayName: 'Long' },
  });
  expect(longPassword.status()).toBe(400);
});

test('401 responses carry a Bearer challenge', async ({ request }) => {
  const anonymous = await request.get('/api/v1/me/dashboard');
  expect(anonymous.status()).toBe(401);
  expect(anonymous.headers()['www-authenticate']).toBe('Bearer');
  const badToken = await request.get('/api/v1/me/dashboard', { headers: { Authorization: 'Bearer abc.def.ghi' } });
  expect(badToken.status()).toBe(401);
  expect(badToken.headers()['www-authenticate']).toBe('Bearer error="invalid_token"');
  expect((await badToken.json()).errors[0].code).toBe('UNAUTHORIZED');
});
