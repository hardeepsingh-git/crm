using System.Net;
using System.Text.Json.Nodes;
using Crm.Api.Auth;
using Crm.Api.Domain;
using Crm.Api.Features.Governance;
using Crm.Api.Features.Security;
using Crm.Api.Features.Users;
using Crm.Api.Platform;

namespace Crm.Api.Tests;

public class RegistrationTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    private const string Password = "Reg#Pass12345";

    private static object Settings(bool selfRegistration = true, bool email = false, bool sms = false, int maxAttempts = 5, int cooldown = 0, int codeLength = 6) => new
    {
        selfRegistrationEnabled = selfRegistration, emailVerificationEnabled = email, smsVerificationEnabled = sms,
        codeLength, codeTtlMinutes = 10, maxAttempts, resendCooldownSeconds = cooldown,
        emailProvider = "Log", smtpPort = 587, smtpUseStartTls = true, emailFromName = "CRM",
        smsProvider = "Log", smsAuthHeader = "Authorization", smsAuthScheme = "Bearer",
    };

    private async Task<HttpClient> ConfigureAsync(object settings, bool tenantOptIn = true)
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        (await admin.PutJsonAsync("/api/platform/settings", settings)).EnsureSuccessStatusCode();
        (await admin.PutJsonAsync("/api/settings", new { name = "Default", currency = "AUD", selfRegistrationEnabled = tenantOptIn }))
            .EnsureSuccessStatusCode();
        return admin;
    }

    private static string NewEmail() => $"new-{Guid.NewGuid():N}@test.local";

    private async Task<HttpResponseMessage> RegisterAsync(string email, string? phone = null, string tenantCode = "default") =>
        await factory.CreateClient().PostJsonAsync("/api/auth/register",
            new { tenantCode, email, firstName = "Nina", lastName = "Newcomer", phoneNumber = phone, password = Password });

    private async Task<HttpResponseMessage> LoginAsync(string email) =>
        await factory.CreateClient().PostJsonAsync("/api/auth/login", new { email, password = Password });

    private static async Task<string?> RegistrationStatusOf(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())?["registrationStatus"]?.GetValue<string>();

    private async Task<Guid> RoleIdAsync(HttpClient admin, string name) =>
        (await (await admin.GetAsync("/api/admin/roles")).ReadAsync<List<RoleItem>>()).Single(r => r.Name == name).Id;

    [Fact]
    public async Task Registration_is_available_only_when_the_platform_and_the_tenant_both_allow_it()
    {
        var anonymous = factory.CreateClient();
        var admin = await ConfigureAsync(Settings(selfRegistration: false), tenantOptIn: false);
        Assert.False((await (await anonymous.GetAsync("/api/auth/registration-options?tenant=default")).ReadAsync<RegistrationOptions>()).Available);
        Assert.False((await (await anonymous.GetAsync("/api/auth/registration-options")).ReadAsync<RegistrationOptions>()).Available);
        Assert.Equal(HttpStatusCode.NotFound, (await RegisterAsync(NewEmail())).StatusCode);

        await ConfigureAsync(Settings(), tenantOptIn: false);
        Assert.False((await (await anonymous.GetAsync("/api/auth/registration-options?tenant=default")).ReadAsync<RegistrationOptions>()).Available);

        await ConfigureAsync(Settings(email: true));
        var options = await (await anonymous.GetAsync("/api/auth/registration-options?tenant=DEFAULT")).ReadAsync<RegistrationOptions>();
        Assert.True(options.Available);
        Assert.Equal("Default", options.TenantName);
        Assert.True(options.EmailVerification);
        Assert.False((await (await anonymous.GetAsync("/api/auth/registration-options?tenant=nope")).ReadAsync<RegistrationOptions>()).Available);
        var platformOnly = await (await anonymous.GetAsync("/api/auth/registration-options")).ReadAsync<RegistrationOptions>();
        Assert.True(platformOnly.Available);
        Assert.Null(platformOnly.TenantName);

        // Saving organisation settings without the flag (older clients) keeps the tenant's choice.
        (await admin.PutJsonAsync("/api/settings", new { name = "Default", currency = "AUD" })).EnsureSuccessStatusCode();
        var org = await (await admin.GetAsync("/api/settings")).ReadAsync<OrganisationSettings>();
        Assert.True(org.SelfRegistrationEnabled);
        Assert.True(org.SelfRegistrationAllowedByPlatform);
    }

    [Fact]
    public async Task Registered_users_cannot_sign_in_until_an_administrator_approves_them()
    {
        var admin = await ConfigureAsync(Settings());
        var email = NewEmail();
        var registered = await RegisterAsync(email);
        Assert.Equal(HttpStatusCode.Accepted, registered.StatusCode);
        Assert.Empty((await registered.ReadAsync<RegisterResponse>()).Verification);

        var blocked = await LoginAsync(email);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("PendingApproval", await RegistrationStatusOf(blocked));

        var notifications = await (await admin.GetAsync("/api/notifications")).ReadAsync<NotificationList>();
        Assert.Contains(notifications.Items, n => n.Message.Contains(email) && n.Link == RegistrationService.ApprovalLink);

        var pending = await (await admin.GetAsync("/api/users?status=PendingApproval")).ReadAsync<List<UserItem>>();
        var user = Assert.Single(pending, u => u.Email == email);
        Assert.False(user.IsActive);
        Assert.Empty(user.Roles);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutJsonAsync($"/api/users/{user.Id}",
            new { firstName = "Nina", lastName = "Newcomer", isActive = true, roleIds = Array.Empty<Guid>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJsonAsync($"/api/users/{user.Id}/approve",
            new { roleIds = Array.Empty<Guid>(), groupIds = Array.Empty<Guid>() })).StatusCode);

        var manager = await factory.CreateUserClientAsync("Manager");
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostJsonAsync($"/api/users/{user.Id}/approve",
            new { roleIds = new[] { await RoleIdAsync(admin, "Administrator") } })).StatusCode);

        var approved = await (await manager.PostJsonAsync($"/api/users/{user.Id}/approve",
            new { roleIds = new[] { await RoleIdAsync(admin, "Standard User") } })).ReadAsync<UserItem>();
        Assert.Equal(RegistrationStatus.Approved, approved.RegistrationStatus);
        Assert.True(approved.IsActive);
        Assert.Contains(factory.Messages.To(email), m => m.Subject.Contains("ready"));

        var session = await CrmApiFactory.LoginAsync(factory.CreateClient(), email, Password);
        Assert.Equal(new[] { "Standard User" }, session.User.Roles);
    }

    [Fact]
    public async Task Email_and_mobile_must_be_verified_before_approval()
    {
        await ConfigureAsync(Settings(email: true, sms: true));
        Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync(NewEmail())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync(NewEmail(), "12345")).StatusCode);

        var email = NewEmail();
        var registered = await RegisterAsync(email, "+61 412 345 678");
        Assert.Equal(new[] { VerificationChannel.Email, VerificationChannel.Sms }, (await registered.ReadAsync<RegisterResponse>()).Verification);

        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest,
            (await anonymous.PostJsonAsync("/api/auth/verify", new { email, channel = "Email", code = "000000" })).StatusCode);

        var blocked = await LoginAsync(email);
        Assert.Equal("PendingVerification", await RegistrationStatusOf(blocked));

        var afterEmail = await (await anonymous.PostJsonAsync("/api/auth/verify",
            new { email, channel = "Email", code = factory.Messages.LatestCode(email, "Email") })).ReadAsync<VerifyResponse>();
        Assert.Equal(RegistrationStatus.PendingVerification, afterEmail.Status);
        Assert.Equal(new[] { VerificationChannel.Sms }, afterEmail.Remaining);

        var afterSms = await (await anonymous.PostJsonAsync("/api/auth/verify",
            new { email, channel = "Sms", code = factory.Messages.LatestCode("+61412345678", "Sms") })).ReadAsync<VerifyResponse>();
        Assert.Equal(RegistrationStatus.PendingApproval, afterSms.Status);
        Assert.Empty(afterSms.Remaining);

        var admin = await factory.CreateAuthenticatedClientAsync();
        var user = (await (await admin.GetAsync("/api/users?status=PendingApproval")).ReadAsync<List<UserItem>>()).Single(u => u.Email == email);
        Assert.True(user.EmailConfirmed);
        Assert.True(user.PhoneNumberConfirmed);
        Assert.Equal("+61412345678", user.PhoneNumber);
    }

    [Fact]
    public async Task Codes_lock_after_too_many_attempts_and_resends_respect_the_cooldown()
    {
        await ConfigureAsync(Settings(email: true, maxAttempts: 2));
        var email = NewEmail();
        await RegisterAsync(email);
        var anonymous = factory.CreateClient();
        var first = factory.Messages.LatestCode(email, "Email");

        for (var i = 0; i < 2; i++)
            await anonymous.PostJsonAsync("/api/auth/verify", new { email, channel = "Email", code = first == "111111" ? "222222" : "111111" });
        Assert.Equal(HttpStatusCode.BadRequest,
            (await anonymous.PostJsonAsync("/api/auth/verify", new { email, channel = "Email", code = first })).StatusCode);

        Assert.Equal(HttpStatusCode.Accepted, (await anonymous.PostJsonAsync("/api/auth/resend-code", new { email, channel = "Email" })).StatusCode);
        Assert.Equal(2, factory.Messages.To(email).Count);
        var verified = await (await anonymous.PostJsonAsync("/api/auth/verify",
            new { email, channel = "Email", code = factory.Messages.LatestCode(email, "Email") })).ReadAsync<VerifyResponse>();
        Assert.Equal(RegistrationStatus.PendingApproval, verified.Status);

        await ConfigureAsync(Settings(email: true, cooldown: 600, codeLength: 10));
        var other = NewEmail();
        await RegisterAsync(other);
        await anonymous.PostJsonAsync("/api/auth/resend-code", new { email = other, channel = "Email" });
        Assert.Single(factory.Messages.To(other));
        Assert.Equal(10, factory.Messages.LatestCode(other, "Email").Length);
    }

    [Fact]
    public async Task Registration_and_verification_do_not_reveal_existing_accounts()
    {
        await ConfigureAsync(Settings(email: true));
        var fresh = await RegisterAsync(NewEmail());
        var existing = await RegisterAsync(CrmApiFactory.AdminEmail);
        Assert.Equal(fresh.StatusCode, existing.StatusCode);
        Assert.Equal(await fresh.Content.ReadAsStringAsync(), await existing.Content.ReadAsStringAsync());
        Assert.Empty(factory.Messages.To(CrmApiFactory.AdminEmail));

        var unknown = await factory.CreateClient().PostJsonAsync("/api/auth/verify", new { email = NewEmail(), channel = "Email", code = "123456" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        await CrmApiFactory.LoginAsync(factory.CreateClient(), CrmApiFactory.AdminEmail, CrmApiFactory.AdminPassword);
    }

    [Fact]
    public async Task Rejected_registrations_cannot_sign_in_and_settings_are_platform_admin_only()
    {
        var admin = await ConfigureAsync(Settings());
        var email = NewEmail();
        await RegisterAsync(email);
        var user = (await (await admin.GetAsync("/api/users?status=PendingApproval")).ReadAsync<List<UserItem>>()).Single(u => u.Email == email);

        var rejected = await (await admin.PostJsonAsync($"/api/users/{user.Id}/reject", new { })).ReadAsync<UserItem>();
        Assert.Equal(RegistrationStatus.Rejected, rejected.RegistrationStatus);
        Assert.Equal("Rejected", await RegistrationStatusOf(await LoginAsync(email)));

        var me = await (await admin.GetAsync("/api/auth/me")).ReadAsync<UserInfo>();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostJsonAsync($"/api/users/{me.Id}/reject", new { })).StatusCode);

        var manager = await factory.CreateUserClientAsync("Manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/platform/settings")).StatusCode);

        var view = await (await admin.GetAsync("/api/platform/settings")).ReadAsync<PlatformSettingsView>();
        Assert.False(view.SmtpPasswordConfigured);
        Assert.DoesNotContain("password", (await (await admin.GetAsync("/api/platform/settings")).Content.ReadAsStringAsync())
            .Replace("smtpPasswordConfigured", ""), StringComparison.OrdinalIgnoreCase);

        var smtpWithoutHost = await admin.PutJsonAsync("/api/platform/settings", new { emailProvider = "Smtp", codeLength = 6, codeTtlMinutes = 10, maxAttempts = 5 });
        Assert.Equal(HttpStatusCode.BadRequest, smtpWithoutHost.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await admin.PostJsonAsync("/api/platform/settings/test-email", new { to = "ops@test.local" })).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_tenant_removes_its_registrations()
    {
        var admin = await ConfigureAsync(Settings(email: true));
        var slug = $"reg-{Guid.NewGuid():N}"[..16];
        var tenant = await (await admin.PostJsonAsync("/api/platform/tenants", new
        {
            name = "Reg Co", slug, template = "blank", currency = "AUD",
            adminEmail = $"admin@{slug}.test", adminFirstName = "Reg", adminLastName = "Admin", adminPassword = "Tenant#Pass123", seedDemoData = false,
        })).ReadAsync<TenantItem>();
        var tenantAdmin = await factory.CreateAuthenticatedClientAsync($"admin@{slug}.test", "Tenant#Pass123");
        (await tenantAdmin.PutJsonAsync("/api/settings", new { name = "Reg Co", currency = "AUD", selfRegistrationEnabled = true })).EnsureSuccessStatusCode();

        var email = NewEmail();
        Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync(email, tenantCode: slug)).StatusCode);
        Assert.Single(factory.Messages.To(email));

        await admin.PutJsonAsync($"/api/platform/tenants/{tenant.Id}", new { name = "Reg Co", currency = "AUD", status = "Suspended" });
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/platform/tenants/{tenant.Id}")).StatusCode);

        // The email is free again: a new registration gets a fresh code instead of the silent existing-account path.
        Assert.Equal(HttpStatusCode.Accepted, (await RegisterAsync(email)).StatusCode);
        Assert.Equal(2, factory.Messages.To(email).Count);
    }
}
