# CRM Platform: Product Requirements Document (PRD)

| | |
|---|---|
| **Product** | CRM Platform: a configurable, multi-tenant relationship-management platform |
| **Version** | 2.0 (platform release) |
| **Previous version** | 1.0: single-tenant sales CRM with fixed roles |
| **Status** | Implemented |
| **Last updated** | 2026-09-30 |

## 1. Summary

v1 was a sales CRM: accounts, contacts, leads, opportunities, activities, a dashboard and three fixed roles. v2 makes it a **platform**. The same product can serve a sales team, a help desk, a recruitment team, or any process that tracks "things moving through stages". It also lets administrators configure almost everything without code:

- **Multi-tenancy.** Many isolated organisations (tenants) share one deployment and one database. Each tenant has its own users, data and configuration.
- **Configurable authorization.** Administrators define roles as permission matrices: object × action × scope (None, Own, Group, All). They assign roles directly or through **groups**.
- **Metadata-driven data model.** Administrators can rename standard objects or switch them off, add **custom fields** (13 types) to any object, create entirely **new objects** with their own pipelines, and edit picklists and stages.
- **Automation.** No-code rules create tasks, send in-app notifications or set fields when records are created, updated or change stage.
- **Governance.** A field-level audit trail with per-record history and a searchable audit log.
- **Templates.** New tenants start from Sales, Help Desk, Recruitment or Blank.

## 2. Problem statement

v1 hard-coded the sales domain and three roles. Other teams asked for the same capabilities (records, pipelines, tasks, dashboards) for their own processes, such as support tickets, hiring and projects. Security asked for finer-grained access, for example "reps only see their region's deals" or "contractors can't see accounts". Each request meant a code change. The platform must:

- let each organisation shape the data model and pipeline to its process
- let administrators control precisely who can see and change what
- keep organisations strictly isolated while sharing infrastructure

## 3. Goals and non-goals

### Goals
| # | Goal | Success metric |
|---|------|----------------|
| G1 | Serve several business processes from one product | A new process (e.g. tickets) is live in under 1 hour with no deployment |
| G2 | Least-privilege access | Every permission is expressible as object × action × scope; no code change for new roles |
| G3 | Strict tenant isolation | Zero cross-tenant reads or writes. Automated tests cover isolation. |
| G4 | Traceability | Every create, update and delete of business records is audited with field-level diffs |
| G5 | Keep v1 value | All v1 sales features keep working; v1 data migrated without loss |
| G6 | Secure by default | No OWASP Top 10 findings rated High or Critical |

### Non-goals (v2)
- Per-tenant databases or schemas (see ARCHITECTURE ADR-6)
- Field-level security (hiding individual fields per role)
- Record sharing rules beyond owner, group and all
- Formula, roll-up and computed fields
- Outbound email, webhooks and external integrations in automation
- Custom page layouts and a report builder
- Self-service tenant sign-up and billing

## 4. Users and personas

| Persona | Typical role | Needs |
|---|---|---|
| **Sam, sales rep** | Standard User | Capture and work their own records, see team data where allowed |
| **Maya, sales manager** | Manager (+ group) | Team pipeline, reassign records, manage users and groups |
| **Alex, tenant administrator** | Administrator | Configure objects, fields, pipelines, roles, automation; audit changes |
| **Priya, platform operator** | Administrator + platform-admin flag | Provision, suspend and delete tenants |
| **Hanna, help-desk agent** | Custom "Agent" role | Tickets and customers only, no access to sales data |

## 5. Functional requirements

Priority: **M** = must, **S** = should, **C** = could. Everything marked M or S is implemented.

