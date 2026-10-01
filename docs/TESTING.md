# CRM Platform: Test Plan and Traceability

## 1. Strategy

| Level | Tooling | Scope | Location |
|---|---|---|---|
| API integration | xUnit, `WebApplicationFactory`, SQLite in-memory | Full HTTP pipeline: auth, tenancy, RBAC, validation, customization, automation, audit | `backend/tests/Crm.Api.Tests` |
| UI unit | Vitest, `HttpTestingController` | Session handling, permission resolution, metadata, custom-field forms, list helpers | `frontend/src/**/*.spec.ts` |
| Build gates | `ng build` (AOT, strict templates), `dotnet build` | Type safety | CI |
| Live end-to-end | `scripts/smoke-test.ps1` (60 checks) | Running API + **PostgreSQL**: every module, the sales workflow, RBAC, tenant provisioning, JSONB filters, automation, audit, isolation, CSV import, attachments, password reset, self-registration and approval, clean-up | `scripts/` |
| Browser end-to-end | Manual/Playwright checklist (§4) | Settings screens, custom objects, role matrix, restricted users, tenant lifecycle | UAT |

The SQLite tests exercise the same EF model and query filters. PostgreSQL-specific behaviour (`jsonb @>` filters) is covered by the live smoke test.

## 2. Automated test inventory

