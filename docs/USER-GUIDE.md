# CRM Platform: User Guide

## 1. Signing in
Open the CRM (development: http://localhost:4200) and sign in with the email and password from your administrator. Your session renews itself while you work.

**Creating your own account** (if your organisation allows it): use the link your administrator shared (`…/register/<organisation code>`), or *Create an account* on the sign-in page and enter the organisation code.
1. Fill in your name, email, mobile number and a password.
2. If asked, enter the codes sent to your email and/or mobile. *Send a new code* works after a short wait.
3. An administrator reviews your request and gives you access. Until then, signing in tells you the request is waiting for approval. Use the menu under your name to **change your password** or **sign out**. The menu also shows your organisation and roles.

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

Every list has **search**, **Mine only**, **column sorting**, **paging**, and **filters** for picklist and yes/no fields. Click a row to open the record.

## 3. Working with records
- **Create**: use *New …* at the top of a list. Required fields are marked with *.
- **Custom fields** appear in forms, on the detail page and as list columns (if your admin enabled that). Lookup fields show as links to the related record.
- **Stages**: on records with a pipeline, click a stage in the chevron bar or drag the card on the board. The green and red buttons close the record as won or lost; your organisation may name them differently, e.g. *Resolved* or *Hired*.
- **Activities**: every record page has a timeline. Use *Log call*, *Note* or *Task*, and tick a task to complete it.
- **History**: expand **History** on a record to see who changed what, and when.
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
| **Organisation** | Organisation name and currency. **Allow self-registration** (only if the platform operator has switched it on) and the shareable sign-up link. |
| **Users** | Add users with an initial password. Edit names, active status, roles and groups. Reset passwords, which also unlocks accounts. **Awaiting approval** lists self-registered people: check the verified ticks, then **Approve** with at least one role or group, or **Reject**. You also get a notification for each new request. |
| **Groups** | Teams, regions or departments. Members share records under *Group* scope and inherit the group's roles. |
| **Roles & permissions** | Define roles as a matrix. **All objects** is the role's default for every object, including ones added later. Set a specific object to override it (choose **None** to hide it). Tick the administration areas the role can manage. The **Administrator** role always has full access. |
| **Objects & fields** | Rename or switch off standard objects. Create new objects with an icon, name field label and optional pipeline. Add fields (text, number, currency, date, yes/no, picklists, email, phone, URL, lookup). Edit pipeline stages (order, colour, probability, open/won/lost). |
| **Picklists** | Industries and lead sources. Records that already use a removed value keep it. |
| **Automation** | Rules: *When a [object] [is created / is updated / changes stage] and [conditions] → create a task / notify someone / set a field*. Use `{{name}}`, `{{stage}}` or `{{cf.fieldkey}}` in text. The toggle switches a rule on or off, and **Runs** shows how often it fired. |
| **Audit log** | Search all changes by object, action, user or record name |

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
