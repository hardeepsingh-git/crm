using System.Text.Json.Nodes;
using Crm.Api.Common;
using Crm.Api.Domain;
using Crm.Api.Platform;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Data;

/// <summary>Sample records for demos and local development. Runs inside the target tenant's context.</summary>
public static class DemoData
{
    private static readonly (string Name, string Industry, string City, string Country)[] Companies =
    [
        ("Acme Manufacturing", "Manufacturing", "Sydney", "Australia"),
        ("Blue Harbour Logistics", "Logistics", "Melbourne", "Australia"),
        ("Coral Health Group", "Healthcare", "Brisbane", "Australia"),
        ("Delta Financial Partners", "Financial Services", "Auckland", "New Zealand"),
        ("Evergreen Retail", "Retail", "Perth", "Australia"),
        ("Falcon Aerospace", "Aerospace", "Adelaide", "Australia"),
        ("Granite Construction", "Construction", "Wellington", "New Zealand"),
        ("Horizon Telecom", "Telecommunications", "Singapore", "Singapore"),
        ("Ironbark Energy", "Energy", "Darwin", "Australia"),
        ("Jade Hospitality", "Hospitality", "Hobart", "Australia"),
        ("Kestrel Software", "Technology", "Canberra", "Australia"),
        ("Lumen Education", "Education", "Christchurch", "New Zealand"),
    ];

    private static readonly string[] FirstNames = ["Olivia", "Liam", "Charlotte", "Noah", "Amelia", "Jack", "Isla", "William", "Mia", "Oliver", "Ava", "Leo", "Grace", "Lucas", "Chloe"];
    private static readonly string[] LastNames = ["Smith", "Jones", "Williams", "Brown", "Wilson", "Taylor", "Nguyen", "Chen", "Patel", "Martin", "Kelly", "Walker", "Singh", "White", "Harris"];
    private static readonly string[] Titles = ["CEO", "CFO", "Head of Procurement", "IT Director", "Operations Manager", "Finance Manager", "COO"];
    private static readonly string[] RecordNames =
    [
        "Cannot log in to portal", "Invoice discrepancy", "Request for API access", "Report export fails", "Password reset loop",
        "Senior Backend Engineer", "Product Designer", "Data Analyst", "Customer Success Lead", "DevOps Engineer",
        "Alex Morgan", "Priya Raman", "Tom Becker", "Sofia Rossi", "Kenji Sato", "Laura Méndez", "Omar Haddad", "Emma Lindqvist",
    ];

