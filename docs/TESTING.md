# CRM Platform: Test Plan and Traceability

## 1. Strategy

| Level | Tooling | Scope | Location |
|---|---|---|---|
| API integration | xUnit, `WebApplicationFactory`, SQLite in-memory | Full HTTP pipeline: auth, tenancy, RBAC, validation, customization, automation, audit | `backend/tests/Crm.Api.Tests` |
| UI unit | Vitest, `HttpTestingController` | Session handling, permission resolution, metadata, custom-field forms, list helpers | `frontend/src/**/*.spec.ts` |
| Build gates | `ng build` (AOT, strict templates), `dotnet build` | Type safety | CI |
| Live end-to-end | `scripts/smoke-test.ps1` (44 checks) | Running API + **PostgreSQL**: every module, the sales workflow, RBAC, tenant provisioning, JSONB filters, automation, audit, isolation, clean-up | `scripts/` |
| Browser end-to-end | Manual/Playwright checklist (§4) | Settings screens, custom objects, role matrix, restricted users, tenant lifecycle | UAT |

The SQLite tests exercise the same EF model and query filters. PostgreSQL-specific behaviour (`jsonb @>` filters) is covered by the live smoke test.

## 2. Automated test inventory

### API (`dotnet test`): 29 tests
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

### UI (`ng test`): 25 tests
| Spec | Tests |
|---|---|
| `auth.spec.ts` (10) | login stores session · metadata cleared when the session ends · bearer token added · refresh once on 401 and retry · session cleared on rejected refresh · session kept on 429 · restore keeps the token when the server is down · guard restores the session · refresh serialized across tabs using the latest stored token · no token to non-API URLs |
| `helpers.spec.ts` (5) | toParams · humanize · validation problem flattening · status messages (including server 403 detail) · password policy |
| `platform.spec.ts` (10) | Own/Group/All scopes on ownership · `*` default with resolved object overrides and admin areas excluded · assignment only with Group/All · administrators · partial admin access · metadata labels, routes, API URLs, stages and picklists · custom-field controls and validators · custom-field value conversion · legacy picklist values · `cf.` filter parameters |

### Live smoke test (`scripts/smoke-test.ps1`): 44 checks
1. Health and authentication (7)
2. Read every module with seeded data (11)
3. End-to-end sales workflow with tagged records (11)
4. Standard User permissions (5)
5. Clean-up (1)
6. Platform (9): provision a Help Desk tenant with demo data · its own configuration · JSONB custom-field filter · automation task and notification · stage change and audit · tenant isolation · custom field on a standard object · suspend and delete

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
