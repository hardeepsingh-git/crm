# CRM: Operations Guide (Setup, Configuration, Deployment)

## 1. Prerequisites

| Tool | Version | Used for |
|---|---|---|
| .NET SDK | 10.0.x | API build and run |
| Node.js / npm | 22.x / 11.x | Frontend build and run |
| dotnet-ef (global tool) | 10.0.x | Migrations |
| PostgreSQL | 14+ (tested against the remote server) | Database |
| Docker (optional) | 24+ | Container deployment |

Packages come through the internal Nexus mirrors that are already configured in NuGet and npm.

## 2. Configuration reference (API)

Configuration is read in this order: `appsettings.json`, then `appsettings.{Environment}.json`, then **user-secrets** (Development only), then **environment variables** (use `__` for `:`, e.g. `Jwt__SigningKey`).

| Key | Required | Default | Description |
|---|---|---|---|
| `ConnectionStrings:Crm` | ✔ | none | Npgsql connection string **without** the password, e.g. `Host=SYDVMPOS01.cbs.fiserv.net;Port=5432;Database=CRM;Username=postgres;SSL Mode=Prefer`. The database name is **case-sensitive** (`CRM` ≠ `crm`). |
| `Database:Password` | ✔* | none | DB password, kept separate so it never sits in a connection string in source control |
| `Database:Strategy` | | `Migrate` | `Migrate` applies migrations on startup. `EnsureCreated` is for tests. `None` means DBAs apply `crm-schema.sql`. |
| `Jwt:SigningKey` | ✔ | none | ≥ 32 bytes of random data (base64). The app refuses to start without it. |
| `Jwt:Issuer` / `Jwt:Audience` | | `crm-api` / `crm-web` | Token issuer and audience |
| `Jwt:AccessTokenMinutes` | | `30` | Access token lifetime |
| `Jwt:RefreshTokenDays` | | `7` | Refresh token lifetime |
| `Seed:AdminEmail` / `Seed:AdminPassword` | first run | none | Creates the first Admin if none exists |
| `Seed:DemoData` | | `false` | Seeds sample data into an empty database |
| `Seed:DemoUserPassword` | | none | If set with DemoData, also creates `manager@crm.local` and `rep@crm.local` |
| `Cors:AllowedOrigins` | | `[]` | Only needed if the UI is served from a different origin than `/api` |
| `RateLimiting:AuthPermitPerMinute` | | `10` | Login attempts per IP per minute |
| `RateLimiting:RefreshPermitPerMinute` | | `120` | Token refreshes per IP per minute (one per page load) |
| `ForwardedHeaders:Enabled` | | `false` | Set to `true` only when the API is reachable **only** through a trusted reverse proxy |
| `OpenApi:Enabled` | | `true` in Development | Exposes `/openapi/v1.json` and `/scalar` |

## 3. Local development (Windows)

### 3.1 One-time secrets (stored outside the repo)
```powershell
cd C:\Users\FBPo48p\CRM\backend\src\Crm.Api
# Already configured: ConnectionStrings:Crm, Jwt:SigningKey, Seed:AdminEmail, Seed:DemoData, Cors
dotnet user-secrets set "Database:Password" "<postgres password>"
dotnet user-secrets set "Seed:AdminPassword" "<initial admin password>"   # 10+ chars, upper, lower, digit, symbol
# Optional sample data:
dotnet user-secrets set "Seed:DemoData" "true"
dotnet user-secrets set "Seed:DemoUserPassword" "<demo users password>"
dotnet user-secrets list   # verify (shows values, so don't share the output)
```

### 3.2 Database
The API applies migrations automatically on startup (`Database:Strategy=Migrate`). To apply them manually instead:
```powershell
cd C:\Users\FBPo48p\CRM\backend
dotnet ef database update --project src/Crm.Api
```
DBA alternative: run `backend/database/crm-schema.sql` against the `CRM` database and set `Database:Strategy=None`.

