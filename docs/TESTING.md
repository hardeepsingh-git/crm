# CRM: Test Plan and Traceability

## 1. Strategy

| Level | Tooling | Scope | Location |
|---|---|---|---|
| API integration | xUnit, `WebApplicationFactory`, SQLite in-memory | Full HTTP pipeline: routing, auth, validation, authorization, EF queries, business rules | `backend/tests/Crm.Api.Tests` |
| UI unit | Vitest (Angular `@angular/build:unit-test`), `HttpTestingController` | Session handling, interceptor refresh/retry, helpers, validators | `frontend/src/**/*.spec.ts` |
| Build gates | `ng build` (AOT + strict templates), `dotnet build` | Type safety of templates and code | CI |
| Schema drift | `dotnet ef migrations has-pending-model-changes` | Model and migrations stay in sync | CI |
| Manual / UAT | Checklist in section 4 | End-to-end UX on the real PostgreSQL | UAT environment |

Why SQLite for API tests: dev machines have no Docker, so there are no Testcontainers. The queries avoid provider-specific functions, so behaviour matches PostgreSQL. A PostgreSQL CI job can be added later with a service container.

## 2. Automated test inventory

### API (`dotnet test`): 15 tests
| Test | Covers |
|---|---|
| `Liveness_endpoint_is_anonymous` | NFR-3 |
| `Protected_endpoint_requires_token` | RBAC-7 |
| `Login_with_wrong_password_returns_401` | AUTH-1 |
| `Login_returns_tokens_and_me_returns_admin` | AUTH-1, ADM-5 |
| `Refresh_rotates_token_and_reuse_revokes_family` | AUTH-2, AUTH-3 |
| `Logout_revokes_refresh_token` | AUTH-8 |
| `Account_crud_and_search` | ACC-1, ACC-2 |
| `Invalid_payload_returns_validation_problem` | Validation, NFR-6 |
| `Contact_requires_existing_account` | CON-1, CON-3 |
| `Account_with_opportunities_cannot_be_deleted` | ACC-4 |
| `Lead_conversion_creates_account_contact_and_opportunity` | LEAD-3, LEAD-4 |
| `Stage_change_to_won_closes_opportunity_and_updates_dashboard` | OPP-2, OPP-3, OPP-4, DASH-1 |
| `Activities_can_be_completed_and_filtered` | ACT-2, ACT-3, ACT-5 |
| `Sales_rep_permissions_are_enforced` | RBAC-3, RBAC-4, RBAC-5, RBAC-6 |
| `Admin_can_manage_users` | ADM-1, ADM-3, AUTH-7 |

### UI (`ng test`): 10 tests
| Test | Covers |
|---|---|
| stores user and refresh token on login | AUTH-1, AUTH-2 |
| adds the bearer token to API calls | AUTH-2 |
| refreshes once on 401 and retries | AUTH-2 |
| clears the session when refresh fails | AUTH-2 |
| does not send tokens to non-API URLs | Security (token leakage) |
| toParams drops empty values | Query building |
| humanize splits enum values | UI labels |
| errorMessage flattens validation problem details | Error UX |
| errorMessage maps common statuses | Error UX |
| strongPassword mirrors the server policy | AUTH-7 |

## 3. Running
```powershell
cd backend;  dotnet test
cd frontend; npx ng test --watch=false
```

## 4. UAT checklist (manual, against PostgreSQL)

| # | Scenario | Expected |
|---|---|---|
| 1 | Sign in as admin, then reload the page | Stays signed in (refresh token restore) |
| 2 | Enter a wrong password 5 times | 5th attempt: "Account is temporarily locked" |
| 3 | Create a SalesRep user and sign in as them | The Users menu is hidden. `/admin/users` redirects to the dashboard. |
| 4 | As the rep, open an admin-owned account | No Edit or Delete buttons |
| 5 | Create lead → Convert with opportunity | You land on the new opportunity. The lead shows the "Converted" banner with links. |
| 6 | Drag a deal on the Pipeline board to Closed Won | The card moves, a toast appears, and the dashboard "Won this month" increases |
| 7 | Drag to Closed Lost and enter a reason | The reason appears on the opportunity detail |
| 8 | Add a Task due yesterday | It appears under Activities → Overdue and in the dashboard overdue count |
| 9 | Try to delete an account with deals | Error: "Account has opportunities…" |
| 10 | Admin deactivates a user | That user's next API call fails and they're sent back to login |
| 11 | Change your own password | You're signed out, and the new password works |
| 12 | Resize the browser to phone width | The side nav collapses to a hamburger and forms stack to one column |
