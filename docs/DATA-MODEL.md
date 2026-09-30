# CRM: Data Model

PostgreSQL schema `public`, database `crm`. Every table is created by the EF Core migration `InitialCreate`. The equivalent idempotent DDL is in [`backend/database/crm-schema.sql`](../backend/database/crm-schema.sql).

## Entity-relationship diagram

```mermaid
erDiagram
    AspNetUsers ||--o{ Accounts : owns
    AspNetUsers ||--o{ Contacts : owns
    AspNetUsers ||--o{ Leads : owns
    AspNetUsers ||--o{ Opportunities : owns
    AspNetUsers ||--o{ Activities : owns
    AspNetUsers ||--o{ RefreshTokens : has
    AspNetUsers ||--o{ AspNetUserRoles : ""
    AspNetRoles ||--o{ AspNetUserRoles : ""
    Accounts ||--o{ Contacts : "employs (SET NULL)"
    Accounts ||--o{ Opportunities : "has (RESTRICT)"
    Contacts |o--o{ Opportunities : "primary contact (SET NULL)"
    Accounts |o--o{ Activities : "CASCADE"
    Contacts |o--o{ Activities : "CASCADE"
    Leads |o--o{ Activities : "CASCADE"
    Opportunities |o--o{ Activities : "CASCADE"
    Leads |o--o| Accounts : "converted to (SET NULL)"
    Leads |o--o| Contacts : "converted to (SET NULL)"
    Leads |o--o| Opportunities : "converted to (SET NULL)"

    Accounts {
        uuid Id PK
        varchar200 Name
        varchar100 Industry
        varchar300 Website
        varchar50 Phone
        varchar256 Email
        varchar Street_City_State_PostalCode_Country
        numeric AnnualRevenue
        int EmployeeCount
        varchar4000 Description
        uuid OwnerId FK
        timestamptz CreatedAt
        timestamptz UpdatedAt
    }
    Contacts {
        uuid Id PK
        varchar100 FirstName
        varchar100 LastName
        varchar256 Email
        varchar50 Phone
        varchar50 Mobile
        varchar100 JobTitle
        varchar100 Department
        varchar4000 Description
        uuid AccountId FK
        uuid OwnerId FK
    }
    Leads {
        uuid Id PK
        varchar100 FirstName
        varchar100 LastName
        varchar200 Company
        varchar256 Email
        varchar32 Source
        varchar32 Status
        numeric EstimatedValue
        timestamptz ConvertedAt
        uuid ConvertedAccountId FK
        uuid ConvertedContactId FK
        uuid ConvertedOpportunityId FK
        uuid OwnerId FK
    }
    Opportunities {
        uuid Id PK
        varchar200 Name
        uuid AccountId FK
        uuid ContactId FK
        numeric Amount
        varchar32 Stage
        int Probability
        date ExpectedCloseDate
        timestamptz ClosedAt
        varchar500 LossReason
        uuid OwnerId FK
    }
    Activities {
        uuid Id PK
        varchar32 Type
        varchar200 Subject
        varchar4000 Description
        timestamptz DueAt
        timestamptz CompletedAt
        varchar32 Priority
        uuid AccountId FK
        uuid ContactId FK
        uuid LeadId FK
        uuid OpportunityId FK
        uuid OwnerId FK
    }
    RefreshTokens {
        uuid Id PK
        uuid UserId FK
        varchar128 TokenHash UK
        timestamptz CreatedAt
        timestamptz ExpiresAt
        timestamptz RevokedAt
    }
```

Every CRM table (Accounts, Contacts, Leads, Opportunities, Activities) has `Id`, `OwnerId`, `CreatedAt` and `UpdatedAt`.

## Conventions

| Topic | Rule |
|---|---|
| Primary keys | `uuid`, generated in the app as UUID v7 (time-ordered) |
| Timestamps | `timestamp with time zone`, always UTC. `CreatedAt` and `UpdatedAt` are set automatically on save. |
| Money | `numeric(18,2)` |
| Enums | Stored as `varchar(32)` strings, e.g. `'ClosedWon'` |
| Identity tables | Standard ASP.NET Core Identity (`AspNetUsers`, `AspNetRoles`, `AspNetUserRoles`, …). `AspNetUsers` adds `FirstName`, `LastName`, `IsActive`, `CreatedAt`. |
| Users | Never hard-deleted. Deactivate with `IsActive = false`. Owner FKs are `RESTRICT`. |

## Enumerations

| Enum | Values |
|---|---|
| LeadSource | Web, Referral, Event, ColdCall, Advertisement, Partner, Other |
| LeadStatus | New, Contacted, Qualified, Unqualified, Converted |
| OpportunityStage (default probability) | Prospecting (10), Qualification (25), Proposal (50), Negotiation (75), ClosedWon (100), ClosedLost (0) |
| ActivityType | Task, Call, Meeting, Email, Note |
| ActivityPriority | Low, Normal, High |
| Roles | Admin, Manager, SalesRep |

## Indexes

| Table | Index |
|---|---|
| Accounts | `Name`, `OwnerId` |
| Contacts | `LastName`, `Email`, `AccountId`, `OwnerId` |
| Leads | `Status`, `Email`, `OwnerId`, converted FKs |
| Opportunities | `Stage`, `ExpectedCloseDate`, `AccountId`, `ContactId`, `OwnerId` |
| Activities | `DueAt`, `OwnerId`, and every related FK |
| RefreshTokens | `TokenHash` (unique), `UserId` |

**Scaling note:** search uses `lower(col) LIKE '%term%'`. For more than about 1M rows, add `pg_trgm` GIN indexes, for example `CREATE INDEX ... USING gin (lower("Name") gin_trgm_ops)`.

## Schema changes

```powershell
cd backend
dotnet ef migrations add <Name> --project src/Crm.Api --output-dir Data/Migrations
dotnet ef migrations script --idempotent --project src/Crm.Api --output database/crm-schema.sql
```
