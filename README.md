# CRM

A full-stack Customer Relationship Management system: accounts, contacts, leads (with conversion), opportunities (list and Kanban pipeline), activities, a dashboard with forecast and win rate, and role-based user administration.

| Layer | Stack |
|---|---|
| UI | Angular 22 (standalone, signals, `httpResource`, zoneless), Angular Material 3, CDK drag-drop, Vitest |
| API | ASP.NET Core 10 Minimal APIs, built-in validation, OpenAPI 3.1 + Scalar, ASP.NET Identity, JWT + rotating refresh tokens, rate limiting |
| Data | PostgreSQL + EF Core 10 (Npgsql), migrations |
| Delivery | Dockerfiles (API and nginx), docker-compose, GitHub Actions CI |

## Quick start (local)

One-time secrets (stored in your Windows profile, not the repo). The connection string (`Database=CRM`) and JWT key are already set:
```powershell
cd backend\src\Crm.Api
dotnet user-secrets set "Database:Password" "<postgres password>"
dotnet user-secrets set "Seed:AdminPassword" "<admin password: 10+ chars, Aa1!>"
```

Start everything (pick one):
- VS Code: *Tasks: Run Task* → **CRM: Start all**
- PowerShell: `./scripts/start-dev.ps1`

Then open http://localhost:4200. The API applies migrations and seeds data on start; its docs are at http://localhost:5290/scalar.

| User | Role | Password |
|---|---|---|
| `admin@crm.local` | Admin | `Seed:AdminPassword` |
| `manager@crm.local` | Manager (demo) | `Seed:DemoUserPassword` |
| `rep@crm.local` | Sales Rep (demo) | `Seed:DemoUserPassword` |

## Tests
```powershell
cd backend;  dotnet test                    # 16 API integration tests
cd frontend; npx ng test --watch=false      # 13 UI unit tests
./scripts/smoke-test.ps1                    # 35 live end-to-end checks (API running)
```

## Documentation
| Doc | Contents |
|---|---|
| [docs/PRD.md](docs/PRD.md) | Product requirements, scope, NFRs, release criteria |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Design, auth flow, security controls, decisions |
| [docs/API.md](docs/API.md) | REST endpoint reference |
| [docs/DATA-MODEL.md](docs/DATA-MODEL.md) | ERD, tables, indexes, conventions |
| [docs/OPERATIONS.md](docs/OPERATIONS.md) | Configuration, local setup, deployment, troubleshooting |
| [docs/TESTING.md](docs/TESTING.md) | Test strategy, traceability, UAT checklist |
| [docs/USER-GUIDE.md](docs/USER-GUIDE.md) | End-user guide |

## Structure
```
backend/   ASP.NET Core API (src/Crm.Api), tests, database/crm-schema.sql
frontend/  Angular app, nginx config
scripts/   start-dev.ps1 (run locally), smoke-test.ps1 (live end-to-end test)
docs/      Documentation
```
