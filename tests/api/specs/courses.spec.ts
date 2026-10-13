import { randomUUID } from 'node:crypto';
import { test, expect, courseData } from '../fixtures';

test('courses: create, read, update and delete', async ({ request, resources }) => {
  const data = courseData();
  const course = await resources.create('/api/v1/courses', data, 'courses');
  const path = `/api/v1/courses/${course.id}`;
  const headers = resources.headers;
  const read = await request.get(path, { headers });
  expect(read.status()).toBe(200);
  expect(await read.json()).toMatchObject(data);
  const update = await request.patch(path, { headers, data: { title: 'Updated course', description: 'API testing' } });
  expect(update.status()).toBe(200);
  expect(await update.json()).toMatchObject({ title: 'Updated course', description: 'API testing' });
  expect(await (await request.get(path, { headers })).json()).toMatchObject({ title: 'Updated course' });
  const deleted = await request.delete(path, { headers });
  expect(deleted.status()).toBe(204);
  expect(await deleted.text()).toBe('');
  expect((await request.get(path, { headers })).status()).toBe(404);
});

test('courses: search, sort and follow filtered pagination links', async ({ request, resources }) => {
  const marker = randomUUID();
  const first = await resources.create('/api/v1/courses', courseData(`${marker} A`), 'courses');
  const second = await resources.create('/api/v1/courses', courseData(`${marker} B`), 'courses');
  await resources.create('/api/v1/courses', courseData('Unrelated course'), 'courses');
  const response = await request.get('/api/v1/courses', {
    headers: resources.headers,
    params: { search: marker, visibility: 'private', sort: 'title', page: 1, pageSize: 1 },
  });
  expect(response.status()).toBe(200);
  const page = await response.json();
  expect(page.meta).toEqual({ page: 1, pageSize: 1, totalItems: 2, totalPages: 2 });
  expect(page.data.map(c => c.id)).toEqual([first.id]);
  expect(page.links.next.method).toBe('GET');
  const next = await request.get(page.links.next.href, { headers: resources.headers });
  expect(next.status()).toBe(200);
  const last = await next.json();
  expect(last.meta).toEqual({ page: 2, pageSize: 1, totalItems: 2, totalPages: 2 });
  expect(last.data.map(c => c.id)).toEqual([second.id]);
  expect(last.links.next).toBeUndefined();
  expect(last.links.prev).toBeDefined();
});
