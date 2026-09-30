# CRM: User Guide

## Signing in
Open the CRM URL (development: http://localhost:4200) and sign in with the email and password your administrator gave you. Your session renews itself while you work. **Sign out** from the menu under your name in the top-right corner.

To change your password, go to *your name → Change password*. Passwords need at least 10 characters with upper and lower case letters, a digit and a symbol. After changing it you're signed out everywhere.

## Navigation
| Menu | What it's for |
|---|---|
| **Dashboard** | Pipeline, forecast, wins, win rate, overdue work, and your next 7 days. Turn on *Only my records* for personal numbers. |
| **Accounts** | Companies you do business with |
| **Contacts** | People, usually linked to an account |
| **Leads** | Unqualified prospects. Convert them once they're qualified. |
| **Opportunities** | Deals, as a sortable list |
| **Pipeline** | The same deals as a drag-and-drop board by stage |
| **Activities** | Every task, call, meeting, email and note |
| **Users** | Administrators only |

Every list supports **search**, a **"My …" toggle**, **column sorting** (click a header) and **paging**. Click a row to open it.

## Typical workflow
1. **Capture a lead.** Go to *Leads → New lead*, then set the source and estimated value.
2. **Work the lead.** Open it and use *Log call*, *Note* or *Task* in the Activities panel. Update the status to *Contacted*, then *Qualified*.
3. **Convert it.** Select *Convert*, then either create a new account or pick an existing one. Keep *Create an opportunity* ticked to open a deal.
4. **Progress the deal.** On the opportunity page, click the stage chevrons or drag the card on the **Pipeline** board. Probability updates automatically, and you can override it in *Edit* while the deal is open.
5. **Close it.** Select **Won** or **Lost**. You can record why a deal was lost.
6. **Stay on top of follow-ups.** Tick the checkbox next to a task to complete it. Overdue items show in red and appear on the dashboard.

## Who can do what
| Action | Sales rep | Manager | Admin |
|---|---|---|---|
| View all records | ✔ | ✔ | ✔ |
| Create records | ✔ | ✔ | ✔ |
| Edit records | Own only | All | All |
| Assign records to others | none | ✔ | ✔ |
| Delete accounts/contacts/leads/deals | none | ✔ | ✔ |
| Delete activities | Own only | All | All |
| Manage users | none | none | ✔ |

## Administration (Admins)
*Users → New user*: enter the name, email, an initial password and roles.

- **Edit** to change roles or deactivate someone. Deactivated users are signed out immediately and can't sign in.
- **Reset password** (key icon) to set a new password. This also unlocks a locked account.

## Tips
- In any record picker, type a few letters to search, then choose from the list.
- The dashboard's **Overdue activities** number links straight to the overdue list.
- Converted leads are read-only. Edit the account, contact or opportunity they created instead.
