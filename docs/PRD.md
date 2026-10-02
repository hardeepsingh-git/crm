# CRM Platform: Product Requirements Document (PRD)

| | |
|---|---|
| **Product** | CRM Platform: a configurable, multi-tenant relationship-management platform |
| **Version** | 2.0 (platform release) |
| **Previous version** | 1.0: single-tenant sales CRM with fixed roles |
| **Status** | Implemented |
| **Last updated** | 2026-10-02 |

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

### 5.10 Self-registration (REG)
| ID | Requirement | Pri |
|---|---|---|
| REG-1 | People can create their own account with their organisation's code (the workspace ID): name, email, mobile (optional unless SMS verification is on) and password. The UI offers a shareable link `/register/<code>`. | M |
| REG-2 | Self-registration works only when the **platform** switch is on (platform admin, Platform settings) **and** the **organisation** has opted in (Settings → Organisation). Otherwise the UI hides it and the API refuses it. | M |
| REG-3 | New users are created inactive with no roles and can't sign in until approved. Sign-in explains the state (pending verification, pending approval, rejected). | M |
| REG-4 | Flags **EmailVerification** and **SmsVerification** (platform settings, editable in the UI). When on, the user must enter a one-time code sent to that channel before approval; when off, the step and fields don't appear. | M |
| REG-5 | Codes: configurable length, expiry, attempt limit and resend cooldown. Stored hashed, single use. | M |
| REG-6 | Administrators (Administrator role or `users:manage`) are notified of each request and see an **Awaiting approval** list. | M |
| REG-7 | Approve requires at least one role (directly or via a group), respects anti-escalation, activates the user and emails them. Reject blocks the account. | M |
| REG-8 | Email (Log or SMTP) and SMS (Log or HTTP gateway) providers are configured in the UI and can be tested. Secrets (SMTP password, SMS API key) stay in server configuration, never in the database or API responses. | M |
| REG-9 | No account enumeration: registering an existing email, verifying and resending give the same responses as for unknown emails. Registration endpoints are rate-limited. | M |
| REG-10 | Only platform admins can change platform settings. | M |

### 5.11 Phase 0: launch foundations (OPS, FILE, IMP)
| ID | Requirement | Pri |
|---|---|---|
| OPS-1 | **Forgot password**: emailed single-use link (60 min), same response whether or not the account exists, resend cooldown, sessions revoked and lockout cleared on reset, "password changed" notice. Links use the configured public URL. | M |
| OPS-2 | **Transactional email** through a provider (Postmark/SendGrid via SMTP), configured in Platform settings. | M |
| OPS-3 | **Error tracking**: server exceptions and browser errors reach Sentry when configured, without personal data. | M |
| OPS-4 | **Backups**: scripted nightly `pg_dump` with verification and retention. | M |
| FILE-1 | **Attachments** on any record (except activities): upload, list, download, delete. Read access to see, update access to change. | M |
| FILE-2 | Allow-listed file types, size limit (10 MB default), sanitised names, content type set by the server, downloads never rendered inline. | M |
| FILE-3 | File changes appear in the record's history. | S |
| FILE-4 | Deleting a record deletes its files; deleting a tenant deletes all of its files. | M |
| IMP-1 | **CSV import** for custom objects with suggested column mapping and a downloadable template. | M |
| IMP-2 | Dry run first; **all-or-nothing** import with per-row errors by line number. | M |
| IMP-3 | Friendly values: lookups by name, case-insensitive picklists, common date/yes-no/number formats; ambiguous numbers rejected. | M |
| IMP-4 | Limits (5 MB / 5,000 rows); automation off by default for imports. | S |

### 5.12 Phase 1: recruitment core (ATS, PLAT)
| ID | Requirement | Pri |
|---|---|---|
| ATS-1 | **Candidates** (talent pool): name, email, phone, location, current title and company, skills, source, LinkedIn, expected salary, notice period, summary, plus files (résumés). | M |
| ATS-2 | **Applications** link one candidate to one job opening and move through the hiring pipeline. A candidate can apply to several jobs. Each application has a source, rating, interview date and feedback. | M |
| ATS-3 | **Pipeline per job**: the applications board and list can be narrowed to one job (or one candidate), and the view can be linked to. | M |
| ATS-4 | Realistic demo data: jobs, candidates and applications spread across the funnel. | S |
| ATS-5 | Rejections create a follow-up task so candidates hear back. | S |
| PLAT-1 | **Related lists**: a record's page lists the records that link to it (a job's applicants, a candidate's applications, a company's jobs), with View all, Pipeline and a prefilled New. | M |
| PLAT-2 | **Unique fields** (text, email, phone, URL), enforced in forms and imports, including duplicates within one file. Emails compare without case. Admins can't switch it on while duplicates exist. | M |
| PLAT-3 | **Template upgrades**: existing tenants receive new template objects and fields once, additively, without changing their data or reinstating items an admin deleted. | M |
| PLAT-4 | Lookup filters on lists and boards; a new record's name can come from its main lookup (an application is named after its candidate). | S |

