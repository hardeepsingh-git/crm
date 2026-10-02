using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Crm.Api.Automation;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Features.Attachments;
using Crm.Api.Features.MockInterviews;
using Crm.Api.Features.Offers;
using Crm.Api.Files;
using Crm.Api.Messaging;
using Crm.Api.Metadata;
using Crm.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Privacy;

public record EraseCandidateRequest([MaxLength(200)] string? ConfirmName);

public record ErasureResult(int Applications, int Activities, int Files, int Emails);

public record RetentionCandidate(Guid Id, string Name, DateTime LastActivity, int Applications);

/// <param name="Eligible">Candidates the policy would erase now; <paramref name="Sample"/> lists the longest-inactive ones.</param>
public record RetentionStatus(int? Months, DateTime? Cutoff, int Eligible, IReadOnlyList<RetentionCandidate> Sample);

public record RetentionRequest(int? Months);

public record RetentionRun(int Erased, int Remaining);

/// <summary>
/// Candidate privacy for recruitment tenants: a full export of one person's data (subject access), erasure (right to be
/// forgotten) and a retention policy. Organisation administrators only (<c>settings:manage</c>), because erasure can't be undone.
/// </summary>
public static class PrivacyEndpoints
{
    public const string CandidateKey = "candidate";
    public const string ApplicationKey = "application";
    public const string ErasedName = "[erased]";
    public const int MinRetentionMonths = 6;
    public const int MaxRetentionMonths = 120;
    public const int RetentionBatch = 100;
    public const int MaxScan = 20000;

    private static readonly JsonSerializerOptions ExportJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static RouteGroupBuilder MapPrivacy(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/privacy").WithTags("Privacy").RequirePermission(AdminResources.Settings, PermissionActions.Manage);
        g.MapGet("/candidates/{id:guid}/export", Export);
        g.MapPost("/candidates/{id:guid}/erase", Erase);
        g.MapGet("/retention", GetRetention);
        g.MapPut("/retention", SetRetention);
        g.MapPost("/retention/run", RunRetention);
        return api;
    }

