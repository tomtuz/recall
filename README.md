# Recall

Spaced-repetition flashcards: REST API for **Course → Topic → Card**
(ASP.NET Core .NET 10, EF Core, PostgreSQL, JWT).

Project task: [Wiki](https://github.com/tomtuz/recall/wiki).

## Start

Requires the .NET 10 SDK and PostgreSQL.

```sh
cd Recall.Api
dotnet watch run
```

Open <http://localhost:15080/swagger>. The first start creates the tables and the demo data.

## Windows

```ps1
./index.ps1 -|watch|stop|status|test|spec
```

- Uses Docker
- Keeps a fixed signing key in user secrets

## Database

**1. Database** (once): user, password and database `recall`.

Docker:
```bash
docker run -d --name recall-pg -p 15432:5432 -e POSTGRES_USER=recall -e POSTGRES_PASSWORD=recall -e POSTGRES_DB=recall postgres:16-alpine
```

## Demo users

| Email | Password | Role |
|---|---|---|
| `admin@recall.local` | `Admin123!` | Administrator |
| `author@recall.local` | `Author123!` | Contributor |
| `learner@recall.local` | `Learner123!` | Contributor |

Seeded in Development only. Guests (no token) can read public courses.
Development signs tokens with a new random key on each start, so log in again after a restart.

## Test

With the API running:

```bash
npx --yes newman@6 run postman/Recall.postman_collection.json
```

More:
```sh
# (unit)
dotnet test Recall.slnx --artifacts-path .artifacts
npm --prefix tests/api ci

# (Playwright)
npm --prefix tests/api test
python3 labs/lab1/smoke_test.py
python3 labs/lab1/defense_probe.py
```

## API

OpenAPI: [`api-spec.json`](api-spec.json) / [`api-spec.yaml`](api-spec.yaml)

Regenerate: 
```sh
dotnet run --project Recall.Api --no-launch-profile -- --export-openapi .
```

Postman tests: [`postman/`](postman/Recall.postman_collection.json).
