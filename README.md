# Recall

**Recall** – internetinė aplikacija mokymosi procesui palengvinti, pritaikant
pasikartojančiais intervalais rodomas klausimų ir atsakymų korteles
(spaced repetition).

> **Projekto užduotis (task description):** žr. [GitHub Wiki](https://github.com/tomtuz/recall/wiki)
> – pilna sistemos paskirtis ir funkciniai reikalavimai.

## Architektūra

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

- **Recall.Api** – ASP.NET Core (.NET 10) REST API: Course → Topic → Card
  hierarchija, JWT autentifikacija su besikeičiančiais refresh tokenais,
  trys rolės, SM-2 pagrindo kartojimo planuoklis, OpenAPI (Swagger).
- **Recall.Web** – React 19 + Vite + TypeScript SPA (planuojama L3 etape).
- **PostgreSQL** – duomenų bazė per Entity Framework Core.

API specifikacija: [`api-spec.json`](api-spec.json) / [`api-spec.yaml`](api-spec.yaml)
(OpenAPI 3.0 su pavyzdžiais), demonstracija per Swagger UI arba
[`postman/`](postman) kolekciją.

## Paleidimas

### Reikalavimai

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Docker (vietai PostgreSQL duomenų bazės)
- Python 3 (smoke testui, neprivaloma)

### 1. PostgreSQL duomenų bazė (Docker)

```powershell
docker run -d --name recall-pg `
  -e POSTGRES_USER=recall -e POSTGRES_PASSWORD=recall -e POSTGRES_DB=recall `
  -p 15432:5432 postgres:16-alpine
```

Jei konteineris jau sukurtas, tiesiog paleiskite jį iš naujo:

```powershell
docker start recall-pg
```

### 2. API konfigūracija

Sistema naudoja naudotojo paslaptis (user secrets) JWT pasirašymo raktui ir
duomenų bazės jungčiai. Nustatykite jas vieną kartą:

```powershell
cd Recall.Api
dotnet user-secrets init
dotnet user-secrets set "Jwt:Secret" "<sugeneruokite-atsitiktinį-raktą>"
dotnet user-secrets set "ConnectionStrings:Default" "Host=localhost;Port=15432;Database=recall;Username=recall;Password=recall"
```

Vietinė Docker PostgreSQL jungtis (aukščiau) yra numatyta demonstracijai –
keiskite, jei naudojate kitą adresą. Produkcinėje aplinkoje abi reikšmes
pateikite per aplinkos kintamuosius (`ConnectionStrings__Default`,
`Jwt__Secret`).

### 3. Paleiskite API

```powershell
dotnet run --project Recall.Api --urls http://localhost:15080
```

API pasiekiamas:

- REST endpoint'ai: `http://localhost:15080/api/v1/...`
- Swagger UI: <http://localhost:15080/swagger>

Pirmasis paleidimas automatiškai sukuria duomenų bazės struktūrą (migrations)
ir, Development aplinkoje, užpildo demonstraciniais duomenimis.

### 4. Testai

```powershell
dotnet test Recall.slnx
```

### 5. Smoke testas (neprivaloma)

Su veikiančiu API:

```powershell
python labs/lab1/smoke_test.py
```

### 6. API specifikacijos regeneravimas

```powershell
pwsh -File scripts/sync-api-docs.ps1
```

## Demo naudotojai (Development)

| El. paštas | Slaptažodis | Rolė |
|---|---|---|
| `admin@recall.local` | `Admin123!` | Administratorius |
| `author@recall.local` | `Author123!` | Contributor |
| `learner@recall.local` | `Learner123!` | Contributor |

Šie duomenys sukuriami tik Development aplinkoje ir skirti tik demonstracijai.

## Būsena

- **Lab 1 (REST API)** – įgyvendinta ir testuota.
- **React SPA (L3)** – planuojama.
- **Debesų talpinimas (L2)** – planuojama: „Azure for Students" („Azure
  Container App" + „Azure Database for PostgreSQL Flexible Server").

## Struktūra

```text
Recall.Api/          REST API (.NET 10, EF Core, JWT, Swagger)
Recall.Api.Tests/    xUnit testai (SM-2 planuoklis, OpenAPI pavyzdžiai)
api-spec.json/yaml   OpenAPI 3.0 specifikacija su pavyzdžiais
postman/             Postman kolekcija (endpoint demonstracija)
labs/lab1/           Lab 1 aprašymas ir smoke testas
docs/                Architektūros dokumentacija
```

## Projekto užduotis

Pilna projekto užduotis (sistemos paskirtis, funkciniai reikalavimai,
naudotojų rolės) – [GitHub Wiki](https://github.com/tomtuz/recall/wiki).