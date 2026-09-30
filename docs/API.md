# CRM: API Reference

- Base URL: `/api`. In development it's proxied from `http://localhost:4200`. The API listens on `http://localhost:5290` and `https://localhost:7185`.
- Interactive docs (Development): **Scalar UI** at `/scalar` and the OpenAPI JSON at `/openapi/v1.json`.
- Format: JSON (camelCase). Enums are strings. Timestamps are ISO-8601 UTC. Dates (`expectedCloseDate`) use `yyyy-MM-dd`.
- Auth: `Authorization: Bearer <accessToken>` on every endpoint except login, refresh and logout.
- Errors: [RFC 7807 ProblemDetails](https://www.rfc-editor.org/rfc/rfc7807). Validation errors return `400` with an `errors` map, for example `{ "errors": { "name": ["The Name field is required."] } }`.

## Common conventions

**Paged lists** accept `page` (1-based), `pageSize` (1–100, default 20), `search`, `sortBy`, `sortDir` (`asc`/`desc`) and `mine` (`true` = records you own), and return:

```json
{ "items": [ ... ], "totalCount": 123, "page": 1, "pageSize": 20 }
```

**Status codes:**

| Code | Meaning |
|---|---|
| 200 / 201 / 204 | OK / Created (with `Location`) / No Content |
| 400 | Validation failed |
| 401 | Missing, invalid or expired token, or bad credentials |
| 403 | Authenticated but not allowed (not owner, wrong role) |
| 404 | Not found |
| 409 | Business rule conflict (e.g. account has opportunities, lead already converted) |
| 423 | Account locked out |
| 429 | Rate limit exceeded |

## Auth (`/api/auth`)

| Method | Path | Body | Response |
|---|---|---|---|
| POST | `/login` | `{ email, password }` | `AuthResponse` · 401 · 423 · 429 |
| POST | `/refresh` | `{ refreshToken }` | `AuthResponse` (rotated) · 401 |
| POST | `/logout` | `{ refreshToken }` | 204 |
| GET | `/me` | none | `UserInfo` |
| POST | `/change-password` | `{ currentPassword, newPassword }` | 204 (all sessions revoked) · 400 |

```json
// AuthResponse
{
  "accessToken": "eyJ...",
  "expiresAt": "2026-09-30T06:00:00Z",
  "refreshToken": "base64...",
  "user": { "id": "0199...", "email": "admin@crm.local", "firstName": "System", "lastName": "Administrator",
            "fullName": "System Administrator", "roles": ["Admin"] }
}
```

## Dashboard

| Method | Path | Query | Response |
|---|---|---|---|
| GET | `/api/dashboard` | `mine` | `DashboardSummary`: KPIs, `pipelineByStage[]`, `leadsBySource[]`, `wonByMonth[]` (6 months), `upcomingActivities[]` (current user, next 7 days, including overdue) |

## Accounts (`/api/accounts`)

| Method | Path | Notes |
|---|---|---|
| GET | `/` | Filters: `search` (name/email/city), `industry`, `ownerId`, `mine`. Sort by: `name` (default), `industry`, `city`, `createdAt` |
| GET | `/{id}` | `AccountDetail` including `contactCount`, `openOpportunityCount`, `openPipelineAmount` |
| POST | `/` | `AccountRequest`, returns 201 |
| PUT | `/{id}` | Owner, Manager or Admin. Returns 403 otherwise. |
| DELETE | `/{id}` | Manager or Admin. Returns **409** if the account has opportunities. |

`AccountRequest`: `name*` (≤200), `industry`, `website` (http/https URL), `phone`, `email`, `street`, `city`, `state`, `postalCode`, `country`, `annualRevenue` (≥0), `employeeCount` (≥0), `description` (≤4000), `ownerId`.

## Contacts (`/api/contacts`)

| Method | Path | Notes |
|---|---|---|
| GET | `/` | Filters: `search` (name/email/account), `accountId`, `ownerId`, `mine`. Sort by: `lastName` (default), `firstName`, `email`, `account`, `createdAt` |
| GET | `/{id}` | `ContactDetail` |
| POST | `/` | `ContactRequest` |
| PUT | `/{id}` | Owner, Manager or Admin |
| DELETE | `/{id}` | Manager or Admin |

`ContactRequest`: `firstName*`, `lastName*`, `email`, `phone`, `mobile`, `jobTitle`, `department`, `description`, `accountId` (must exist), `ownerId`.

## Leads (`/api/leads`)

| Method | Path | Notes |
|---|---|---|
| GET | `/` | Filters: `search`, `status`, `source`, `ownerId`, `mine`. Sort by: `createdAt` (default, desc), `name`, `company`, `status`, `estimatedValue` |
| GET | `/{id}` | `LeadDetail` (includes `converted*Id` links) |
| POST | `/` | `LeadRequest`. `status` can't be `Converted`. |
| PUT | `/{id}` | Returns 409 if the lead is already converted |
| POST | `/{id}/convert` | `LeadConvertRequest`, returns `{ accountId, contactId, opportunityId? }`. Returns 409 if already converted. |
| DELETE | `/{id}` | Manager or Admin |

`LeadRequest`: `firstName*`, `lastName*`, `company*`, `email`, `phone`, `jobTitle`, `source` (`Web|Referral|Event|ColdCall|Advertisement|Partner|Other`), `status` (`New|Contacted|Qualified|Unqualified`), `estimatedValue`, `description`, `ownerId`.

`LeadConvertRequest`: `existingAccountId?` (omit to create an account from `company`), `createOpportunity`, `opportunityName?`, `amount?` (defaults to `estimatedValue`), `expectedCloseDate?` (defaults to today + 30 days).

## Opportunities (`/api/opportunities`)

| Method | Path | Notes |
|---|---|---|
| GET | `/` | Filters: `search` (name/account), `stage`, `accountId`, `contactId`, `open` (true/false), `ownerId`, `mine`. Sort by: `expectedCloseDate` (default), `name`, `amount`, `stage`, `account`, `createdAt` |
| GET | `/pipeline` | All open deals plus deals closed in the last 30 days (max 500), for the Kanban board. Query: `mine` |
| GET | `/{id}` | `OpportunityDetail` |
| POST | `/` | `OpportunityRequest` |
| PUT | `/{id}` | Owner, Manager or Admin |
| PATCH | `/{id}/stage` | `{ stage, lossReason? }`. Resets probability to the stage default and sets or clears `closedAt`. |
| DELETE | `/{id}` | Manager or Admin |

`OpportunityRequest`: `name*`, `accountId*`, `contactId`, `amount*` (≥0), `stage` (`Prospecting|Qualification|Proposal|Negotiation|ClosedWon|ClosedLost`), `probability?` (0–100; null means the stage default), `expectedCloseDate*`, `lossReason` (kept only for ClosedLost), `description`, `ownerId`.

## Activities (`/api/activities`)

| Method | Path | Notes |
|---|---|---|
| GET | `/` | Filters: `search` (subject), `type`, `status` (`Open|Completed|Overdue`), `accountId`, `contactId`, `leadId`, `opportunityId`, `dueFrom`, `dueTo`, `ownerId`, `mine`. Sort by: `dueAt` (default; nulls last), `subject`, `priority`, `createdAt` |
| GET | `/{id}` | `ActivityItem` |
| POST | `/` | `ActivityRequest` |
| PUT | `/{id}` | Owner, Manager or Admin |
| POST | `/{id}/complete` | Sets `completedAt` |
| POST | `/{id}/reopen` | Clears `completedAt` |
| DELETE | `/{id}` | Owner, Manager or Admin |

`ActivityRequest`: `type` (`Task|Call|Meeting|Email|Note`), `subject*`, `description`, `dueAt`, `priority` (`Low|Normal|High`), `accountId`, `contactId`, `leadId`, `opportunityId`, `ownerId`.

## Users (`/api/users`)

| Method | Path | Role | Notes |
|---|---|---|---|
| GET | `/lookup` | any | Active users `{ id, fullName, email }`, used by owner pickers |
| GET | `/` | Admin | All users with roles, status and lockout |
| POST | `/` | Admin | `{ email, firstName, lastName, password, roles[] }` |
| PUT | `/{id}` | Admin | `{ firstName, lastName, isActive, roles[] }`. You can't deactivate yourself or drop your own Admin role. Deactivating a user revokes their sessions. |
| POST | `/{id}/reset-password` | Admin | `{ newPassword }`. Revokes sessions and clears lockout. |

## Health

| Path | Auth | Meaning |
|---|---|---|
| `/health/live` | none | The process is up |
| `/health/ready` | none | The database is reachable |

## Example (PowerShell)

```powershell
$login = Invoke-RestMethod -Method Post -Uri http://localhost:5290/api/auth/login `
  -ContentType 'application/json' -Body (@{ email = 'admin@crm.local'; password = Read-Host -AsSecureString | ConvertFrom-SecureString -AsPlainText } | ConvertTo-Json)
$h = @{ Authorization = "Bearer $($login.accessToken)" }
Invoke-RestMethod -Uri 'http://localhost:5290/api/accounts?search=acme' -Headers $h
```
