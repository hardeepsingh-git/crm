# CRM Platform: Operations Guide (Setup, Configuration, Deployment)

## 1. Prerequisites

| Tool | Version | Used for |
|---|---|---|
| .NET SDK | 10.0.x | API build and run |
| Node.js / npm | 22.x / 11.x | Frontend build and run |
| dotnet-ef (global tool) | 10.0.x | Migrations and SQL scripts |
| PostgreSQL | 14+ (remote `SYDVMPOS01.cbs.fiserv.net`) | Database |
| Docker (optional) | 24+ | Container deployment |

Packages come through the internal Nexus mirrors that are already configured for NuGet and npm. Nothing is downloaded from the public internet.

## 2. Configuration reference (API)

Configuration is read in this order: `appsettings.json`, then `appsettings.{Environment}.json`, then **user-secrets** (Development), then **environment variables** (use `__` for `:`, e.g. `Jwt__SigningKey`).

| Key | Required | Default | Description |
|---|---|---|---|
| `ConnectionStrings:Crm` | ✔ | none | Npgsql connection string **without** the password, e.g. `Host=SYDVMPOS01.cbs.fiserv.net;Port=5432;Database=CRM;Username=postgres;SSL Mode=Prefer`. The database name is **case-sensitive** (`CRM` ≠ `crm`). |
| `Database:Password` | ✔ | none | DB password, kept out of the connection string |
| `Database:Strategy` | | `Migrate` | `Migrate` applies migrations on startup. `EnsureCreated` is for tests. `None` means DBAs apply `crm-schema.sql`. |
| `Jwt:SigningKey` | ✔ | none | ≥ 32 bytes of random data (base64) |
| `Jwt:Issuer` / `Jwt:Audience` | | `crm-api` / `crm-web` | |
| `Jwt:AccessTokenMinutes` / `Jwt:RefreshTokenDays` | | `30` / `7` | Token lifetimes |
| `Seed:AdminEmail` / `Seed:AdminPassword` | first run | none | Creates the first **Administrator + platform admin** in the default tenant if none exists |
| `Seed:DemoData` | | `false` | Seeds sample data into the default tenant when it's empty |
| `Seed:DemoUserPassword` | | none | With DemoData, also creates `manager@crm.local` (Manager) and `rep@crm.local` (Standard User) |
| `Cors:AllowedOrigins` | | `[]` | Only needed if the UI isn't served from the same origin as `/api` |
| `RateLimiting:AuthPermitPerMinute` | | `10` | Sign-in attempts per IP per minute |
| `RateLimiting:RefreshPermitPerMinute` | | `120` | Token refreshes per IP per minute |
| `ForwardedHeaders:Enabled` | | `false` | `true` only when the API is reachable **only** through a trusted reverse proxy |
| `OpenApi:Enabled` | | `true` in Development | `/openapi/v1.json` and `/scalar` |

Tenant-level settings (name, currency, objects, fields, roles…) are **data**, not configuration. Administrators manage them in the UI under **Settings**, and platform admins manage tenants under **Tenants**.

## 3. Local development (Windows)

### 3.1 One-time secrets (stored in your Windows profile, not the repo)
```powershell
cd C:\Users\FBPo48p\CRM\backend\src\Crm.Api
# Already configured: ConnectionStrings:Crm (Database=CRM), Jwt:SigningKey, Seed:AdminEmail, Seed:DemoData, Cors
dotnet user-secrets set "Database:Password" "<postgres password>"
dotnet user-secrets set "Seed:AdminPassword" "<initial admin password>"    # 10+ chars, upper, lower, digit, symbol
dotnet user-secrets set "Seed:DemoUserPassword" "<demo users password>"    # optional
dotnet user-secrets list   # shows values, so don't share the output
```

### 3.2 Database
The API applies migrations on startup. To do it manually:
```powershell
cd C:\Users\FBPo48p\CRM\backend
dotnet ef database update --project src/Crm.Api
```
DBA alternative: run `backend/database/crm-schema.sql` (idempotent) against `CRM`, then set `Database:Strategy=None`.

**Upgrading from v1:** the `MultiTenantPlatform` migration moves all existing data into the **Default** tenant and maps Admin → Administrator (also platform admin), Manager → Manager and SalesRep → Standard User. On the next start, the sales template adds the default stages, picklists, fields and rules. Take a backup (`pg_dump -Fc`) first.

> **Housekeeping:** an earlier run created a separate lowercase database `crm` on the server by mistake (PostgreSQL names are case-sensitive). The application uses `CRM` only. A DBA can drop `crm` after confirming it isn't used: `DROP DATABASE crm;` (connect to another database first).

### 3.3 Run
Pick one:
- **VS Code:** *Tasks: Run Task* → **CRM: Start all**. To debug the API, use Run and Debug → **CRM API (debug)**.
- **Script:** `./scripts/start-dev.ps1`
- **Manually:**
  ```powershell
  cd C:\Users\FBPo48p\CRM\backend;  dotnet run --project src/Crm.Api --launch-profile http   # http://localhost:5290 (/scalar)
  cd C:\Users\FBPo48p\CRM\frontend; npm start                                                # http://localhost:4200
  ```

