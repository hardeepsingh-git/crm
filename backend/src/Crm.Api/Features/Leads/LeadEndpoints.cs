using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Metadata;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using static Crm.Api.Domain.PermissionActions;

namespace Crm.Api.Features.Leads;

public record LeadListItem(
    Guid Id, string FirstName, string LastName, string FullName, string Company, string? Email, string? Phone,
    LeadStatus Status, string Source, decimal? EstimatedValue, Guid OwnerId, string OwnerName, DateTime CreatedAt, JsonObject CustomFields);

public record LeadDetail(
    Guid Id, string FirstName, string LastName, string FullName, string Company, string? Email, string? Phone,
    string? JobTitle, LeadStatus Status, string Source, decimal? EstimatedValue, string? Description,
    DateTime? ConvertedAt, Guid? ConvertedAccountId, Guid? ConvertedContactId, Guid? ConvertedOpportunityId,
    Guid OwnerId, string OwnerName, DateTime CreatedAt, DateTime UpdatedAt, JsonObject CustomFields);

public record LeadRequest(
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    [Required, MaxLength(200)] string Company,
    [EmailAddress, MaxLength(256)] string? Email,
    [MaxLength(50)] string? Phone,
    [MaxLength(100)] string? JobTitle,
    [Required, MaxLength(64)] string Source,
    LeadStatus Status,
    [Range(0, 1_000_000_000_000)] decimal? EstimatedValue,
    [MaxLength(4000)] string? Description,
    Guid? OwnerId,
    JsonObject? CustomFields = null);

public record LeadConvertRequest(
    Guid? ExistingAccountId,
    bool CreateOpportunity,
    [MaxLength(200)] string? OpportunityName,
    [Range(0, 1_000_000_000_000)] decimal? Amount,
    DateOnly? ExpectedCloseDate);

public record LeadConvertResult(Guid AccountId, Guid ContactId, Guid? OpportunityId);

public record LeadQuery(
    string? Search, LeadStatus? Status, string? Source, Guid? OwnerId, bool Mine = false,
    int Page = 1, int PageSize = 20, string? SortBy = null, string? SortDir = null);

public static class LeadEndpoints
{
    private const string R = ObjectKeys.Lead;

    public static RouteGroupBuilder MapLeads(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/leads").WithTags("Leads");
        g.MapGet("/", List).RequirePermission(R, Read);
        g.MapGet("/{id:guid}", Get).RequirePermission(R, Read);
        g.MapPost("/", CreateAsync).RequirePermission(R, Create);
        g.MapPut("/{id:guid}", UpdateAsync).RequirePermission(R, Update);
        g.MapPost("/{id:guid}/convert", Convert).RequirePermission(R, Update);
        g.MapDelete("/{id:guid}", DeleteAsync).RequirePermission(R, Delete);
        return api;
    }

