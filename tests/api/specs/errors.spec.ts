import { test, expect, courseData, registerAndLogin } from '../fixtures';

test('ownership: another user cannot read or modify a private course', async ({ request, resources, playwright, baseURL }) => {
  const course = await resources.create('/api/v1/courses', courseData(), 'courses');
  const outsider = await playwright.request.newContext({ baseURL });
  try {
    const { headers } = await registerAndLogin(outsider);
    const path = `/api/v1/courses/${course.id}`;
    expect((await outsider.get(path, { headers })).status()).toBe(404);
    expect((await outsider.patch(path, { headers, data: { title: 'Not allowed' } })).status()).toBe(403);
    expect((await outsider.delete(path, { headers })).status()).toBe(403);
    expect((await request.get(path, { headers: resources.headers })).status()).toBe(200);
  } finally {
    await outsider.dispose();
  }
});

test('status codes: unauthenticated mutation is 401 and unknown resources are 404', async ({ request }) => {
  const unauthorized = await request.post('/api/v1/courses', { data: courseData() });
  expect(unauthorized.status()).toBe(401);
  expect((await unauthorized.json()).errors[0].code).toBe('UNAUTHORIZED');
  for (const path of ['courses/-1', 'topics/-1', 'cards/-1', 'courses/-1/topics', 'topics/-1/cards']) {
    await test.step(`GET ${path} returns 404`, async () => {
      const response = await request.get(`/api/v1/${path}`);
      expect(response.status()).toBe(404);
      expect((await response.json()).errors[0].code).toBe('NOT_FOUND');
    });
  }
});

test('validation: invalid course, topic and card bodies return 400', async ({ request, resources }) => {
  const headers = resources.headers;
  const invalidCourse = await request.post('/api/v1/courses', { headers, data: { code: '', title: '' } });
  expect(invalidCourse.status()).toBe(400);
  expect((await invalidCourse.json()).errors[0].code).toBe('VALIDATION_ERROR');
  const course = await resources.create('/api/v1/courses', courseData(), 'courses');
  const topicsPath = `/api/v1/courses/${course.id}/topics`;
  const invalidTopic = await request.post(topicsPath, { headers, data: { title: 'Invalid', position: 0 } });
  expect(invalidTopic.status()).toBe(400);
  expect((await invalidTopic.json()).errors[0].code).toBe('VALIDATION_ERROR');
  const topic = await resources.create(topicsPath, { title: 'Valid topic' }, 'topics');
  const invalidCard = await request.post(`/api/v1/topics/${topic.id}/cards`, {
    headers, data: { question: 'Q', answer: 'A', difficulty: 6 },
  });
  expect(invalidCard.status()).toBe(400);
  expect((await invalidCard.json()).errors[0].code).toBe('VALIDATION_ERROR');
});
