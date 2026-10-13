# Recall — Lab 2: JWT authorization

Authentication and authorization are implemented in the API and covered by
`labs/lab1/smoke_test.py` (register, login, refresh rotation, replay
detection, family revocation, logout, ownership 403, admin override).
Line numbers below refer to `Recall.Api/` in this repository.

## Token structure

The access token is a JWT (`header.payload.signature`, Base64URL parts):

```json
{ "alg": "HS256", "typ": "JWT" }
{ "sub": "2", "role": "Contributor", "email": "author@recall.local",
  "iss": "recall-api", "aud": "recall-api", "exp": 1791400000 }
```

| Claim | Meaning | Created at |
|---|---|---|
| `sub` | user id (ownership checks) | `Auth.cs:17` |
| `role` | `Contributor` or `Administrator` (admin override) | `Auth.cs:18` |
| `email` | display only | `Auth.cs:19` |
| `iss`, `aud`, `exp` | issuer, audience, 15-minute expiry | `Auth.cs:23-28`, lifetime `Auth.cs:10` |

Putting the role in the token lets every request be authorized without a
database lookup; the server can trust it because the token is signed.
The payload is only encoded, not encrypted: anyone can read it, so it holds
no secrets. Changing `role` in the payload breaks the signature → 401.

## Signing and validation

- **Signing:** HMAC-SHA256 with a symmetric key, `Auth.cs:21-29`
  (`SecurityAlgorithms.HmacSha256`).
- **Key:** 512-bit random secret, Base64, read from configuration `Jwt:Secret`
  (`Program.cs:22`). It comes from user secrets locally
  (`scripts/init.ps1` generates it) and from the `Jwt__Secret` environment
  variable in production; it is never committed.
- **Validation:** `Program.cs:48-61` — signature, issuer, audience and
  lifetime are all checked (`ValidateIssuerSigningKey`, `ValidateLifetime`);
  `MapInboundClaims = false` keeps the raw `sub`/`role` names.
  `app.UseAuthentication()` runs it for every request (`Program.cs:136`).
- **How it could be broken:** a leaked secret lets an attacker forge tokens
  (keep it out of git, rotate it by changing the variable — all tokens become
  invalid); a weak secret could be brute-forced offline (512 random bits
  prevent that); `alg: none` or a changed algorithm is rejected because a
  signing key is required.

## Transport

| Token | Sent as | Why |
|---|---|---|
| Access (15 min) | `Authorization: Bearer <jwt>` header | not sent automatically by the browser → no CSRF; the SPA keeps it in memory only |
| Refresh (7 days) | cookie `recall_refresh`, `HttpOnly`, `Secure` (HTTPS), `SameSite=Lax`, `Path=/api/v1/auth` (`Program.cs:1124-1134`) | JavaScript cannot read it (XSS cannot steal it); sent only to the auth endpoints |

Browser requests (with an `Origin` header) never receive the refresh token in
JSON (`Program.cs:946`, `927`); Postman gets it in the body for testing.
`Secure` is required outside Development (`Program.cs:33`).