### API (`dotnet test`): 74 tests
| Test | Covers |
|---|---|
| `Liveness_endpoint_is_anonymous` | NFR-4 |
| `Protected_endpoint_requires_token` | RBAC-11 |
| `Login_with_wrong_password_returns_401` | AUTH-1 |
| `Login_returns_tokens_and_me_returns_admin` | AUTH-1, AUTH-7, TEN-6 (roles, tenant, platform admin in `/me`) |
| `Refresh_rotates_token_and_reuse_revokes_family` | AUTH-2, AUTH-3 |
| `Refresh_has_its_own_rate_limit_and_login_stays_limited` | AUTH-5 |
| `Logout_revokes_refresh_token` | AUTH-2 |
| `Account_crud_and_search` | CRM-1 |
| `Invalid_payload_returns_validation_problem` | Validation |
| `Contact_requires_existing_account` | CRM-1 |
| `Account_with_opportunities_cannot_be_deleted` | CRM-1 |
| `Lead_conversion_creates_account_contact_and_opportunity` | CRM-1, CRM-5 |
| `Stage_change_to_won_closes_opportunity_and_updates_dashboard` | CRM-2, CRM-4 |
| `Activities_can_be_completed_and_filtered` | CRM-1 |
| `Sales_rep_permissions_are_enforced` | RBAC-3, RBAC-9, RBAC-12 |
| `Admin_can_manage_users` | SET-3 |
| `Tenants_cannot_see_each_others_data` | TEN-1, TEN-2, AUTH-1 |
| `Templates_provision_their_own_configuration_and_demo_data` | TEN-3, CUS-2, CUS-3 |
| `Suspended_tenants_cannot_sign_in_and_only_platform_admins_manage_tenants` | AUTH-8, TEN-3 |
| `Suspended_tenants_can_be_deleted_with_all_their_data` | TEN-4 |
| `Group_scope_shares_records_within_a_team_only` | RBAC-3, GRP-1..3, RBAC-10 |
| `Object_grants_override_the_wildcard_and_roles_combine` | RBAC-4, RBAC-5 |
| `Escalation_check_uses_resolved_access_not_raw_grants` | RBAC-8 |
| `Non_administrators_cannot_escalate_privileges` | RBAC-6, RBAC-7, RBAC-8 |
| `Picklist_changes_keep_existing_values_editable` | CUS-7 |
| `Custom_objects_with_fields_pipeline_and_filters` | CUS-2..6, CRM-3 |
| `Standard_objects_accept_custom_fields_and_configurable_stages` | CUS-1, CUS-3, CUS-6, CRM-2 |
| `Automation_rules_create_tasks_notifications_and_set_fields` | AUTO-1..6 |
| `Changes_are_audited_with_field_level_diffs` | AUD-1..3 |
| `Registration_is_available_only_when_the_platform_and_the_tenant_both_allow_it` | REG-1, REG-2 |
| `Registered_users_cannot_sign_in_until_an_administrator_approves_them` | REG-3, REG-6, REG-7, REG-8 |
| `Email_and_mobile_must_be_verified_before_approval` | REG-4 |
| `Codes_lock_after_too_many_attempts_and_resends_respect_the_cooldown` | REG-5 |
| `Registration_and_verification_do_not_reveal_existing_accounts` | REG-9 |
| `Rejected_registrations_cannot_sign_in_and_settings_are_platform_admin_only` | REG-7, REG-10 |
| `Deleting_a_tenant_removes_its_registrations` | TEN-4 |
| `Reset_link_sets_a_new_password_signs_out_everywhere_clears_lockout_and_works_once` | OPS-1 |
| `Requests_reveal_nothing_skip_inactive_users_respect_the_cooldown_and_links_expire` | OPS-1 |
| `Files_upload_download_show_in_history_and_are_removed_with_their_record` | FILE-1, FILE-3, FILE-4 |
| `Unsafe_or_oversized_files_are_rejected` | FILE-2 |
| `Access_follows_the_record_permissions` | FILE-1 |
| `File_names_are_sanitised` (5 cases) | FILE-2 |
| `Positions_and_applications_import_with_suggested_mapping_lookups_by_name_and_all_or_nothing` | IMP-1..4 (candidates imported first, applications linked to candidate and job by name) |
| `Bad_files_and_mappings_are_rejected_before_anything_is_saved` | IMP-2, IMP-4 |
| `Csv_parser_handles_quotes_line_breaks_delimiters_and_reports_line_numbers` | IMP-1, IMP-4 |
| `Numbers_are_parsed_strictly` (7 cases) | IMP-3 |
| `Recruitment_template_has_a_talent_pool_and_applications_link_candidates_to_jobs` | ATS-1, ATS-2, ATS-4, ATS-5 |
| `Unique_fields_block_duplicates_in_forms_imports_and_settings` | PLAT-2 |
| `Boards_and_lists_filter_by_linked_job_or_candidate` | ATS-3, PLAT-1 |
| `Older_recruitment_tenants_are_upgraded_once_without_touching_their_data` | PLAT-3 |
| `Addresses_and_subjects_are_sanitised` | MAIL-5 |
| `Templates_validate_placeholders_and_stay_while_rules_use_them` | MAIL-1 |
| `Users_email_a_records_own_addresses_and_it_is_logged_and_delivered` | MAIL-2, MAIL-4, MAIL-5 |
| `Automation_emails_candidates_once_and_retries_failed_deliveries` | MAIL-3, MAIL-4, MAIL-5 |
| `Date_based_rules_fire_once_per_due_date_and_never_for_the_past` | SCHED-1, SCHED-2 |
| `Daily_limit_caps_sending_and_deleting_a_tenant_removes_its_mail` | MAIL-5, TEN-4 |
| `Recruitment_tenants_get_email_templates_and_reminders_once_with_candidate_emails_off` | ATS-6, PLAT-3 |
| `Careers_page_is_off_until_enabled_and_lists_only_published_open_jobs` | CAR-1 |
| `Applying_creates_the_candidate_and_application_attaches_the_cv_and_tells_the_recruiter` | CAR-2, CAR-4 |
| `Reapplying_never_changes_existing_candidates_or_duplicates_applications` | CAR-3 |
| `Invalid_or_abusive_submissions_are_refused` | CAR-3 |
| `Cells_are_quoted_and_formulas_are_neutralised` | EXP-2 |
| `Exports_follow_list_filters_and_permissions_show_names_and_are_audited` | EXP-1, EXP-2, EXP-3 |
| `Standard_objects_export_with_their_own_filters_and_large_exports_are_refused` | EXP-1, EXP-3 |

The dispatcher and scheduler are switched off in tests (`BackgroundJobs:Enabled=false`) and called directly with explicit times, so retries and due dates are tested without waiting.

The registration tests replace the email and SMS senders with an in-memory capture (`CapturedMessages`), so they read the real codes that were sent.

