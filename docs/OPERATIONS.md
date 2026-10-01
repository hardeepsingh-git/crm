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
| `RateLimiting:RegisterPermitPerMinute` | | `10` | Self-registration, verify and resend-code calls per IP per minute |
| `RateLimiting:PasswordResetPermitPerMinute` | | `5` | Forgot-password and reset-password calls per IP per minute |
| `RateLimiting:ClientErrorPermitPerMinute` | | `30` | Browser error reports per IP per minute |
| `RateLimiting:EmailSendPermitPerMinute` | | `20` | Emails sent from records, per user per minute |
| `RateLimiting:CareersPermitPerMinute` | | `120` | Careers page and job views, per IP per minute |
| `RateLimiting:CareersApplyPermitPerMinute` | | `5` | Careers applications per IP per minute |
| `RateLimiting:OfferResponsePermitPerMinute` | | `30` | Candidate offer page views and answers per IP per minute |
| `Careers:MaxCvSizeMb` | | `5` | Largest CV accepted from the careers page |
| `Export:MaxRows` | | `10000` | Largest CSV export; larger results must be narrowed with filters |
| `Email:DailyLimitPerTenant` | | `1000` | Emails each organisation can queue per UTC day; beyond it, sends get 429 and automation emails are logged as Failed |
| `Email:DispatchIntervalSeconds` | | `15` | How often the outbox is checked (new emails also wake it immediately) |
| `Automation:ScheduleIntervalSeconds` | | `300` | How often date-based rules are checked |
| `Automation:ScheduleLookbackHours` | | `48` | Due times older than this are skipped, e.g. after a long outage |
| `BackgroundJobs:Enabled` | | `true` | Runs the email dispatcher and date-rule scheduler in this API instance. Safe on several instances; set `false` to run them only on some. |
| `App:PublicUrl` | ✔ outside Development | `http://localhost:4200` in Development | The UI's public address, used in password-reset links and the `{{link}}` email placeholder (never taken from the request's Host header). Without it, reset emails aren't sent. |
| `PasswordReset:TokenMinutes` | | `60` | How long a reset link is valid |
| `Storage:LocalPath` | | `<content root>/App_Data/files` | Where attachments are stored. Use a persistent, backed-up volume (shared between nodes if you run several). |
| `Attachments:MaxFileSizeMb` | | `10` | Per file. Above 28 MB also raise Kestrel/IIS/proxy request size limits. |
| `Import:MaxFileSizeMb` / `Import:MaxRows` | | `5` / `5000` | CSV import limits |
| `Sentry:Dsn` | | none | Enables error tracking (see §2.3). Also `Sentry:Environment`, `Sentry:TracesSampleRate`. |
| `Email:SmtpPassword` | with the SMTP provider | none | SMTP password. The rest of the SMTP settings are in **Platform settings** (UI). |
| `Sms:ApiKey` | with the HTTP SMS provider | none | Sent as `<SmsAuthHeader>: <SmsAuthScheme> <key>` to the SMS gateway configured in **Platform settings** |
| `ForwardedHeaders:Enabled` | | `false` | `true` only when the API is reachable **only** through a trusted reverse proxy |
| `OpenApi:Enabled` | | `true` in Development | `/openapi/v1.json` and `/scalar` |

Tenant-level settings (name, currency, objects, fields, roles…) are **data**, not configuration. Administrators manage them in the UI under **Settings**, and platform admins manage tenants under **Tenants**.

**Platform settings** (self-registration, email/SMS verification flags, code rules, email and SMS providers) are also data, in the `AppSettings` table, edited by platform admins under **Platform settings**. Defaults on first start: everything off, both providers `Log`. Only the two secrets above live in configuration.

