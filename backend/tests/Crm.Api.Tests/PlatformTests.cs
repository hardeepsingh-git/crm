using System.Net;
using System.Text.Json.Nodes;
using Crm.Api.Auth;
using Crm.Api.Common;
using Crm.Api.Features.Accounts;
using Crm.Api.Features.Activities;
using Crm.Api.Features.Customization;
using Crm.Api.Features.Dashboard;
using Crm.Api.Features.Governance;
using Crm.Api.Features.Leads;
using Crm.Api.Features.Opportunities;
using Crm.Api.Features.Records;
using Crm.Api.Features.Security;
using Crm.Api.Platform;

namespace Crm.Api.Tests;

public class MultiTenancyTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    private async Task<(HttpClient Client, TenantItem Tenant)> CreateTenantAsync(string template, bool demo = false)
    {
        var platform = await factory.CreateAuthenticatedClientAsync();
        var slug = $"t-{Guid.NewGuid():N}"[..20];
        var email = $"admin@{slug}.test";
        var response = await platform.PostJsonAsync("/api/platform/tenants", new
        {
            name = $"Tenant {slug}", slug, template, currency = "NZD",
            adminEmail = email, adminFirstName = "Tenant", adminLastName = "Admin", adminPassword = "Tenant#Pass123", seedDemoData = demo,
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await factory.CreateAuthenticatedClientAsync(email, "Tenant#Pass123"), await response.ReadAsync<TenantItem>());
    }

    [Fact]
    public async Task Tenants_cannot_see_each_others_data()
    {
        var defaultAdmin = await factory.CreateAuthenticatedClientAsync();
        var secret = await (await defaultAdmin.PostJsonAsync("/api/accounts", new { name = $"Default secret {Guid.NewGuid():N}" })).ReadAsync<AccountDetail>();

        var (other, _) = await CreateTenantAsync(TenantTemplates.Sales);
        var list = await (await other.GetAsync("/api/accounts?pageSize=100")).ReadAsync<PagedResult<AccountListItem>>();
        Assert.DoesNotContain(list.Items, a => a.Id == secret.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/accounts/{secret.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutJsonAsync($"/api/accounts/{secret.Id}", new { name = "Hijacked" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/accounts/{secret.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/audit/account/{secret.Id}")).StatusCode);

        // Cross-tenant references are rejected too.
        var contact = await other.PostJsonAsync("/api/contacts", new { firstName = "X", lastName = "Y", accountId = secret.Id });
        Assert.Equal(HttpStatusCode.BadRequest, contact.StatusCode);

        var users = await (await other.GetAsync("/api/users")).ReadAsync<List<Crm.Api.Features.Users.UserItem>>();
        Assert.Single(users);
    }

    [Fact]
    public async Task Templates_provision_their_own_configuration_and_demo_data()
    {
        var (helpdesk, tenant) = await CreateTenantAsync(TenantTemplates.HelpDesk, demo: true);
        Assert.Equal("NZD", tenant.Currency);

        var metadata = await (await helpdesk.GetAsync("/api/metadata")).ReadAsync<MetadataDto>();
        var ticket = Assert.Single(metadata.Objects, o => o.Key == "ticket");
        Assert.True(ticket.HasPipeline);
        Assert.Contains(ticket.Fields, f => f.Key == "priority" && f.IsRequired);
        Assert.Equal("Customers", metadata.Objects.Single(o => o.Key == "account").PluralLabel);
        Assert.False(metadata.Objects.Single(o => o.Key == "opportunity").IsActive);

        var tickets = await (await helpdesk.GetAsync("/api/records/ticket")).ReadAsync<PagedResult<RecordItem>>();
        Assert.Equal(8, tickets.TotalCount);

        var dashboard = await (await helpdesk.GetAsync("/api/dashboard")).ReadAsync<DashboardSummary>();
        Assert.Contains(dashboard.CustomPipelines, p => p.ObjectKey == "ticket");

        // The default (sales) tenant has no ticket object.
        var defaultAdmin = await factory.CreateAuthenticatedClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await defaultAdmin.GetAsync("/api/records/ticket")).StatusCode);
    }

    [Fact]
    public async Task Suspended_tenants_cannot_sign_in_and_only_platform_admins_manage_tenants()
    {
        var (tenantAdmin, tenant) = await CreateTenantAsync(TenantTemplates.Blank);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync("/api/platform/tenants")).StatusCode);

        var platform = await factory.CreateAuthenticatedClientAsync();
        var suspend = await platform.PutJsonAsync($"/api/platform/tenants/{tenant.Id}", new { name = tenant.Name, currency = "NZD", status = "Suspended" });
        Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);

        var login = await factory.CreateClient().PostJsonAsync("/api/auth/login",
            new { email = $"admin@{tenant.Slug}.test", password = "Tenant#Pass123" });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tenantAdmin.GetAsync("/api/accounts")).StatusCode);

        var self = await (await platform.GetAsync("/api/auth/me")).ReadAsync<UserInfo>();
        var suspendSelf = await platform.PutJsonAsync($"/api/platform/tenants/{self.Tenant.Id}", new { name = "x", currency = "AUD", status = "Suspended" });
        Assert.Equal(HttpStatusCode.BadRequest, suspendSelf.StatusCode);
    }

    [Fact]
    public async Task Suspended_tenants_can_be_deleted_with_all_their_data()
    {
        var (tenantAdmin, tenant) = await CreateTenantAsync(TenantTemplates.Recruitment, demo: true);
        var positions = await (await tenantAdmin.GetAsync("/api/records/position")).ReadAsync<PagedResult<RecordItem>>();
        Assert.True(positions.TotalCount > 0);

        var platform = await factory.CreateAuthenticatedClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await platform.DeleteAsync($"/api/platform/tenants/{tenant.Id}")).StatusCode);

        await platform.PutJsonAsync($"/api/platform/tenants/{tenant.Id}", new { name = tenant.Name, currency = "NZD", status = "Suspended" });
        Assert.Equal(HttpStatusCode.NoContent, (await platform.DeleteAsync($"/api/platform/tenants/{tenant.Id}")).StatusCode);

        var tenants = await (await platform.GetAsync("/api/platform/tenants")).ReadAsync<List<TenantItem>>();
        Assert.DoesNotContain(tenants, t => t.Id == tenant.Id);
        var login = await factory.CreateClient().PostJsonAsync("/api/auth/login", new { email = $"admin@{tenant.Slug}.test", password = "Tenant#Pass123" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }
}