### UI (`ng test`): 60 tests
| Spec | Tests |
|---|---|
| `auth.spec.ts` (10) | login stores session · metadata cleared when the session ends · bearer token added · refresh once on 401 and retry · session cleared on rejected refresh · session kept on 429 · restore keeps the token when the server is down · guard restores the session · refresh serialized across tabs using the latest stored token · no token to non-API URLs |
| `helpers.spec.ts` (5) | toParams · humanize · validation problem flattening · status messages (including server 403 detail) · password policy |
| `platform.spec.ts` (10) | Own/Group/All scopes on ownership · `*` default with resolved object overrides and admin areas excluded · assignment only with Group/All · administrators · partial admin access · metadata labels, routes, API URLs, stages and picklists · custom-field controls and validators · custom-field value conversion · legacy picklist values · `cf.` filter parameters |
| `registration.spec.ts` (8) | unavailable organisation message · mobile required when SMS is on, trimmed payload, continue to verification · password mismatch blocked, done message without verification · per-channel verify and resend, then pending approval · sign-in page offers registration only when the platform allows it · Enter in the code forms is handled in-app (real DOM submit) · platform settings: flags, log-only warning, secret status, clean save payload · phone pattern |
| `phase0.spec.ts` (7) | forgot password via Enter, no account disclosure · reset with token: mismatch blocked, expired-link message, success · attachments listed, upload only for editors · attachment pre-checks and sizes · CSV import: mapping, required fields, row errors, dry run then import · template CSV quoting · browser error reporting (once, not for HTTP errors or signed-out users) |
| `phase1.spec.ts` (6) | related objects found only where readable · name source lookup from the name label · new application prefilled, named after the chosen candidate, typed names kept · related list query, count, Pipeline and View all links · `cf.` deep links become lookup filters and stay in sync · Unique option only for text-like fields |
| `phase2.spec.ts` (7) | schedule offsets to/from minutes · placeholder insertion at the cursor · compose: record addresses, template, blank-field warning, send · no-address message · Email button only with update access · date-based rule with a SendEmail action saves the right payload · email log: Log-provider banner, daily usage, retry |
| `phase3.spec.ts` (7) | job facts and applicant-friendly error messages · careers page lists jobs with links · unavailable careers page · application checked in the browser, then sent as multipart with CV, consent and empty honeypot · oversized CV refused before upload, server errors shown · export downloads the filtered list (paging dropped) and explains a too-large export · careers settings saved |

