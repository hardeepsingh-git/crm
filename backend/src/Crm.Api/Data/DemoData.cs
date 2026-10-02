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
        string[] ats = ["position", "candidate", "application"];
        var hasAts = ats.All(Active);
        if (hasAts) customRecords.AddRange(await SeedRecruitmentAsync(db, objects, accounts, owners, rnd, now, ct));
        foreach (var definition in objects.Where(o => !o.IsStandard && o.IsActive && !(hasAts && ats.Contains(o.Key)) && o.Key != Features.MockInterviews.MockKeys.QuestionObject).OrderBy(o => o.SortOrder))
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

    private static readonly (string Title, string Department, string Location, string Type, int Openings, int Salary)[] Jobs =
    [
        ("Senior Backend Engineer", "Engineering", "Sydney", "Full-time", 2, 165000),
        ("Product Designer", "Design", "Melbourne", "Full-time", 1, 130000),
        ("Data Analyst", "Analytics", "Remote", "Contract", 1, 110000),
        ("Customer Success Lead", "Customer Success", "Brisbane", "Full-time", 1, 120000),
        ("DevOps Engineer", "Engineering", "Sydney", "Full-time", 1, 155000),
        ("Graduate Accountant", "Finance", "Perth", "Full-time", 3, 75000),
    ];

    private static readonly string[] CandidateTitles =
        ["Software Engineer", "Senior Developer", "UX Designer", "Business Analyst", "Data Scientist", "Account Manager", "Accountant", "SRE"];

    /// <summary>Job openings, a talent pool and applications spread across the hiring stages, for recruitment tenants.</summary>
    private static async Task<List<CustomRecord>> SeedRecruitmentAsync(CrmDbContext db, List<ObjectDefinition> objects, List<Account> accounts,
        List<AppUser> owners, Random rnd, DateTime now, CancellationToken ct)
    {
        T Pick<T>(IReadOnlyList<T> items) => items[rnd.Next(items.Count)];
        var position = objects.Single(o => o.Key == "position");
        var candidate = objects.Single(o => o.Key == "candidate");
        var application = objects.Single(o => o.Key == "application");
        var skills = Cf.ParseStrings(await db.FieldDefinitions.Where(f => f.ObjectKey == "candidate" && f.Key == "skills").Select(f => f.Options).FirstOrDefaultAsync(ct));
        var stages = await db.PipelineStages.Where(s => s.ObjectKey == "application").OrderBy(s => s.SortOrder).ToListAsync(ct);
        var today = DateOnly.FromDateTime(now);

        var jobs = Jobs.Select((j, i) => new CustomRecord
        {
            ObjectDefinitionId = position.Id, ObjectDefinition = position, Name = j.Title, OwnerId = Pick(owners).Id,
            CustomFields = new JsonObject
            {
                ["department"] = j.Department, ["location"] = j.Location, ["employment_type"] = j.Type,
                ["status"] = i == Jobs.Length - 1 ? "On hold" : "Open", ["openings"] = j.Openings, ["salary_max"] = j.Salary,
                ["hiring_company"] = accounts.Count > 0 ? accounts[i % accounts.Count].Id.ToString() : null,
                ["closing_date"] = today.AddDays(14 + 7 * i).ToString("yyyy-MM-dd"),
                ["publish"] = i < 4,
                ["description"] = $"We're looking for a {j.Title} to join our {j.Department} team ({j.Location}, {j.Type.ToLowerInvariant()}).\n\n" +
                    "You'll work with a friendly, experienced team on meaningful projects, with flexible hours and support to keep learning.",
            }.ToJsonString(),
        }).ToList();

        string[] sources = ["Job board", "Referral", "LinkedIn", "Agency", "Website"];
        var people = Enumerable.Range(0, 16).Select(i =>
        {
            var first = FirstNames[i % FirstNames.Length];
            var last = LastNames[(i * 7 + 3) % LastNames.Length];
            return new CustomRecord
            {
                ObjectDefinitionId = candidate.Id, ObjectDefinition = candidate, Name = $"{first} {last}", OwnerId = Pick(owners).Id,
                CustomFields = new JsonObject
                {
                    ["email"] = $"{first}.{last}{i}@candidate.example".ToLowerInvariant(),
                    ["phone"] = $"+61 4{rnd.Next(10, 99)} {rnd.Next(100, 999)} {rnd.Next(100, 999)}",
                    ["location"] = Pick(["Sydney", "Melbourne", "Brisbane", "Perth", "Remote"]),
                    ["current_title"] = Pick(CandidateTitles),
                    ["skills"] = skills.Length == 0 ? null : new JsonArray(skills.OrderBy(_ => rnd.Next()).Take(2).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
                    ["source"] = Pick(sources),
                    ["expected_salary"] = rnd.Next(70, 180) * 1000,
                    ["notice_period"] = Pick(["Immediate", "2 weeks", "4 weeks", "3 months"]),
                }.ToJsonString(),
            };
        }).ToList();

        // Most applications sit in the early stages, as in a real funnel.
        int[] funnel = [0, 0, 0, 1, 1, 1, 2, 2, 3, 4, 5];
        var pairs = new HashSet<(int, int)>();
        var applications = new List<CustomRecord>();
        while (applications.Count < 22 && stages.Count > 0)
        {
            var (c, j) = (rnd.Next(people.Count), rnd.Next(jobs.Count));
            if (!pairs.Add((c, j))) continue;
            var stage = stages[Math.Min(Pick(funnel), stages.Count - 1)];
            applications.Add(new CustomRecord
            {
                ObjectDefinitionId = application.Id, ObjectDefinition = application, Name = people[c].Name, OwnerId = jobs[j].OwnerId,
                Stage = stage.Key, ClosedAt = stage.Category == StageCategory.Open ? null : now.AddDays(-rnd.Next(0, 20)),
                CustomFields = new JsonObject
                {
                    ["candidate"] = people[c].Id.ToString(), ["position"] = jobs[j].Id.ToString(),
                    ["source"] = Cf.AsText(Cf.Parse(people[c].CustomFields)["source"]),
                    ["rating"] = stage.SortOrder == 0 ? null : rnd.Next(2, 6),
                }.ToJsonString(),
            });
        }
        await SeedInterviewsAsync(db, applications, rnd, now, ct);
        await SeedOffersAsync(db, applications, jobs, rnd, now, ct);
        var questions = objects.FirstOrDefault(o => o.Key == Features.MockInterviews.MockKeys.QuestionObject) is { } library
            ? Features.MockInterviews.MockInterviewEndpoints.StarterRecords(library, owners[0].Id).ToList()
            : [];
        if (questions.Count > 0) SeedMockInterviews(db, applications, jobs, rnd, now);
        return [.. jobs, .. people, .. applications, .. questions];
    }

    /// <summary>A reviewed practice interview for an application at Interview, and one waiting for a candidate at Screening. Demo links are never usable.</summary>
    private static void SeedMockInterviews(CrmDbContext db, List<CustomRecord> applications, List<CustomRecord> jobs, Random rnd, DateTime now)
    {
        var titles = jobs.ToDictionary(j => j.Id.ToString(), j => j.Name);
        var questions = Features.MockInterviews.MockKeys.StarterQuestions.Take(4)
            .Select((q, i) => new Features.MockInterviews.MockQuestion($"q{i + 1}", q.Prompt, null, q.Competency, q.Guidance, q.Minutes)).ToList();
        string[] sampleAnswers =
        [
            "I'm a developer with five years' experience building customer-facing web apps. I recently led a migration that cut page load times by 40%, and I'm keen to bring that focus on users to your team.",
            "Our nightly billing job kept timing out. I profiled it, found an N+1 query, batched the reads and added an index. Runtime dropped from 3 hours to 12 minutes and we stopped missing invoices.",
            "A colleague wanted to ship without tests. I listened to the deadline pressure, then proposed covering only the riskiest paths. We shipped on time and caught a bug before release.",
        ];
        foreach (var (app, submitted) in new[] { (applications.FirstOrDefault(a => a.Stage == "interview"), true), (applications.FirstOrDefault(a => a.Stage == "screening"), false) })
        {
            if (app is null) continue;
            var sent = now.AddDays(-rnd.Next(2, 6));
            var mock = new MockInterview
            {
                ApplicationId = app.Id, JobTitle = titles.GetValueOrDefault(Cf.AsText(Cf.Parse(app.CustomFields)["position"]) ?? "", "the role"),
                Questions = Features.MockInterviews.MockJson.Serialize(questions), SentById = app.OwnerId, SentAt = sent, ExpiresAt = sent.AddDays(14),
                TokenHash = Features.Offers.OfferEndpoints.HashToken(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")),
                Message = "A short practice round to help you prepare. There are no trick questions.",
            };
            if (submitted)
            {
                mock.Status = MockInterviewStatus.Submitted;
                mock.StartedAt = sent.AddDays(1);
                mock.SubmittedAt = mock.StartedAt.Value.AddMinutes(18);
                mock.Answers = Features.MockInterviews.MockJson.Serialize(sampleAnswers.Select((text, i) => (Id: $"q{i + 1}", text))
                    .ToDictionary(x => x.Id, x => new Features.MockInterviews.MockAnswer(x.text, rnd.Next(3, 6), rnd.Next(90, 300), mock.SubmittedAt.Value)));
                (mock.ReadinessRating, mock.ReviewNotes, mock.ReviewedById, mock.ReviewedAt) =
                    (4, "Structured, concrete answers with measurable results. Probe system design depth in the real interview.", app.OwnerId, mock.SubmittedAt.Value.AddHours(3));
            }
            db.MockInterviews.Add(mock);
        }
    }

    /// <summary>An open offer for applications at Offer, and an accepted one for hires. Demo links are never usable.</summary>
    private static async Task SeedOffersAsync(CrmDbContext db, List<CustomRecord> applications, List<CustomRecord> jobs, Random rnd, DateTime now, CancellationToken ct)
    {
        var tenantId = db.CurrentTenantId;
        var currency = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.Currency).FirstOrDefaultAsync(ct) ?? "AUD";
        var titles = jobs.ToDictionary(j => j.Id.ToString(), j => j.Name);
        foreach (var app in applications.Where(a => a.Stage is "offer" or "hired"))
        {
            var job = titles.GetValueOrDefault(Cf.AsText(Cf.Parse(app.CustomFields)["position"]) ?? "", "the role");
            var hired = app.Stage == "hired";
            var sent = now.AddDays(-rnd.Next(hired ? 10 : 1, hired ? 30 : 4));
            var offer = new Offer
            {
                ApplicationId = app.Id, JobTitle = job, Salary = rnd.Next(70, 170) * 1000m, Currency = currency, PayPeriod = PayPeriod.Year,
                StartDate = DateOnly.FromDateTime(now).AddDays(hired ? 14 : 30), ExpiresAt = hired ? sent.AddDays(7) : now.AddDays(5),
                CreatedById = app.OwnerId, SentById = app.OwnerId, SentAt = sent, Status = hired ? OfferStatus.Accepted : OfferStatus.Sent,
                TokenHash = Features.Offers.OfferEndpoints.HashToken(Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")),
                Letter = $"Dear {app.Name},\n\nWe are delighted to offer you the position of {job}.\n\nWe look forward to welcoming you to the team.",
            };
            if (hired) (offer.RespondedAt, offer.SignedName) = (sent.AddDays(2), app.Name);
            db.Offers.Add(offer);
        }
    }

    /// <summary>A completed interview with a scorecard for applications past screening, and an upcoming one for those in Interview.</summary>
    private static async Task SeedInterviewsAsync(CrmDbContext db, List<CustomRecord> applications, Random rnd, DateTime now, CancellationToken ct)
    {
        var criteria = Cf.ParseStrings(await db.Picklists.Where(p => p.Key == "interview.criteria").Select(p => p.Options).FirstOrDefaultAsync(ct));
        string[] notes = ["Clear communicator with solid fundamentals.", "Strong problem solving; would like more depth on system design.", "Great culture fit and very motivated."];
        foreach (var app in applications.Where(a => a.Stage is "interview" or "offer" or "hired"))
        {
            var held = new Interview
            {
                ApplicationId = app.Id, ScheduledAt = now.Date.AddDays(-rnd.Next(2, 15)).AddHours(rnd.Next(9, 16)), DurationMinutes = 45,
                Kind = InterviewKind.Video, Location = "https://meet.example.com/screen", Status = InterviewStatus.Completed, OrganizerId = app.OwnerId,
            };
            held.Participants.Add(new InterviewParticipant { UserId = app.OwnerId });
            held.Scorecards.Add(new Scorecard
            {
                InterviewerId = app.OwnerId, Recommendation = app.Stage == "interview" ? Recommendation.Yes : Recommendation.StrongYes,
                Ratings = new JsonObject(criteria.Select(c => KeyValuePair.Create(c, (JsonNode?)JsonValue.Create(rnd.Next(3, 6))))).ToJsonString(),
                Notes = notes[rnd.Next(notes.Length)], SubmittedAt = held.ScheduledAt.AddHours(2),
            });
            db.Interviews.Add(held);
            if (app.Stage != "interview") continue;

            var next = new Interview
            {
                ApplicationId = app.Id, ScheduledAt = now.Date.AddDays(rnd.Next(1, 8)).AddHours(rnd.Next(9, 16)), DurationMinutes = 60,
                Kind = InterviewKind.Onsite, Location = "Head office, level 3", OrganizerId = app.OwnerId,
            };
            var meeting = new Activity
            {
                Type = ActivityType.Meeting, OwnerId = app.OwnerId, CustomRecord = app, DueAt = next.ScheduledAt, Priority = ActivityPriority.High,
                Subject = $"Onsite interview: {app.Name}", Description = "60 minutes, onsite\nWhere: Head office, level 3",
            };
            db.Activities.Add(meeting);
            next.Participants.Add(new InterviewParticipant { UserId = app.OwnerId, ActivityId = meeting.Id });
            db.Interviews.Add(next);
            var values = Cf.Parse(app.CustomFields);
            values["interview_date"] = next.ScheduledAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
            app.CustomFields = values.ToJsonString();
        }
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