Sign in as `admin@crm.local` with your `Seed:AdminPassword`. This user is an Administrator of the default tenant **and** a platform admin, so they see **Settings** and **Tenants**.

### 3.4 Tests
```powershell
cd C:\Users\FBPo48p\CRM\backend;  dotnet test                  # stop the running API first (it locks bin/)
cd C:\Users\FBPo48p\CRM\frontend; npx ng test --watch=false
cd C:\Users\FBPo48p\CRM;          ./scripts/smoke-test.ps1       # live end-to-end against the running API + PostgreSQL
```

## 4. Tenant operations

| Task | How |
|---|---|
| Create a tenant | **Tenants → New tenant**: name, workspace ID (slug), template (Sales / Help Desk / Recruitment / Blank), currency, optional sample data, and the first administrator's name, email and password. Or `POST /api/platform/tenants`. |
| Hand over | Give the new administrator their email and initial password over a secure channel. They should change it on first sign-in. |
| Suspend | **Tenants → pause**. Users are blocked from signing in, and their existing sessions lose access on the next request. No data is deleted. |
| Reactivate | **Tenants → play** |
| Delete | Suspend first, then **delete** and type the workspace ID. This removes **all** of the tenant's users, records and configuration permanently. It isn't possible for the default tenant or your own. |
| Back up one tenant | `pg_dump` with `--where` isn't supported. Export per table with `COPY (SELECT * FROM "Accounts" WHERE "TenantId" = '…') TO …`, or back up the whole database. |

## 5. Deployment

### 5.1 Containers
```powershell
Copy-Item .env.example .env    # fill in secrets
docker compose up --build -d   # UI + API at http://localhost:8080
```
To use the remote server instead of the bundled PostgreSQL, remove the `db` service and set `CRM_DB_HOST` and the credentials in `.env`.

### 5.2 Without containers (IIS / Linux service)
1. Build and host the API:
   ```powershell
   dotnet publish backend/src/Crm.Api -c Release -o out/api
   ```
   Host it with the ASP.NET Core Hosting Bundle or systemd.
2. Build the UI:
   ```powershell
   cd frontend; npx ng build
   ```
   Serve `dist/frontend/browser` with:
   - SPA fallback to `index.html`
   - a reverse proxy for `/api` and `/health`
   - the headers from `frontend/security-headers.conf`
3. Configure the API with environment variables, and terminate TLS at the proxy.

**Scaling out:** the API is stateless. The permission cache is per node and expires within 5 minutes; changes made on one node take effect on other nodes within that window. If you need instant propagation, use a distributed cache or sticky sessions for admins.

### 5.3 Production checklist
- [ ] Use a least-privilege DB role (not `postgres`) with DML on the tables. Use `Database:Strategy=None` if DBAs run migrations.
- [ ] Use `SSL Mode=Require` or `VerifyFull`.
- [ ] Set a fresh `Jwt:SigningKey` per environment, stored in a vault.
- [ ] Rotate the seeded admin password. Consider a separate platform-admin account that isn't used for day-to-day tenant work.
- [ ] Add GIN indexes on `CustomFields` for large tables (see DATA-MODEL §3).
- [ ] Plan retention for `AuditLogs` and `Notifications` (e.g. archive audit rows older than 2 years).
- [ ] Keep `OpenApi:Enabled=false` unless needed.
- [ ] Monitor `/health/ready`. Back up nightly (`pg_dump -Fc CRM`) with WAL archiving.
- [ ] Run a Fortify SAST scan before release.

## 6. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `Jwt:SigningKey must be at least 32 bytes` | Set the secret (§3.1) |
| `28P01: password authentication failed` | Wrong `Database:Password`, or `pg_hba.conf` doesn't allow the user |
| Data appears "missing" / wrong database | `Database=CRM` and `Database=crm` are different databases. Check the connection string. |
| Sign-in shows "Your organisation's account is suspended" (403) | A platform admin suspended the tenant. Reactivate it under **Tenants**. |
| A user can't see a menu item or object | Check their roles (directly **and** via groups) under **Settings → Roles**. An explicit **None** on an object hides it even if "All objects" allows it, unless another role grants it. Also check the object is **Active**. |
| "You can't grant permissions you don't have yourself" | Anti-escalation. Ask an Administrator to make the change. |
| Can't delete a stage / object / account | It still has records. Move or delete them first (the 409 message says how many). |
| Automation rule didn't fire | Is the rule active? Do all conditions match? Stage-change rules need a pipeline object. Changes made by automation don't trigger other rules. Check **Runs** on the Automation page. |
| `429 Too Many Requests` on sign-in | Wait a minute, or raise `RateLimiting:AuthPermitPerMinute` |
| `dotnet test` fails with file-lock errors | Stop the running API first (it locks `bin/`) |
| `NU1900` warnings during restore | The Nexus vulnerability feed returns 403. This is harmless. |