### Live smoke test (`scripts/smoke-test.ps1`): 60 checks
1. Health and authentication (7)
2. Read every module with seeded data (11)
3. End-to-end sales workflow with tagged records (11)
4. Standard User permissions (5)
5. Clean-up (1)
6. Platform (25): remove test tenants left by interrupted runs · provision a Help Desk tenant with demo data · its own configuration · JSONB custom-field filter · automation task and notification · stage change and audit · tenant isolation · custom field on a standard object · CSV import (bad row reported, then all-or-nothing import) · attachment upload/download/delete and a refused script upload · forgot-password gives the same 202 for known and unknown emails · self-registration needs the platform switch and the organisation opt-in · registered user blocked (403) and admins notified · approval needs a role, then sign-in works · restore platform settings · provision a Recruitment tenant with demo jobs, candidates and applications · duplicate candidate email refused (any case) and flagged within an import file · pipeline per job (board and list filtered by job or candidate) · email a candidate from a template, refused for an outside address, delivered by the background dispatcher and logged on the timeline · date-based rule validated and saved · careers page switched on, anonymous application with a PDF CV lands in the job's pipeline, a script upload is refused · CSV export of that candidate's applications, with names and an audit entry · delete the recruitment tenant · suspend and delete (removes the tenant's files too)

The registration checks temporarily switch on global self-registration (with verification off) and always put the original platform settings back.

## 3. Running
```powershell
cd backend;  dotnet test                      # stop the API first
cd frontend; npx ng test --watch=false
./scripts/smoke-test.ps1                      # API running; -Keep leaves the [E2E] records in place
```
VS Code tasks: *CRM: Backend tests*, *CRM: Frontend tests*, *CRM: Smoke test (API end-to-end)*.

## 4. UAT / browser checklist

| # | Scenario | Expected |
|---|---|---|
| 1 | Sign in as admin, then reload | Still signed in. The navigation shows the tenant's objects plus **Settings** and **Tenants**. |
| 2 | **Settings → Objects & fields → New object** "Support Case" with a pipeline | It appears in the navigation immediately with 4 default stages |
| 3 | Add fields: Priority (picklist, required, in list), Customer (lookup → Account), Estimated hours (number), Billable (yes/no, in list) | They show in the object's field table |
| 4 | Create a Support Case without Priority | "Priority is required". After filling it in, the record opens and the Customer shows as a link to the account. |
| 5 | Filter the list by Priority / Billable | Only matching records are shown |
| 6 | Click **In Progress** on the stage path, then open **History** | Stage chip updated. History shows "Stage: New → In Progress". |
| 7 | **Automation → New rule**: Support Case created, Priority equals Urgent → Create task + Notify | Creating an urgent case adds a task to its activity panel and a notification (bell badge) whose message has the placeholders filled in |
| 8 | **Roles → Standard User**: Support Cases read Own, create All, update Own | Summary shows "Support Cases (Own)" |
| 9 | Sign in as `rep@crm.local` | No Settings or Tenants. Support Cases list shows only their own cases. `/settings/roles` redirects to the dashboard. |
| 10 | As the rep, open someone else's account, then their own | Other's: no Edit/Delete. Own: Edit only, and no owner picker in the form. |
| 11 | **Roles**: set Leads read = None for Standard User | The rep no longer sees Leads in the navigation. `/leads` redirects. The API returns 403. |
| 12 | **Groups**: create "East" with rep + manager, and a role with Accounts update = Group | Group members can edit each other's accounts but not others' |
| 13 | **Tenants → New tenant** from Help Desk with sample data | Tenant listed with template `helpdesk`. Its admin sees Tickets and Customers, and none of the default tenant's data. |
| 14 | Suspend that tenant and sign in as its admin | "Your organisation's account is suspended" |
| 15 | Delete it (type the wrong ID, then the right one) | The wrong ID is refused. The right one removes the tenant. |
| 16 | **Picklists**: remove an industry that an account uses, then edit that account | Saving works and the old value stays selectable |
| 17 | **Audit log**: filter by object/user | Entries show field-level before and after |
| 18 | Organisation: change the currency to EUR | Amounts across the app show € |
| 19 | Phone width | The navigation collapses and forms stack |
| 20 | **Platform settings**: turn on self-registration and email verification (Log provider). **Settings → Organisation**: allow self-registration and copy the link. | The sign-in page shows *Create an account*. The organisation page shows the shareable link. |
| 21 | Open the link in a private window and register | The verify page asks for the email code. In Development the code is in the API console. |
| 22 | Sign in as the new user before approval | "Waiting for administrator approval" (not a generic error) |
| 23 | As admin: bell notification → **Users → Awaiting approval → Approve** without a role, then with a role | Refused without a role. With one, the user can sign in. |
| 24 | Turn the platform switch off | *Create an account* disappears and `/register/<code>` says registration isn't available |
| 25 | Recruitment tenant: open **Candidates**, then a candidate | Their **Applications** list shows each job they applied for, with stage chips |
| 26 | Open a job opening → **Applications** → **Pipeline** | The board shows only that job's applicants, with the job in the filter bar. **List view** keeps the filter. |
| 27 | On that board, **New Application**, pick a candidate | The job is already filled in, and the name fills in with the candidate's name |
| 28 | Create a candidate with an email another candidate has (different case) | "Another record already has this Email" |
| 29 | **Objects & fields** → a text field → tick **Unique** while duplicates exist | Refused, with the number of duplicates |
| 30 | Open an application → **Email** → template *Interview invitation* | To is the candidate's email. A warning lists *Next interview* if it isn't set. After **Send**, an Email entry appears on the timeline. |
| 31 | **Settings → Email log** | The email is listed as Sent (with the Log provider, a banner says it went to the server log) |
| 32 | **Settings → Automation** → switch on *Email rejected candidates*, then move an application to Rejected | The candidate gets the *Application outcome* email; moving it again within a day doesn't send a second one |
| 33 | **Automation → New rule**: Application *reaches a date*, 2 hours before *Next interview*, Notify owner | Saved and listed as "reaches 2 hours before Next interview" |
| 34 | **Settings → Email templates**: new template, click fields to insert them, type `{{oops}}` | Saving with `{{oops}}` is refused; deleting a template a rule uses is refused |
| 35 | **Settings → Organisation**: switch on *Publish a public careers page*, add an introduction, open the link in a private window | The page lists the open jobs with *Show on careers page* ticked |
| 36 | Apply for a job with a PDF CV; try once without ticking consent, once with a renamed .exe | Missing consent and the fake PDF are refused with clear messages. The real application appears in the job's pipeline as *Applied*, the CV is under Files on the application and candidate, and the job owner gets a notification. |
| 37 | Apply again with the same email but a different name | The existing candidate's name is unchanged; the application gets an "Applied again" note and the new CV |
| 38 | Applications list → filter by a job → **Export**; open the file in Excel | Only that job's applications, with candidate and job names; the export shows in the audit log |