### 5.13 Phase 2: candidate communication and time-based automation (MAIL, SCHED)
| ID | Requirement | Pri |
|---|---|---|
| MAIL-1 | **Email templates** per object with placeholders, including linked records (`{{cf.position.name}}`), owner and organisation. Typos in placeholders are rejected. | M |
| MAIL-2 | **Email from any record** to one of its own addresses (or a linked record's, e.g. the application's candidate), from a template or free text, with a warning for template fields that are empty on the record. Logged on the record's timeline; replies go to the sender. | M |
| MAIL-3 | Automation action **Send an email** to the owner, a user or an email field (also through a lookup). | M |
| MAIL-4 | **Reliable delivery**: outbox saved with the change, background sending, retries with backoff, failures visible and retryable in an **email log**; safe with several API instances. | M |
| MAIL-5 | **Abuse limits**: recipients restricted to the record's addresses, update access required, per-user rate limit, per-organisation daily cap, one automated email per rule/record/recipient a day, header injection prevented. | M |
| SCHED-1 | **Date-based rules**: run a set time before or after a date field (e.g. a day before an interview, 7 days after the last update), once per record and date. | M |
| SCHED-2 | Date rules never fire for dates that passed before the rule existed, and catch up after short outages. | M |
| ATS-6 | Recruitment template v3: candidate email templates, interview reminders and stalled-application chasers; candidate emails start switched off. | S |

### 5.14 Phase 3: careers page and data export (CAR, EXP)
| ID | Requirement | Pri |
|---|---|---|
| CAR-1 | **Public careers page** per recruitment organisation (`/careers/<workspace id>`), switched on by its administrator, with an introduction text. Lists open job openings marked *Show on careers page* whose closing date hasn't passed; job pages show the description but no internal data (salary, company, owner). | M |
| CAR-2 | **Online application**: name, email, phone, location, LinkedIn, cover note, CV (PDF/Word, 5 MB) and privacy consent. Creates the candidate (or reuses one with the same email) and an application in the job's pipeline, owned by the job's owner, with the CV on both records and a notification to the owner. Automation applies (e.g. acknowledgement email). | M |
| CAR-3 | **Safe by design**: existing candidates are never changed by the form; one open application per candidate and job; identical responses whatever the outcome; file type checked by content; per-IP rate limits; honeypot. | M |
| CAR-4 | Consent is recorded on the candidate (time of consent). | S |
| EXP-1 | **CSV export** of every list (standard and custom objects) with the list's filters, limited to the records the user can read. | M |
| EXP-2 | Readable files: field labels as headers (re-importable), linked records by name, stage labels; protected against CSV/formula injection; Excel-friendly UTF-8. | M |
| EXP-3 | Every export is recorded in the audit log with its filters; very large exports are refused with advice to filter. | M |

### 5.15 Phase 4: interviews, scorecards and hiring analytics (INT, SCORE, ANL)
| ID | Requirement | Pri |
|---|---|---|
| INT-1 | **Schedule interviews** on an application: date and time, duration, type (phone, video, onsite), location or meeting link, notes and 1–10 interviewers from the organisation. Needs edit access to the application. | M |
| INT-2 | Each interviewer gets a meeting on their activities and a notification. Rescheduling, changing interviewers and cancelling keep both in step. The application's *Next interview* date always shows the next scheduled interview, so interview reminders follow reschedules. | M |
| INT-3 | Optionally **email the candidate** an invitation from the *Interview invitation* template, through the outbox and its limits; the user is told when it can't be sent. | M |
| INT-4 | **My interviews**: each user's upcoming interviews and those still waiting for their feedback, with calendar (.ics) downloads. | M |
| SCORE-1 | **Scorecards**: each interviewer gives an overall recommendation (strong yes to strong no), 1–5 ratings on the organisation's interview criteria, and notes. Only interviewers can submit; one card each, editable. | M |
| SCORE-2 | **Blind feedback**: interviewers see colleagues' scorecards only after submitting their own. Hiring managers (edit access) see all; read-only users see none. The organiser is notified of new feedback. | M |
| SCORE-3 | Administrators manage the **interview criteria** picklist. | S |
| ANL-1 | **Hiring analytics**: applications, hires, rejections, average and median time to hire, interviews this week and outstanding feedback, for 30/90/180/365 days and optionally one job. | M |
| ANL-2 | **Funnel** from the stage history: how many applications reached each stage, conversion to the next stage and average days in stage; plus **source** quality (hire rate per source) and a per-job stage breakdown. Limited to the applications the user can read. | M |
| ATS-7 | Recruitment template v5 adds the interview criteria; demo data includes interviews and scorecards. | S |

