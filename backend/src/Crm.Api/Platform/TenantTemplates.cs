using System.Text.Json;
using Crm.Api.Automation;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Platform;

public record TemplateInfo(string Key, string Name, string Description);

/// <summary>Starter configurations. Applying one to a tenant creates objects, fields, pipelines, picklists, roles and automation rules.</summary>
public static class TenantTemplates
{
    public const string Sales = "sales";
    public const string HelpDesk = "helpdesk";
    public const string Recruitment = "recruitment";
    public const string Blank = "blank";

    public static readonly TemplateInfo[] All =
    [
        new(Sales, "Sales CRM", "Accounts, contacts, leads, opportunity pipeline and sales automation."),
        new(HelpDesk, "Help Desk", "Customers, contacts and a ticket pipeline with priority-based alerts."),
        new(Recruitment, "Recruitment (ATS)", "Job openings, a candidate talent pool and an application pipeline per job."),
        new(Blank, "Blank", "Standard objects and roles only. Build your own configuration."),
    ];

    public const string AdministratorRole = "Administrator";
    public const string ManagerRole = "Manager";
    public const string StandardRole = "Standard User";

    public static readonly string[] DefaultIndustries =
    [
        "Aerospace", "Construction", "Education", "Energy", "Financial Services", "Government", "Healthcare", "Hospitality",
        "Logistics", "Manufacturing", "Media", "Non-profit", "Professional Services", "Real Estate", "Retail", "Software",
        "Technology", "Telecommunications", "Other",
    ];

    public static readonly string[] DefaultLeadSources = ["Web", "Referral", "Event", "ColdCall", "Advertisement", "Partner", "Other"];

    /// <summary>Latest revision of each template. Raising it makes startup apply the template's new pieces to existing tenants once.</summary>
    public static int CurrentVersion(string template) => template == Recruitment ? 2 : 1;

    /// <summary>
    /// Brings tenants created from an older template revision up to date. Only missing objects, fields, stages and rules are
    /// added (records and admin changes are never touched), and each tenant is upgraded once, so later deletions stick.
    /// </summary>
    public static async Task UpgradeTenantsAsync(CrmDbContext db, TenantContext tenantContext, ILogger logger, CancellationToken ct = default)
    {
        var tenants = await db.Tenants.ToListAsync(ct);
        foreach (var tenant in tenants.Where(t => t.TemplateVersion < CurrentVersion(t.Template)))
        {
            using (tenantContext.Use(tenant.Id, userName: "System"))
            {
                await ApplyAsync(db, tenant.Template, ct);
                tenant.TemplateVersion = CurrentVersion(tenant.Template);
                await db.SaveChangesAsync(ct);
            }
            logger.LogInformation("Upgraded tenant {Slug} to {Template} template v{Version}", tenant.Slug, tenant.Template, tenant.TemplateVersion);
        }
    }