    private static async Task<Ok<PagedResult<LeadListItem>>> List(
        [AsParameters] LeadQuery q, HttpRequest http, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var query = p.Filter(db.Leads.AsNoTracking(), R).WhereCustomFields(http.Query, db);
        var search = QueryableExtensions.NormalizeSearch(q.Search);
        if (search is not null)
            query = query.Where(l => (l.FirstName + " " + l.LastName).ToLower().Contains(search)
                || l.Company.ToLower().Contains(search)
                || (l.Email != null && l.Email.ToLower().Contains(search)));
        if (q.Status is not null) query = query.Where(l => l.Status == q.Status);
        if (!string.IsNullOrWhiteSpace(q.Source)) query = query.Where(l => l.Source == q.Source);
        if (q.Mine) query = query.Where(l => l.OwnerId == p.UserId);
        else if (q.OwnerId is not null) query = query.Where(l => l.OwnerId == q.OwnerId);

        var desc = QueryableExtensions.IsDescending(q.SortDir);
        query = q.SortBy?.ToLowerInvariant() switch
        {
            "name" => query.OrderByDir(l => l.LastName, desc),
            "company" => query.OrderByDir(l => l.Company, desc),
            "status" => query.OrderByDir(l => l.Status, desc),
            "estimatedvalue" => query.OrderByDir(l => l.EstimatedValue, desc),
            _ => query.OrderByDir(l => l.CreatedAt, q.SortDir is null || desc),
        };

        var result = await query.Select(l => new LeadListItem(
            l.Id, l.FirstName, l.LastName, l.FirstName + " " + l.LastName, l.Company, l.Email, l.Phone,
            l.Status, l.Source, l.EstimatedValue, l.OwnerId, l.Owner!.FirstName + " " + l.Owner.LastName, l.CreatedAt, Cf.Parse(l.CustomFields)))
            .ToPagedAsync(q.Page, q.PageSize, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<LeadDetail>, NotFound>> Get(Guid id, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var detail = await Project(p.Filter(db.Leads.AsNoTracking(), R).Where(l => l.Id == id)).SingleOrDefaultAsync(ct);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<Results<Created<LeadDetail>, ValidationProblem>> CreateAsync(
        LeadRequest req, Access access, CrmDbContext db, MetadataService meta, CustomFieldValidator validator, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var lead = new Lead { OwnerId = req.OwnerId ?? p.UserId };
        if (await ValidateAsync(req, lead, p.UserId, p, db, meta, validator, ct) is { } problem) return problem;

        db.Leads.Add(lead);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/leads/{lead.Id}", await Project(db.Leads.AsNoTracking().Where(l => l.Id == lead.Id)).SingleAsync(ct));
    }

    private static async Task<Results<Ok<LeadDetail>, NotFound, ForbidHttpResult, ValidationProblem, Conflict<string>>> UpdateAsync(
        Guid id, LeadRequest req, Access access, CrmDbContext db, MetadataService meta, CustomFieldValidator validator, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var lead = await db.Leads.FindAsync([id], ct);
        if (lead is null || !p.CanAccess(R, Read, lead.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(R, Update, lead.OwnerId)) return TypedResults.Forbid();
        if (lead.Status == LeadStatus.Converted) return TypedResults.Conflict("Converted leads are read-only.");
        if (await ValidateAsync(req, lead, lead.OwnerId, p, db, meta, validator, ct) is { } problem) return problem;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.Leads.AsNoTracking().Where(l => l.Id == id)).SingleAsync(ct));
    }

    private static async Task<Results<Ok<LeadConvertResult>, NotFound, ForbidHttpResult, ValidationProblem, Conflict<string>>> Convert(
        Guid id, LeadConvertRequest req, Access access, CrmDbContext db, MetadataService meta, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var lead = await db.Leads.FindAsync([id], ct);
        if (lead is null || !p.CanAccess(R, Read, lead.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(R, Update, lead.OwnerId)
            || !p.Can(ObjectKeys.Contact, Create)
            || (req.ExistingAccountId is null && !p.Can(ObjectKeys.Account, Create))
            || (req.CreateOpportunity && !p.Can(ObjectKeys.Opportunity, Create)))
            return TypedResults.Forbid();
        if (lead.Status == LeadStatus.Converted) return TypedResults.Conflict("Lead is already converted.");

        Account? account;
        if (req.ExistingAccountId is not null)
        {
            account = await p.Filter(db.Accounts, ObjectKeys.Account).SingleOrDefaultAsync(a => a.Id == req.ExistingAccountId, ct);
            if (account is null) return Errors.Validation("existingAccountId", "Account does not exist.");
        }
        else
        {
            account = new Account { Name = lead.Company, Phone = lead.Phone, OwnerId = lead.OwnerId };
            db.Accounts.Add(account);
        }

        var contact = new Contact
        {
            FirstName = lead.FirstName, LastName = lead.LastName, Email = lead.Email, Phone = lead.Phone,
            JobTitle = lead.JobTitle, Account = account, OwnerId = lead.OwnerId,
        };
        db.Contacts.Add(contact);

        Opportunity? opportunity = null;
        if (req.CreateOpportunity)
        {
            var firstStage = (await meta.StagesAsync(ObjectKeys.Opportunity, ct)).FirstOrDefault(s => s.Category == StageCategory.Open);
            if (firstStage is null) return Errors.Validation("createOpportunity", "The opportunity pipeline has no open stage.");
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            opportunity = new Opportunity
            {
                Name = string.IsNullOrWhiteSpace(req.OpportunityName) ? $"{lead.Company} opportunity" : req.OpportunityName.Trim(),
                Account = account, Contact = contact, OwnerId = lead.OwnerId,
                Amount = req.Amount ?? lead.EstimatedValue ?? 0,
                Stage = firstStage.Key,
                Probability = firstStage.Probability,
                ExpectedCloseDate = req.ExpectedCloseDate ?? today.AddDays(30),
            };
            db.Opportunities.Add(opportunity);
        }

        lead.Status = LeadStatus.Converted;
        lead.ConvertedAt = clock.GetUtcNow().UtcDateTime;
        lead.ConvertedAccount = account;
        lead.ConvertedContact = contact;
        lead.ConvertedOpportunity = opportunity;
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(new LeadConvertResult(account.Id, contact.Id, opportunity?.Id));
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult>> DeleteAsync(Guid id, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var lead = await db.Leads.FindAsync([id], ct);
        if (lead is null || !p.CanAccess(R, Read, lead.OwnerId)) return TypedResults.NotFound();
        if (!p.CanAccess(R, Delete, lead.OwnerId)) return TypedResults.Forbid();
        db.Leads.Remove(lead);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<ValidationProblem?> ValidateAsync(LeadRequest r, Lead l, Guid currentOwner, EffectivePermissions p,
        CrmDbContext db, MetadataService meta, CustomFieldValidator validator, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (r.Status == LeadStatus.Converted) errors["status"] = ["Use the convert action to convert a lead."];
        if (await Ownership.ValidateAsync(r.OwnerId, currentOwner, R, p, db, ct) is { } ownerError) errors["ownerId"] = [ownerError];
        if (await meta.CheckPicklistAsync("lead.source", r.Source, l.Source, ct) is { } sourceError) errors["source"] = [sourceError];
        var cf = await validator.ValidateAsync(R, r.CustomFields, l.CustomFields, l.Id, ct);
        foreach (var (k, v) in cf.Errors) errors[k] = v;
        if (errors.Count > 0) return Errors.Validation(errors);

        l.FirstName = r.FirstName.Trim();
        l.LastName = r.LastName.Trim();
        l.Company = r.Company.Trim();
        l.Email = r.Email;
        l.Phone = r.Phone;
        l.JobTitle = r.JobTitle;
        l.Source = r.Source;
        l.Status = r.Status;
        l.EstimatedValue = r.EstimatedValue;
        l.Description = r.Description;
        l.OwnerId = r.OwnerId ?? l.OwnerId;
        l.CustomFields = cf.Json;
        return null;
    }

    private static IQueryable<LeadDetail> Project(IQueryable<Lead> q) => q.Select(l => new LeadDetail(
        l.Id, l.FirstName, l.LastName, l.FirstName + " " + l.LastName, l.Company, l.Email, l.Phone,
        l.JobTitle, l.Status, l.Source, l.EstimatedValue, l.Description,
        l.ConvertedAt, l.ConvertedAccountId, l.ConvertedContactId, l.ConvertedOpportunityId,
        l.OwnerId, l.Owner!.FirstName + " " + l.Owner.LastName, l.CreatedAt, l.UpdatedAt, Cf.Parse(l.CustomFields)));
}