### 5.16 Phase 5: offers and candidate privacy (OFFER, PRIV)
| ID | Requirement | Pri |
|---|---|---|
| OFFER-1 | **Draft an offer** on an application: job title, salary and pay period, currency, start date, response deadline (up to 60 days), a letter from the organisation's *Offer letter* template with placeholders, and internal notes. Suggested from the job and the candidate's expected salary. Needs edit access to the application; others never see pay details. | M |
| OFFER-2 | At most **one open offer** per application, enforced by the database. Drafts can be edited, previewed and deleted. | M |
| OFFER-3 | **Send** freezes the letter, moves the application to *Offer*, and emails the candidate a personal link (through the outbox and its limits). The link is also shown once to the sender; sending again replaces it. | M |
| OFFER-4 | **Candidate response page** (no account): read the terms and letter, print or save as PDF, **accept by typing their full name** (matched to the application) and ticking a confirmation, or decline with an optional reason. | M |
| OFFER-5 | **Evidence and follow-up**: time, IP address, browser and typed name are recorded; a note is added to the application; the sender and owner are notified. Offers can be withdrawn (link stops working) and expire after their deadline. | M |
| OFFER-6 | **Link security**: 256-bit random token, only its hash stored, never shown on the record timeline, rate-limited, only for active organisations. | M |
| OFFER-7 | Offer statistics in hiring analytics: sent, accepted, declined, expired, awaiting reply, acceptance rate and time to respond. | S |
| PRIV-1 | **Subject access**: an administrator downloads everything held about a candidate as a ZIP (data plus files), with scorecards included but interviewers not named. The export is audited. | M |
| PRIV-2 | **Right to erasure**: an administrator erases a candidate after typing their name: candidate, applications, interviews, scorecards, offers, notes, files, emails and notifications are deleted, and their names and values are removed from the audit log, leaving one anonymous *Erased* entry. | M |
| PRIV-3 | **Retention policy** (6–120 months, off by default): preview who would be erased (inactive, all applications closed, never hired), then erase them in batches. | M |
| ATS-8 | Recruitment template v6 adds the *Offer letter* and *Offer email* templates; demo data includes open and accepted offers. | S |

