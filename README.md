# Recall

**Recall** — a web application that simplifies studying with flashcards shown
at repeating intervals (spaced repetition).

> **Project task description:** see the [GitHub Wiki](https://github.com/tomtuz/recall/wiki)
> for the full system purpose and functional requirements.

## Architecture

```text
┌─────────────────────┐         ┌──────────────────────────┐
│  React SPA          │  HTTPS  │  ASP.NET Core REST API   │
│  (Recall.Web)       │ ──────► │  (Recall.Api)  /api/v1   │
└─────────────────────┘  JSON   └────────────┬─────────────┘
                                             │ EF Core + Npgsql
                              ┌──────────────▼─────────────┐
                              │  PostgreSQL                │
                              └────────────────────────────┘
```

- **Recall.Api** — ASP.NET Core (.NET 10) REST API: Course → Topic → Card
  hierarchy, JWT authentication with rotating refresh tokens, three roles,
  an SM-2-based review scheduler, OpenAPI (Swagger).
- **Recall.Web** — React 19 + Vite + TypeScript SPA (planned for the L3 stage).
- **PostgreSQL** — database via Entity Framework Core.

API specification: [`api-spec.json`](api-spec.json) / [`api-spec.yaml`](api-spec.yaml)
(OpenAPI 3.0 with examples); explore it via Swagger UI or the
[`postman/`](postman) collection.

## Getting started

**Quick start (recommended):** run the init script once — it generates the
JWT signing key, sets user secrets and starts PostgreSQL (an already running
server at the configured address, otherwise the Docker container) — then use
the run script every time after. The API applies migrations and seeds demo
data on its first start.

```powershell
pwsh -File scripts/init.ps1   # one-time setup (safe to re-run)
pwsh -File scripts/run.ps1    # start the API -> http://localhost:15080/swagger
```

The scripts work from any directory (also as `.\scripts\run.ps1` in an open
shell) and leave the current location unchanged, including after Ctrl+C.

**Launcher:** `index.ps1` in the repository root opens the API in a new
Windows Terminal tab (running `scripts/run.ps1`) and waits until it answers.
A stale API from this checkout is restarted; any other process on port 15080
is reported, never killed.

```powershell
.\index.ps1          # start or restart the API -> http://localhost:15080/swagger
.\index.ps1 status   # Docker, PostgreSQL and API port
.\index.ps1 test     # Playwright API tests (tests/api)
.\index.ps1 stop     # stop the API
```

### Manual setup (what the scripts do)

#### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Docker (for the local PostgreSQL database), or an existing PostgreSQL server
- Python 3 (optional, for the smoke test)

#### 1. PostgreSQL database (Docker)

```powershell
docker run -d --name recall-pg `
  -e POSTGRES_USER=recall -e POSTGRES_PASSWORD=recall -e POSTGRES_DB=recall `
  -p 15432:5432 postgres:16-alpine
```

If the container already exists, just start it again:

```powershell
docker start recall-pg
```

#### 2. API configuration

The app uses user secrets for the JWT signing key and the database connection
string. Set them once:

```powershell
dotnet user-secrets set "Jwt:Secret" "<generate-a-random-key>" --project Recall.Api
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=15432;Database=recall;Username=recall;Password=recall" --project Recall.Api
```

The local Docker PostgreSQL connection (above) is the demonstration default —
change it if you use a different PostgreSQL address. In production, provide
both values via environment variables (`ConnectionStrings__Default`,
`Jwt__Secret`).

#### 3. Run the API

```powershell
dotnet run --project Recall.Api --urls http://localhost:15080
```

The API is available at:

- REST endpoints: `http://localhost:15080/api/v1/...`
- Swagger UI: <http://localhost:15080/swagger>

The first start automatically applies database migrations and, in the
Development environment, seeds demonstration data.

### 4. Tests

```powershell
dotnet test Recall.slnx   # xUnit: scheduler + OpenAPI examples
.\index.ps1 test          # Playwright end-to-end API tests, see tests/api/README.md
```

### 5. Smoke test, edge-case probe and Postman run (optional)

With the API running:

```powershell
python labs/lab1/smoke_test.py      # full Lab 1 + Lab 2 flows
python labs/lab1/defense_probe.py   # malformed/edge requests: expected 4xx, never 5xx
npx --yes newman@6 run postman/Recall.postman_collection.json
```

### 6. Regenerate the API specification

```powershell
pwsh -File scripts/sync-api-docs.ps1
```

## Demo users (Development)

| Email | Password | Role |
|---|---|---|
| `admin@recall.local` | `Admin123!` | Administrator |
| `author@recall.local` | `Author123!` | Contributor |
| `learner@recall.local` | `Learner123!` | Contributor |

These accounts are created only in the Development environment and exist for
demonstration purposes only.

## Status

- **Lab 1 (REST API)** — implemented and tested.
- **React SPA (L3)** — planned.
- **Cloud deployment (L2)** — planned: Azure for Students (Azure Container
  App + Azure Database for PostgreSQL Flexible Server).

## Repository structure

```text
Recall.Api/          REST API (.NET 10, EF Core, JWT, Swagger)
Recall.Api.Tests/    xUnit tests (SM-2 scheduler, OpenAPI examples)
api-spec.json/yaml   OpenAPI 3.0 specification with examples
postman/             Postman collection (endpoint demonstration)
index.ps1            quick launcher (start / stop / status / test)
scripts/             init.ps1 (setup) + run.ps1 (launch) + sync-api-docs.ps1 + common.ps1 (shared helpers)
tests/api/           Playwright end-to-end API tests
labs/lab1/           Lab 1 smoke test (73 checks) + edge-case probe
```

## Project task

The full project task (system purpose, functional requirements, user roles)
is available on the [GitHub Wiki](https://github.com/tomtuz/recall/wiki).