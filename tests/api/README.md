# Recall API tests (Playwright)

End-to-end tests that call the real API over HTTP with Playwright's `request`
fixture. No browser is launched or installed.

## Run

From the repository root:

```powershell
.\index.ps1 test
```

or manually:

```powershell
cd tests/api
npm ci
npm test
npm run show-report   # HTML report: tests/api/playwright-report/index.html
```

## Server lifecycle

- Prerequisites: Node.js/npm, the .NET 10 SDK and the local setup from
  `scripts/init.ps1` (user secrets + PostgreSQL on port 15432).
- By default Playwright starts the API itself
  (`dotnet run --project ../../Recall.Api --urls http://localhost:15080`), waits
  for a database-backed response and stops it afterwards. Port 15080 must then be free.
- To test an already running API, set `$env:RECALL_REUSE_API='1'` before
  `npm test`; `.\index.ps1 test` does this automatically when the API is up.
- Use a local development database only: the API applies migrations and the
  Development seed at startup.

## Coverage

| Spec | Checks |
|---|---|
| `auth.spec.ts` | register/login, refresh-cookie rotation, token replay → 401 `REFRESH_TOKEN_REUSED`, 400/401 on bad input |
| `courses.spec.ts` | course CRUD; search, sorting, pagination metadata and next-page link |
| `hierarchy.spec.ts` | Course → Topic → Card nesting, card filtering, full-path `/courses/{id}/topics/{id}/cards` (404 for a topic of another course), `/courses/{id}/overview` aggregates, updates, parent-delete 409 |
| `errors.spec.ts` | private-course ownership (403/404), 401 unauthenticated mutation, 404 unknown resources, 400 validation |
| `integrity.spec.ts` | 5 concurrent reviews end in the same scheduler state as 5 sequential ones, topic position overflow → 409, text stored trimmed as validated, unknown filter/sort values → 400, import (200 cards) and password (128 chars) caps, `WWW-Authenticate` on 401 |
| `regressions.spec.ts` | logout revokes the token family, NUL input, text limits, malformed JSON, origin gate on auth routes |

## Isolation

Each test registers its own user and deletes only the resources it created
(children before parents), even after a failure; a failed cleanup fails the
test. Seeded data is never modified. Test accounts remain, since the API has
no user-deletion endpoint. Traces are off, so no tokens or cookies are written
to the report.
