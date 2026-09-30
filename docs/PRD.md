# CRM: Product Requirements Document (PRD)

| | |
|---|---|
| **Product** | CRM (Customer Relationship Management) |
| **Version** | 1.0 (MVP) |
| **Status** | Implemented |
| **Last updated** | 2026-09-30 |

## 1. Summary

A web-based CRM that lets a sales team manage **accounts, contacts, leads, opportunities and activities** in one place. Users can track the sales pipeline visually and forecast revenue. Role-based access controls who can change what. The system has an Angular single-page app, a REST API on ASP.NET Core, and a PostgreSQL database.

## 2. Problem statement

Sales teams that track customers in spreadsheets and inboxes lose context and miss follow-ups. They also can't see pipeline health. The team needs one system of record for customer relationships that:

- shows everyone the current state of each customer and deal
- tells each rep what to do next
- gives managers a reliable, current forecast

## 3. Goals and non-goals

### Goals
| # | Goal | Success metric |
|---|------|----------------|
| G1 | One source of truth for customers and deals | 100% of active deals recorded in the CRM within 1 month of rollout |
| G2 | Faster lead follow-up | Median time from lead creation to first activity < 2 business days |
| G3 | Pipeline visibility | Managers can see open pipeline, weighted forecast and win rate without exporting data |
| G4 | No missed follow-ups | Overdue activities visible on dashboard; target < 10% of activities overdue |
| G5 | Secure by default | No OWASP Top 10 findings rated High or Critical in security review |

### Non-goals (v1)
- Marketing automation (campaigns, email sequences)
- Customer support ticketing
- Quotes, products and price books
- Email/calendar sync (Outlook/Gmail)
- Multi-tenant SaaS hosting
- Native mobile apps (the web UI is responsive)

## 4. Users and personas

| Persona | Role in system | Needs |
|---|---|---|
| **Sam, sales rep** | `SalesRep` | Capture leads fast, see "my" deals and tasks, log calls, move deals through stages |
| **Maya, sales manager** | `Manager` | Team-wide pipeline and forecast, reassign records, clean up bad data |
| **Alex, CRM administrator** | `Admin` | Manage user accounts and roles, reset passwords, full data access |

## 5. Scope and functional requirements

Priority: **M** = must have, **S** = should have, **C** = could have. Everything marked M or S is implemented in v1.

### 5.1 Authentication and session (AUTH)
| ID | Requirement | Pri |
|---|---|---|
| AUTH-1 | Users sign in with email and password. | M |
| AUTH-2 | Sessions use short-lived access tokens (30 min) and rotating refresh tokens (7 days). The session survives a page reload. | M |
| AUTH-3 | If a refresh token is reused, every session for that user is revoked (token theft detection). | M |
| AUTH-4 | The account locks for 15 minutes after 5 failed sign-ins. | M |
| AUTH-5 | Sign-in and refresh endpoints are rate-limited per client IP (10/minute by default). | M |
| AUTH-6 | Users can change their own password. Changing it signs out all their sessions. | M |
| AUTH-7 | Password policy: at least 10 characters with upper case, lower case, a digit and a symbol. | M |
| AUTH-8 | Sign out revokes the refresh token on the server. | M |
| AUTH-9 | SSO (Entra ID / OIDC) | C (future) |

### 5.2 Roles and permissions (RBAC)
| ID | Requirement | Pri |
|---|---|---|
| RBAC-1 | Three roles: **Admin**, **Manager** and **SalesRep**. A user can hold more than one. | M |
| RBAC-2 | All authenticated users can **view** all CRM records (team selling). | M |
| RBAC-3 | A SalesRep can **edit** only records they own. Managers and Admins can edit any record. | M |
| RBAC-4 | Only Managers and Admins can **delete** accounts, contacts, leads and opportunities. Owners can delete their own activities. | M |
| RBAC-5 | Only Managers and Admins can assign a record to another user. | M |
| RBAC-6 | Only Admins can manage users. | M |
| RBAC-7 | The UI hides actions the user can't perform, and the API enforces every rule. | M |

