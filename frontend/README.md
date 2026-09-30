# CRM Platform: Frontend

Angular 22 single-page app (standalone components, zoneless, signals, `httpResource`, Angular Material 3). See the root [README](../README.md) and [docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md) §7 for the design.

## Commands
```powershell
npm start                   # dev server on http://localhost:4200, proxies /api to http://localhost:5290 (proxy.conf.json)
npx ng build                # production build into dist/frontend/browser
npx ng test --watch=false   # Vitest unit tests
```

## Layout
| Folder | Contents |
|---|---|
| `src/app/core` | `models.ts` (API contracts), `api.ts` (`CrmApi`, lookups, record-name cache), `auth.service.ts` (session + permission helpers: `scope`, `can`, `canModify`, `canDelete`, `canAssign`), `metadata.service.ts` (tenant objects, fields, stages, picklists, currency), `http.ts` (interceptors and guards: `authGuard`, `metadataGuard`, `objectGuard`, `permissionGuard`, `adminAreaGuard`, `platformGuard`) |
| `src/app/shared` | Custom-field editor, view and filters; stage chip, path and board; record history; entity picker; owner field; activity panel; list state |
| `src/app/layout` | Shell: dynamic navigation, notifications bell, user menu |
| `src/app/features` | Standard objects, `records/` (generic custom-object list, board, detail and form), `admin/` (settings hub: organisation, users, groups, roles, objects/fields/stages, picklists, automation, audit), `platform/` (tenants) |

## Adding UI for a new standard field
Most customization needs no code: administrators add **custom fields** in Settings, and they appear in forms, detail pages, lists and filters automatically. Only built-in columns (new C# properties) need changes to the object's form, detail and list components.
