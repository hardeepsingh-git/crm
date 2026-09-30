using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Leads;

public record LeadListItem(
    Guid Id, string FirstName, string LastName, string FullName, string Company, string? Email, string? Phone,
    LeadStatus Status, LeadSource Source, decimal? EstimatedValue, Guid OwnerId, string OwnerName, DateTime CreatedAt);

public record LeadDetail(
    Guid Id, string FirstName, string LastName, string FullName, string Company, string? Email, string? Phone,
    string? JobTitle, LeadStatus Status, LeadSource Source, decimal? EstimatedValue, string? Description,
    DateTime? ConvertedAt, Guid? ConvertedAccountId, Guid? ConvertedContactId, Guid? ConvertedOpportunityId,
    Guid OwnerId, string OwnerName, DateTime CreatedAt, DateTime UpdatedAt);

public record LeadRequest(
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    [Required, MaxLength(200)] string Company,
    [EmailAddress, MaxLength(256)] string? Email,
    [MaxLength(50)] string? Phone,
    [MaxLength(100)] string? JobTitle,
    LeadSource Source,
    LeadStatus Status,
    [Range(0, 1_000_000_000_000)] decimal? EstimatedValue,
    [MaxLength(4000)] string? Description,
    Guid? OwnerId);

public record LeadConvertRequest(
    Guid? ExistingAccountId,
    bool CreateOpportunity,
    [MaxLength(200)] string? OpportunityName,
    [Range(0, 1_000_000_000_000)] decimal? Amount,
    DateOnly? ExpectedCloseDate);

public record LeadConvertResult(Guid AccountId, Guid ContactId, Guid? OpportunityId);

public record LeadQuery(
    string? Search, LeadStatus? Status, LeadSource? Source, Guid? OwnerId, bool Mine = false,
    int Page = 1, int PageSize = 20, string? SortBy = null, string? SortDir = null);

public static class LeadEndpoints
{
    public static RouteGroupBuilder MapLeads(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/leads").WithTags("Leads");
        g.MapGet("/", List);
        g.MapGet("/{id:guid}", Get);
        g.MapPost("/", Create);
        g.MapPut("/{id:guid}", Update);
        g.MapPost("/{id:guid}/convert", Convert);
        g.MapDelete("/{id:guid}", Delete).RequireAuthorization(Policies.ManagerOrAdmin);
        return api;
    }

    private static async Task<Ok<PagedResult<LeadListItem>>> List(
        [AsParameters] LeadQuery q, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        var query = db.Leads.AsNoTracking();
        var search = QueryableExtensions.NormalizeSearch(q.Search);
        if (search is not null)
            query = query.Where(l => (l.FirstName + " " + l.LastName).ToLower().Contains(search)
                || l.Company.ToLower().Contains(search)
                || (l.Email != null && l.Email.ToLower().Contains(search)));
        if (q.Status is not null) query = query.Where(l => l.Status == q.Status);
        if (q.Source is not null) query = query.Where(l => l.Source == q.Source);
        if (q.Mine) query = query.Where(l => l.OwnerId == user.GetUserId());
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
            l.Status, l.Source, l.EstimatedValue, l.OwnerId, l.Owner!.FirstName + " " + l.Owner.LastName, l.CreatedAt))
            .ToPagedAsync(q.Page, q.PageSize, ct);
        return TypedResults.Ok(result);
    }

    private static async Task<Results<Ok<LeadDetail>, NotFound>> Get(Guid id, CrmDbContext db, CancellationToken ct)
    {
        var detail = await Project(db.Leads.AsNoTracking().Where(l => l.Id == id)).SingleOrDefaultAsync(ct);
        return detail is null ? TypedResults.NotFound() : TypedResults.Ok(detail);
    }

    private static async Task<Results<Created<LeadDetail>, ValidationProblem>> Create(
        LeadRequest req, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        var me = user.GetUserId();
        if (req.Status == LeadStatus.Converted) return Errors.Validation("status", "Use the convert action to convert a lead.");
        if (await Ownership.ValidateAsync(req.OwnerId, me, user, db, ct) is { } err) return Errors.Validation("ownerId", err);

        var lead = new Lead { OwnerId = req.OwnerId ?? me };
        Apply(lead, req);
        db.Leads.Add(lead);
        await db.SaveChangesAsync(ct);

        var detail = await Project(db.Leads.AsNoTracking().Where(l => l.Id == lead.Id)).SingleAsync(ct);
        return TypedResults.Created($"/api/leads/{lead.Id}", detail);
    }

    private static async Task<Results<Ok<LeadDetail>, NotFound, ForbidHttpResult, ValidationProblem, Conflict<string>>> Update(
        Guid id, LeadRequest req, ClaimsPrincipal user, CrmDbContext db, CancellationToken ct)
    {
        var lead = await db.Leads.FindAsync([id], ct);
        if (lead is null) return TypedResults.NotFound();
        if (!user.CanModify(lead.OwnerId)) return TypedResults.Forbid();
        if (lead.Status == LeadStatus.Converted) return TypedResults.Conflict("Converted leads are read-only.");
        if (req.Status == LeadStatus.Converted) return Errors.Validation("status", "Use the convert action to convert a lead.");
        if (await Ownership.ValidateAsync(req.OwnerId, lead.OwnerId, user, db, ct) is { } err) return Errors.Validation("ownerId", err);

        Apply(lead, req);
        lead.OwnerId = req.OwnerId ?? lead.OwnerId;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await Project(db.Leads.AsNoTracking().Where(l => l.Id == id)).SingleAsync(ct));
    }

    private static async Task<Results<Ok<LeadConvertResult>, NotFound, ForbidHttpResult, ValidationProblem, Conflict<string>>> Convert(
        Guid id, LeadConvertRequest req, ClaimsPrincipal user, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var lead = await db.Leads.FindAsync([id], ct);
        if (lead is null) return TypedResults.NotFound();
        if (!user.CanModify(lead.OwnerId)) return TypedResults.Forbid();
        if (lead.Status == LeadStatus.Converted) return TypedResults.Conflict("Lead is already converted.");

        Account? account;
        if (req.ExistingAccountId is not null)
        {
            account = await db.Accounts.FindAsync([req.ExistingAccountId], ct);
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
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            opportunity = new Opportunity
            {
                Name = string.IsNullOrWhiteSpace(req.OpportunityName) ? $"{lead.Company} opportunity" : req.OpportunityName.Trim(),
                Account = account, Contact = contact, OwnerId = lead.OwnerId,
                Amount = req.Amount ?? lead.EstimatedValue ?? 0,
                Stage = OpportunityStage.Qualification,
                Probability = StageDefaults.Probability(OpportunityStage.Qualification),
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

    private static async Task<Results<NoContent, NotFound>> Delete(Guid id, CrmDbContext db, CancellationToken ct)
    {
        var lead = await db.Leads.FindAsync([id], ct);
        if (lead is null) return TypedResults.NotFound();
        db.Leads.Remove(lead);
        await db.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    private static void Apply(Lead l, LeadRequest r)
    {
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
    }

    private static IQueryable<LeadDetail> Project(IQueryable<Lead> q) => q.Select(l => new LeadDetail(
        l.Id, l.FirstName, l.LastName, l.FirstName + " " + l.LastName, l.Company, l.Email, l.Phone,
        l.JobTitle, l.Status, l.Source, l.EstimatedValue, l.Description,
        l.ConvertedAt, l.ConvertedAccountId, l.ConvertedContactId, l.ConvertedOpportunityId,
        l.OwnerId, l.Owner!.FirstName + " " + l.Owner.LastName, l.CreatedAt, l.UpdatedAt));
}