### 3.3 Run
Pick one:
- **VS Code:** `Ctrl+Shift+P` → *Tasks: Run Task* → **CRM: Start all** (API + UI in dedicated terminals). To debug the API, use the Run and Debug view → **CRM API (debug)**.
- **One script:** `./scripts/start-dev.ps1` opens the API and UI in two windows and waits until the API is ready.
- **Manually:**
```powershell
# Terminal 1: API on http://localhost:5290 (Scalar UI at http://localhost:5290/scalar)
cd C:\Users\FBPo48p\CRM\backend
dotnet run --project src/Crm.Api --launch-profile http

# Terminal 2: UI on http://localhost:4200 (proxies /api to :5290)
cd C:\Users\FBPo48p\CRM\frontend
npm start
```
Sign in with `admin@crm.local` and your `Seed:AdminPassword`. With demo data enabled, `manager@crm.local` (Manager) and `rep@crm.local` (Sales Rep) use `Seed:DemoUserPassword`. To see it: `dotnet user-secrets list` in `backend/src/Crm.Api`.

### 3.4 Tests
```powershell
cd C:\Users\FBPo48p\CRM\backend;  dotnet test
cd C:\Users\FBPo48p\CRM\frontend; npx ng test --watch=false
# End-to-end against the running API + database (creates and removes tagged records):
cd C:\Users\FBPo48p\CRM; ./scripts/smoke-test.ps1
```

## 4. Deployment

### 4.1 Containers
```powershell
cd C:\Users\FBPo48p\CRM
Copy-Item .env.example .env   # fill in secrets
docker compose up --build -d  # UI+API at http://localhost:8080
```
To use the existing remote server instead of the bundled PostgreSQL:
1. Delete the `db` service and the api `depends_on` from `docker-compose.yml`.
2. Set `CRM_DB_HOST=SYDVMPOS01.cbs.fiserv.net` and the matching user and password in `.env`.

### 4.2 Without containers (IIS / Linux service)
1. Build the API:
   ```powershell
   dotnet publish backend/src/Crm.Api -c Release -o out/api
   ```
   Host it with the ASP.NET Core Hosting Bundle (IIS) or as a systemd service.
2. Build the UI:
   ```powershell
   cd frontend; npx ng build
   ```
   Serve `frontend/dist/frontend/browser` from any web server with:
   - SPA fallback to `index.html`
   - reverse proxy of `/api` and `/health` to the API
   - the headers in `frontend/security-headers.conf`
3. Provide configuration as environment variables. Never commit `appsettings.Production.json` containing secrets.
4. Terminate TLS at the proxy or load balancer. The API adds HSTS outside Development.

### 4.3 Production checklist
- [ ] Use a dedicated least-privilege DB role, not `postgres`. For example:
  ```sql
  CREATE ROLE crm_app LOGIN PASSWORD '...';
  GRANT CONNECT ON DATABASE crm TO crm_app;
  ```
  Grant it DML on the tables. If DBAs run migrations, use `Database:Strategy=None`.
- [ ] Use `SSL Mode=Require` (or `VerifyFull`) in the connection string.
- [ ] Generate a fresh `Jwt:SigningKey` per environment and store it in a vault.
- [ ] Rotate the seeded admin password after first sign-in.
- [ ] Keep `OpenApi:Enabled=false` unless you need it.
- [ ] Monitor `/health/ready` and back up PostgreSQL (e.g. nightly `pg_dump -Fc crm` plus WAL archiving).
- [ ] Run a Fortify SAST scan before release.

## 5. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `Jwt:SigningKey must be at least 32 bytes` on start | Set the secret (section 3.1) |
| `Connection string 'Crm' is not configured` | Run from `src/Crm.Api` in Development (user-secrets) or set `ConnectionStrings__Crm` |
| `28P01: password authentication failed` | Wrong `Database:Password`, or the user isn't allowed in `pg_hba.conf` |
| `3D000: database "CRM" does not exist` | PostgreSQL names are case-sensitive. The DB is `crm` (lowercase). |
| Warning `No admin user exists` | Set `Seed:AdminEmail` and `Seed:AdminPassword`, then restart |
| UI shows "Cannot reach the server" | The API isn't running on :5290, or the proxy isn't configured |
| `429 Too Many Requests` on login | Wait 1 minute, or raise `RateLimiting:AuthPermitPerMinute` |
| Tables/data appear in the wrong database | PostgreSQL names are case-sensitive: `Database=CRM` and `Database=crm` are different databases. Migrations create the named database if it doesn't exist. |
| `NU1900` warnings during restore | The Nexus vulnerability feed returns 403. This is harmless. |
