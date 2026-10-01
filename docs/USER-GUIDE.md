# CRM Platform: User Guide

## 1. Signing in
Open the CRM (development: http://localhost:4200) and sign in with the email and password from your administrator. Your session renews itself while you work.

**Creating your own account** (if your organisation allows it): use the link your administrator shared (`…/register/<organisation code>`), or *Create an account* on the sign-in page and enter the organisation code.
1. Fill in your name, email, mobile number and a password.
2. If asked, enter the codes sent to your email and/or mobile. *Send a new code* works after a short wait.
3. An administrator reviews your request and gives you access. Until then, signing in tells you the request is waiting for approval.

**Forgot your password?** Use *Forgot password?* on the sign-in page and enter your email. If you have an account you'll get a link (valid for an hour, usable once) to choose a new password; you're then signed out on all devices.

Use the menu under your name to **change your password** or **sign out**. The menu also shows your organisation and roles.

## 2. Finding your way around
The left navigation is built from **your organisation's configuration and your permissions**. You only see the objects you're allowed to read, with the names your organisation uses: for example *Customers* instead of *Accounts*, or extra objects like *Tickets* or *Support Cases*.

| Area | What it's for |
|---|---|
| **Dashboard** | Headline figures and charts for everything you can see: pipeline, forecast, wins, open leads, overdue work, a card per custom pipeline, and your next 7 days. *Only my records* switches to personal figures. |
| **Accounts, Contacts, Leads, Opportunities, Activities** | The standard CRM objects (if your organisation uses them) |
| **Pipeline** | Opportunities as a drag-and-drop board |
| **Custom objects** (e.g. Support Cases) | Your organisation's own record types, each with a list, a board if it has a pipeline, and a detail page |
| 🔔 **Notifications** | Messages from automation rules. Click one to open the record. |
| **Settings** | Only if you administer something (see §5) |
| **Tenants** | Platform operators only (see §6) |

Every list has **search**, **Mine only**, **column sorting**, **paging**, and **filters** for picklist and yes/no fields, and for lookup fields shown in the list (e.g. pick a job opening to see only its applications). Click a row to open the record.

**Export** downloads the list as a CSV file (for Excel), with the filters you've set: only the records you can see, with linked records shown by name. Exports of up to 10,000 rows are allowed, and each export is recorded in the audit log.

## 3. Working with records
- **Create**: use *New …* at the top of a list. Required fields are marked with *.
- **Custom fields** appear in forms, on the detail page and as list columns (if your admin enabled that). Lookup fields show as links to the related record.
- **Stages**: on records with a pipeline, click a stage in the chevron bar or drag the card on the board. The green and red buttons close the record as won or lost; your organisation may name them differently, e.g. *Resolved* or *Hired*.
- **Related lists**: below a record's details you see the records that link to it, such as a job opening's applications, a candidate's applications or a company's job openings. Each list shows the latest 10 with their stage. **View all** opens the full filtered list, **Pipeline** opens the board for just this record, and **New** starts a record already linked to it.
- **Unique fields**: some fields, such as a candidate's email, can't be shared by two records. If you enter a value that's already used, the form tells you, so you can open the existing record instead of creating a duplicate.
- **Activities**: every record page has a timeline. Use *Log call*, *Note* or *Task*, and tick a task to complete it.
- **Email**: if you can edit a record, the timeline has an **Email** button. Choose one of the record's addresses (for an application, the candidate's email), optionally a template, check the text and **Send**. Fields the template needs but the record doesn't have (for example an interview date) are flagged so you can fill them in first. Replies come to your own inbox. The email is added to the timeline; if it can't be delivered, the entry says "[Not sent]".
- **History**: expand **History** on a record to see who changed what, and when.
- **Files**: every record page (except activities) has a **Files** panel. *Upload* résumés, contracts, IDs or spreadsheets (PDF, Word, Excel, PowerPoint, text, CSV and images, up to 10 MB each); click a file to download it. You need edit rights on the record to upload or delete. Deleting a record deletes its files.
- **Import from CSV** (custom objects such as Candidates, Job Openings or Applications, if you can create them): *Import* on the list page. Choose a CSV file (or *Download a template* first), check how its columns map to fields, then **Check file**. Problems are listed by row number and nothing is saved until every row is valid; then **Import**. Link to other records by their name, e.g. the job opening's title or the candidate's full name, so import job openings and candidates before applications. Duplicate values in unique fields are reported, including two rows of the same file.
- **Recruitment** (organisations using the Recruitment template):
  - **Candidates** is your talent pool. Add each person once, with their résumé under **Files**.
  - **Applications** connect a candidate to a job opening and move through the hiring stages (Applied, Screening, Interview, Offer, Hired or Rejected). One candidate can apply for several jobs.
  - To work one job, open it and click **Pipeline** on its Applications list, or pick the job in the board's filter. Drag cards between stages. **New Application** from there fills in the job, and the application takes the candidate's name.
  - Rejecting an application creates a follow-up task to let the candidate know.
  - Ready-made emails: *Application received*, *Interview invitation*, *Application outcome* and *Job offer*. Use them from an application's **Email** button. Your administrator can also switch on automatic acknowledgement and rejection emails.
  - The owner gets a notification a day before each interview (when the application is in the Interview stage and has a *Next interview* date).
  - **Careers page**: tick *Show on careers page* on an open job opening to list it on your organisation's public careers page (an administrator switches the page on under **Settings → Organisation**). Applicants fill in their details, attach a CV (PDF or Word, up to 5 MB) and agree to the privacy notice. Each application appears in that job's pipeline in *Applied*, with the CV under **Files** on both the application and the candidate, a note with what they typed, and a notification to the job's owner. If the email matches an existing candidate, the application is added to that candidate and their details are left unchanged.
  - **Interviews**: on an application, **Schedule interview** with a date and time, type (phone, video or onsite), link or location and the interviewers. Each interviewer gets a notification and a meeting under their activities; tick *Email the candidate an invitation* to send the *Interview invitation* email. *Next interview* on the application always shows the next scheduled one. Use the menu on an interview to reschedule, mark it completed, cancel it or **Add to calendar** (.ics file for Outlook or Google).
  - **Feedback (scorecards)**: after the interview, interviewers click **Give feedback** (on the application or under **My interviews**): an overall recommendation, a 1–5 rating for each criterion and notes. You see colleagues' feedback only after submitting your own, so everyone gives an independent view. Hiring managers with edit rights on the application see all feedback.
  - **My interviews** lists your upcoming interviews and the ones still waiting for your feedback.
  - **Hiring analytics** shows, for a period and optionally one job: applications, hires, time to hire, the funnel (how many got to each stage, how many moved on and how long they spent there), which sources lead to hires, and each job's pipeline. It only counts applications you're allowed to see.
