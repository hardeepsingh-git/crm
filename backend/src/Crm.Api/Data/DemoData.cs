using Crm.Api.Common;
using Crm.Api.Domain;
using Microsoft.AspNetCore.Identity;

namespace Crm.Api.Data;

/// <summary>Sample records for demos and local development. Only runs against an empty database.</summary>
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

    public static async Task SeedAsync(CrmDbContext db, UserManager<AppUser> users, AppUser admin, string? demoUserPassword, ILogger logger)
    {
        var rnd = new Random(42);
        var owners = new List<AppUser> { admin };

        if (!string.IsNullOrWhiteSpace(demoUserPassword))
        {
            owners.Add(await CreateUserAsync(users, "manager@crm.local", "Maya", "Manager", Roles.Manager, demoUserPassword));
            owners.Add(await CreateUserAsync(users, "rep@crm.local", "Sam", "Seller", Roles.SalesRep, demoUserPassword));
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        T Pick<T>(IReadOnlyList<T> items) => items[rnd.Next(items.Count)];

        var accounts = Companies.Select(c => new Account
        {
            Name = c.Name, Industry = c.Industry, City = c.City, Country = c.Country,
            Website = $"https://www.{c.Name.Split(' ')[0].ToLowerInvariant()}.example",
            Phone = $"+61 2 {rnd.Next(1000, 9999)} {rnd.Next(1000, 9999)}",
            AnnualRevenue = rnd.Next(1, 500) * 100_000m,
            EmployeeCount = rnd.Next(20, 5000),
            OwnerId = Pick(owners).Id,
        }).ToList();
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

        var stages = Enum.GetValues<OpportunityStage>();
        var opportunities = new List<Opportunity>();
        for (var i = 0; i < 20; i++)
        {
            var account = Pick(accounts);
            var stage = stages[i % stages.Length];
            var closed = stage is OpportunityStage.ClosedWon or OpportunityStage.ClosedLost;
            opportunities.Add(new Opportunity
            {
                Name = $"{account.Name.Split(' ')[0]} - {Pick(["Platform licence", "Renewal", "Expansion", "Consulting", "Support contract"])}",
                Account = account,
                Contact = contacts.First(c => c.Account == account),
                Amount = rnd.Next(5, 250) * 1000m,
                Stage = stage,
                Probability = StageDefaults.Probability(stage),
                ExpectedCloseDate = today.AddDays(closed ? -rnd.Next(1, 120) : rnd.Next(5, 120)),
                ClosedAt = closed ? now.AddDays(-rnd.Next(0, 150)) : null,
                LossReason = stage == OpportunityStage.ClosedLost ? "Chose competitor" : null,
                OwnerId = account.OwnerId,
            });
        }
        db.Opportunities.AddRange(opportunities);

        var leadStatuses = new[] { LeadStatus.New, LeadStatus.Contacted, LeadStatus.Qualified, LeadStatus.Unqualified };
        var leads = Enumerable.Range(0, 15).Select(i =>
        {
            var first = Pick(FirstNames);
            var last = Pick(LastNames);
            return new Lead
            {
                FirstName = first, LastName = last,
                Company = $"{Pick(["Summit", "Pioneer", "Atlas", "Nova", "Vertex", "Orbit"])} {Pick(["Labs", "Group", "Holdings", "Systems", "Co"])}",
                Email = $"{first}.{last}{i}@lead.example".ToLowerInvariant(),
                JobTitle = Pick(Titles),
                Source = Pick(Enum.GetValues<LeadSource>()),
                Status = leadStatuses[i % leadStatuses.Length],
                EstimatedValue = rnd.Next(5, 100) * 1000m,
                OwnerId = Pick(owners).Id,
            };
        }).ToList();
        db.Leads.AddRange(leads);

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
            var opp = Pick(opportunities);
            var dueOffset = rnd.Next(-10, 14);
            return new Activity
            {
                Type = type,
                Subject = Pick(subjects[type]),
                DueAt = type == ActivityType.Note ? null : now.Date.AddDays(dueOffset).AddHours(rnd.Next(8, 17)),
                CompletedAt = type == ActivityType.Note || (dueOffset < 0 && rnd.Next(2) == 0) ? now.AddDays(dueOffset) : null,
                Priority = (ActivityPriority)rnd.Next(3),
                Opportunity = opp,
                Account = opp.Account,
                Contact = opp.Contact,
                OwnerId = opp.OwnerId,
            };
        }).ToList();
        db.Activities.AddRange(activities);

        await db.SaveChangesAsync();
        logger.LogInformation("Seeded demo data: {Accounts} accounts, {Contacts} contacts, {Leads} leads, {Opps} opportunities, {Activities} activities",
            accounts.Count, contacts.Count, leads.Count, opportunities.Count, activities.Count);
    }

    private static async Task<AppUser> CreateUserAsync(UserManager<AppUser> users, string email, string first, string last, string role, string password)
    {
        var user = await users.FindByEmailAsync(email);
        if (user is not null) return user;

        user = new AppUser { UserName = email, Email = email, EmailConfirmed = true, FirstName = first, LastName = last };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
            throw new InvalidOperationException($"Demo user {email}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        await users.AddToRoleAsync(user, role);
        return user;
    }
}