### 2.1 Enabling self-registration
1. **Platform settings**: switch on *Self-registration*; optionally *Email verification* and/or *SMS verification*.
2. For real delivery, choose the *SMTP* email provider and/or the *HTTP gateway* SMS provider, fill in the fields, set the secret, and use **Send test**:
   ```powershell
   cd C:\Users\FBPo48p\CRM\backend\src\Crm.Api
   dotnet user-secrets set "Email:SmtpPassword" "<smtp password>"   # production: env var Email__SmtpPassword
   dotnet user-secrets set "Sms:ApiKey" "<gateway key>"             # production: env var Sms__ApiKey
   ```
   The HTTP gateway receives `POST <url>` with JSON `{ "to": "+61…", "from": "<sender id>", "message": "…" }`. With the *Log* providers, messages are only written to the API log (including the code, in Development only), which is fine for testing but means real users receive nothing.
3. Each organisation's administrator switches on *Allow self-registration* under **Settings → Organisation** and shares the link `https://<host>/register/<workspace id>`.
4. New requests appear under **Settings → Users → Awaiting approval**, with a notification to every user administrator.

### 2.2 Transactional email (password reset, verification, approvals)
Use a transactional email provider rather than a mailbox server; they handle deliverability (SPF/DKIM, reputation, bounces). Both of these work through the built-in SMTP provider, so **no code change** is needed:

| Provider | SMTP host | Port | Username | `Email:SmtpPassword` |
|---|---|---|---|---|
| Postmark | `smtp.postmarkapp.com` | 587 | Server API token | Server API token |
| SendGrid | `smtp.sendgrid.net` | 587 | `apikey` | API key |

1. Verify your sending domain with the provider (it gives you DNS records for SPF and DKIM) and use an address on that domain as the *From address*.
2. In **Platform settings → Email provider**, choose *SMTP server*, enter host, port **587** and username, and keep *Use TLS (STARTTLS)* on. Port 465 (implicit TLS) isn't supported.
3. Set the secret: `dotnet user-secrets set "Email:SmtpPassword" "<token>"` (production: `Email__SmtpPassword`), restart the API, and use **Send test**.
4. Set `App:PublicUrl` so password-reset links point at the real site.

Candidate and customer emails (record **Email** button and automation) use the same provider. Until it's configured they are only written to the API log, and **Settings → Email log** says so. They go through an outbox: check **Settings → Email log** for failures (SMTP rejections, bad addresses), which can be retried there. Pending emails of a suspended organisation wait until it's reactivated.

### 2.3 Error tracking (Sentry)
Off unless `Sentry:Dsn` is set (environment variable `Sentry__Dsn`). When on, unhandled API exceptions and every Error-level log (including browser errors the UI reports to `/api/client-errors`) are sent to Sentry. Personal data and request bodies are **not** sent (`SendDefaultPii=false`, `MaxRequestBodySize=None`). Without a DSN the same events are in the API log.