### 5.1 Authentication (AUTH), unchanged from v1 except where noted
| ID | Requirement | Pri |
|---|---|---|
| AUTH-1 | Email and password sign-in. Emails are unique across the whole platform, and each user belongs to exactly one tenant. | M |
| AUTH-2 | Short-lived JWT access tokens (30 min) and rotating refresh tokens (7 days). The session survives page reloads, and several open tabs share one session safely: refreshes are serialized across tabs. | M |
| AUTH-3 | Reusing a refresh token revokes all of that user's sessions. | M |
| AUTH-4 | Lockout for 15 minutes after 5 failed sign-ins. | M |
| AUTH-5 | Rate limits: sign-in 10/min per IP; token refresh 120/min per IP (separate policy so page reloads never trip the sign-in limit). | M |
| AUTH-6 | Change your own password, which signs out all sessions. Password policy: at least 10 characters with upper case, lower case, a digit and a symbol. | M |
| AUTH-7 | Access tokens carry the tenant id (`tid`). Tokens without one are rejected. **New** | M |
| AUTH-8 | Users of a suspended tenant can't sign in (403), and their existing tokens lose all permissions immediately. **New** | M |

### 5.2 Multi-tenancy (TEN)
| ID | Requirement | Pri |
|---|---|---|
| TEN-1 | Every business and configuration row belongs to a tenant. All queries are automatically filtered to the caller's tenant. | M |
| TEN-2 | Writes that would create or move data into another tenant are rejected at the data layer. | M |
| TEN-3 | Platform admins can list tenants, create one from a template (with an optional sample-data set and the first administrator), rename it, change its currency, suspend and reactivate it. | M |
| TEN-4 | Deleting a tenant requires it to be suspended first, and it can't be the default tenant or your own. Deletion removes all of its data. The UI requires typing the workspace ID. | M |
| TEN-5 | Each tenant has its own name, workspace ID (slug), currency and template. | M |
| TEN-6 | v1 data is migrated into a "Default" tenant. v1 Admins become platform admins. | M |

