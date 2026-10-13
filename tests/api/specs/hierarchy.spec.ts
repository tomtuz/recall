import { test, expect, courseData } from '../fixtures';

test('hierarchy: nested topics/cards, filtering, updates and child-first deletion', async ({ request, resources }) => {
  const headers = resources.headers;
  const course = await resources.create('/api/v1/courses', courseData(), 'courses');
  const topicsPath = `/api/v1/courses/${course.id}/topics`;
  const topic = await resources.create(topicsPath, { title: 'HTTP', position: 1 }, 'topics');
  expect(topic.courseId).toBe(course.id);
  const topics = await request.get(topicsPath, { headers });
  expect(topics.status()).toBe(200);
  expect((await topics.json()).data.map(t => t.id)).toEqual([topic.id]);
  const cardsPath = `/api/v1/topics/${topic.id}/cards`;
  const card = await resources.create(cardsPath, { question: 'Created?', answer: '201', tags: 'http' }, 'cards');
  expect(card.topicId).toBe(topic.id);
  const dbCard = await resources.create(cardsPath, { question: 'Database?', answer: 'PostgreSQL', tags: 'db' }, 'cards');
  const cards = await request.get(cardsPath, { headers, params: { tag: 'http', state: 'new' } });
  expect(cards.status()).toBe(200);
  expect((await cards.json()).data.map(c => c.id)).toEqual([card.id]);

  // Full-path hierarchical endpoint: the topic must belong to the course in the URL.
  const nested = await request.get(`/api/v1/courses/${course.id}/topics/${topic.id}/cards`, { headers });
  expect(nested.status()).toBe(200);
  expect((await nested.json()).data.map(c => c.id)).toEqual([card.id, dbCard.id]);
  const otherCourse = await resources.create('/api/v1/courses', courseData('Other course'), 'courses');
  const mismatched = await request.get(`/api/v1/courses/${otherCourse.id}/topics/${topic.id}/cards`, { headers });
  expect(mismatched.status()).toBe(404);

  // Composite read: course, topics and card aggregates in one response.
  const overview = await request.get(`/api/v1/courses/${course.id}/overview`, { headers });
  expect(overview.status()).toBe(200);
  expect(await overview.json()).toMatchObject({
    course: { id: course.id },
    topicCount: 1,
    cardCount: 2,
    dueCount: 0,
    stateBreakdown: { New: 2 },
    difficultyMix: { average: 3, hardest: 3 },
    topics: [{ id: topic.id, cardCount: 2, dueCount: 0 }],
  });

  const topicUpdate = await request.patch(`/api/v1/topics/${topic.id}`, { headers, data: { title: 'REST' } });
  expect(topicUpdate.status()).toBe(200);
  expect((await topicUpdate.json()).title).toBe('REST');
  const cardUpdate = await request.patch(`/api/v1/cards/${card.id}`, { headers, data: { answer: '201 Created', difficulty: 2 } });
  expect(cardUpdate.status()).toBe(200);
  const read = await request.get(`/api/v1/cards/${card.id}`, { headers });
  expect(read.status()).toBe(200);
  expect(await read.json()).toMatchObject({ answer: '201 Created', difficulty: 2, topicId: topic.id });

  expect((await request.delete(`/api/v1/courses/${course.id}`, { headers })).status()).toBe(409);
  expect((await request.delete(`/api/v1/topics/${topic.id}`, { headers })).status()).toBe(409);
  // Fixture teardown then deletes cards, topic and courses child-first and fails on any unexpected status.
});
