# CRM Platform: API Reference

- **Base URL:** `/api`. In development it's proxied from `http://localhost:4200`; the API itself listens on `http://localhost:5290`.
- **Interactive docs (Development):** Scalar UI at `/scalar`, OpenAPI JSON at `/openapi/v1.json`.
- **Format:** JSON (camelCase). Enums are strings. Timestamps are ISO-8601 UTC. Dates use `yyyy-MM-dd`.
- **Auth:** `Authorization: Bearer <accessToken>` on everything except login, refresh, logout and the self-registration endpoints. The token carries the user (`sub`) and tenant (`tid`), so every request is automatically scoped to the caller's tenant.
- **Errors:** [RFC 7807 ProblemDetails](https://www.rfc-editor.org/rfc/rfc7807). Validation returns `400` with an `errors` map. Custom-field errors are keyed `customFields.<key>`.

## Conventions

**Paged lists** accept `page` (1-based), `pageSize` (1–100, default 20), `search`, `sortBy`, `sortDir` (`asc`/`desc`) and `mine=true`, and return `{ items, totalCount, page, pageSize }`. Every list is also filtered by the caller's **read scope**: Own, Group or All.

**Custom-field filters:** any list of an object with custom fields accepts `cf.<fieldKey>=<value>` (exact match; numbers and `true`/`false` are typed), e.g. `GET /api/records/ticket?cf.priority=High&cf.vip=true`.

**Permissions:** each endpoint below lists the permission it needs, e.g. `account:update`. `*` means any authenticated user. A record the caller can't **read** returns `404`. A readable record the caller can't modify returns `403`.

| Code | Meaning |
|---|---|
| 200 / 201 / 204 | OK / Created / No Content |
| 400 | Validation failed |
| 401 | Missing, invalid or expired token; bad credentials; token without tenant |
| 403 | Not permitted (scope, admin area or escalation), or the tenant is suspended (sign-in) |
| 404 | Not found, or not visible to you |
| 409 | Business rule conflict (in use, already converted, stage has records…) |
| 423 | Account locked out |
| 429 | Rate limit exceeded |

## Auth (`/api/auth`)

| Method | Path | Body | Response |
|---|---|---|---|
| POST | `/login` | `{ email, password }` | `AuthResponse` · 401 · 403 (tenant suspended, or registration not approved) · 423 · 429 |
| POST | `/refresh` | `{ refreshToken }` | `AuthResponse` (rotated) · 401 |
| POST | `/logout` | `{ refreshToken }` | 204 |
| GET | `/me` | none | `UserInfo` |
| POST | `/change-password` | `{ currentPassword, newPassword }` | 204 (all sessions revoked) |
| POST | `/forgot-password` | `{ email }` | **202** always. Emails a single-use link `<App:PublicUrl>/reset-password?token=…` (valid `PasswordReset:TokenMinutes`, default 60) to active, approved users of active organisations. One email per user per minute. |
| POST | `/reset-password` | `{ token, newPassword }` | 204 (all sessions revoked, lockout cleared, "password changed" email sent) · 400 (invalid or expired link, or weak password) |

Forgot and reset are anonymous and share the `RateLimiting:PasswordResetPermitPerMinute` limit (default 5 per IP).

### Self-registration (anonymous, rate limit `RateLimiting:RegisterPermitPerMinute` per IP)

| Method | Path | Body | Response |
|---|---|---|---|
| GET | `/registration-options?tenant=<code>` | none | `{ available, tenantName, emailVerification, smsVerification }`. Without `tenant`, only says whether the platform allows self-registration (`tenantName` is null). |
| POST | `/register` | `{ tenantCode, email, firstName, lastName, phoneNumber?, password }` | **202** `{ verification: ["Email","Sms"], message }` · 400 · 404 (not available for that code) |
| POST | `/verify` | `{ email, channel: Email\|Sms, code }` | `{ status, remaining[] }` · 400 (wrong, expired or locked code) |
| POST | `/resend-code` | `{ email, channel }` | **202** always |

- `tenantCode` is the organisation's workspace ID (slug). `phoneNumber` is required when SMS verification is on and is stored in E.164 form.
- New users are created **inactive with no roles**. `status` moves `PendingVerification` → `PendingApproval`; an administrator then approves (→ `Approved`) or rejects.
- Anti-enumeration: registering an email that already exists returns the same 202, and `verify`/`resend-code` don't reveal whether an account exists.
- Codes are stored as SHA-256 hashes, expire after `codeTtlMinutes`, lock after `maxAttempts` wrong tries, and can be re-sent after `resendCooldownSeconds`.
- Sign-in for an unapproved registration returns **403** ProblemDetails with extensions `registrationStatus` and `verification[]` (channels still to verify).
```jsonc
// UserInfo (inside AuthResponse.user)
{
  "id": "0199…", "email": "rep@crm.local", "fullName": "Sam Seller",
  "roles": ["Standard User"],
  "tenant": { "id": "0199a000-…-000000000001", "name": "Default", "slug": "default", "currency": "AUD", "template": "sales" },
  "isAdministrator": false, "isPlatformAdmin": false,
  // Resolved scopes for "*" and every resource any of the user's roles mentions.
  // Anything else follows "*" (business objects) or is None (admin areas).
  "permissions": { "*": { "read": "All", "create": "All", "update": "Own" }, "support_case": { "read": "Own", "create": "All", "update": "Own" } },
  "teamUserIds": ["0199…"]   // the user plus members of their groups (Group scope)
}
```

## Metadata and settings

| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/api/metadata` | * | `{ tenant, objects[], picklists }`. Each object includes `fields[]` and `stages[]`, and inactive objects are included with `isActive: false`. |
| GET | `/api/settings` | * | `OrganisationSettings { id, name, slug, currency, template, selfRegistrationEnabled, selfRegistrationAllowedByPlatform, emailVerification, smsVerification }` |
| PUT | `/api/settings` | settings:manage | `{ name, currency, selfRegistrationEnabled? }` (ISO 4217). Omitting `selfRegistrationEnabled` keeps the stored value. |

## Dashboard

| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/api/dashboard?mine=` | * | KPIs, `pipelineByStage[]` (`stage`, `label`, `color`, `count`, `amount`), `leadsBySource[]`, `wonByMonth[]`, `upcomingActivities[]`, `customPipelines[]` (per custom object with a pipeline). `sections[]` lists the object keys the caller can read; other sections are zero or empty. |

## Standard objects

All request bodies accept `customFields: { <key>: value | null }`. Omitted keys keep their stored value, and `null` clears it. `ownerId` is optional; omit it for "me", and any other owner must be within your update scope.

### Accounts (`/api/accounts`)
| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/` | account:read | `search` (name/email/city), `industry`, `ownerId`, `mine`, `cf.*`. Sort: `name`, `industry`, `city`, `createdAt`. |
| GET | `/{id}` | account:read | includes `contactCount`, `openOpportunityCount`, `openPipelineAmount`, `customFields` |
| POST | `/` | account:create | `AccountRequest` → 201 |
| PUT | `/{id}` | account:update (scope) | |
| DELETE | `/{id}` | account:delete (scope) | **409** if the account has opportunities |

`AccountRequest`: `name*`, `industry` (must be in the `account.industry` picklist if it has options; the record's current value always stays valid), `website`, `phone`, `email`, `street`, `city`, `state`, `postalCode`, `country`, `annualRevenue`, `employeeCount`, `description`, `ownerId`, `customFields`.

### Contacts (`/api/contacts`)
| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/` | contact:read | `search`, `accountId`, `ownerId`, `mine`, `cf.*` |
| GET / POST / PUT / DELETE | `/`, `/{id}` | contact:read/create/update/delete | `ContactRequest`: `firstName*`, `lastName*`, `email`, `phone`, `mobile`, `jobTitle`, `department`, `description`, `accountId`, `ownerId`, `customFields` |

### Leads (`/api/leads`)
| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/` | lead:read | `search`, `status`, `source`, `ownerId`, `mine`, `cf.*` |
| GET / POST / PUT / DELETE | `/`, `/{id}` | lead:* | `source` validated against the `lead.source` picklist. `status` can't be set to `Converted`. |
| POST | `/{id}/convert` | lead:update + contact:create (+ account:create when creating one, + opportunity:create when creating one) | `{ existingAccountId?, createOpportunity, opportunityName?, amount?, expectedCloseDate? }` → `{ accountId, contactId, opportunityId? }` · 409 if already converted |

### Opportunities (`/api/opportunities`)
| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/` | opportunity:read | `search`, `stage` (key), `accountId`, `contactId`, `open`, `ownerId`, `mine`, `cf.*` |
| GET | `/pipeline?mine=` | opportunity:read | open deals, plus those closed in the last 30 days (max 500) |
| GET / POST / PUT / DELETE | `/`, `/{id}` | opportunity:* | `stage` must be a key from the tenant's opportunity stages. `probability` null means the stage default. |
| PATCH | `/{id}/stage` | opportunity:update | `{ stage, lossReason? }`. Sets the default probability, and sets or clears `closedAt` from the stage category. |

### Activities (`/api/activities`)
| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/` | activity:read | `search`, `type`, `status` (`Open`/`Completed`/`Overdue`), `accountId`, `contactId`, `leadId`, `opportunityId`, `customRecordId`, `dueFrom`, `dueTo`, `ownerId`, `mine` |
| GET / POST / PUT / DELETE | `/`, `/{id}` | activity:* | `ActivityRequest`: `type`, `subject*`, `description`, `dueAt`, `priority`, `accountId`, `contactId`, `leadId`, `opportunityId`, `customRecordId`, `ownerId`, `customFields` |
| POST | `/{id}/complete`, `/{id}/reopen` | activity:update | |

Activity items include `customRecordId`, `customRecordName` and `customObjectKey` when linked to a custom record.

## Custom objects (`/api/records/{objectKey}`)

The permission resource is the object key, e.g. `ticket:read`.

| Method | Path | Notes |
|---|---|---|
| GET | `/` | `search` (name), `stage`, `open`, `ownerId`, `mine`, `cf.*`. Sort: `name`, `stage`, `updatedAt`, `createdAt`. |
| GET | `/board?mine=&cf.*=` | Pipeline objects only: open records plus those closed in the last 30 days (max 500). `cf.*` filters work as on the list, e.g. `/api/records/application/board?cf.position={jobId}` is the pipeline for one job. |
| GET | `/{id}` | `RecordItem { id, objectKey, name, stage, closedAt, ownerId, ownerName, createdAt, updatedAt, customFields }` |
| POST | `/` | `{ name*, stage?, ownerId?, customFields }` (stage defaults to the first open stage) |
| PUT | `/{id}` | same body |
| PATCH | `/{id}/stage` | `{ stage }` |
| DELETE | `/{id}` | Linked activities are deleted too |

Unknown or inactive object keys return 404.

**Lookup filters and related lists.** `cf.<lookupField>=<recordId>` lists the records linked to another record. The UI uses it for the related lists on detail pages, e.g. `/api/records/application?cf.candidate={id}` (a candidate's applications) or `?cf.position={id}` (a job's applicants).

**Unique fields.** On a field marked `isUnique`, saving a value that another record of the same object already has returns 400 with `"Another record already has this {label} ({value})."` under `cf.<key>`. Email comparisons ignore case, and unique emails are stored in lower case. Blank values are not checked.

## Attachments (`/api/attachments`)

Files on any record except activities. Reading needs **read** access to the record; uploading and deleting need **update** access. Hidden records return 404.

| Method | Path | Notes |
|---|---|---|
| GET | `/?objectKey=&recordId=` | `AttachmentItem { id, fileName, contentType, size, uploadedById, uploadedByName, createdAt }[]`, newest first |
| POST | `/?objectKey=&recordId=` | `multipart/form-data` with one `file` → 201. Max `Attachments:MaxFileSizeMb` (default 10), 100 files per record. Allowed: .pdf .doc .docx .rtf .odt .txt .csv .xls .xlsx .ppt .pptx .png .jpg .jpeg .gif .webp. The content type is set from the extension; the file name is sanitised. |
| GET | `/{id}/content` | Download (`Content-Disposition: attachment`, `nosniff`) |
| DELETE | `/{id}` | 204 |

Uploads and deletions appear in the record's history as `attachment: [old, new]`. Deleting a record deletes its files.

## CSV import (`/api/import/{objectKey}`, objectKey:create)

Custom objects only (e.g. the Recruitment template's candidates, job openings and applications).

| Method | Path | Body | Response |
|---|---|---|---|
| POST | `/analyze` | `{ csv }` | `{ headers[], rowCount, sample[][] (5 rows), suggestedMapping: { header: target \| null }, targets[] }` |
| POST | `/` | `{ csv, mapping: { header: target }, dryRun = true, runAutomation = false }` | `{ dryRun, total, created, errorCount, errors: [{ row, messages[] }] }` (first 200 errors) |

- Targets: `name`, `stage` (key or label), `owner` (user email, same assignment rules as forms) and `cf.<fieldKey>`.
- Values: picklists match case-insensitively; multi-picklists split on `;` or `|`; yes/no accepts Yes/No/True/False/1/0; dates `yyyy-MM-dd` or `dd/MM/yyyy`; numbers `1,234.50` (ambiguous formats such as `1.234,50` are rejected); lookups by the related record's **name** (or id), with an error if a name matches several records.
- Every row goes through the same validation as the forms, including unique fields. Two rows in the same file with the same unique value are also rejected (`"Email: same value as row 2 in this file."`). **All-or-nothing:** if any row fails, nothing is saved. `row` is the line number in the file (the header is line 1).
- Limits: `Import:MaxFileSizeMb` (default 5) and `Import:MaxRows` (default 5000). Automation rules don't run unless `runAutomation` is true.

## CSV export (`/api/export/{objectKey}`, objectKey:read)

`GET /api/export/{objectKey}?<list filters>` returns `text/csv` (UTF-8 with BOM) as an attachment named `<plural>-<yyyyMMdd>.csv`. Any object, standard or custom.

- **Rows**: the records the caller can read (their read scope), filtered like the lists: `search`, `mine`, `ownerId`, `stage`, `open`, `cf.<key>=value`, equality on standard fields (e.g. `status=Qualified`, `industry=Retail`, `accountId=<id>`) and, for activities, `status=Open|Completed|Overdue`. Newest first. More than `Export:MaxRows` (default 10,000) matches → 400 asking to narrow the filters.
- **Columns**: `Id`, the standard fields, custom fields (headers are the field labels, so the file can be re-imported), `Owner`, `Created at`, `Updated at`. Linked records (lookups and standard references) are shown by **name**, or by id if the caller can't read them. Stages show their label, yes/no as Yes/No, multi-picklists joined with `; `, dates in ISO format.
- **Safety**: text starting with `=`, `+`, `-`, `@`, tab or CR is prefixed with `'` so spreadsheets don't run it as a formula (CSV injection). Numbers and dates are left as they are.
- **Audit**: each export writes an `Exported` audit entry (`entityName` "23 applications", `changes.filters` = the query).

## Careers (`/api/careers/{slug}`, anonymous)

Public endpoints for an organisation's job board. They exist only when the organisation is active, has switched the careers page on (`PUT /api/settings { careersEnabled }`) and has the *position*, *candidate* and *application* objects (Recruitment template). Otherwise every call returns 404.

| Method | Path | Notes |
|---|---|---|
| GET | `/` | `{ organisation, intro, jobs: CareerJob[] }`. Jobs are job openings with `status = Open`, `publish = true` and a closing date that hasn't passed. Rate limit `RateLimiting:CareersPermitPerMinute` per IP (120). |
| GET | `/jobs/{id}` | `{ organisation, job: { id, title, department, location, employmentType, closingDate, postedAt }, description }`. Internal fields (salary, company, owner) are never exposed. |
| POST | `/jobs/{id}/apply` | `multipart/form-data`: `name`*, `email`*, `phone`, `location`, `linkedin` (http(s) URL), `coverNote`, `consent`* (`true`), `cv`* (file), `website` (honeypot, must be empty). → **202** `{ message }`. 400 with field errors. Rate limit `RateLimiting:CareersApplyPermitPerMinute` per IP (5). |

Applying:
- **CV**: `.pdf`, `.doc`, `.docx`, `.odt` or `.rtf`, at most `Careers:MaxCvSizeMb` (5 MB). The file's first bytes must match its type, so renamed programs or scripts are refused. It's stored on both the candidate and the application.
- **Candidate**: matched by email (case-insensitive). A new candidate gets name, email, phone, location, LinkedIn, source *Website* and `privacy_consent` (time of consent). An **existing candidate is never changed**: what the applicant typed is kept in a note on the application instead.
- **Application**: one open application per candidate and job (a repeat adds the new CV and an "Applied again" note), first open stage, source *Website*, cover note, owned by the job's owner, who gets a notification. Automation rules run as usual (e.g. *Acknowledge new applications*). Changes are audited as *Careers site*.
- The response is the same for new, repeat and honeypot submissions, so the form can't be used to find out who has applied.

## Interviews (`/api/interviews`)

Interviews belong to an **application** (recruitment tenants). Reading needs read access to the application; scheduling, rescheduling, cancelling and completing need **update** access to it (403 otherwise; 404 for other tenants' records).

| Method | Path | Notes |
|---|---|---|
| GET | `?applicationId=` | `InterviewItem[]`, newest first |
| GET | `/mine` | `{ upcoming, awaitingFeedback }`: the caller's scheduled interviews from an hour ago onwards, and earlier ones (not cancelled) still without their scorecard. Only applications the caller can read. |
| POST | `/` | `{ applicationId, scheduledAt, durationMinutes, kind, location, notes, interviewerIds[], emailCandidate }` → 201 `{ interview, emailNote }` |
| PUT | `/{id}` | Same body; reschedule or change interviewers → `{ interview, emailNote }` |
| POST | `/{id}/cancel`, `/{id}/complete` | Only while *Scheduled* (400 otherwise) → `InterviewItem` |
| PUT | `/{id}/scorecard` | `{ recommendation, ratings: { criterion: 1..5 }, notes }`. **Interviewers only** (403), not on cancelled interviews. Resubmitting replaces the caller's card. → `InterviewItem` |
| GET | `/{id}/ics` | `text/calendar` (RFC 5545) for calendar apps; cancelled interviews have `STATUS:CANCELLED` |

`InterviewItem`: `id, applicationId, applicationName, jobTitle, scheduledAt, durationMinutes, kind (Phone|Video|Onsite), location, notes, status (Scheduled|Completed|Cancelled), organizerName, interviewers [{ id, name, submitted }], iAmInterviewer, myScorecard, scorecards [{ interviewerId, interviewerName, recommendation, ratings, notes, submittedAt }], canEdit`.

- **Validation**: `scheduledAt` from 30 days ago to a year ahead; 15–480 minutes; 1–10 active users of the organisation as interviewers; a *Video* location must be an http(s) link. Ratings must be 1–5 on the current `interview.criteria` picklist values.
- **Side effects**: each interviewer gets a *Meeting* activity on the application (due at the interview, high priority) and a notification; rescheduling moves the activities and notifies them, removing an interviewer deletes theirs, cancelling deletes all. The application's `interview_date` is kept equal to its next scheduled interview, so the *Interview reminder* rule follows reschedules. A submitted scorecard completes the interviewer's activity and notifies the organiser (once).
- **Candidate email** (`emailCandidate: true`): queues the *Interview invitation* template through the outbox, with the same rules as user-sent email. When it can't be sent (no template, no candidate address, daily limit) the interview is still saved and `emailNote` says why.
- **Blind feedback**: an interviewer sees colleagues' scorecards only after submitting their own (`interviewers[].submitted` shows who has). Others with update access to the application see all scorecards; read-only users see none.

## Hiring analytics (`/api/recruiting/analytics`, application:read)

`GET ?days=90&positionId=` (days 7–730) → `{ days, applications, open, hired, rejected, averageDaysToHire, medianDaysToHire, funnel[], sources[], jobs[], interviewsNext7Days, feedbackOutstanding }` for applications **created** in the period that the caller can read (at most 20,000).

- `funnel[]`: per stage `{ key, label, color, current, reached, conversionToNext, averageDaysInStage }`. *Reached* counts applications that got at least that far (from the stage history in the audit log; hires count as passing every open stage). *Conversion* is the share that reached the next open stage, or were hired from the last one.
- `sources[]`: `{ source, applications, hired, hireRate }` by the application's *source*. `jobs[]`: `{ id, title, applications, open, hired, byStage }`.
- Time to hire: from creation to being closed in a *Won* stage.
- `offers`: `{ sent, accepted, declined, expired, pending, acceptanceRate, averageDaysToRespond }` for offers sent on those applications; the acceptance rate is accepted / (accepted + declined + expired).
- `mock`: `{ sent, started, submitted, completionRate, averageMinutes, averageSelfRating, averageReadiness }` for mock interviews on those applications (withdrawn ones excluded).

## Offers (`/api/offers`)

Offers belong to an **application** and carry pay details, so every call needs **update** access to the application (403 otherwise; 404 if it can't be read).

| Method | Path | Notes |
|---|---|---|
| GET | `?applicationId=` | `OfferItem[]`, newest first. Sent offers past their deadline are marked *Expired*. |
| GET | `/defaults?applicationId=` | `{ jobTitle, salary, currency, payPeriod, startDate, expiresAt, letter }`: the job's title, the candidate's expected salary, the organisation's currency, a start in 4 weeks, a week to respond and the *Offer letter* template |
| POST | `/` | `{ applicationId, jobTitle, salary, currency, payPeriod (Year|Month|Hour), startDate, expiresAt, letter, notes }` → 201 draft. 400 if the application already has an open (draft or sent) offer. |
| PUT | `/{id}` | Same body; drafts only |
| DELETE | `/{id}` | Drafts only → 204 |
| GET | `/{id}/preview` | `{ letter, blankFields }`: the letter filled in for this application |
| POST | `/{id}/send` | Fills in and freezes the letter (first send), creates a new candidate link (the previous one stops working), moves the application to *Offer* if it's in an earlier open stage, and emails the *Offer email* template through the outbox → `{ offer, link, emailNote }`. The link is returned only here. |
| POST | `/{id}/withdraw` | `{ reason }`; draft or sent → *Withdrawn*, the link stops working, and a note is added to the application |

- **Validation**: salary 0–100,000,000; three-letter upper-case currency; start date today or later; deadline between an hour and 60 days ahead; job title and letter required, and the letter may only use known placeholders.
- **Placeholders** (letters and application emails): `{{offer.job_title}}`, `{{offer.salary}}` ("AUD 120,000.00 per year"), `{{offer.start_date}}`, `{{offer.expires}}`; `{{offer.link}}` is filled only in the offer email. Other emails use the latest offer that wasn't withdrawn.
- **The link stays off the timeline**: the Email activity logged on the application shows `[link sent to the candidate]`; only the outbox row (email log, administrators) holds the real link.

## Offer response (`/api/offer-response/{token}`, anonymous)

The candidate's link is `<App:PublicUrl>/offer/<token>` (256-bit random, Base64Url). Only its SHA-256 hash is stored; it enters the offer's tenant (which must be active) and gives access to that one offer. Rate limit `RateLimiting:OfferResponsePermitPerMinute` per IP (30). Unknown, withdrawn or replaced links → 404.

| Method | Path | Notes |
|---|---|---|
| GET | `/` | `{ organisation, candidateName, jobTitle, status, expiresAt, salary, currency, payPeriod, startDate, letter, respondedAt, signedName }`. Pay details and the letter only while *Sent* or after *Accepted*. |
| POST | `/accept` | `{ signedName, agree: true }`. The typed name must match the application's name (case and spacing ignored). Records the time, IP address and browser as evidence → `{ status, message }` |
| POST | `/decline` | `{ reason }` (optional) |

Both answers need the offer to be *Sent* and not past its deadline (400 with an explanation otherwise), add a note to the application and notify the sender and the application's owner. The application isn't moved to *Hired* automatically.

## Mock interviews (`/api/mock-interviews`)

Practice interviews on an **application**; every call needs **update** access to it (403 otherwise; 404 if it can't be read), except the settings, which need `settings:manage`.

| Method | Path | Notes |
|---|---|---|
| GET | `?applicationId=` | `{ items: MockInterviewItem[], aiAvailable, aiUnavailableReason }`, newest first. Open sessions past their deadline become *Expired*. An item has the snapshot of questions with each answer (`text, rating, seconds, answeredAt`) and AI feedback, plus `readinessRating`, `reviewNotes`, `aiStatus` (NotRequested/Pending/Completed/Failed), `aiScore`, `aiSummary`, `aiStrengths[]`, `aiImprovements[]`, `aiError`. |
| GET | `/library?applicationId=` | `{ available, canAdd, questions: [{ id, prompt, competency, minutes, forThisJob }] }`: practice questions for the application's job and for every job, job-specific first, retired ones left out |
| POST | `/library/starter` | Adds the 10 starter questions the library doesn't have yet (matched by wording), owned by the caller; needs create on `mock_question` → `{ added }` |
| POST | `/` | `{ applicationId, questionIds[1..20], validDays (1–30, default 7), message }` → `{ interview, link, emailNote }`. Copies the questions, creates the link and emails the *Mock interview invitation* template. 400 if the application already has an open session. The link is returned only here. |
| POST | `/{id}/resend` | Open sessions only: a new link (the old one stops working) and a new email; answers are kept → `{ interview, link, emailNote }` |
| POST | `/{id}/withdraw` | `{ reason }`; the link stops working and a note is added to the application |
| PUT | `/{id}/review` | Submitted sessions only: `{ readinessRating (1–5), notes }` (at least one) |
| POST | `/{id}/ai-review` | Submitted sessions: queue AI feedback (again). 400 with the reason when AI isn't available or a review is already pending. |
| GET/PUT | `/settings` | `{ aiReviewEnabled, aiTrigger (Manual/OnSubmit), aiInstructions }` → plus `aiConnectionReady`, `aiConnectionProblem`, `libraryAvailable` |

- **Placeholders** (application emails): `{{mock.job_title}}`, `{{mock.questions}}` (count), `{{mock.expires}}`; `{{mock.link}}` is filled only in the invitation. The Email activity shows `[link sent to the candidate]` instead of the link.
- **AI feedback** runs in the background (`Ai:ReviewIntervalSeconds`, default 20) only when the server switch `Ai:Enabled`, a platform AI provider and the organisation's opt-in are all on. The model receives the job title, questions, competencies, model answers, answers and self-ratings; never names, emails or ids.

## Mock interview response (`/api/mock-response/{token}`, anonymous)

The candidate's link is `<App:PublicUrl>/mock/<token>` (same scheme as offers). Rate limit `RateLimiting:MockResponsePermitPerMinute` per IP (60). Unknown, withdrawn or replaced links → 404.

| Method | Path | Notes |
|---|---|---|
| GET | `/` | `{ organisation, candidateName, jobTitle, message, status, expiresAt, submittedAt, questions: [{ id, prompt, details, competency, minutes, answer, rating, guidance }] }`. `guidance` (model answer and tips) only for answered questions. Expired sessions return no questions or message. |
| PUT | `/answers/{questionId}` | `{ text (≤5,000), rating (1–5, optional), seconds }`; blank text clears the answer. The first answer starts the session → `{ guidance, answered, total }` |
| POST | `/submit` | Needs at least one answer. Adds a note to the application, notifies the sender and owner, and queues AI feedback when the organisation chose automatic → `{ message }` |

Answering or submitting a session that is submitted, expired or withdrawn returns 400 with an explanation.

## Preferences

| Method | Path | Notes |
|---|---|---|
| PUT | `/api/auth/me/preferences` | `{ theme: System|Light|Dark }` → 204. `/api/auth/me` returns `theme`. |

## Privacy (`/api/privacy`, settings:manage)

For organisations with the *candidate* object (404 otherwise).

| Method | Path | Notes |
|---|---|---|
| GET | `/candidates/{id}/export` | `application/zip`: `data.json` (the candidate's fields, applications with interviews, scorecards without interviewer names, offers, timeline, emails and file list) and `files/` (their files). Audited as *Exported* ("Personal data export"). |
| POST | `/candidates/{id}/erase` | `{ confirmName }` (must match the candidate's name) → `{ applications, activities, files, emails }`. See DATA-MODEL §6a. |
| GET | `/retention` | `{ months, cutoff, eligible, sample[] }`: the policy and who it would erase now (50 longest inactive) |
| PUT | `/retention` | `{ months }`: 6–120, or null to switch the policy off → the same preview |
| POST | `/retention/run` | Erases up to 100 eligible candidates → `{ erased, remaining }` |

Eligible for retention: candidates unchanged since the cutoff whose applications are all closed, none in a *Won* (hired) stage, and also unchanged since the cutoff.

## Diagnostics

| Method | Path | Notes |
|---|---|---|
| POST | `/api/client-errors` | `{ message, stack?, url? }` from the UI's error handler (signed-in users, `RateLimiting:ClientErrorPermitPerMinute`, default 30). Logged at Error level, so it reaches Sentry when configured. |

## Administration

### Users (`/api/users`)
| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/lookup` | * | Active users in the tenant `{ id, fullName, email }` |
| GET | `/?status=` | users:manage | Users with `roles[]`, `groups[]`, status, lockout, `isPlatformAdmin`, `registrationStatus`, `phoneNumber`, `emailConfirmed`, `phoneNumberConfirmed`. `status` filters by registration status, e.g. `PendingApproval`. |
| POST | `/` | users:manage | `{ email, firstName, lastName, password, roleIds[], groupIds[] }` |
| PUT | `/{id}` | users:manage | `{ firstName, lastName, isActive, roleIds[], groupIds[] }`. A pending registration can't be activated here; use approve. |
| POST | `/{id}/approve` | users:manage | `{ roleIds[], groupIds[]? }`. Needs at least one role, directly or through a group. Activates the user and emails them (best effort). |
| POST | `/{id}/reject` | users:manage | Pending registrations only. The user stays inactive and can't sign in. |
| POST | `/{id}/reset-password` | users:manage | `{ newPassword }`. Revokes sessions and clears lockout. |

Assignments are checked for escalation: you can't hand out access you don't hold, and only administrators can grant the Administrator role.

### Roles (`/api/admin/roles`) and groups (`/api/admin/groups`)
| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/admin/roles` | users, groups or roles:manage | `RoleItem { id, name, description, isSystem, isAdministrator, permissions[], userCount, groupCount }` |
| POST / PUT | `/admin/roles`, `/admin/roles/{id}` | roles:manage | `{ name, description, permissions: [{ resource, action, scope }] }`. The Administrator role can't be edited. |
| DELETE | `/admin/roles/{id}` | roles:manage | System roles can't be deleted |
| GET | `/admin/groups` | users, groups or roles:manage | `GroupItem { id, name, description, members[], roles[] }` |
| POST / PUT / DELETE | `/admin/groups[/{id}]` | groups:manage | `{ name, description, memberIds[], roleIds[] }` |

**Grants:**
- `resource`: an object key, `*`, or one of `users`, `groups`, `roles`, `customization`, `settings` (action `manage`) or `audit` (action `read`).
- Record actions: `read`, `create`, `update`, `delete`.
- `scope`: `None`, `Own`, `Group` or `All`.

**Semantics:**
- In each role, an explicit object grant overrides that role's `*` grant for the action, and `None` denies.
- Across roles the widest scope wins.
- Create and manage are all-or-nothing: any scope becomes `All`.

### Customization (`/api/admin`, customization:manage)
| Method | Path | Body / notes |
|---|---|---|
| POST | `/objects` | `{ key, label, pluralLabel, icon, description, nameLabel, hasPipeline, isActive, sortOrder }`. The key must match `^[a-z][a-z0-9_]{1,39}$`, and reserved keys are rejected. Pipeline objects get default stages. |
| PUT | `/objects/{key}` | same (key immutable). Standard objects can't change `hasPipeline`. |
| DELETE | `/objects/{key}` | Custom objects only. **409** if records exist. |
| POST | `/objects/{key}/fields` | `{ key, label, type, isRequired, isUnique, options[], lookupObjectKey, maxLength, min, max, helpText, showInList, sortOrder }` |
| PUT / DELETE | `/objects/{key}/fields/{fieldKey}` | The type can't change. Deleting a field keeps stored values but hides them. |

`isUnique` is allowed on `Text`, `Email`, `Phone` and `Url` fields. Turning it on for a field whose existing records already share a value returns 400 with the number of duplicates, so they can be cleaned up first.
| PUT | `/objects/{key}/stages` | `{ stages: [{ key, label, probability, category: Open\|Won\|Lost, color: #RRGGBB }] }` replaces the list (order matters). Needs at least one open stage. **409** if removed stages still have records. |
| PUT | `/picklists/{key}` | `{ options[] }` for `account.industry` or `lead.source` |

Field types: `Text`, `TextArea`, `Number`, `Currency`, `Date`, `DateTime`, `Boolean`, `Picklist`, `MultiPicklist`, `Email`, `Phone`, `Url`, `Lookup`.

### Automation (`/api/admin/automation`, customization:manage)
| Method | Path | Notes |
|---|---|---|
| GET | `/` | Rules with `runCount`, `lastRunAt`, `scheduleField`, `scheduleOffsetMinutes` |
| GET | `/fields/{objectKey}` | Fields usable in conditions, with `settable` for Set-field actions and `isDate` for schedules (includes `cf.*`) |
| POST / PUT / DELETE | `/`, `/{id}` | `RuleRequest` |

```jsonc
{
  "name": "Urgent ticket alert", "objectKey": "ticket", "trigger": "Created", "isActive": true,  // Created | Updated | StageChanged | DateReached
  "conditions": [ { "field": "cf.priority", "operator": "Equals", "value": "Urgent" } ],
  // operators: Equals, NotEquals, GreaterThan, LessThan, Contains, IsEmpty, IsNotEmpty, Changed (not for DateReached)
  "actions": [
    { "type": "CreateTask", "subject": "Respond to {{name}}", "dueInDays": 0, "priority": "High", "assignTo": "owner" },
    { "type": "Notify", "message": "Urgent: {{name}}", "assignTo": "<userId>" },
    { "type": "SetField", "field": "cf.escalated", "value": "true" },
    { "type": "SendEmail", "to": "cf.requester.email", "templateId": "<id>" },               // or "subject" + "message"
  ],
  // DateReached only: a date field and a signed offset (negative = before)
  "scheduleField": "cf.due_date", "scheduleOffsetMinutes": -1440
}
```

- **SendEmail** `to`: `owner` (default), an active user's id, or an email field path from `/api/email/fields/{objectKey}` (`recipients`), including one level through a lookup, e.g. `cf.candidate.cf.email`. Use a `templateId` for the same object, or a `subject` and `message`. Unknown placeholders are rejected. A rule emails the same recipient about the same record at most once in 24 hours; records with no address are skipped.
- **DateReached** rules are checked every `Automation:ScheduleIntervalSeconds`. Each record fires once per due time (`date + offset`). Due times before the rule was created, or older than `Automation:ScheduleLookbackHours`, are skipped. Conditions are evaluated at the due time.
- Placeholders in all text: `{{name}}`, standard fields (`{{amount}}`), `{{stage.label}}`, `{{cf.<key>}}` (dates as "5 Oct 2026", amounts with currency), lookups as the linked record's name (`{{cf.position}}`), the linked record's fields (`{{cf.position.name}}`, `{{cf.candidate.cf.email}}`), `{{owner.name}}`, `{{owner.email}}`, `{{organisation.name}}` and `{{link}}` (needs `App:PublicUrl`).

### Email
| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/api/email/fields/{objectKey}` | object:read | `{ placeholders: [{ key, label }], recipients: [{ key, label }] }` |
| GET | `/api/email/templates?objectKey=` | object:read | Templates for the object |
| POST | `/api/email/compose` | read **and** update on the record | `{ objectKey, recordId, templateId? }` → `{ recipients: [{ label, address }], subject, body, blankFields[] }`, rendered for the record. `blankFields` lists template fields that are empty on this record. |
| POST | `/api/email/send` | read and update on the record | `{ objectKey, recordId, to, subject, body, templateId? }` → **202** `{ id, status }`. `to` must be one of the record's addresses (400 otherwise). Placeholders are filled in. Reply-To is the sender. Logs an Email activity on the record. 429 over the daily limit. Rate limit `RateLimiting:EmailSendPermitPerMinute` per user (20). |
| GET / POST / PUT / DELETE | `/api/admin/email-templates[/{id}]` | customization:manage | `{ name (unique), objectKey, subject, body }`. Unknown placeholders → 400. Deleting a template used by a rule → **409** naming the rules. |
| GET | `/api/admin/emails` | settings:manage | `status`, `search`, paging (25) → `{ page, deliveryMode: Log\|Smtp, dailyLimit, queuedToday }` |
| GET | `/api/admin/emails/{id}` | settings:manage | `{ email, body, replyTo, ruleName }` |
| POST | `/api/admin/emails/{id}/retry`, `/cancel` | settings:manage | Retry a Failed email (attempts reset); cancel a Pending one |

Emails go to an outbox and are sent by a background dispatcher (`Pending` → `Sent`). Failures are retried after 1, 5, 15 and 60 minutes; after 5 attempts, or at once for an invalid address, the email is `Failed` and its timeline entry is marked "[Not sent]". Subjects are reduced to one line and addresses must be a single plain address, so record data can't inject mail headers.

### Audit and notifications
| Method | Path | Permission | Notes |
|---|---|---|---|
| GET | `/api/admin/audit` | audit:read | `entityType`, `entityId`, `userId`, `action`, `search` (record name), `from`, `to`, paging (default 50) |
| GET | `/api/audit/{objectKey}/{id}` | read access to that record | Up to 200 latest entries: `{ action, userName, timestamp, changes: { field: [old, new] } }` |
| GET | `/api/notifications` | * | `{ unreadCount, items[] }`: your latest notifications |
| POST | `/api/notifications/{id}/read`, `/api/notifications/read-all` | * | |

## Platform (`/api/platform`, platform admins only)

| Method | Path | Notes |
|---|---|---|
| GET | `/templates` | `sales`, `helpdesk`, `recruitment`, `blank` |
| GET | `/tenants` | `TenantItem { id, name, slug, status, currency, template, createdAt, userCount }` |
| POST | `/tenants` | `{ name, slug, template, currency, adminEmail, adminFirstName, adminLastName, adminPassword, seedDemoData }`. Slug: `^[a-z0-9][a-z0-9-]{1,62}$`. Runs in a transaction. |
| PUT | `/tenants/{id}` | `{ name, currency, status: Active\|Suspended }`. You can't suspend your own tenant. |
| DELETE | `/tenants/{id}` | Only when suspended, and not the default tenant. Deletes all of its data, including pending registrations. |
| GET | `/settings` | `{ settings: PlatformSettings, smtpPasswordConfigured, smsApiKeyConfigured }` |
| PUT | `/settings` | `PlatformSettings` (validated, e.g. SMTP host and from-address are required for the Smtp provider) |
| POST | `/settings/test-email`, `/settings/test-sms` | `{ to }`: sends a test message with the saved settings. 400 with the provider error if it fails. |

`PlatformSettings`: `selfRegistrationEnabled`, `emailVerificationEnabled`, `smsVerificationEnabled`, `codeLength` (4–10), `codeTtlMinutes` (1–60), `maxAttempts` (1–10), `resendCooldownSeconds` (0–600), `emailProvider` (`Log`\|`Smtp`), `smtpHost`, `smtpPort`, `smtpUsername`, `smtpUseStartTls`, `emailFromAddress`, `emailFromName`, `smsProvider` (`Log`\|`Http`), `smsGatewayUrl`, `smsSenderId`, `smsAuthHeader`, `smsAuthScheme`, `aiProvider` (`None`\|`OpenAI`\|`AzureOpenAI`), `aiEndpoint` (https only), `aiModel` (model or Azure deployment), `aiApiVersion` (Azure), `aiApiKeyName` (letters, digits, `-`, `_`), `aiTimeoutSeconds` (5–120), `aiMaxRetries` (0–5), `aiMaxOutputTokens` (200–4000), `aiTemperature` (0–1). Secrets are never stored or returned: the SMTP password, SMS API key and AI key come from configuration (`Email:SmtpPassword`, `Sms:ApiKey`, `Ai:Keys:<aiApiKeyName>`). The GET/PUT response also has `smtpPasswordConfigured`, `smsApiKeyConfigured`, `aiServerEnabled` (`Ai:Enabled`) and `aiKeyConfigured`.

## Health

| Path | Meaning |
|---|---|
| `/health/live` | The process is up (anonymous) |
| `/health/ready` | The database is reachable (anonymous) |

## Example (PowerShell)

```powershell
$pw = Read-Host -AsSecureString 'Password' | ConvertFrom-SecureString -AsPlainText
$login = Invoke-RestMethod -Method Post -Uri http://localhost:5290/api/auth/login -ContentType 'application/json' `
  -Body (@{ email = 'admin@crm.local'; password = $pw } | ConvertTo-Json)
$h = @{ Authorization = "Bearer $($login.accessToken)" }
Invoke-RestMethod -Uri 'http://localhost:5290/api/metadata' -Headers $h | Select-Object -ExpandProperty objects | Select-Object key, pluralLabel, isActive
Invoke-RestMethod -Uri 'http://localhost:5290/api/accounts?cf.tier=Gold' -Headers $h
```