    public static async Task SeedAsync(CrmDbContext db, UserManager<AppUser> users, AppUser admin, string? demoUserPassword, ILogger logger,
        CancellationToken ct = default)
    {
        var rnd = new Random(42);
        var owners = new List<AppUser> { admin };
        if (!string.IsNullOrWhiteSpace(demoUserPassword))
        {
            owners.Add(await CreateUserAsync(db, users, "manager@crm.local", "Maya", "Manager", TenantTemplates.ManagerRole, demoUserPassword, ct));
            owners.Add(await CreateUserAsync(db, users, "rep@crm.local", "Sam", "Seller", TenantTemplates.StandardRole, demoUserPassword, ct));
            var team = new Group { Name = "Sales Team", Description = "Front-line sellers and their manager." };
            team.Members.AddRange(owners.Skip(1).Select(u => new GroupMember { UserId = u.Id }));
            if (!await db.Groups.AnyAsync(g => g.Name == team.Name, ct)) db.Groups.Add(team);
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        T Pick<T>(IReadOnlyList<T> items) => items[rnd.Next(items.Count)];

        var objects = await db.ObjectDefinitions.ToListAsync(ct);
        bool Active(string key) => objects.Any(o => o.Key == key && o.IsActive);
        var fields = await db.FieldDefinitions.ToListAsync(ct);

        var accounts = Companies.Select(c => new Account
        {
            Name = c.Name, Industry = c.Industry, City = c.City, Country = c.Country,
            Website = $"https://www.{c.Name.Split(' ')[0].ToLowerInvariant()}.example",
            Phone = $"+61 2 {rnd.Next(1000, 9999)} {rnd.Next(1000, 9999)}",
            AnnualRevenue = rnd.Next(1, 500) * 100_000m,
            EmployeeCount = rnd.Next(20, 5000),
            OwnerId = Pick(owners).Id,
        }).ToList();
        foreach (var a in accounts) a.CustomFields = SampleCustomFields(fields, ObjectKeys.Account, rnd, [], []);
        db.Accounts.AddRange(accounts);

        var contacts = new List<Contact>();
        foreach (var account in accounts)
        {
            for (var i = 0; i < 2; i++)
            {
                var first = Pick(FirstNames);
                var last = Pick(LastNames);
                contacts.Add(new Contact
                {
                    FirstName = first, LastName = last, JobTitle = Pick(Titles), Account = account,
                    Email = $"{first}.{last}@{account.Name.Split(' ')[0].ToLowerInvariant()}.example".ToLowerInvariant(),
                    Phone = $"+61 4{rnd.Next(10, 99)} {rnd.Next(100, 999)} {rnd.Next(100, 999)}",
                    OwnerId = account.OwnerId,
                });
            }
        }
        db.Contacts.AddRange(contacts);

        var opportunities = new List<Opportunity>();
        if (Active(ObjectKeys.Opportunity))
        {
            var stages = await db.PipelineStages.Where(s => s.ObjectKey == ObjectKeys.Opportunity).OrderBy(s => s.SortOrder).ToListAsync(ct);
            for (var i = 0; i < 20 && stages.Count > 0; i++)
            {
                var account = Pick(accounts);
                var stage = stages[i % stages.Count];
                var closed = stage.Category != StageCategory.Open;
                opportunities.Add(new Opportunity
                {
                    Name = $"{account.Name.Split(' ')[0]} - {Pick(["Platform licence", "Renewal", "Expansion", "Consulting", "Support contract"])}",
                    Account = account,
                    Contact = contacts.First(c => c.Account == account),
                    Amount = rnd.Next(5, 250) * 1000m,
                    Stage = stage.Key,
                    Probability = stage.Probability,
                    ExpectedCloseDate = today.AddDays(closed ? -rnd.Next(1, 120) : rnd.Next(5, 120)),
                    ClosedAt = closed ? now.AddDays(-rnd.Next(0, 150)) : null,
                    LossReason = stage.Category == StageCategory.Lost ? "Chose competitor" : null,
                    OwnerId = account.OwnerId,
                    CustomFields = SampleCustomFields(fields, ObjectKeys.Opportunity, rnd, [], []),
                });
            }
            db.Opportunities.AddRange(opportunities);
        }

        var leads = new List<Lead>();
        if (Active(ObjectKeys.Lead))
        {
            var sources = Cf.ParseStrings(await db.Picklists.Where(p => p.Key == "lead.source").Select(p => p.Options).FirstOrDefaultAsync(ct));
            var leadStatuses = new[] { LeadStatus.New, LeadStatus.Contacted, LeadStatus.Qualified, LeadStatus.Unqualified };
            leads = Enumerable.Range(0, 15).Select(i =>
            {
                var first = Pick(FirstNames);
                var last = Pick(LastNames);
                return new Lead
                {
                    FirstName = first, LastName = last,
                    Company = $"{Pick(["Summit", "Pioneer", "Atlas", "Nova", "Vertex", "Orbit"])} {Pick(["Labs", "Group", "Holdings", "Systems", "Co"])}",
                    Email = $"{first}.{last}{i}@lead.example".ToLowerInvariant(),
                    JobTitle = Pick(Titles),
                    Source = sources.Length > 0 ? Pick(sources) : "Other",
                    Status = leadStatuses[i % leadStatuses.Length],
                    EstimatedValue = rnd.Next(5, 100) * 1000m,
                    OwnerId = Pick(owners).Id,
                };
            }).ToList();
            db.Leads.AddRange(leads);
        }

        // Custom objects: records created in definition order so lookups can reference earlier objects.
        var customRecords = new List<CustomRecord>();
        foreach (var definition in objects.Where(o => !o.IsStandard && o.IsActive).OrderBy(o => o.SortOrder))
        {
            var stages = await db.PipelineStages.Where(s => s.ObjectKey == definition.Key).OrderBy(s => s.SortOrder).ToListAsync(ct);
            for (var i = 0; i < 8; i++)
            {
                var stage = stages.Count == 0 ? null : stages[i % stages.Count];
                var record = new CustomRecord
                {
                    ObjectDefinitionId = definition.Id,
                    ObjectDefinition = definition,
                    Name = RecordNames[(i + definition.SortOrder) % RecordNames.Length],
                    Stage = stage?.Key ?? "",
                    ClosedAt = stage is { Category: not StageCategory.Open } ? now.AddDays(-rnd.Next(0, 20)) : null,
                    OwnerId = Pick(owners).Id,
                    CustomFields = SampleCustomFields(fields, definition.Key, rnd, accounts, customRecords),
                };
                customRecords.Add(record);
            }
        }
        db.CustomRecords.AddRange(customRecords);

        var subjects = new Dictionary<ActivityType, string[]>
        {
            [ActivityType.Call] = ["Discovery call", "Follow-up call", "Pricing discussion"],
            [ActivityType.Meeting] = ["Product demo", "Quarterly review", "Contract negotiation"],
            [ActivityType.Email] = ["Send proposal", "Share case study", "Send contract draft"],
            [ActivityType.Task] = ["Prepare quote", "Update forecast", "Research competitors"],
            [ActivityType.Note] = ["Budget confirmed", "Decision maker identified", "Procurement process notes"],
        };
        var activities = Enumerable.Range(0, 30).Select(i =>
        {
            var type = (ActivityType)(i % 5);
            var dueOffset = rnd.Next(-10, 14);
            var activity = new Activity
            {
                Type = type,
                Subject = Pick(subjects[type]),
                DueAt = type == ActivityType.Note ? null : now.Date.AddDays(dueOffset).AddHours(rnd.Next(8, 17)),
                CompletedAt = type == ActivityType.Note || (dueOffset < 0 && rnd.Next(2) == 0) ? now.AddDays(dueOffset) : null,
                Priority = (ActivityPriority)rnd.Next(3),
            };
            if (opportunities.Count > 0 && i % 3 != 0)
            {
                var opp = Pick(opportunities);
                (activity.Opportunity, activity.Account, activity.Contact, activity.OwnerId) = (opp, opp.Account, opp.Contact, opp.OwnerId);
            }
            else if (customRecords.Count > 0 && i % 3 == 0)
            {
                var record = Pick(customRecords);
                (activity.CustomRecord, activity.OwnerId) = (record, record.OwnerId);
            }
            else
            {
                var contact = Pick(contacts);
                (activity.Contact, activity.Account, activity.OwnerId) = (contact, contact.Account, contact.OwnerId);
            }
            return activity;
        }).ToList();
        db.Activities.AddRange(activities);

        await db.SaveChangesAsync(ct);
        logger.LogInformation(
            "Seeded demo data: {Accounts} accounts, {Contacts} contacts, {Leads} leads, {Opps} opportunities, {Records} custom records, {Activities} activities",
            accounts.Count, contacts.Count, leads.Count, opportunities.Count, customRecords.Count, activities.Count);
    }

    private static string SampleCustomFields(List<FieldDefinition> fields, string objectKey, Random rnd,
        List<Account> accounts, List<CustomRecord> records)
    {
        var json = new JsonObject();
        foreach (var f in fields.Where(f => f.ObjectKey == objectKey))
        {
            var options = Cf.ParseStrings(f.Options);
            JsonNode? value = f.Type switch
            {
                FieldType.Picklist when options.Length > 0 => options[rnd.Next(options.Length)],
                FieldType.MultiPicklist when options.Length > 0 => new JsonArray(JsonValue.Create(options[rnd.Next(options.Length)])),
                FieldType.Number => (int)Math.Clamp(rnd.Next(1, 6), f.Min ?? 1, f.Max ?? 1000),
                FieldType.Currency => rnd.Next(60, 180) * 1000,
                FieldType.Date => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(rnd.Next(1, 30))).ToString("yyyy-MM-dd"),
                FieldType.Boolean => rnd.Next(2) == 0,
                FieldType.Email => $"person{rnd.Next(100, 999)}@example.com",
                FieldType.Phone => $"+61 4{rnd.Next(10, 99)} {rnd.Next(100, 999)} {rnd.Next(100, 999)}",
                FieldType.Text => f.Key switch { "department" => "Engineering", "location" => "Sydney", _ => null },
                FieldType.Lookup when f.LookupObjectKey == ObjectKeys.Account && accounts.Count > 0 => accounts[rnd.Next(accounts.Count)].Id.ToString(),
                FieldType.Lookup when records.FirstOrDefault(r => r.ObjectDefinition?.Key == f.LookupObjectKey) is not null =>
                    records.Where(r => r.ObjectDefinition?.Key == f.LookupObjectKey).ElementAt(0).Id.ToString(),
                _ => null,
            };
            if (value is not null) json[f.Key] = value;
        }
        return json.ToJsonString();
    }

    private static async Task<AppUser> CreateUserAsync(CrmDbContext db, UserManager<AppUser> users, string email, string first, string last,
        string roleName, string password, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is not null) return user;

        user = new AppUser { UserName = email, Email = email, EmailConfirmed = true, FirstName = first, LastName = last };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Demo user {email}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        var role = await db.Roles.SingleAsync(r => r.Name == roleName, ct);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync(ct);
        return user;
    }
}
