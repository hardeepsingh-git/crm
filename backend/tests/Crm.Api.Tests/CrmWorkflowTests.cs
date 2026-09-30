using System.Net;
using Crm.Api.Common;
using Crm.Api.Domain;
using Crm.Api.Features.Accounts;
using Crm.Api.Features.Activities;
using Crm.Api.Features.Contacts;
using Crm.Api.Features.Dashboard;
using Crm.Api.Features.Leads;
using Crm.Api.Features.Opportunities;
using Crm.Api.Features.Security;
using Crm.Api.Features.Users;

namespace Crm.Api.Tests;

public class CrmWorkflowTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    private static object NewAccount(string name) => new { name, industry = "Technology", city = "Sydney", country = "Australia" };

    private static async Task<AccountDetail> CreateAccountAsync(HttpClient client, string name)
    {
        var response = await client.PostJsonAsync("/api/accounts", NewAccount(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.ReadAsync<AccountDetail>();
    }

    [Fact]
    public async Task Account_crud_and_search()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var name = $"Contoso {Guid.NewGuid():N}";
        var created = await CreateAccountAsync(client, name);
        Assert.Equal(name, created.Name);

        var list = await (await client.GetAsync($"/api/accounts?search={created.Name[..12]}")).ReadAsync<PagedResult<AccountListItem>>();
        Assert.Contains(list.Items, a => a.Id == created.Id);

        var update = await client.PutJsonAsync($"/api/accounts/{created.Id}", new { name = name + " Ltd", industry = "Retail" });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal("Retail", (await update.ReadAsync<AccountDetail>()).Industry);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/accounts/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/accounts/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Invalid_payload_returns_validation_problem()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.PostJsonAsync("/api/accounts", new { name = "", email = "not-an-email" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Contact_requires_existing_account()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var response = await client.PostJsonAsync("/api/contacts", new { firstName = "Ann", lastName = "Lee", accountId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var account = await CreateAccountAsync(client, $"Fabrikam {Guid.NewGuid():N}");
        var ok = await client.PostJsonAsync("/api/contacts", new { firstName = "Ann", lastName = "Lee", email = "ann@fabrikam.example", accountId = account.Id });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        var contact = await ok.ReadAsync<ContactDetail>();
        Assert.Equal(account.Name, contact.AccountName);
    }

    [Fact]
    public async Task Account_with_opportunities_cannot_be_deleted()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var account = await CreateAccountAsync(client, $"Tailspin {Guid.NewGuid():N}");
        var opp = await client.PostJsonAsync("/api/opportunities", new
        {
            name = "Deal", accountId = account.Id, amount = 1000m, stage = "Prospecting", expectedCloseDate = "2030-01-31",
        });
        Assert.Equal(HttpStatusCode.Created, opp.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/accounts/{account.Id}")).StatusCode);
    }

    [Fact]
    public async Task Lead_conversion_creates_account_contact_and_opportunity()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var company = $"Northwind {Guid.NewGuid():N}";
        var leadResponse = await client.PostJsonAsync("/api/leads", new
        {
            firstName = "Jane", lastName = "Doe", company, email = "jane@northwind.example",
            source = "Web", status = "Qualified", estimatedValue = 25000m,
        });
        Assert.Equal(HttpStatusCode.Created, leadResponse.StatusCode);
        var lead = await leadResponse.ReadAsync<LeadDetail>();

        var convert = await client.PostJsonAsync($"/api/leads/{lead.Id}/convert", new { createOpportunity = true });
        Assert.Equal(HttpStatusCode.OK, convert.StatusCode);
        var result = await convert.ReadAsync<LeadConvertResult>();
        Assert.NotNull(result.OpportunityId);

        var account = await (await client.GetAsync($"/api/accounts/{result.AccountId}")).ReadAsync<AccountDetail>();
        Assert.Equal(company, account.Name);
        Assert.Equal(1, account.ContactCount);
        Assert.Equal(25000m, account.OpenPipelineAmount);

        var converted = await (await client.GetAsync($"/api/leads/{lead.Id}")).ReadAsync<LeadDetail>();
        Assert.Equal(LeadStatus.Converted, converted.Status);

        var again = await client.PostJsonAsync($"/api/leads/{lead.Id}/convert", new { createOpportunity = false });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Stage_change_to_won_closes_opportunity_and_updates_dashboard()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var account = await CreateAccountAsync(client, $"Adventure Works {Guid.NewGuid():N}");
        var created = await (await client.PostJsonAsync("/api/opportunities", new
        {
            name = "Big deal", accountId = account.Id, amount = 50000m, stage = "Negotiation", expectedCloseDate = "2030-06-30",
        })).ReadAsync<OpportunityDetail>();
        Assert.Equal(75, created.Probability);

        var before = await (await client.GetAsync("/api/dashboard")).ReadAsync<DashboardSummary>();

        var patched = await client.PatchJsonAsync($"/api/opportunities/{created.Id}/stage", new { stage = "ClosedWon" });
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        var won = await patched.ReadAsync<OpportunityDetail>();
        Assert.Equal("ClosedWon", won.Stage);
        Assert.Equal(100, won.Probability);
        Assert.NotNull(won.ClosedAt);

        var after = await (await client.GetAsync("/api/dashboard")).ReadAsync<DashboardSummary>();
        Assert.Equal(before.WonThisMonthAmount + 50000m, after.WonThisMonthAmount);
        Assert.Equal(before.OpenPipelineAmount - 50000m, after.OpenPipelineAmount);

        var pipeline = await (await client.GetAsync("/api/opportunities/pipeline")).ReadAsync<List<OpportunityListItem>>();
        Assert.Contains(pipeline, o => o.Id == created.Id);
    }

    [Fact]
    public async Task Activities_can_be_completed_and_filtered()
    {
        var client = await factory.CreateAuthenticatedClientAsync();
        var account = await CreateAccountAsync(client, $"Litware {Guid.NewGuid():N}");
        var overdue = await (await client.PostJsonAsync("/api/activities", new
        {
            type = "Call", subject = "Overdue call", priority = "High", accountId = account.Id, dueAt = DateTime.UtcNow.AddDays(-2),
        })).ReadAsync<ActivityItem>();

        var overdueList = await (await client.GetAsync($"/api/activities?status=Overdue&accountId={account.Id}")).ReadAsync<PagedResult<ActivityItem>>();
        Assert.Contains(overdueList.Items, a => a.Id == overdue.Id);

        var completed = await (await client.PostAsync($"/api/activities/{overdue.Id}/complete", null)).ReadAsync<ActivityItem>();
        Assert.NotNull(completed.CompletedAt);

        var openList = await (await client.GetAsync($"/api/activities?status=Open&accountId={account.Id}")).ReadAsync<PagedResult<ActivityItem>>();
        Assert.DoesNotContain(openList.Items, a => a.Id == overdue.Id);
    }

    [Fact]
    public async Task Sales_rep_permissions_are_enforced()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var rep = await factory.CreateUserClientAsync("Standard User");
        var adminAccount = await CreateAccountAsync(admin, $"Admin owned {Guid.NewGuid():N}");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await rep.PutJsonAsync($"/api/accounts/{adminAccount.Id}", NewAccount("Hijack"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.DeleteAsync($"/api/accounts/{adminAccount.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.GetAsync("/api/users")).StatusCode);

        var own = await CreateAccountAsync(rep, $"Rep owned {Guid.NewGuid():N}");
        Assert.Equal(HttpStatusCode.OK, (await rep.PutJsonAsync($"/api/accounts/{own.Id}", NewAccount(own.Name + " 2"))).StatusCode);

        var assignToAdmin = await rep.PostJsonAsync("/api/accounts", new { name = "Assign", ownerId = adminAccount.OwnerId });
        Assert.Equal(HttpStatusCode.BadRequest, assignToAdmin.StatusCode);
    }

    [Fact]
    public async Task Admin_can_manage_users()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var managerRole = (await (await admin.GetAsync("/api/admin/roles")).ReadAsync<List<RoleItem>>()).Single(r => r.Name == "Manager").Id;
        var email = $"new-{Guid.NewGuid():N}@test.local";
        var create = await admin.PostJsonAsync("/api/users", new
        {
            email, firstName = "New", lastName = "Person", password = "Str0ng#Password", roleIds = new[] { managerRole },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var user = await create.ReadAsync<UserItem>();
        Assert.Equal("Manager", Assert.Single(user.Roles).Name);

        var weak = await admin.PostJsonAsync("/api/users", new
        {
            email = $"weak-{Guid.NewGuid():N}@test.local", firstName = "W", lastName = "P", password = "weakpassword", roleIds = new[] { managerRole },
        });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);

        var deactivate = await admin.PutJsonAsync($"/api/users/{user.Id}", new { firstName = "New", lastName = "Person", isActive = false, roleIds = new[] { managerRole } });
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);

        var login = await factory.CreateClient().PostJsonAsync("/api/auth/login", new { email, password = "Str0ng#Password" });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);

        var lookup = await (await admin.GetAsync("/api/users/lookup")).ReadAsync<List<UserLookup>>();
        Assert.DoesNotContain(lookup, u => u.Id == user.Id);
    }
}