### 2.4 Attachments storage
Files are stored under `Storage:LocalPath` as `<tenant>/<yyyyMM>/<random id>` (no user-supplied names on disk); names and types are in the `Attachments` table. Back up this folder together with the database (§5.3). Deleting a record deletes its files; deleting a tenant deletes its folder. For several API nodes, point `Storage:LocalPath` at a shared volume; cloud object storage (Azure Blob/S3) can be added as another `IFileStorage` implementation.

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
| Template upgrades | Automatic. On startup the API upgrades tenants whose `TemplateVersion` is behind their template, and logs `Upgraded tenant <slug> to <template> template v<n>`. Upgrades only add objects, fields, stages and rules; existing data and settings are untouched, and each version is applied once. Back up before deploying a release that raises a template version. |
| Careers page | The organisation's administrator switches it on under **Settings → Organisation**; the public address is `https://<host>/careers/<workspace id>`. Behind a reverse proxy, set `ForwardedHeaders:Enabled` so per-IP rate limits see the real client address. Each application is logged as `Careers application <id> for job <id> in tenant <slug>`. |
| Recruitment demo data | `./scripts/seed-recruitment.ps1 -Email <admin email>` (API running; prompts for the password) adds job openings, candidates, applications, interviews, scorecards and offers to an existing recruitment organisation through the API. Safe to re-run: it reuses candidates by email and skips applications that already have interviews or offers. |
| Interview calendars | Interviews are stored as *Meeting* activities for each interviewer, plus `.ics` downloads (`/api/interviews/{id}/ics`). There is no calendar server sync, so no outbound calls or credentials are needed. Organisation-wide invitation emails count towards `Email:DailyLimitPerTenant`. |
| Hiring analytics | Computed on request from the audit log (stage changes), for at most 20,000 applications per request. Keep the audit log if you rely on the funnel: removing old `AuditLogs` rows removes that history. |
| Offers | Candidate links are `App:PublicUrl` + `/offer/<token>`, so `App:PublicUrl` must be set in production (sending refuses without it). The link is in the outbox row (email log) but not on the record's timeline. Each answer is logged as `Offer <id> accepted/declined in tenant <slug>`; behind a reverse proxy set `ForwardedHeaders:Enabled` so the recorded IP is the candidate's. |
| Candidate privacy | Administrators handle access and erasure requests from the candidate's page (**Privacy**). Erasure can't be undone (restore from backup is the only way back), and erased people also stay in older database backups until those expire under your backup retention (`backup-db.ps1` keeps 14 days by default). The retention policy (Settings → Privacy) is off by default and only runs when an administrator clicks *Erase now*. |

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
- [ ] Monitor `/health/ready`. Back up nightly with `scripts/backup-db.ps1` (§5.4) plus WAL archiving, and back up `Storage:LocalPath`.
- [ ] Set `App:PublicUrl`, a transactional email provider (§2.2) and `Sentry:Dsn` (§2.3).
- [ ] Run a Fortify SAST scan before release.

### 5.4 Backups
`scripts/backup-db.ps1` runs `pg_dump --format=custom`, checks the archive can be read back with `pg_restore --list`, and deletes backups older than `-RetentionDays` (default 14). It reads `ConnectionStrings__Crm` / `Database__Password` from the environment (or the API's user-secrets on a developer machine) and passes the password only through `PGPASSWORD` for that process. It needs the PostgreSQL client tools (same major version as the server).
```powershell
./scripts/backup-db.ps1 -OutputDir D:\Backups\crm -RetentionDays 30
# Nightly at 02:00 (Windows Task Scheduler, run as the service account):
schtasks /Create /TN "CRM backup" /SC DAILY /ST 02:00 /TR "pwsh -NoProfile -File C:\crm\scripts\backup-db.ps1 -OutputDir D:\Backups\crm"
# Restore into an empty database:
pg_restore --clean --if-exists --no-owner -d CRM D:\Backups\crm\CRM-20261001-020000.dump
```
Copy backups off the server, and test a restore regularly.

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
| *Create an account* isn't shown / "Self-registration isn't available" | The platform switch (**Platform settings**) and the organisation opt-in (**Settings → Organisation**) must both be on, the organisation must be active, and the code must be its workspace ID. |
| Password reset emails don't arrive | The email provider is still *Log*, `App:PublicUrl` isn't set (the API logs an error), or the user is inactive or pending approval. One email per minute per user. |
| "This file type isn't allowed" / file too large | See the allow-list in API.md; raise `Attachments:MaxFileSizeMb` if needed. |
| CSV import says "No record named …" | Lookups match the related record's exact name (any case). Import the related records first (e.g. job openings before applications), or use the record ID. |
| Registered users get no code | The provider is still *Log* (check the API log), or delivery fails: use **Send test** and check `Email:SmtpPassword` / `Sms:ApiKey`. Send failures are logged but don't fail the registration; the user can request a new code. |
| A user says they're "waiting for approval" | Approve them under **Settings → Users → Awaiting approval** with at least one role or group. |
| `dotnet test` fails with file-lock errors | Stop the running API first (it locks `bin/`) |
| `NU1900` warnings during restore | The Nexus vulnerability feed returns 403. This is harmless. |
