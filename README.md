# CRM Platform

A configurable, **multi-tenant** relationship-management platform. It ships as a full sales CRM (accounts, contacts, leads with conversion, opportunity pipeline, activities, dashboard), and each organisation can reshape it into a help desk, an applicant-tracking system or anything else built on records moving through stages.

- **Multi-tenancy**: isolated organisations in one deployment. Platform admins provision tenants from templates (Sales, Help Desk, Recruitment, Blank), and can suspend or delete them.
- **Authorization**: roles as permission matrices (object × read/create/update/delete × None/Own/Group/All), groups (teams) that grant roles and share records, per-object overrides, admin-area permissions and anti-escalation.
- **Customization**: rename or disable standard objects, create custom objects with their own pipelines, add 13 types of custom fields, and edit stages and picklists. Lists, forms, filters, boards and dashboards adapt automatically.
- **Automation**: no-code rules that create tasks, send notifications or set fields.
- **Governance**: field-level audit trail, per-record history and in-app notifications.
- **Files and data loading**: attach résumés, contracts and other documents to any record, and bulk-load custom-object data from CSV with a validated, all-or-nothing import.
- **Recruitment (ATS)**: a talent pool of candidates, applications that link a candidate to a job and move through hiring stages, a pipeline per job, and unique candidate emails. Related lists on every record page show what links to it. Existing recruitment tenants are upgraded automatically and additively.
- **Operations**: password reset by email, error tracking (Sentry, optional) and a backup script.
- **Self-registration** (optional): people sign up with their organisation's code, optionally verify their email and/or mobile with a one-time code, and can sign in only after an administrator approves them and assigns roles or groups. The platform switches and the email/SMS providers are configured in the UI (**Platform settings**).

| Layer | Stack |
|---|---|
| UI | Angular 22 (standalone, zoneless, signals, `httpResource`), Angular Material 3, CDK drag-drop, Vitest |
| API | ASP.NET Core 10 Minimal APIs, built-in validation, OpenAPI 3.1 + Scalar, ASP.NET Identity + JWT with rotating refresh tokens, custom tenant-scoped RBAC |
| Data | PostgreSQL + EF Core 10 (Npgsql): global tenant filters, `jsonb` custom fields |

## Quick start (local)

The secrets are already configured (connection string `Database=CRM`, JWT key, admin email, demo data). If you're on a new machine:
```powershell
cd backend\src\Crm.Api
dotnet user-secrets set "Database:Password" "<postgres password>"
dotnet user-secrets set "Seed:AdminPassword" "<admin password: 10+ chars, Aa1!>"
```

Start it with *Tasks: Run Task* → **CRM: Start all** in VS Code, or run `./scripts/start-dev.ps1`. Then open http://localhost:4200. API docs are at http://localhost:5290/scalar.

| User | Access | Password |
|---|---|---|
| `admin@crm.local` | Administrator of the Default tenant + platform admin | `Seed:AdminPassword` |
| `manager@crm.local` | Manager (demo) | `Seed:DemoUserPassword` |
| `rep@crm.local` | Standard User (demo) | `Seed:DemoUserPassword` |

To look up a password: `dotnet user-secrets list` in `backend/src/Crm.Api`. Don't share the output.

## Tests
```powershell
cd backend;  dotnet test                    # 60 API integration tests (stop the API first)
cd frontend; npx ng test --watch=false      # 46 UI unit tests
./scripts/smoke-test.ps1                    # 55 live end-to-end checks against PostgreSQL (API running)
```

## Documentation
| Doc | Contents |
|---|---|
| [docs/PRD.md](docs/PRD.md) | Requirements: tenancy, RBAC, customization, automation, audit, NFRs, release criteria |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Tenancy enforcement, permission resolution, metadata, automation pipeline, security, ADRs |
| [docs/API.md](docs/API.md) | REST reference, including admin, customization, automation and platform endpoints |
| [docs/DATA-MODEL.md](docs/DATA-MODEL.md) | Tables, JSON columns, indexes, v1 → v2 migration |
| [docs/OPERATIONS.md](docs/OPERATIONS.md) | Configuration, local setup, tenant operations, deployment, troubleshooting |
| [docs/TESTING.md](docs/TESTING.md) | Test inventory and traceability, UAT checklist |
| [docs/USER-GUIDE.md](docs/USER-GUIDE.md) | End users, administrators and platform operators |

## Structure
```
backend/   ASP.NET Core API (src/Crm.Api), integration tests, database/crm-schema.sql
frontend/  Angular app (metadata-driven UI, settings hub, platform console), nginx config
scripts/   start-dev.ps1 (run locally), smoke-test.ps1 (live end-to-end test), backup-db.ps1 (nightly pg_dump with retention)
docs/      Documentation
```
