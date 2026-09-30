# CRM

A full-stack Customer Relationship Management system: accounts, contacts, leads (with conversion), opportunities (list and Kanban pipeline), activities, a dashboard with forecast and win rate, and role-based user administration.

| Layer | Stack |
|---|---|
| UI | Angular 22 (standalone, signals, `httpResource`, zoneless), Angular Material 3, CDK drag-drop, Vitest |
| API | ASP.NET Core 10 Minimal APIs, built-in validation, OpenAPI 3.1 + Scalar, ASP.NET Identity, JWT + rotating refresh tokens, rate limiting |
| Data | PostgreSQL + EF Core 10 (Npgsql), migrations |
| Delivery | Dockerfiles (API and nginx), docker-compose, GitHub Actions CI |

## Quick start (local)

```powershell
# 1) Secrets (once). The connection string and JWT key are already set in user-secrets.
cd backend\src\Crm.Api
dotnet user-secrets set "Database:Password" "<postgres password>"
dotnet user-secrets set "Seed:AdminPassword" "<admin password: 10+ chars, Aa1!>"

# 2) API: applies DB migrations on start. http://localhost:5290/scalar
cd ..\..
dotnet run --project src/Crm.Api --launch-profile http

# 3) UI (new terminal): http://localhost:4200
cd ..\frontend
npm start
```

Sign in as `admin@crm.local`.

## Tests
```powershell
cd backend;  dotnet test                    # 15 API integration tests
cd frontend; npx ng test --watch=false      # 10 UI unit tests
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
docs/      Documentation
```