    /// <summary>Idempotent: existing objects, stages, picklists and roles (matched by key/name) are left untouched.</summary>
    public static async Task ApplyAsync(CrmDbContext db, string template, CancellationToken ct = default)
    {
        var existingObjects = await db.ObjectDefinitions.Select(o => o.Key).ToListAsync(ct);
        var existingStages = await db.PipelineStages.Select(s => s.ObjectKey).Distinct().ToListAsync(ct);
        var existingPicklists = await db.Picklists.Select(p => p.Key).ToListAsync(ct);
        var existingFields = await db.FieldDefinitions.Select(f => f.ObjectKey + "." + f.Key).ToListAsync(ct);
        var existingRules = await db.AutomationRules.Select(r => r.Name).ToListAsync(ct);

        void Object(string key, string label, string plural, string icon, int order, bool active = true, bool pipeline = false,
            bool standard = true, string nameLabel = "Name", string? description = null)
        {
            if (existingObjects.Contains(key)) return;
            db.ObjectDefinitions.Add(new ObjectDefinition
            {
                Key = key, Label = label, PluralLabel = plural, Icon = icon, SortOrder = order, IsActive = active,
                HasPipeline = pipeline, IsStandard = standard, NameLabel = nameLabel, Description = description,
            });
            existingObjects.Add(key);
        }

        void Stages(string objectKey, params (string Key, string Label, int Probability, StageCategory Category, string Color)[] stages)
        {
            if (existingStages.Contains(objectKey)) return;
            var order = 0;
            foreach (var s in stages)
                db.PipelineStages.Add(new PipelineStage
                {
                    ObjectKey = objectKey, Key = s.Key, Label = s.Label, Probability = s.Probability, Category = s.Category,
                    Color = s.Color, SortOrder = order++,
                });
            existingStages.Add(objectKey);
        }

        void Picklist(string key, string[] options)
        {
            if (existingPicklists.Contains(key)) return;
            db.Picklists.Add(new Picklist { Key = key, Options = Cf.Serialize(options) });
            existingPicklists.Add(key);
        }

        void Field(string objectKey, string key, string label, FieldType type, int order, bool required = false, bool list = false,
            string[]? options = null, string? lookup = null, decimal? min = null, decimal? max = null, string? help = null, bool unique = false)
        {
            if (existingFields.Contains($"{objectKey}.{key}")) return;
            db.FieldDefinitions.Add(new FieldDefinition
            {
                ObjectKey = objectKey, Key = key, Label = label, Type = type, SortOrder = order, IsRequired = required,
                ShowInList = list, Options = Cf.Serialize(options ?? []), LookupObjectKey = lookup, Min = min, Max = max, HelpText = help,
                IsUnique = unique,
            });
            existingFields.Add($"{objectKey}.{key}");
        }

        void Rule(string name, string objectKey, AutomationTrigger trigger, AutomationCondition[] conditions, AutomationAction[] actions)
        {
            if (existingRules.Contains(name)) return;
            existingRules.Add(name);
            db.AutomationRules.Add(new AutomationRule
            {
                Name = name, ObjectKey = objectKey, Trigger = trigger,
                Conditions = JsonSerializer.Serialize(conditions, AutomationJson.Options),
                Actions = JsonSerializer.Serialize(actions, AutomationJson.Options),
            });
        }

        var salesStages = new[]
        {
            ("Prospecting", "Prospecting", 10, StageCategory.Open, "#5c6bc0"),
            ("Qualification", "Qualification", 25, StageCategory.Open, "#1e88e5"),
            ("Proposal", "Proposal", 50, StageCategory.Open, "#00897b"),
            ("Negotiation", "Negotiation", 75, StageCategory.Open, "#f9a825"),
            ("ClosedWon", "Closed Won", 100, StageCategory.Won, "#2e7d32"),
            ("ClosedLost", "Closed Lost", 0, StageCategory.Lost, "#c62828"),
        };
        Picklist("account.industry", DefaultIndustries);
        Picklist("lead.source", DefaultLeadSources);

        switch (template)
        {
            case HelpDesk:
                Object(ObjectKeys.Account, "Customer", "Customers", "domain", 10);
                Object(ObjectKeys.Contact, "Contact", "Contacts", "contacts", 20);
                Object(ObjectKeys.Lead, "Lead", "Leads", "person_search", 30, active: false);
                Object(ObjectKeys.Opportunity, "Opportunity", "Opportunities", "paid", 40, active: false, pipeline: true);
                Object("ticket", "Ticket", "Tickets", "support_agent", 5, pipeline: true, standard: false, nameLabel: "Subject",
                    description: "Customer support requests.");
                Object(ObjectKeys.Activity, "Activity", "Activities", "event_note", 90);
                Stages(ObjectKeys.Opportunity, salesStages);
                Stages("ticket",
                    ("new", "New", 0, StageCategory.Open, "#1e88e5"),
                    ("in_progress", "In Progress", 0, StageCategory.Open, "#f9a825"),
                    ("waiting", "Waiting on Customer", 0, StageCategory.Open, "#8e24aa"),
                    ("resolved", "Resolved", 0, StageCategory.Won, "#2e7d32"),
                    ("wont_fix", "Won't Fix", 0, StageCategory.Lost, "#757575"));
                Field("ticket", "priority", "Priority", FieldType.Picklist, 1, required: true, list: true, options: ["Low", "Medium", "High", "Urgent"]);
                Field("ticket", "category", "Category", FieldType.Picklist, 2, list: true, options: ["Question", "Incident", "Bug", "Feature request", "Billing"]);
                Field("ticket", "customer", "Customer", FieldType.Lookup, 3, list: true, lookup: ObjectKeys.Account);
                Field("ticket", "requester", "Requester", FieldType.Lookup, 4, lookup: ObjectKeys.Contact);
                Field("ticket", "due_date", "Due date", FieldType.Date, 5, list: true);
                Field("ticket", "details", "Details", FieldType.TextArea, 6);
                Field(ObjectKeys.Account, "support_plan", "Support plan", FieldType.Picklist, 1, list: true, options: ["Basic", "Business", "Enterprise"]);
                Rule("Urgent ticket alert", "ticket", AutomationTrigger.Created,
                    [new("cf.priority", ConditionOperator.Equals, "Urgent")],
                    [new(AutomationActionType.Notify, Message: "Urgent ticket: {{name}}"),
                     new(AutomationActionType.CreateTask, Subject: "Respond to urgent ticket: {{name}}", DueInDays: 0, Priority: ActivityPriority.High)]);
                Rule("Ticket resolved follow-up", "ticket", AutomationTrigger.StageChanged,
                    [new("stage", ConditionOperator.Equals, "resolved")],
                    [new(AutomationActionType.CreateTask, Subject: "Confirm resolution with customer: {{name}}", DueInDays: 2)]);
                break;

            case Recruitment:
                // Applications that predate the candidate object have no candidate, so the link can't be required for them.
                var upgrading = existingObjects.Contains("application");
                Object(ObjectKeys.Account, "Company", "Companies", "domain", 30);
                Object(ObjectKeys.Contact, "Contact", "Contacts", "contacts", 40);
                Object(ObjectKeys.Lead, "Lead", "Leads", "person_search", 50, active: false);
                Object(ObjectKeys.Opportunity, "Opportunity", "Opportunities", "paid", 60, active: false, pipeline: true);
                Object("position", "Job Opening", "Job Openings", "work", 10, standard: false, nameLabel: "Job title",
                    description: "Open roles being recruited for.");
                Object("candidate", "Candidate", "Candidates", "person", 15, standard: false, nameLabel: "Full name",
                    description: "Your talent pool: everyone who has applied or been sourced.");
                Object("application", "Application", "Applications", "assignment_ind", 20, pipeline: true, standard: false,
                    nameLabel: "Candidate name", description: "A candidate applying for a job opening, moving through your hiring stages.");
                Object(ObjectKeys.Activity, "Activity", "Activities", "event_note", 90);
                Stages(ObjectKeys.Opportunity, salesStages);
                Stages("application",
                    ("applied", "Applied", 10, StageCategory.Open, "#1e88e5"),
                    ("screening", "Screening", 25, StageCategory.Open, "#00897b"),
                    ("interview", "Interview", 50, StageCategory.Open, "#f9a825"),
                    ("offer", "Offer", 80, StageCategory.Open, "#8e24aa"),
                    ("hired", "Hired", 100, StageCategory.Won, "#2e7d32"),
                    ("rejected", "Rejected", 0, StageCategory.Lost, "#c62828"));
                Field("position", "department", "Department", FieldType.Text, 1, list: true);
                Field("position", "location", "Location", FieldType.Text, 2, list: true);
                Field("position", "status", "Status", FieldType.Picklist, 3, required: true, list: true, options: ["Open", "On hold", "Filled", "Cancelled"]);
                Field("position", "openings", "Openings", FieldType.Number, 4, min: 1);
                Field("position", "salary_max", "Max salary", FieldType.Currency, 5, min: 0);
                Field("position", "hiring_company", "Company", FieldType.Lookup, 6, lookup: ObjectKeys.Account);
                Field("position", "employment_type", "Employment type", FieldType.Picklist, 7, list: true,
                    options: ["Full-time", "Part-time", "Contract", "Temporary"]);
                Field("position", "closing_date", "Closing date", FieldType.Date, 8);
                Field("position", "description", "Job description", FieldType.TextArea, 9);

                Field("candidate", "email", "Email", FieldType.Email, 1, list: true, unique: true,
                    help: "Used to spot duplicates: two candidates can't share an email.");
                Field("candidate", "phone", "Phone", FieldType.Phone, 2, list: true);
                Field("candidate", "location", "Location", FieldType.Text, 3, list: true);
                Field("candidate", "current_title", "Current title", FieldType.Text, 4, list: true);
                Field("candidate", "current_company", "Current company", FieldType.Text, 5);
                Field("candidate", "skills", "Skills", FieldType.MultiPicklist, 6,
                    options: ["Java", ".NET", "Python", "JavaScript", "Angular", "React", "SQL", "Cloud", "DevOps", "Data & analytics",
                              "UX design", "Project management", "Sales", "Marketing", "Finance", "Customer service"]);
                Field("candidate", "source", "Source", FieldType.Picklist, 7, list: true,
                    options: ["Job board", "Referral", "LinkedIn", "Agency", "Website", "Direct"]);
                Field("candidate", "linkedin", "LinkedIn profile", FieldType.Url, 8);
                Field("candidate", "expected_salary", "Expected salary", FieldType.Currency, 9, min: 0);
                Field("candidate", "notice_period", "Notice period", FieldType.Text, 10, help: "e.g. 4 weeks, immediate");
                Field("candidate", "summary", "Summary", FieldType.TextArea, 11);

                Field("application", "candidate", "Candidate", FieldType.Lookup, 0, required: !upgrading, list: true, lookup: "candidate");
                Field("application", "position", "Job opening", FieldType.Lookup, 1, required: true, list: true, lookup: "position");
                Field("application", "source", "Source", FieldType.Picklist, 4, list: true, options: ["Job board", "Referral", "LinkedIn", "Agency", "Website"]);
                Field("application", "rating", "Rating (1-5)", FieldType.Number, 6, list: true, min: 1, max: 5);
                Field("application", "interview_date", "Next interview", FieldType.DateTime, 7);
                Field("application", "feedback", "Interview feedback", FieldType.TextArea, 8);
                Rule("Schedule interview", "application", AutomationTrigger.StageChanged,
                    [new("stage", ConditionOperator.Equals, "interview")],
                    [new(AutomationActionType.CreateTask, Subject: "Schedule interview with {{name}}", DueInDays: 2, Priority: ActivityPriority.High)]);
                Rule("Candidate hired", "application", AutomationTrigger.StageChanged,
                    [new("stage", ConditionOperator.Equals, "hired")],
                    [new(AutomationActionType.Notify, Message: "{{name}} accepted the offer. Start onboarding."),
                     new(AutomationActionType.CreateTask, Subject: "Prepare onboarding for {{name}}", DueInDays: 5)]);
                Rule("Rejection follow-up", "application", AutomationTrigger.StageChanged,
                    [new("stage", ConditionOperator.Equals, "rejected")],
                    [new(AutomationActionType.CreateTask, Subject: "Let {{name}} know the outcome", DueInDays: 1)]);
                break;

            default:
                Object(ObjectKeys.Account, "Account", "Accounts", "domain", 10);
                Object(ObjectKeys.Contact, "Contact", "Contacts", "contacts", 20);
                Object(ObjectKeys.Lead, "Lead", "Leads", "person_search", 30);
                Object(ObjectKeys.Opportunity, "Opportunity", "Opportunities", "paid", 40, pipeline: true);
                Object(ObjectKeys.Activity, "Activity", "Activities", "event_note", 90);
                Stages(ObjectKeys.Opportunity, salesStages);
                if (template == Sales)
                {
                    Field(ObjectKeys.Account, "tier", "Customer tier", FieldType.Picklist, 1, list: true, options: ["Gold", "Silver", "Bronze"]);
                    Field(ObjectKeys.Opportunity, "competitor", "Main competitor", FieldType.Text, 1);
                    Field(ObjectKeys.Opportunity, "next_step", "Next step", FieldType.Text, 2);
                    Rule("Won deal onboarding", ObjectKeys.Opportunity, AutomationTrigger.StageChanged,
                        [new("stage", ConditionOperator.Equals, "ClosedWon")],
                        [new(AutomationActionType.CreateTask, Subject: "Kick off onboarding for {{name}}", DueInDays: 2),
                         new(AutomationActionType.Notify, Message: "Deal won: {{name}} ({{amount}})")]);
                    Rule("Large deal alert", ObjectKeys.Opportunity, AutomationTrigger.Created,
                        [new("amount", ConditionOperator.GreaterThan, "100000")],
                        [new(AutomationActionType.Notify, Message: "New large deal: {{name}} ({{amount}})")]);
                }
                break;
        }

        await EnsureRolesAsync(db, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Default roles. "*" grants apply to every object, including custom objects added later.</summary>
    public static async Task EnsureRolesAsync(CrmDbContext db, CancellationToken ct)
    {
        var roles = await db.Roles.Include(r => r.Permissions).ToListAsync(ct);

        Role Ensure(string name, string description, bool admin, params (string Resource, string Action, PermissionScope Scope)[] grants)
        {
            var role = roles.FirstOrDefault(r => r.Name == name);
            if (role is null)
            {
                role = new Role { Name = name, Description = description, IsSystem = true, IsAdministrator = admin };
                db.Roles.Add(role);
                roles.Add(role);
            }
            if (!role.IsAdministrator && role.Permissions.Count == 0)
                // Added via the DbSet: client-generated keys on a tracked parent would otherwise be treated as updates.
                db.RolePermissions.AddRange(grants.Select(g => new RolePermission { RoleId = role.Id, Resource = g.Resource, Action = g.Action, Scope = g.Scope }));
            return role;
        }

        Ensure(AdministratorRole, "Full access to all data and settings.", admin: true);
        Ensure(ManagerRole, "Full access to all records; can manage users and groups.", false,
            ("*", PermissionActions.Read, PermissionScope.All),
            ("*", PermissionActions.Create, PermissionScope.All),
            ("*", PermissionActions.Update, PermissionScope.All),
            ("*", PermissionActions.Delete, PermissionScope.All),
            (AdminResources.Users, PermissionActions.Manage, PermissionScope.All),
            (AdminResources.Groups, PermissionActions.Manage, PermissionScope.All),
            (AdminResources.Audit, PermissionActions.Read, PermissionScope.All));
        Ensure(StandardRole, "Sees all records, edits their own, cannot delete.", false,
            ("*", PermissionActions.Read, PermissionScope.All),
            ("*", PermissionActions.Create, PermissionScope.All),
            ("*", PermissionActions.Update, PermissionScope.Own));
    }
}