### 5.17 Phase 6: mock interviews, AI-ready feedback and appearance (MOCK, UX)
| ID | Requirement | Pri |
|---|---|---|
| MOCK-1 | **Company question library**: practice questions are records of the hiring organisation (*Practice questions*), with competency, suggested minutes, optional detail, a model answer and tips, an optional job opening (blank = every job) and a *Retired* flag. Ten starter questions can be added in one click. | M |
| MOCK-2 | **Invite a candidate** from an application: choose 1–20 library questions (job-specific ones first), a link lifetime of 1–30 days and an optional message. The questions are copied into the session, the candidate is emailed a personal link from the *Mock interview invitation* template (outbox and its limits), and the link is shown once to the sender. Needs edit access to the application. | M |
| MOCK-3 | **Candidate practice page** (`/mock/<token>`, no account): introduction, one question at a time with a timer against the suggested minutes, written answer and a 1–5 confidence rating, saved as they go (they can stop and come back), tips revealed after each answer, review and submit, then a printable recap with tips. | M |
| MOCK-4 | **Submission**: records start and finish times, answers and ratings; adds a note to the application and notifies the sender and owner. | M |
| MOCK-5 | **Recruiter review**: the hiring team sees every answer, adds a 1–5 readiness rating and private notes. Nothing the team writes is ever returned to the candidate. | M |
| MOCK-6 | **Safety**: one open session per application (database index), 256-bit token stored only as a hash, never on the timeline, rate-limited, active organisations only; resending replaces the link and keeps answers; withdrawal and expiry stop it working and hide the questions. | M |
| MOCK-7 | **AI feedback, ready to switch on**: per-answer and overall 1–5 scores, a summary, strengths and areas to improve, run on request or automatically on submission. Three switches: the server (`Ai:Enabled`, off by default), the platform connection (provider, endpoint, model or deployment, API version, key name, timeout, retries, output tokens, temperature; the key itself only in server configuration), and each organisation's opt-in with its own grading guidance. Only the job title, questions, model answers and answers are sent: never names or contact details. | S |
| MOCK-8 | Mock interview statistics in hiring analytics: sent, started, completed, completion rate, average time, self-rated confidence and readiness. | C |
| ATS-9 | Recruitment template v7 adds the *Practice questions* object and the *Mock interview invitation* template; demo data includes starter questions, a reviewed session and one awaiting the candidate. | S |
| UX-1 | **Light, dark or match device** theme for every screen, chosen from the toolbar (and on the sign-in and candidate pages); saved on the user's profile so it follows them, and remembered on the device for public pages. | M |
| UX-2 | Refreshed visual design: semantic status colours that meet WCAG AA in both themes, rounded surfaces, a clearer navigation rail, avatar initials, a skip-to-content link and visible keyboard focus; motion reduced when the device asks for it. | S |

### 5.18 Future (backlog)
Field-level security · Sharing rules and territories · Formula and roll-up fields · Webhook automation actions · HTML email and attachments in email · Inbound email (replies on the timeline) · Per-tenant time zones for schedules · Report builder · Custom layouts · SSO (Entra ID) · Tenant (organisation) self sign-up · Billing (Stripe) · Cloud file storage · Résumé parsing · Careers page branding (logo, colours) and CAPTCHA · Candidate portal and self-scheduling of interview slots · Calendar sync (Outlook/Google) · Qualified e-signatures (DocuSign) and PDF offer documents · Offer approval workflows · Scheduled (automatic) retention runs · Export permission per role · Per-tenant rate limits and quotas · Optimistic concurrency (ETags) · Soft delete and restore · Spoken (audio/video) mock interviews with transcription · AI-generated practice questions from a job description · Per-organisation AI providers and usage quotas.

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
- [x] API integration tests green (89), including tenancy, RBAC override and escalation, customization, automation, audit, self-registration, password reset, attachments, CSV import, the recruitment template, unique fields, template upgrades, email (templates, sending, outbox retries, limits), date-based rules, the careers page, CSV export, interviews, blind scorecards, hiring analytics, offers (drafting, sending, signing, expiry, withdrawal), candidate privacy (export, erasure, retention), mock interviews (library, invitation, practice, submission, review, resend, withdrawal, expiry), AI feedback through a recorded model exchange (switches, retries, no identities sent, failures) and theme preferences
- [x] UI unit tests green (84), including permission resolution, metadata/custom-field helpers, cross-tab token refresh, registration, password reset, attachments, import, related lists, lookup filters, record naming, email compose, email log, the date-rule editor, careers pages, export, interview scheduling, scorecards, the analytics page, offers, the candidate offer page, privacy settings, the mock interview panel, invitation and review dialogs, the candidate practice page, mock interview settings and the theme service
- [x] Live smoke test green against PostgreSQL (66 checks, including tenant provisioning, JSONB filters, automation, isolation, self-registration, CSV import, attachments, a recruitment tenant, email delivery through the outbox, date-based rules, an anonymous careers application, CSV export, interviews with a candidate invitation and scorecard, hiring analytics, an offer signed by the candidate, a mock interview practised, submitted and reviewed, a saved theme preference, and a candidate data export and erasure)
- [x] Browser end-to-end: custom object, fields, records, board, automation, role matrix, rep restrictions, tenant lifecycle
- [x] Production build with no errors or warnings
- [ ] UAT sign-off per template (Sales, Help Desk, Recruitment)
- [ ] Security review and Fortify SAST scan with no High or Critical findings

## 10. Open questions
1. Is a database-per-tenant option needed for customers with data-residency requirements?
2. Should tenant administrators be able to see the platform audit (tenant lifecycle events)?
3. What data retention applies to the audit log and to notifications?
4. Is SSO (Entra ID) required, and should it map IdP groups to CRM groups?
