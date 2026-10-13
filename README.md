# Recall

Spaced-repetition flashcards.

REST API for **Course → Topic → Card**
(ASP.NET Core .NET 10, EF Core, PostgreSQL, JWT).

Project task: [Wiki](https://github.com/tomtuz/recall/wiki).

Requires .NET 10 SDK, PowerShell 7 and Docker Desktop; tests also use Node.js and Python 3.

## Start

```powershell
./index.ps1
```

Sets up user secrets and PostgreSQL on first run, then opens the API in a new tab:
<http://localhost:15080/swagger>. Also `./index.ps1 stop | status`.

Or with hot reload in this console (replaces a running API):

```powershell
./index.ps1 watch
```

Plain `dotnet watch run` in `Recall.Api` also works once `./index.ps1 setup` has run and no other API instance is running.

## Test

```powershell
dotnet test Recall.slnx --artifacts-path .artifacts
./index.ps1 test
```

With the API running:

```powershell
python labs/lab1/smoke_test.py
python labs/lab1/defense_probe.py
npx --yes newman@6 run postman/Recall.postman_collection.json
```

## Demo users

| Email | Password | Role |
|---|---|---|
| `admin@recall.local` | `Admin123!` | Administrator |
| `author@recall.local` | `Author123!` | Contributor |
| `learner@recall.local` | `Learner123!` | Contributor |

Seeded in Development only. Guests (no token) can read public courses.

## API

OpenAPI: [`api-spec.json`](api-spec.json) / [`api-spec.yaml`](api-spec.yaml) (regenerate: `./index.ps1 spec`).
Postman: [`postman/`](postman/Recall.postman_collection.json).

Status: Lab 1 REST API done; cloud deployment not yet; React SPA (`Recall.Web/`) in Lab 3.