**Show the flags live:** `dotnet run --project Recall.Api --launch-profile https`
(https://localhost:15443, dev certificate, `Auth__CookieSecure=true`), log in,
then DevTools → Application → Cookies, or:

```text
Set-Cookie: recall_refresh=<token>; expires=...; path=/api/v1/auth; secure; samesite=lax; httponly
```

Header vs cookie: a header needs JavaScript access to the token (XSS risk) but
is immune to CSRF; a cookie is hidden from JavaScript but sent automatically
(CSRF risk, mitigated by `SameSite` and the narrow path).

## Refresh strategy

- Refresh token: 64 random bytes (`Auth.cs:34`); the database stores only its
  SHA-256 hash (`Auth.cs:40`), so a database leak does not leak sessions.
- `POST /auth/refresh` (`Program.cs:983`): every refresh **rotates** — the old
  token is revoked and a new one issued in one transaction (`Program.cs:1038`).
- **Replay detection:** presenting an already-rotated token revokes the whole
  token family (one family per login) → 401 `REFRESH_TOKEN_REUSED`
  (`Program.cs:1010-1020`). A stolen token is useless after the victim's next
  refresh, and its use logs everyone out of that login.
- Concurrent refresh/logout calls for a user are serialized with a row lock
  (`SELECT ... FOR UPDATE`, `Program.cs:1006`).
- Result: the user stays signed in for up to 7 days without logging in again,
  while a leaked access token is valid for at most 15 minutes.

## Register, login, logout

| Endpoint | Line | Notes |
|---|---|---|
| `POST /api/v1/auth/register` | `Program.cs:903` | new users are always `Contributor` (`:881`) — the role cannot be chosen; duplicate email → 409 |
| `POST /api/v1/auth/login` | `Program.cs:955` | PBKDF2-SHA256 password hash, 100 000 iterations, constant-time compare (`Services.cs:12-31`); wrong password → 401 |
| `POST /api/v1/auth/refresh` | `Program.cs:983` | see above |
| `POST /api/v1/auth/logout` | `Program.cs:1080` | revokes the refresh token's whole family and deletes the cookie; works with a valid refresh cookie even after the access token expired |

## Roles and ownership

| Role | How | Can |
|---|---|---|
| Guest | no token | read public courses/topics/cards |
| Contributor | `role=Contributor` | + create courses; edit/delete **own** courses, topics, cards; review; dashboard |
| Administrator | `role=Administrator` | + edit/delete any course, see private courses |

The role alone cannot stop a contributor from editing someone else's data, so
every mutation also checks the owner: `course.OwnerId != userId && !user.IsAdmin()`
→ 403 (`Program.cs:317`, `321`, `366`, and the topic/card equivalents).
Reads go through `ScopeCourses` (`ApiCommon.cs:104`): private courses of other
users are returned as 404, not 403, so their existence is not revealed.
Helpers: `UserId()` / `IsAdmin()` read the claims (`ApiCommon.cs:114-118`).

## Status codes to demonstrate

| Case | Code |
|---|---|
| No token / bad signature / expired token on a protected endpoint | 401 |
| Wrong password, reused or invalid refresh token | 401 |
| Editing another user's course | 403 |
| Another user's private course | 404 |
| Duplicate email on register | 409 |
| Successful logout | 204 |

`python labs/lab1/smoke_test.py` runs all of them; the Postman collection's
Auth folder shows them one by one.

## Threats and mitigations

| Threat | Mitigation | Where |
|---|---|---|
| SQL injection | EF Core LINQ queries are parameterized; the one raw query uses `FromSqlInterpolated` (parameters, not string concatenation) | `Program.cs:1006` |
| Privilege escalation | role comes only from the signed token; registration cannot set it; ownership checks on every mutation | `Program.cs:925`, `290` |
| Token forgery | HS256 signature + issuer/audience/lifetime validation | `Program.cs:48-61` |
| Token theft (XSS) | refresh token only in an `HttpOnly` cookie; short access lifetime | `Program.cs:1124-1134` |
| Refresh token replay | rotation + family revocation | `Program.cs:1010-1020` |
| CSRF | access token in a header; refresh cookie `SameSite=Lax`, path `/api/v1/auth` | `Program.cs:1132` |
| Password database leak | salted PBKDF2, 100 000 iterations | `Services.cs:12-31` |
| Information leakage | generic JSON errors, no stack traces; malformed input → 400 | `Program.cs:104-134` |
| Brute-force login | not mitigated (no rate limiting) — future work | — |

## Rubric mapping

| Criterion | Evidence |
|---|---|
| OAuth/JWT; endpoints only for signed-in users; role and id in the token | Bearer JWT with `sub` + `role`; protected endpoints return 401 without it |
| Refresh strategy; short access, longer refresh | 15 min access, 7 day rotating refresh |
| Register, login, logout; logout invalidates tokens | `/auth/*`; logout revokes the token family |
| ≥3 roles; authorization by role and id | Guest / Contributor / Administrator + ownership 403 |
| Two-factor authentication (bonus) | not implemented |

## Deployment

Cloud hosting is planned, not deployed yet: Azure for Students (Azure Container
App with the SPA and API on one HTTPS origin + Azure Database for PostgreSQL
Flexible Server). Production sets `Jwt__Secret`, `ConnectionStrings__Default`
and `Auth__CookieSecure=true` as environment variables.