### 5.3 Accounts (ACC)
| ID | Requirement | Pri |
|---|---|---|
| ACC-1 | Create, view, edit and delete accounts (name, industry, website, phone, email, address, annual revenue, employees, description, owner). | M |
| ACC-2 | List with search (name, email, city), "my accounts" filter, sorting and paging. | M |
| ACC-3 | The account detail page shows related contacts, opportunities and an activity timeline, plus open pipeline totals. | M |
| ACC-4 | An account that still has opportunities can't be deleted (409 Conflict). | M |

### 5.4 Contacts (CON)
| ID | Requirement | Pri |
|---|---|---|
| CON-1 | Create, view, edit and delete contacts (name, email, phone, mobile, title, department, notes, account, owner). | M |
| CON-2 | List with search (name, email, account name), "my contacts" filter, sorting and paging. | M |
| CON-3 | A contact can link to an account. Deleting the account unlinks the contact but keeps it. | M |

### 5.5 Leads (LEAD)
| ID | Requirement | Pri |
|---|---|---|
| LEAD-1 | Create, view, edit and delete leads (name, company, email, phone, title, source, status, estimated value, description, owner). | M |
| LEAD-2 | Status lifecycle: New → Contacted → Qualified or Unqualified → Converted. | M |
| LEAD-3 | **Convert lead**: creates a contact, then either creates an account or attaches to an existing one, and optionally creates an opportunity. It runs in a single transaction. | M |
| LEAD-4 | Converted leads are read-only and link to the records they created. | M |
| LEAD-5 | List filters: status, source, "my leads", and search. | M |

### 5.6 Opportunities (OPP)
| ID | Requirement | Pri |
|---|---|---|
| OPP-1 | Create, view, edit and delete opportunities (name, account (required), primary contact, amount, stage, probability, expected close date, loss reason, description, owner). | M |
| OPP-2 | Stages: Prospecting (10%), Qualification (25%), Proposal (50%), Negotiation (75%), Closed Won (100%), Closed Lost (0%). Each stage sets a default probability, which the user can override while the deal is open. | M |
| OPP-3 | Moving to a closed stage records `ClosedAt`. Reopening clears it. Closed Lost can record a loss reason. | M |
| OPP-4 | **Kanban pipeline board** with drag-and-drop between stages (optimistic UI, rolled back on error). | M |
| OPP-5 | List view with search, stage filter, open/all toggle, "my deals" filter, sorting and paging. | M |

### 5.7 Activities (ACT)
| ID | Requirement | Pri |
|---|---|---|
| ACT-1 | Activity types: Task, Call, Meeting, Email, Note, each with subject, description, due date and time, and priority. | M |
| ACT-2 | An activity can link to any combination of account, contact, lead and opportunity. | M |
| ACT-3 | Mark complete or reopen. Overdue means not completed and past its due date. | M |
| ACT-4 | Activity timeline embedded on account, contact, lead and opportunity pages, with quick "Log call", "Note" and "Task" actions. | M |
| ACT-5 | Global activity list with filters: Open, Overdue, Completed or All; type; mine; and search. | M |

### 5.8 Dashboard (DASH)
| ID | Requirement | Pri |
|---|---|---|
| DASH-1 | KPIs: open pipeline, weighted forecast, won this month (count and amount), 90-day win rate, open leads, overdue activities. | M |
| DASH-2 | Charts: pipeline by stage, won revenue over the last 6 months, leads by source. | M |
| DASH-3 | "My upcoming activities" for the next 7 days, including overdue items. | M |
| DASH-4 | Toggle between team-wide and personal figures. | S |

### 5.9 User administration (ADM)
| ID | Requirement | Pri |
|---|---|---|
| ADM-1 | Admins can list, create and edit users (name, roles, active flag). | M |
| ADM-2 | Admins can reset a user's password. This revokes the user's sessions and clears any lockout. | M |
| ADM-3 | Deactivated users can't sign in, their sessions are revoked, and they don't appear in owner pickers. Their records keep them as owner. | M |
| ADM-4 | Admins can't deactivate themselves or remove their own Admin role. | M |
| ADM-5 | On first start, the system creates an initial Admin from configuration. | M |