public class AuthorizationTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    [Fact]
    public async Task Group_scope_shares_records_within_a_team_only()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var role = await (await admin.PostJsonAsync("/api/admin/roles", new
        {
            name = $"Team seller {Guid.NewGuid():N}"[..30],
            permissions = new object[]
            {
                new { resource = "account", action = "read", scope = "Group" },
                new { resource = "account", action = "create", scope = "All" },
                new { resource = "account", action = "update", scope = "Group" },
            },
        })).ReadAsync<RoleItem>();

        var group = await (await admin.PostJsonAsync("/api/admin/groups", new { name = $"Team {Guid.NewGuid():N}"[..20], memberIds = Array.Empty<Guid>(), roleIds = Array.Empty<Guid>() }))
            .ReadAsync<GroupItem>();
        var (alice, _) = await factory.CreateUserAsync([role.Name], [group.Id], admin);
        var (bob, _) = await factory.CreateUserAsync([role.Name], [group.Id], admin);
        var (carol, _) = await factory.CreateUserAsync([role.Name], [], admin);

        var account = await (await alice.PostJsonAsync("/api/accounts", new { name = $"Alice deal {Guid.NewGuid():N}" })).ReadAsync<AccountDetail>();

        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/accounts/{account.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await bob.PutJsonAsync($"/api/accounts/{account.Id}", new { name = account.Name + " (Bob)" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await carol.GetAsync($"/api/accounts/{account.Id}")).StatusCode);
        var carolList = await (await carol.GetAsync("/api/accounts?pageSize=100")).ReadAsync<PagedResult<AccountListItem>>();
        Assert.DoesNotContain(carolList.Items, a => a.Id == account.Id);

        // No grant for contacts at all, and no delete on accounts.
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.GetAsync("/api/contacts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.DeleteAsync($"/api/accounts/{account.Id}")).StatusCode);

        var me = await (await alice.GetAsync("/api/auth/me")).ReadAsync<UserInfo>();
        Assert.Equal(Crm.Api.Domain.PermissionScope.Group, me.Permissions["account"]["read"]);

        // Editing the role takes effect immediately (permission cache is invalidated).
        var updated = await admin.PutJsonAsync($"/api/admin/roles/{role.Id}", new
        {
            name = role.Name,
            permissions = role.Permissions.Append(new Crm.Api.Security.GrantDto("contact", "read", Crm.Api.Domain.PermissionScope.All)),
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(4, (await updated.ReadAsync<RoleItem>()).Permissions.Count);
        Assert.Equal(HttpStatusCode.OK, (await alice.GetAsync("/api/contacts")).StatusCode);
    }

    [Fact]
    public async Task Object_grants_override_the_wildcard_and_roles_combine()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var restricted = await (await admin.PostJsonAsync("/api/admin/roles", new
        {
            name = $"Restricted {suffix}",
            permissions = new object[]
            {
                new { resource = "*", action = "read", scope = "All" },
                new { resource = "*", action = "create", scope = "All" },
                new { resource = "account", action = "read", scope = "None" },
                new { resource = "lead", action = "read", scope = "Own" },
            },
        })).ReadAsync<RoleItem>();
        Assert.Contains(restricted.Permissions, g => g is { Resource: "account", Action: "read", Scope: Crm.Api.Domain.PermissionScope.None });

        var (user, _) = await factory.CreateUserAsync([restricted.Name], [], admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/contacts")).StatusCode);
        var othersLead = await (await admin.PostJsonAsync("/api/leads", new { firstName = "Other", lastName = "Lead", company = "Elsewhere", source = "Web", status = "New" }))
            .ReadAsync<LeadDetail>();
        Assert.Equal(HttpStatusCode.NotFound, (await user.GetAsync($"/api/leads/{othersLead.Id}")).StatusCode);

        var me = await (await user.GetAsync("/api/auth/me")).ReadAsync<UserInfo>();
        Assert.Equal(Crm.Api.Domain.PermissionScope.None, me.Permissions["account"]["read"]);
        Assert.Equal(Crm.Api.Domain.PermissionScope.Own, me.Permissions["lead"]["read"]);
        Assert.Equal(Crm.Api.Domain.PermissionScope.All, me.Permissions["*"]["read"]);

        // A second role that grants accounts widens access (roles are a union).
        var viewer = await (await admin.PostJsonAsync("/api/admin/roles", new
        {
            name = $"Account viewer {suffix}",
            permissions = new[] { new { resource = "account", action = "read", scope = "All" } },
        })).ReadAsync<RoleItem>();
        var (both, _) = await factory.CreateUserAsync([restricted.Name, viewer.Name], [], admin);
        Assert.Equal(HttpStatusCode.OK, (await both.GetAsync("/api/accounts")).StatusCode);
    }

    [Fact]
    public async Task Escalation_check_uses_resolved_access_not_raw_grants()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var limited = await (await admin.PostJsonAsync("/api/admin/roles", new
        {
            name = $"Limited admin {suffix}",
            permissions = new object[]
            {
                new { resource = "*", action = "read", scope = "All" },
                new { resource = "account", action = "read", scope = "None" },
                new { resource = "roles", action = "manage", scope = "All" },
            },
        })).ReadAsync<RoleItem>();
        var (roleAdmin, _) = await factory.CreateUserAsync([limited.Name], [], admin);

        // "*" read without the account override would give accounts, which the caller cannot see.
        var tooWide = await roleAdmin.PostJsonAsync("/api/admin/roles", new
        {
            name = $"Too wide {suffix}", permissions = new[] { new { resource = "*", action = "read", scope = "All" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, tooWide.StatusCode);

        var withinBounds = await roleAdmin.PostJsonAsync("/api/admin/roles", new
        {
            name = $"Within bounds {suffix}",
            permissions = new object[]
            {
                new { resource = "*", action = "read", scope = "All" },
                new { resource = "account", action = "read", scope = "None" },
            },
        });
        Assert.Equal(HttpStatusCode.Created, withinBounds.StatusCode);
    }

    [Fact]
    public async Task Non_administrators_cannot_escalate_privileges()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var roles = await (await admin.GetAsync("/api/admin/roles")).ReadAsync<List<RoleItem>>();
        var administrator = roles.Single(r => r.IsAdministrator);
        var manager = await factory.CreateUserClientAsync("Manager");

        var grantAdmin = await manager.PostJsonAsync("/api/users", new
        {
            email = $"x-{Guid.NewGuid():N}@test.local", firstName = "X", lastName = "Y", password = "Str0ng#Password", roleIds = new[] { administrator.Id },
        });
        Assert.Equal(HttpStatusCode.BadRequest, grantAdmin.StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostJsonAsync("/api/admin/roles",
            new { name = "Sneaky", permissions = new[] { new { resource = "settings", action = "manage", scope = "All" } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostJsonAsync("/api/admin/objects",
            new { key = "sneaky", label = "S", pluralLabel = "S", icon = "x", nameLabel = "Name", hasPipeline = false })).StatusCode);

        var editAdmin = await admin.PutJsonAsync($"/api/admin/roles/{administrator.Id}", new { name = "Administrator", permissions = Array.Empty<object>() });
        Assert.Equal(HttpStatusCode.BadRequest, editAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/admin/roles/{administrator.Id}")).StatusCode);
    }
}

public class CustomizationTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    [Fact]
    public async Task Picklist_changes_keep_existing_values_editable()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var metadata = JsonNode.Parse(await admin.GetStringAsync("/api/metadata"))!;
        var original = metadata["picklists"]!["account.industry"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        try
        {
            await admin.PutJsonAsync("/api/admin/picklists/account.industry", new { options = new[] { "Alpha", "Beta" } });
            var account = await (await admin.PostJsonAsync("/api/accounts", new { name = $"Legacy {Guid.NewGuid():N}", industry = "Beta" }))
                .ReadAsync<AccountDetail>();
            await admin.PutJsonAsync("/api/admin/picklists/account.industry", new { options = new[] { "Alpha" } });

            var unchanged = await admin.PutJsonAsync($"/api/accounts/{account.Id}", new { name = account.Name + " renamed", industry = "Beta" });
            Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
            var changed = await admin.PutJsonAsync($"/api/accounts/{account.Id}", new { name = account.Name, industry = "Gamma" });
            Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
            var newRecord = await admin.PostJsonAsync("/api/accounts", new { name = $"New {Guid.NewGuid():N}", industry = "Beta" });
            Assert.Equal(HttpStatusCode.BadRequest, newRecord.StatusCode);
        }
        finally
        {
            await admin.PutJsonAsync("/api/admin/picklists/account.industry", new { options = original });
        }
    }

    [Fact]
    public async Task Custom_objects_with_fields_pipeline_and_filters()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var key = $"proj_{Guid.NewGuid():N}"[..12];
        var created = await admin.PostJsonAsync("/api/admin/objects", new
        {
            key, label = "Project", pluralLabel = "Projects", icon = "rocket_launch", nameLabel = "Project name", hasPipeline = true,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(4, (await created.ReadAsync<ObjectDto>()).Stages.Count);

        await admin.PostJsonAsync($"/api/admin/objects/{key}/fields", new
        {
            key = "priority", label = "Priority", type = "Picklist", isRequired = true, options = new[] { "Low", "High" }, showInList = true, sortOrder = 1,
        });
        await admin.PostJsonAsync($"/api/admin/objects/{key}/fields", new
        {
            key = "budget", label = "Budget", type = "Currency", isRequired = false, min = 0, showInList = false, sortOrder = 2,
        });
        await admin.PostJsonAsync($"/api/admin/objects/{key}/fields", new
        {
            key = "client", label = "Client", type = "Lookup", isRequired = false, lookupObjectKey = "account", showInList = false, sortOrder = 3,
        });

        var missing = await admin.PostJsonAsync($"/api/records/{key}", new { name = "No priority" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var badOption = await admin.PostJsonAsync($"/api/records/{key}", new { name = "Bad", customFields = new { priority = "Urgent" } });
        Assert.Equal(HttpStatusCode.BadRequest, badOption.StatusCode);
        var negative = await admin.PostJsonAsync($"/api/records/{key}", new { name = "Neg", customFields = new { priority = "Low", budget = -5 } });
        Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
        var badLookup = await admin.PostJsonAsync($"/api/records/{key}", new { name = "Lk", customFields = new { priority = "Low", client = Guid.NewGuid() } });
        Assert.Equal(HttpStatusCode.BadRequest, badLookup.StatusCode);

        var account = await (await admin.PostJsonAsync("/api/accounts", new { name = $"Client {Guid.NewGuid():N}" })).ReadAsync<AccountDetail>();
        var high = await (await admin.PostJsonAsync($"/api/records/{key}", new
        {
            name = "Website relaunch", customFields = new { priority = "High", budget = 12500.50, client = account.Id },
        })).ReadAsync<RecordItem>();
        Assert.Equal("new", high.Stage);
        Assert.Equal("High", high.CustomFields["priority"]!.GetValue<string>());
        await admin.PostJsonAsync($"/api/records/{key}", new { name = "Internal wiki", customFields = new { priority = "Low" } });

        var filtered = await (await admin.GetAsync($"/api/records/{key}?cf.priority=High")).ReadAsync<PagedResult<RecordItem>>();
        Assert.Equal(high.Id, Assert.Single(filtered.Items).Id);

        var done = await (await admin.PatchJsonAsync($"/api/records/{key}/{high.Id}/stage", new { stage = "done" })).ReadAsync<RecordItem>();
        Assert.NotNull(done.ClosedAt);
        var board = await (await admin.GetAsync($"/api/records/{key}/board")).ReadAsync<List<RecordItem>>();
        Assert.Equal(2, board.Count);

        var activity = await admin.PostJsonAsync("/api/activities", new { type = "Task", subject = "Kick-off", priority = "Normal", customRecordId = high.Id });
        Assert.Equal(HttpStatusCode.Created, activity.StatusCode);
        var linked = await (await admin.GetAsync($"/api/activities?customRecordId={high.Id}")).ReadAsync<PagedResult<ActivityItem>>();
        Assert.Equal(key, Assert.Single(linked.Items).CustomObjectKey);

        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/objects/{key}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutJsonAsync($"/api/admin/objects/{key}/stages", new
        {
            stages = new[] { new { key = "new", label = "New", probability = 0, category = "Open", color = "#1e88e5" } },
        })).StatusCode);
    }

    [Fact]
    public async Task Standard_objects_accept_custom_fields_and_configurable_stages()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var gold = await admin.PostJsonAsync("/api/accounts", new { name = $"Gold {Guid.NewGuid():N}", customFields = new { tier = "Gold" } });
        Assert.Equal(HttpStatusCode.Created, gold.StatusCode);
        var goldAccount = await gold.ReadAsync<AccountDetail>();
        Assert.Equal("Gold", goldAccount.CustomFields["tier"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/accounts", new { name = "P", customFields = new { tier = "Platinum" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/accounts", new { name = "U", customFields = new { unknown = "x" } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/accounts", new { name = "I", industry = "Wizardry" })).StatusCode);

        var filtered = await (await admin.GetAsync("/api/accounts?cf.tier=Gold&pageSize=100")).ReadAsync<PagedResult<AccountListItem>>();
        Assert.Contains(filtered.Items, a => a.Id == goldAccount.Id);
        Assert.All(filtered.Items, a => Assert.Equal("Gold", a.CustomFields["tier"]!.GetValue<string>()));

        // Add a stage to the opportunity pipeline and use it.
        var metadata = await (await admin.GetAsync("/api/metadata")).ReadAsync<MetadataDto>();
        var stages = metadata.Objects.Single(o => o.Key == "opportunity").Stages.ToList();
        stages.Insert(3, new StageDto("Legal", "Legal review", 85, Crm.Api.Domain.StageCategory.Open, "#6d4c41"));
        Assert.Equal(HttpStatusCode.OK, (await admin.PutJsonAsync("/api/admin/objects/opportunity/stages", new { stages })).StatusCode);

        var opp = await (await admin.PostJsonAsync("/api/opportunities", new
        {
            name = "Legal deal", accountId = goldAccount.Id, amount = 1000, stage = "Legal", expectedCloseDate = "2031-01-01",
        })).ReadAsync<OpportunityDetail>();
        Assert.Equal(85, opp.Probability);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJsonAsync("/api/opportunities", new
        {
            name = "Bad stage", accountId = goldAccount.Id, amount = 1, stage = "Nope", expectedCloseDate = "2031-01-01",
        })).StatusCode);
    }

    [Fact]
    public async Task Automation_rules_create_tasks_notifications_and_set_fields()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var account = await (await admin.PostJsonAsync("/api/accounts", new { name = $"Auto {Guid.NewGuid():N}" })).ReadAsync<AccountDetail>();

        // Template rule: "Large deal alert" notifies the owner of deals over 100k.
        var before = await (await admin.GetAsync("/api/notifications")).ReadAsync<NotificationList>();
        var opp = await (await admin.PostJsonAsync("/api/opportunities", new
        {
            name = "Huge deal", accountId = account.Id, amount = 250000, stage = "Prospecting", expectedCloseDate = "2031-01-01",
        })).ReadAsync<OpportunityDetail>();
        var after = await (await admin.GetAsync("/api/notifications")).ReadAsync<NotificationList>();
        Assert.Equal(before.UnreadCount + 1, after.UnreadCount);
        Assert.Contains(after.Items, n => n.Message.Contains("Huge deal") && n.Link == $"/opportunities/{opp.Id}");

        // Template rule: "Won deal onboarding" creates a follow-up task.
        await admin.PatchJsonAsync($"/api/opportunities/{opp.Id}/stage", new { stage = "ClosedWon" });
        var tasks = await (await admin.GetAsync($"/api/activities?opportunityId={opp.Id}")).ReadAsync<PagedResult<ActivityItem>>();
        Assert.Contains(tasks.Items, t => t.Subject == "Kick off onboarding for Huge deal");

        // Custom rule: when an account's tier changes, stamp its description.
        var rule = await admin.PostJsonAsync("/api/admin/automation", new
        {
            name = "Tier change note", objectKey = "account", trigger = "Updated",
            conditions = new[] { new { field = "cf.tier", @operator = "Changed", value = (string?)null } },
            actions = new[] { new { type = "SetField", field = "description", value = "Tier set to {{cf.tier}}" } },
        });
        Assert.Equal(HttpStatusCode.Created, rule.StatusCode);
        await admin.PutJsonAsync($"/api/accounts/{account.Id}", new { name = account.Name, customFields = new { tier = "Silver" } });
        var updated = await (await admin.GetAsync($"/api/accounts/{account.Id}")).ReadAsync<AccountDetail>();
        Assert.Equal("Tier set to Silver", updated.Description);

        var invalid = await admin.PostJsonAsync("/api/admin/automation", new
        {
            name = "Bad", objectKey = "account", trigger = "Updated",
            conditions = Array.Empty<object>(), actions = new[] { new { type = "SetField", field = "id", value = "x" } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        await admin.PostAsync("/api/notifications/read-all", null);
        Assert.Equal(0, (await (await admin.GetAsync("/api/notifications")).ReadAsync<NotificationList>()).UnreadCount);
    }

    [Fact]
    public async Task Changes_are_audited_with_field_level_diffs()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var account = await (await admin.PostJsonAsync("/api/accounts", new { name = $"Audit {Guid.NewGuid():N}", industry = "Retail" })).ReadAsync<AccountDetail>();
        await admin.PutJsonAsync($"/api/accounts/{account.Id}", new { name = account.Name, industry = "Software", customFields = new { tier = "Bronze" } });

        // Other tests may add automation rules that write further entries; assert on the user's own changes.
        var history = await (await admin.GetAsync($"/api/audit/account/{account.Id}")).ReadAsync<List<AuditItem>>();
        Assert.Single(history, h => h.Action == Crm.Api.Domain.AuditAction.Created);
        var update = history.Single(h => h.Action == Crm.Api.Domain.AuditAction.Updated && h.Changes.ContainsKey("industry"));
        Assert.Equal("Retail", update.Changes["industry"]![0]!.GetValue<string>());
        Assert.Equal("Software", update.Changes["industry"]![1]!.GetValue<string>());
        Assert.Equal("Bronze", update.Changes["cf.tier"]![1]!.GetValue<string>());
        Assert.Equal("System Administrator", update.UserName);

        var search = await (await admin.GetAsync($"/api/admin/audit?entityId={account.Id}")).ReadAsync<PagedResult<AuditItem>>();
        Assert.Equal(history.Count, search.TotalCount);

        var rep = await factory.CreateUserClientAsync("Standard User");
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.GetAsync("/api/admin/audit")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rep.GetAsync($"/api/audit/account/{account.Id}")).StatusCode);
    }
}