- **Lead conversion** (if you may create accounts and contacts): *Convert* creates a contact, an account (new or existing) and optionally an opportunity.

What you can edit depends on your role. If you don't see *Edit* or *Delete*, you don't have that permission for this record. The **Owner** picker only appears if you're allowed to assign records to others.

## 4. Access levels explained
Your administrator gives you **roles**, directly or through **groups** (teams). For each object, a role says what you can do (read, create, update, delete) and on which records:

| Scope | Records |
|---|---|
| **Own** | Records you own |
| **Group** | Records owned by you or anyone in your groups |
| **All** | Every record in the organisation |
| **None** | No access. The object is hidden. |

If you have several roles, you get the widest access any of them gives.

## 5. Administration (Settings)
You see only the areas you're allowed to manage.

| Area | What you can do |
|---|---|
| **Organisation** | Organisation name and currency. **Allow self-registration** (only if the platform operator has switched it on) and the shareable sign-up link. For recruitment organisations, **Publish a public careers page**, its introduction text, and the link to share (`/careers/<workspace id>`). |
| **Users** | Add users with an initial password. Edit names, active status, roles and groups. Reset passwords, which also unlocks accounts. **Awaiting approval** lists self-registered people: check the verified ticks, then **Approve** with at least one role or group, or **Reject**. You also get a notification for each new request. |
| **Groups** | Teams, regions or departments. Members share records under *Group* scope and inherit the group's roles. |
| **Roles & permissions** | Define roles as a matrix. **All objects** is the role's default for every object, including ones added later. Set a specific object to override it (choose **None** to hide it). Tick the administration areas the role can manage. The **Administrator** role always has full access. |
| **Objects & fields** | Rename or switch off standard objects. Create new objects with an icon, name field label and optional pipeline. Add fields (text, number, currency, date, yes/no, picklists, email, phone, URL, lookup). Tick **Unique** on a text, email, phone or URL field to stop two records sharing a value (clean up existing duplicates first). Tick **Show in list** on a lookup to make it a list column and a filter. Edit pipeline stages (order, colour, probability, open/won/lost). |
| **Picklists** | Industries and lead sources, and for recruitment organisations the **interview criteria** interviewers rate on scorecards. Records that already use a removed value keep it. |
| **Automation** | Rules: *When a [object] [is created / is updated / changes stage / reaches a date] and [conditions] → create a task / notify someone / set a field / send an email*. *Reaches a date* runs a set time before or after a date field, such as 1 day before *Next interview* or 7 days after *Updated at*; each record fires once per date, and dates that passed before you saved the rule are ignored. Emails go to the owner, a user, or an email field (also a linked record's, e.g. *Candidate → Email*), using a template or your own text. Use `{{name}}`, `{{stage}}` or `{{cf.fieldkey}}` in text. The toggle switches a rule on or off, and **Runs** shows how often it fired. |
| **Email templates** | Reusable emails per object. Click a field to insert it, e.g. *Job opening → Job title* becomes `{{cf.position.name}}`. Templates used by rules can't be deleted. |
| **Email log** | Every email sent, with its status. Failed emails (for example a rejected address) are retried automatically up to 5 times and can be retried by hand; waiting emails can be cancelled. Organisations can send up to 1,000 emails a day. |
| **Audit log** | Search all changes by object, action, user or record name. *Exported* entries show who downloaded which list, with which filters. |

Tips:
- New objects are visible to Administrators straight away. Give other roles access under **Roles**; roles with *All objects* access get it automatically.
- You can't grant access you don't have yourself. Only Administrators can grant the Administrator role.
- A field's type can't change later. Create a new field instead.
- Stages, objects and accounts that still have records can't be deleted. Move or delete the records first.

## 6. Platform operators (Tenants)
Platform operators manage organisations (tenants) under **Tenants**:
- **New tenant**: name, workspace ID, a template (Sales CRM, Help Desk, Recruitment or Blank), currency, optional sample data, and the first administrator's details.
- **Suspend / reactivate**: suspended organisations can't sign in. Nothing is deleted.
- **Delete**: suspend first, then type the workspace ID to confirm. All of the tenant's data is removed permanently.

**Platform settings** controls self-registration for every organisation:
- **Self-registration**: the master switch. Each organisation must also opt in under *Settings → Organisation*.
- **Email verification** / **SMS verification**: when on, new users must enter a one-time code for that channel. When off, the step doesn't appear at all.
- **Codes**: length, validity, allowed attempts and the wait before a resend.
- **Email**: *Log* (development: messages go to the API log) or *SMTP* (host, port, username, STARTTLS, from address). **SMS**: *Log* or *HTTP gateway* (URL, sender ID, auth header and scheme). Passwords and API keys aren't entered here; the page only shows whether they're configured on the server.
- **Send test** checks the email or SMS settings.

Each organisation is completely separate: its users, records and configuration are invisible to the others.