### 5.3 Authorization (RBAC)
| ID | Requirement | Pri |
|---|---|---|
| RBAC-1 | Roles are defined per tenant. Each role is a set of grants: **resource × action → scope**. | M |
| RBAC-2 | Resources are any object key (standard or custom), `*` ("all objects", the role's default for every business object including future ones), and admin areas: `users`, `groups`, `roles`, `customization`, `settings` (action *manage*) and `audit` (action *read*). | M |
| RBAC-3 | Record actions are read, create, update and delete. Scopes are **None**, **Own** (records the user owns), **Group** (records owned by any member of any of the user's groups) and **All**. | M |
| RBAC-4 | Within a role, an explicit object grant overrides the role's `*` default for that action. An explicit **None** hides the object from that role. | M |
| RBAC-5 | A user's access is the union (widest scope) of every role assigned directly and every role inherited from their groups. | M |
| RBAC-6 | `*` never grants admin areas. They must be granted explicitly. | M |
| RBAC-7 | The built-in **Administrator** role has full access, can't be edited or deleted, and only administrators can grant it. | M |
| RBAC-8 | Anti-escalation: a non-administrator can only create roles, or assign roles and groups, whose resolved access doesn't exceed their own. | M |
| RBAC-9 | Records can only be assigned to other owners within the assigner's update scope: self for Own, team for Group, anyone active for All. | M |
| RBAC-10 | Permission changes take effect on the next request (cached permissions are invalidated per tenant). | M |
| RBAC-11 | The UI hides navigation, buttons and fields the user can't use. The API enforces every rule independently. | M |
| RBAC-12 | Built-in roles per template: Administrator, Manager (all records plus users, groups and audit), Standard User (reads and creates everything, updates own). | M |

### 5.4 Groups (GRP)
| ID | Requirement | Pri |
|---|---|---|
| GRP-1 | Administrators create groups (teams, regions, departments) with members and roles. | M |
| GRP-2 | Members inherit the group's roles. | M |
| GRP-3 | "Group" scope gives access to records owned by anyone who shares a group with the user. | M |

### 5.5 Customization (CUS)
| ID | Requirement | Pri |
|---|---|---|
| CUS-1 | Standard objects (account, contact, lead, opportunity, activity) can be relabelled (singular and plural), given a new icon and menu order, and deactivated (hidden from menus, routes and dashboards). They can't be deleted. | M |
| CUS-2 | Custom objects: key, labels, icon, description, name-field label, optional pipeline, active flag and order. Custom objects get list, board, detail, create, edit and delete screens automatically. | M |
| CUS-3 | Custom fields on any object, of types Text, Text area, Number, Currency, Date, Date/time, Yes/No, Picklist, Multi-picklist, Email, Phone, URL and Lookup (to any object). Each field has required, help text, min/max, max length, options, list-column visibility and order. | M |
| CUS-4 | Custom field values are validated on the server (type, required, range, length, options, lookup target exists in the tenant). | M |
| CUS-5 | Lists show custom fields marked "show in list", and can filter by picklist and yes/no custom fields (`?cf.key=value`). | M |
| CUS-6 | Pipelines: ordered stages with a label, key, category (Open, Won or Lost), default probability and colour. Opportunities and custom objects with pipelines use them. Stages that still contain records can't be removed. | M |
| CUS-7 | Picklists for account industry and lead source. Existing values removed from a picklist stay valid on the records that already have them. | M |
| CUS-8 | A field's type can't change after creation. Deleting a field hides stored values but doesn't erase them. | S |

### 5.6 Automation (AUTO)
| ID | Requirement | Pri |
|---|---|---|
| AUTO-1 | Rules per object with trigger **Created**, **Updated** or **Stage changed** (pipeline objects only). | M |
| AUTO-2 | Conditions (all must match) on any standard or custom field: equals, not equals, greater/less than, contains, is empty, is not empty, changed. | M |
| AUTO-3 | Actions: **Create task** (subject, due in N days, priority, assign to owner or a user), **Notify** (message to owner or a user) and **Set field**. | M |
| AUTO-4 | Text supports placeholders such as `{{name}}`, `{{stage}}` and `{{cf.priority}}`. | M |
| AUTO-5 | Rules run right after the triggering save. Changes made by rules don't trigger further rules (no loops). Each rule shows its run count and last run time. | M |
| AUTO-6 | In-app notifications: a bell with an unread count (refreshed every 60 s), mark as read, mark all as read, and a link to the record. | M |

### 5.7 Audit (AUD)
| ID | Requirement | Pri |
|---|---|---|
| AUD-1 | Every create, update and delete of a business record writes an audit entry: who, when, record name and field-level old and new values, including custom fields. | M |
| AUD-2 | A **History** panel on every record detail page, visible to anyone who can read the record. | M |
| AUD-3 | A tenant-wide **audit log**, filterable by object, action, user and record name, for users with `audit:read`. | M |

### 5.8 CRM features (carried over from v1, now metadata-aware)
| ID | Requirement | Pri |
|---|---|---|
| CRM-1 | Accounts, contacts, leads (with conversion), opportunities (list and Kanban) and activities work as in v1, plus custom fields, labels and permissions. | M |
| CRM-2 | Opportunity stages come from the tenant's pipeline. The stage path, board columns, colours, probabilities and won/lost logic follow the configuration. | M |
| CRM-3 | Activities can link to custom records, and every custom record page has an activity timeline. | M |
| CRM-4 | The dashboard shows only sections the user can read and the tenant has active, plus a card and chart for each custom pipeline. Amounts use the tenant's currency. | M |
| CRM-5 | Lead conversion is available only to users who can create accounts and contacts. | M |

### 5.9 Settings (SET)
| ID | Requirement | Pri |
|---|---|---|
| SET-1 | A Settings hub showing only the areas the user may manage: Organisation, Users, Groups, Roles, Objects and fields, Picklists, Automation, Audit log. | M |
| SET-2 | Organisation settings: name and currency (ISO 4217). | M |
| SET-3 | Users: create (initial password), edit name, active flag, roles and groups; reset password. | M |

### 5.10 Future (backlog)
Field-level security · Sharing rules and territories · Formula and roll-up fields · Email and webhook automation actions · Scheduled automation (e.g. "3 days before due") · CSV import/export · Report builder · Custom layouts · SSO (Entra ID) · Tenant self sign-up · Per-tenant rate limits and quotas · Optimistic concurrency (ETags) · Soft delete and restore.

## 6. Non-functional requirements

| ID | Category | Requirement |
|---|---|---|
| NFR-1 | Performance | List and detail calls p95 < 300 ms with 100k records per object. Server-side paging (max 100). Custom-field filters use PostgreSQL `jsonb @>` containment. Permissions and metadata are cached. |
| NFR-2 | Security | OWASP Top 10 controls from v1, plus tenant isolation at the data layer, per-request permission checks and anti-escalation. |
| NFR-3 | Isolation | Tenant filters are global EF Core query filters that can't be forgotten in a handler, and a save-time guard rejects cross-tenant writes. |
| NFR-4 | Availability | Stateless API, horizontally scalable. Liveness and readiness probes. The in-process permission cache expires within 5 minutes on other nodes. |
| NFR-5 | Usability | Responsive to 360 px, keyboard accessible, WCAG 2.1 AA contrast. Labels, icons and stage colours follow each tenant's configuration. |
| NFR-6 | Maintainability | Vertical slices. Integration tests for tenancy, RBAC, customization, automation and audit. UI unit tests for permission and metadata logic. |
| NFR-7 | Data | PostgreSQL 14+. Custom fields in `jsonb`. EF Core migrations plus an idempotent SQL script. v1 → v2 migration preserves data. |
| NFR-8 | Browsers | Latest two versions of Edge, Chrome and Firefox, and Safari 17+. |

## 7. Technology choices

| Layer | Choice |
|---|---|
| UI | Angular 22 (standalone, zoneless, signals, `httpResource`), Angular Material 3, CDK drag-drop, Vitest |
| API | ASP.NET Core 10 Minimal APIs, built-in validation, OpenAPI 3.1 + Scalar, ProblemDetails, rate limiting, `IMemoryCache` |
| Auth | ASP.NET Core Identity (users, hashing, lockout) with JWT bearer and rotating refresh tokens. Custom tenant-scoped RBAC replaces Identity roles. |
| Data | PostgreSQL + EF Core 10 (Npgsql): global query filters, `jsonb` columns and `EF.Functions.JsonContains` |

## 8. Assumptions and constraints
- Shared database and schema. Tenant isolation is enforced by the application (ADR-6).
- One tenant per user, with platform-wide unique emails.
- Development machines can't install software from the internet. Packages come through the internal Nexus mirror.
- PostgreSQL is remote: `SYDVMPOS01.cbs.fiserv.net`, database **`CRM`** (case-sensitive).

## 9. Release criteria (v2)
- [x] All M and S requirements implemented
- [x] v1 → v2 data migration applied to `CRM` with no data loss (12 accounts, 24 contacts, 14 open deals preserved)
- [x] API integration tests green (29), including tenancy, RBAC override and escalation, customization, automation and audit
- [x] UI unit tests green (25), including permission resolution, metadata/custom-field helpers and cross-tab token refresh
- [x] Live smoke test green against PostgreSQL (44 checks, including tenant provisioning, JSONB filters, automation and isolation)
- [x] Browser end-to-end: custom object, fields, records, board, automation, role matrix, rep restrictions, tenant lifecycle
- [x] Production build with no errors or warnings
- [ ] UAT sign-off per template (Sales, Help Desk, Recruitment)
- [ ] Security review and Fortify SAST scan with no High or Critical findings

## 10. Open questions
1. Is a database-per-tenant option needed for customers with data-residency requirements?
2. Should tenant administrators be able to see the platform audit (tenant lifecycle events)?
3. What data retention applies to the audit log and to notifications?
4. Is SSO (Entra ID) required, and should it map IdP groups to CRM groups?