    private static Task<Guid?> ObjectIdAsync(CrmDbContext db, string key, CancellationToken ct) =>
        db.ObjectDefinitions.Where(o => o.Key == key && o.IsActive).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);

    private static async Task<CustomRecord?> CandidateAsync(CrmDbContext db, Guid id, CancellationToken ct) =>
        await ObjectIdAsync(db, CandidateKey, ct) is { } definition
            ? await db.CustomRecords.FirstOrDefaultAsync(r => r.Id == id && r.ObjectDefinitionId == definition, ct)
            : null;

    private static async Task<List<CustomRecord>> ApplicationsOfAsync(CrmDbContext db, Guid candidateId, CancellationToken ct) =>
        await ObjectIdAsync(db, ApplicationKey, ct) is { } definition
            ? await db.CustomRecords.Where(r => r.ObjectDefinitionId == definition)
                .WhereCustomField(db, "candidate", JsonValue.Create(candidateId.ToString())).ToListAsync(ct)
            : [];

    // ---------- Subject access export ----------

    private static async Task<IResult> Export(Guid id, CrmDbContext db, IFileStorage storage, TimeProvider clock, CancellationToken ct)
    {
        if (await CandidateAsync(db, id, ct) is not { } candidate) return TypedResults.NotFound();
        var now = clock.GetUtcNow().UtcDateTime;
        var tenantId = db.CurrentTenantId!.Value;
        var org = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => new { t.Name, t.Currency }).FirstAsync(ct);
        var apps = await ApplicationsOfAsync(db, id, ct);
        var recordIds = apps.Select(a => a.Id).Append(id).ToList();
        var appIds = apps.Select(a => a.Id).ToList();

        var fields = (await db.FieldDefinitions.AsNoTracking().Where(f => f.ObjectKey == CandidateKey || f.ObjectKey == ApplicationKey).ToListAsync(ct))
            .GroupBy(f => f.ObjectKey).ToDictionary(g => g.Key, g => g.OrderBy(f => f.SortOrder).ToList());
        var stages = await db.PipelineStages.AsNoTracking().Where(s => s.ObjectKey == ApplicationKey).ToDictionaryAsync(s => s.Key, s => s.Label, ct);
        var linkedIds = apps.SelectMany(a => Cf.Parse(a.CustomFields).Select(kv => Guid.TryParse(Cf.AsText(kv.Value), out var g) ? g : Guid.Empty))
            .Where(g => g != Guid.Empty).Distinct().ToList();
        var names = await db.CustomRecords.AsNoTracking().Where(r => linkedIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);

        JsonObject Values(CustomRecord r, string objectKey)
        {
            var values = Cf.Parse(r.CustomFields);
            var result = new JsonObject();
            foreach (var f in fields.GetValueOrDefault(objectKey) ?? [])
            {
                var text = f.Type == FieldType.Lookup
                    ? Guid.TryParse(Cf.AsText(values[f.Key]), out var linked) ? names.GetValueOrDefault(linked) : null
                    : EmailComposer.FormatValue(f, values[f.Key], org.Currency);
                if (!string.IsNullOrEmpty(text)) result[f.Label] = text;
            }
            return result;
        }

        var interviews = await db.Interviews.AsNoTracking().Include(i => i.Scorecards).Where(i => appIds.Contains(i.ApplicationId))
            .OrderBy(i => i.ScheduledAt).ToListAsync(ct);
        var offers = await db.Offers.AsNoTracking().Where(o => appIds.Contains(o.ApplicationId)).OrderBy(o => o.CreatedAt).ToListAsync(ct);
        var mocks = await db.MockInterviews.AsNoTracking().Where(m => appIds.Contains(m.ApplicationId)).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        var activities = await db.Activities.AsNoTracking().Where(a => a.CustomRecordId != null && recordIds.Contains(a.CustomRecordId.Value))
            .OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var email = EmailText.Address(Cf.AsText(Cf.Parse(candidate.CustomFields)["email"]));
        var emails = await db.OutboundEmails.AsNoTracking()
            .Where(e => (e.RecordId != null && recordIds.Contains(e.RecordId.Value)) || (email != null && e.To == email))
            .OrderBy(e => e.CreatedAt).ToListAsync(ct);
        var files = await db.Attachments.AsNoTracking().Where(a => recordIds.Contains(a.RecordId)).OrderBy(a => a.CreatedAt).ToListAsync(ct);

        var document = new
        {
            ExportedAt = now,
            Organisation = org.Name,
            Note = "Personal data held about you. Interviewers are not named; scorecards are their professional opinions about your application.",
            Candidate = new { candidate.Name, candidate.CreatedAt, candidate.UpdatedAt, Fields = Values(candidate, CandidateKey) },
            Applications = apps.OrderBy(a => a.CreatedAt).Select(a => new
            {
                a.Name, Stage = stages.GetValueOrDefault(a.Stage, a.Stage), a.CreatedAt, a.ClosedAt, Fields = Values(a, ApplicationKey),
                Interviews = interviews.Where(i => i.ApplicationId == a.Id).Select(i => new
                {
                    i.ScheduledAt, i.DurationMinutes, i.Kind, i.Status, i.Location,
                    Scorecards = i.Scorecards.OrderBy(s => s.SubmittedAt).Select((s, n) => new
                    {
                        Interviewer = $"Interviewer {n + 1}", s.Recommendation, Ratings = Cf.Parse(s.Ratings), s.Notes, s.SubmittedAt,
                    }),
                }),
                Offers = offers.Where(o => o.ApplicationId == a.Id && o.Status != OfferStatus.Draft).Select(o => new
                {
                    o.JobTitle, o.Status, o.Salary, o.Currency, o.PayPeriod, o.StartDate, o.ExpiresAt, o.SentAt, o.RespondedAt, o.SignedName,
                    o.DeclineReason, o.Letter,
                }),
                MockInterviews = mocks.Where(m => m.ApplicationId == a.Id).Select(m =>
                {
                    var answers = MockJson.Answers(m);
                    var ai = m.AiStatus == AiReviewStatus.Completed ? MockJson.Feedback(m) : null;
                    return new
                    {
                        m.JobTitle, m.Status, m.SentAt, m.StartedAt, m.SubmittedAt,
                        Questions = MockJson.Questions(m).Select(q => new
                        {
                            q.Prompt, Answer = answers.GetValueOrDefault(q.Id)?.Text, SelfRating = answers.GetValueOrDefault(q.Id)?.Rating,
                            AiFeedback = ai?.Questions.FirstOrDefault(f => f.Id == q.Id)?.Feedback,
                        }),
                        Review = m.ReviewedAt is null ? null : new { Reviewer = "Hiring team", m.ReadinessRating, m.ReviewNotes, m.ReviewedAt },
                        AiReview = ai is null ? null : new { Score = m.AiScore, ai.Summary, ai.Strengths, ai.Improvements, m.AiReviewedAt },
                    };
                }),
            }),
            Activities = activities.Select(a => new { a.Type, a.Subject, a.Description, a.DueAt, a.CompletedAt, a.CreatedAt }),
            Emails = emails.Select(e => new { e.To, e.Subject, e.Body, e.Status, e.CreatedAt, e.SentAt }),
            Files = files.Select(f => new { f.FileName, f.ContentType, f.Size, f.CreatedAt, Path = $"files/{f.Id:N}-{AttachmentEndpoints.SafeFileName(f.FileName)}" }),
        };

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using (var entry = await zip.CreateEntry("data.json", CompressionLevel.Optimal).OpenAsync(ct))
                await JsonSerializer.SerializeAsync(entry, document, ExportJson, ct);
            foreach (var f in files)
            {
                await using var source = await storage.OpenReadAsync(f.StorageKey, ct);
                if (source is null) continue;
                await using var target = await zip.CreateEntry($"files/{f.Id:N}-{AttachmentEndpoints.SafeFileName(f.FileName)}").OpenAsync(ct);
                await source.CopyToAsync(target, ct);
            }
        }

        db.AuditLogs.Add(new AuditLog
        {
            EntityType = CandidateKey, EntityId = id, EntityName = "Personal data export", Action = AuditAction.Exported,
            UserId = db.TenantContext.UserId, UserName = db.TenantContext.UserName, Timestamp = now,
            Changes = new JsonObject { ["applications"] = apps.Count, ["files"] = files.Count, ["emails"] = emails.Count }.ToJsonString(),
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.File(buffer.ToArray(), "application/zip", $"candidate-data-{now:yyyyMMdd}.zip");
    }

    // ---------- Erasure ----------

    private static async Task<IResult> Erase(Guid id, EraseCandidateRequest req, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (await CandidateAsync(db, id, ct) is not { } candidate) return TypedResults.NotFound();
        if (OfferResponseEndpoints.NormalizeName(req.ConfirmName) != OfferResponseEndpoints.NormalizeName(candidate.Name))
            return Errors.Validation("confirmName", "Type the candidate's full name to confirm.");
        return TypedResults.Ok(await EraseAsync(db, candidate, "Request", clock.GetUtcNow().UtcDateTime, ct));
    }

    /// <summary>
    /// Deletes a candidate with their applications, interviews, scorecards, offers, mock interviews, timeline, files, emails and notifications,
    /// and blanks their names and values in the audit log, leaving one <see cref="AuditAction.Erased"/> entry without personal data.
    /// </summary>
    public static async Task<ErasureResult> EraseAsync(CrmDbContext db, CustomRecord candidate, string reason, DateTime now, CancellationToken ct)
    {
        var apps = await ApplicationsOfAsync(db, candidate.Id, ct);
        var appIds = apps.Select(a => a.Id).ToList();
        var recordIds = appIds.Append(candidate.Id).ToList();
        var activities = await db.Activities.Where(a => a.CustomRecordId != null && recordIds.Contains(a.CustomRecordId.Value)).ToListAsync(ct);
        var interviewIds = await db.Interviews.Where(i => appIds.Contains(i.ApplicationId)).Select(i => i.Id).ToListAsync(ct);
        var scorecardIds = await db.Scorecards.Where(s => interviewIds.Contains(s.InterviewId)).Select(s => s.Id).ToListAsync(ct);
        var offerIds = await db.Offers.Where(o => appIds.Contains(o.ApplicationId)).Select(o => o.Id).ToListAsync(ct);
        var mockIds = await db.MockInterviews.Where(m => appIds.Contains(m.ApplicationId)).Select(m => m.Id).ToListAsync(ct);
        var files = await db.Attachments.CountAsync(a => recordIds.Contains(a.RecordId), ct);
        var email = EmailText.Address(Cf.AsText(Cf.Parse(candidate.CustomFields)["email"]));
        var links = recordIds.SelectMany(rid => new[] { $"/o/{CandidateKey}/{rid}", $"/o/{ApplicationKey}/{rid}" }).ToList();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var emails = await db.OutboundEmails
            .Where(e => (e.RecordId != null && recordIds.Contains(e.RecordId.Value)) || (email != null && e.To == email)).ExecuteDeleteAsync(ct);
        await db.Notifications.Where(n => n.Link != null && links.Contains(n.Link)).ExecuteDeleteAsync(ct);
        db.Activities.RemoveRange(activities);
        db.CustomRecords.RemoveRange(apps);
        db.CustomRecords.Remove(candidate);
        // Files go with their records; interviews, scorecards, offers and mock interviews cascade from the applications.
        await db.SaveChangesAsync(ct);

        var erased = recordIds.Concat(activities.Select(a => a.Id)).Concat(interviewIds).Concat(scorecardIds).Concat(offerIds).Concat(mockIds).ToList();
        foreach (var chunk in erased.Chunk(1000))
            await db.AuditLogs.Where(a => chunk.Contains(a.EntityId))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.EntityName, ErasedName).SetProperty(a => a.Changes, "{}"), ct);
        db.AuditLogs.Add(new AuditLog
        {
            EntityType = CandidateKey, EntityId = candidate.Id, EntityName = ErasedName, Action = AuditAction.Erased,
            UserId = db.TenantContext.UserId, UserName = db.TenantContext.UserName ?? "System", Timestamp = now,
            Changes = new JsonObject
            {
                ["reason"] = reason, ["applications"] = apps.Count, ["activities"] = activities.Count, ["files"] = files, ["emails"] = emails,
            }.ToJsonString(),
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ErasureResult(apps.Count, activities.Count, files, emails);
    }

    // ---------- Retention ----------

    private static async Task<IResult> GetRetention(CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (await ObjectIdAsync(db, CandidateKey, ct) is null) return TypedResults.NotFound();
        return TypedResults.Ok(await StatusAsync(db, clock.GetUtcNow().UtcDateTime, ct));
    }

    private static async Task<IResult> SetRetention(RetentionRequest req, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (await ObjectIdAsync(db, CandidateKey, ct) is null) return TypedResults.NotFound();
        if (req.Months is { } m && (m < MinRetentionMonths || m > MaxRetentionMonths))
            return Errors.Validation("months", $"Keep candidates for between {MinRetentionMonths} and {MaxRetentionMonths} months, or switch the policy off.");
        var tenantId = db.CurrentTenantId!.Value;
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId, ct);
        tenant.CandidateRetentionMonths = req.Months;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await StatusAsync(db, clock.GetUtcNow().UtcDateTime, ct));
    }

    private static async Task<IResult> RunRetention(CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var tenantId = db.CurrentTenantId!.Value;
        var months = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.CandidateRetentionMonths).FirstAsync(ct);
        if (months is null) return Errors.Validation("months", "Set a retention period first.");
        var now = clock.GetUtcNow().UtcDateTime;
        var eligible = await EligibleAsync(db, months.Value, now, ct);
        var erased = 0;
        foreach (var c in eligible.Take(RetentionBatch))
        {
            db.ChangeTracker.Clear();
            if (await db.CustomRecords.FirstOrDefaultAsync(r => r.Id == c.Id, ct) is not { } candidate) continue;
            await EraseAsync(db, candidate, "Retention policy", now, ct);
            erased++;
        }
        return TypedResults.Ok(new RetentionRun(erased, Math.Max(0, eligible.Count - erased)));
    }

    private static async Task<RetentionStatus> StatusAsync(CrmDbContext db, DateTime now, CancellationToken ct)
    {
        var tenantId = db.CurrentTenantId!.Value;
        var months = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.CandidateRetentionMonths).FirstAsync(ct);
        if (months is null) return new RetentionStatus(null, null, 0, []);
        var eligible = await EligibleAsync(db, months.Value, now, ct);
        return new RetentionStatus(months, now.AddMonths(-months.Value), eligible.Count, eligible.Take(50).ToList());
    }

    /// <summary>
    /// Candidates untouched since the cutoff whose applications are all closed, not hired, and also untouched since then.
    /// Hired people are kept: their data now belongs to employment records.
    /// </summary>
    public static async Task<List<RetentionCandidate>> EligibleAsync(CrmDbContext db, int months, DateTime now, CancellationToken ct)
    {
        if (await ObjectIdAsync(db, CandidateKey, ct) is not { } candidateDef) return [];
        var cutoff = now.AddMonths(-months);
        var candidates = await db.CustomRecords.AsNoTracking().Where(r => r.ObjectDefinitionId == candidateDef && r.UpdatedAt < cutoff)
            .OrderBy(r => r.UpdatedAt).Select(r => new { r.Id, r.Name, r.UpdatedAt }).Take(MaxScan).ToListAsync(ct);
        if (candidates.Count == 0) return [];

        var apps = await ObjectIdAsync(db, ApplicationKey, ct) is { } appDef
            ? await db.CustomRecords.AsNoTracking().Where(r => r.ObjectDefinitionId == appDef)
                .Select(r => new { r.CustomFields, r.Stage, r.ClosedAt, r.UpdatedAt }).ToListAsync(ct)
            : [];
        var won = (await db.PipelineStages.AsNoTracking().Where(s => s.ObjectKey == ApplicationKey && s.Category == StageCategory.Won)
            .Select(s => s.Key).ToListAsync(ct)).ToHashSet();
        var byCandidate = apps
            .Select(a => (Candidate: Guid.TryParse(Cf.AsText(Cf.Parse(a.CustomFields)["candidate"]), out var c) ? c : Guid.Empty, App: a))
            .Where(x => x.Candidate != Guid.Empty)
            .GroupBy(x => x.Candidate)
            .ToDictionary(g => g.Key, g => g.Select(x => x.App).ToList());

        return candidates
            .Select(c => (c, Apps: byCandidate.GetValueOrDefault(c.Id) ?? []))
            .Where(x => x.Apps.All(a => a.ClosedAt != null && !won.Contains(a.Stage) && a.UpdatedAt < cutoff))
            .Select(x => new RetentionCandidate(x.c.Id, x.c.Name,
                x.Apps.Select(a => a.UpdatedAt).Append(x.c.UpdatedAt).Max(), x.Apps.Count))
            .OrderBy(r => r.LastActivity)
            .ToList();
    }
}