### 5.10 Future (backlog)
Audit history per record · CSV import/export · Custom fields · Email/calendar integration · SSO · Notifications/reminders · Optimistic concurrency (ETags) · Soft delete with restore · Territory/team hierarchy · Multi-currency · Reports builder.

## 6. Non-functional requirements

| ID | Category | Requirement |
|---|---|---|
| NFR-1 | Performance | List and detail API calls p95 < 300 ms with 100k records per entity on recommended hardware. All lists are paged on the server (max 100 per page). |
| NFR-2 | Security | OWASP Top 10: parameterized queries (EF Core), server-side authorization on every endpoint, PBKDF2 password hashing (ASP.NET Identity), refresh tokens stored only as SHA-256 hashes, security headers (API and nginx), CSP, no secrets in source control, rate limiting, account lockout, open-redirect protection on login. |
| NFR-3 | Availability | Stateless API that can scale horizontally. Liveness (`/health/live`) and readiness (`/health/ready`, checks the DB) probes. |
| NFR-4 | Usability | Responsive layout down to 360 px, keyboard-accessible controls, WCAG 2.1 AA colour contrast (Material 3 tokens). |
| NFR-5 | Maintainability | Vertical-slice feature folders. Automated integration tests for the API, unit tests for the UI. CI on every push. |
| NFR-6 | Observability | Structured logging via ASP.NET Core logging. Errors returned as RFC 7807 ProblemDetails. |
| NFR-7 | Data | PostgreSQL 14+. Schema managed through EF Core migrations, with an idempotent SQL script for DBAs. All timestamps stored in UTC (`timestamptz`). |
| NFR-8 | Browser support | Latest two versions of Edge, Chrome and Firefox, and Safari 17+. |

## 7. Technology choices

| Layer | Choice | Why |
|---|---|---|
| UI | Angular 22, standalone components, signals, `httpResource`, zoneless change detection, Angular Material 3 (M3), CDK drag-drop, Vitest | Latest LTS-track Angular. Signals-first reactivity. Matches the organisation's Angular skill set. |
| API | ASP.NET Core 10 (LTS) Minimal APIs, built-in validation, OpenAPI 3.1 + Scalar UI, ProblemDetails, rate limiter | Latest .NET LTS, high performance, and first-party security and identity. |
| Auth | ASP.NET Core Identity (user store, hashing, lockout) with JWT bearer and rotating refresh tokens | Proven and standards-based. SPA-friendly. |
| Data | PostgreSQL + EF Core 10 (Npgsql) | Open source, robust, and matches the preferred DB. |
| Delivery | Dockerfiles (API and nginx), docker-compose, GitHub Actions CI | Reproducible builds and deployments. |

## 8. Assumptions and constraints
- Single organisation (single tenant). All users belong to the same sales team.
- Currency is AUD for display (configurable in `frontend/src/app/shared/list.ts`). Amounts are stored as `numeric(18,2)`.
- Development machines can't install software from the internet. Packages come through the internal Nexus mirror.
- The PostgreSQL server is remote (`SYDVMPOS01.cbs.fiserv.net`, database `crm`).

## 9. Release criteria (v1)
- [x] All requirements marked M and S implemented
- [x] API integration tests green (auth, CRUD, conversion, stage changes, permissions, validation)
- [x] UI unit tests green (auth/session handling, helpers)
- [x] Production build with no errors
- [x] Initial migration and SQL script generated
- [ ] UAT sign-off by the sales manager persona
- [ ] Security review / Fortify SAST scan with no High or Critical findings

## 10. Open questions
1. Should reps be restricted to **viewing** only their own records (private model) rather than team-wide visibility?
2. Is SSO (Entra ID) required for go-live?
3. What data retention and deletion policy applies (privacy/GDPR)?
