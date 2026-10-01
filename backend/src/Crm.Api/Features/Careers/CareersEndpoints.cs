using System.Globalization;
using System.Text.Json.Nodes;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Features.Attachments;
using Crm.Api.Files;
using Crm.Api.Messaging;
using Crm.Api.Metadata;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Careers;

public record CareerJob(
    Guid Id, string Title, string? Department, string? Location, string? EmploymentType, string? ClosingDate, DateTime PostedAt);

public record CareerJobDetail(string Organisation, CareerJob Job, string? Description);

public record CareersPage(string Organisation, string? Intro, IReadOnlyList<CareerJob> Jobs);

public record ApplicationReceived(string Message);

/// <summary>
/// Anonymous job board and application form for recruitment tenants (<c>/careers/{slug}</c>). Applicants can't read or change
/// existing data: a known email gets a new application linked to the existing candidate, whose details stay as they were, and
/// every accepted submission gets the same response so the form can't be used to discover who has applied.
/// </summary>
public static class CareersEndpoints
{
    public const string ReadRateLimitPolicy = "careers";
    public const string ApplyRateLimitPolicy = "careers-apply";
    public const string Actor = "Careers site";
    public const string Thanks = "Thank you for applying. We've received your application and will be in touch.";

    private static readonly string[] Objects = ["position", "candidate", "application"];

    /// <summary>CV formats and the leading bytes each must start with, so renamed executables or scripts are refused.</summary>
    private static readonly Dictionary<string, byte[][]> CvSignatures = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = [[0x25, 0x50, 0x44, 0x46]],                                    // %PDF
        [".docx"] = [[0x50, 0x4B, 0x03, 0x04]],                                   // ZIP (Office Open XML)
        [".odt"] = [[0x50, 0x4B, 0x03, 0x04]],                                    // ZIP (OpenDocument)
        [".doc"] = [[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]],            // OLE compound file
        [".rtf"] = [[0x7B, 0x5C, 0x72, 0x74, 0x66]],                              // {\rtf
    };

    public static RouteGroupBuilder MapCareers(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/careers/{slug}").WithTags("Careers").AllowAnonymous();
        g.MapGet("/", Page).RequireRateLimiting(ReadRateLimitPolicy);
        g.MapGet("/jobs/{id:guid}", Job).RequireRateLimiting(ReadRateLimitPolicy);
        // Bearer-only API (no cookies), so cross-site request forgery doesn't apply.
        g.MapPost("/jobs/{id:guid}/apply", Apply).RequireRateLimiting(ApplyRateLimitPolicy).DisableAntiforgery();
        return api;
    }

    /// <summary>The careers page needs job openings, candidates and applications, active, in the current tenant.</summary>
    public static async Task<bool> AvailableAsync(MetadataService meta, CancellationToken ct)
    {
        foreach (var key in Objects)
            if (await meta.ObjectAsync(key, ct) is not { IsActive: true }) return false;
        return true;
    }

    /// <summary>Enters the tenant behind a public slug, if its careers page is on; everything after this is tenant-filtered.</summary>
    private static async Task<Tenant?> EnterAsync(string slug, CrmDbContext db, TenantContext tenant, MetadataService meta, CancellationToken ct)
    {
        var normalized = slug.Trim().ToLowerInvariant();
        var found = await db.Tenants.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Slug == normalized && t.Status == TenantStatus.Active && t.CareersEnabled, ct);
        if (found is null) return null;
        tenant.Set(found.Id, userName: Actor);
        return await AvailableAsync(meta, ct) ? found : null;
    }

    private static async Task<List<CustomRecord>> PublishedAsync(CrmDbContext db, MetadataService meta, DateOnly today, Guid? id, CancellationToken ct)
    {
        var position = (await meta.ObjectAsync("position", ct))!;
        var query = db.CustomRecords.AsNoTracking().Where(r => r.ObjectDefinitionId == position.Id)
            .WhereCustomField(db, "publish", JsonValue.Create(true))
            .WhereCustomField(db, "status", JsonValue.Create("Open"));
        if (id is not null) query = query.Where(r => r.Id == id);
        var jobs = await query.OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
        return jobs.Where(j => !DateOnly.TryParse(Text(j, "closing_date"), CultureInfo.InvariantCulture, out var closes) || closes >= today).ToList();
    }

    private static string? Text(CustomRecord r, string key) => Cf.AsText(Cf.Parse(r.CustomFields)[key]) is { Length: > 0 } s ? s : null;

    private static CareerJob ToJob(CustomRecord r) => new(
        r.Id, r.Name, Text(r, "department"), Text(r, "location"), Text(r, "employment_type"), Text(r, "closing_date"), r.CreatedAt);

    private static async Task<Results<Ok<CareersPage>, NotFound>> Page(
        string slug, CrmDbContext db, TenantContext tenant, MetadataService meta, TimeProvider clock, CancellationToken ct)
    {
        if (await EnterAsync(slug, db, tenant, meta, ct) is not { } org) return TypedResults.NotFound();
        var jobs = await PublishedAsync(db, meta, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), null, ct);
        return TypedResults.Ok(new CareersPage(org.Name, org.CareersIntro, jobs.Select(ToJob).ToList()));
    }

    private static async Task<Results<Ok<CareerJobDetail>, NotFound>> Job(
        string slug, Guid id, CrmDbContext db, TenantContext tenant, MetadataService meta, TimeProvider clock, CancellationToken ct)
    {
        if (await EnterAsync(slug, db, tenant, meta, ct) is not { } org) return TypedResults.NotFound();
        var job = (await PublishedAsync(db, meta, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), id, ct)).FirstOrDefault();
        return job is null ? TypedResults.NotFound() : TypedResults.Ok(new CareerJobDetail(org.Name, ToJob(job), Text(job, "description")));
    }

    private sealed record Submission(string Name, string Email, string? Phone, string? Location, string? LinkedIn, string? CoverNote);

    private static async Task<IResult> Apply(
        string slug, Guid id, HttpRequest http, CrmDbContext db, TenantContext tenant, MetadataService meta, IFileStorage storage,
        IConfiguration config, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        if (!http.HasFormContentType) return Errors.Validation("form", "Send the application as a form.");
        if (await EnterAsync(slug, db, tenant, meta, ct) is not { } org) return TypedResults.NotFound();
        var now = clock.GetUtcNow().UtcDateTime;
        var job = (await PublishedAsync(db, meta, DateOnly.FromDateTime(now), id, ct)).FirstOrDefault();
        if (job is null) return TypedResults.NotFound();

        var form = await http.ReadFormAsync(ct);
        var logger = loggers.CreateLogger("Crm.Api.Careers");
        // Hidden from people, filled in by naive bots: pretend success, store nothing.
        if (!string.IsNullOrEmpty(form["website"].ToString()))
        {
            logger.LogInformation("Careers application for tenant {Slug} dropped by the honeypot", org.Slug);
            return TypedResults.Accepted((string?)null, new ApplicationReceived(Thanks));
        }

        var maxBytes = config.GetValue("Careers:MaxCvSizeMb", 5) * 1024L * 1024L;
        var (submission, cv, errors) = await ReadAsync(form, maxBytes, ct);
        if (errors.Count > 0) return Errors.Validation(errors);

        var definitions = await db.ObjectDefinitions.AsNoTracking().Where(o => Objects.Contains(o.Key)).ToDictionaryAsync(o => o.Key, ct);
        var fields = (await db.FieldDefinitions.AsNoTracking().Where(f => f.ObjectKey == "candidate" || f.ObjectKey == "application").ToListAsync(ct))
            .ToDictionary(f => $"{f.ObjectKey}.{f.Key}");

        var candidate = await db.CustomRecords.Where(r => r.ObjectDefinitionId == definitions["candidate"].Id)
            .WhereCustomField(db, "email", JsonValue.Create(submission!.Email)).FirstOrDefaultAsync(ct);
        var isNewCandidate = candidate is null;
        if (candidate is null)
        {
            var values = new JsonObject();
            Set(values, fields, "candidate.email", submission.Email);
            Set(values, fields, "candidate.phone", submission.Phone);
            Set(values, fields, "candidate.location", submission.Location);
            Set(values, fields, "candidate.linkedin", submission.LinkedIn);
            SetOption(values, fields, "candidate.source", "Website");
            Set(values, fields, "candidate.privacy_consent", now.ToString("o", CultureInfo.InvariantCulture));
            candidate = new CustomRecord
            {
                ObjectDefinitionId = definitions["candidate"].Id, Name = submission.Name, OwnerId = job.OwnerId, CustomFields = values.ToJsonString(),
            };
            db.CustomRecords.Add(candidate);
        }

        var application = isNewCandidate ? null : (await db.CustomRecords
            .Where(r => r.ObjectDefinitionId == definitions["application"].Id && r.ClosedAt == null)
            .WhereCustomField(db, "candidate", JsonValue.Create(candidate.Id.ToString()))
            .WhereCustomField(db, "position", JsonValue.Create(job.Id.ToString()))
            .FirstOrDefaultAsync(ct));
        var reapplied = application is not null;
        if (application is null)
        {
            var stage = await db.PipelineStages.AsNoTracking().Where(s => s.ObjectKey == "application" && s.Category == StageCategory.Open)
                .OrderBy(s => s.SortOrder).Select(s => s.Key).FirstAsync(ct);
            var values = new JsonObject();
            Set(values, fields, "application.candidate", candidate.Id.ToString());
            Set(values, fields, "application.position", job.Id.ToString());
            SetOption(values, fields, "application.source", "Website");
            Set(values, fields, "application.cover_note", submission.CoverNote);
            application = new CustomRecord
            {
                ObjectDefinitionId = definitions["application"].Id, Name = candidate.Name, Stage = stage, OwnerId = job.OwnerId,
                CustomFields = values.ToJsonString(),
            };
            db.CustomRecords.Add(application);
            db.Notifications.Add(new Notification
            {
                UserId = job.OwnerId, Title = "New application",
                Message = Truncate($"{candidate.Name} applied for {job.Name} on the careers page.", 1000),
                Link = $"/o/application/{application.Id}", CreatedAt = now,
            });
        }

        // What the applicant typed, kept for the recruiter without overwriting an existing candidate's details.
        db.Activities.Add(new Activity
        {
            Type = ActivityType.Note, OwnerId = job.OwnerId, CustomRecordId = application.Id,
            Subject = reapplied ? "Applied again on the careers page" : "Applied on the careers page",
            Description = Truncate(Describe(submission), 4000),
        });

        var keys = new List<string>();
        try
        {
            foreach (var record in new[] { (Key: "candidate", Record: candidate), (Key: "application", Record: application) })
            {
                var key = StorageKeys.New(org.Id, now);
                await using (var stream = cv!.OpenReadStream()) await storage.SaveAsync(key, stream, ct);
                keys.Add(key);
                db.Attachments.Add(new Attachment
                {
                    ObjectKey = record.Key, RecordId = record.Record.Id, FileName = AttachmentEndpoints.SafeFileName(cv.FileName),
                    ContentType = AttachmentEndpoints.AllowedTypes[Path.GetExtension(cv.FileName)], Size = cv.Length, StorageKey = key,
                    UploadedById = Guid.Empty, UploadedByName = Actor, CreatedAt = now,
                });
            }
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            foreach (var key in keys) await storage.DeleteAsync(key, CancellationToken.None);
            throw;
        }
        logger.LogInformation("Careers application {ApplicationId} for job {JobId} in tenant {Slug}", application.Id, job.Id, org.Slug);
        return TypedResults.Accepted((string?)null, new ApplicationReceived(Thanks));
    }

    private static async Task<(Submission? Submission, IFormFile? Cv, Dictionary<string, string[]> Errors)> ReadAsync(
        IFormCollection form, long maxBytes, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        string? Field(string key, int max, bool required, string label)
        {
            var value = form[key].ToString().Trim();
            if (value.Length == 0)
            {
                if (required) errors[key] = [$"{label} is required."];
                return null;
            }
            if (value.Length > max) errors[key] = [$"{label} can be at most {max} characters."];
            return value;
        }

        var name = Field("name", 200, true, "Name");
        var rawEmail = Field("email", 256, true, "Email");
        var email = EmailText.Address(rawEmail);
        if (rawEmail is not null && email is null) errors["email"] = ["Enter a valid email address."];
        var phone = Field("phone", 50, false, "Phone");
        var location = Field("location", 255, false, "Location");
        var linkedIn = Field("linkedin", 500, false, "LinkedIn profile");
        if (linkedIn is not null && !(Uri.TryCreate(linkedIn, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
            errors["linkedin"] = ["Enter a full link, starting with https://."];
        var coverNote = Field("coverNote", 4000, false, "Cover note");
        if (!bool.TryParse(form["consent"].ToString(), out var consent) || !consent)
            errors["consent"] = ["Please agree to the privacy notice to apply."];

        var cv = form.Files.GetFile("cv");
        if (cv is null || cv.Length == 0) errors["cv"] = ["Attach your CV."];
        else if (cv.Length > maxBytes) errors["cv"] = [$"Your CV can be at most {maxBytes / 1024 / 1024} MB."];
        else if (!CvSignatures.TryGetValue(Path.GetExtension(cv.FileName), out var signatures))
            errors["cv"] = ["Upload your CV as a PDF or Word document (.pdf, .doc, .docx, .odt or .rtf)."];
        else if (!await StartsWithAsync(cv, signatures, ct))
            errors["cv"] = ["This file doesn't look like the document type its name says. Save it again as PDF or Word and retry."];

        return errors.Count > 0 ? (null, null, errors) : (new Submission(name!, email!, phone, location, linkedIn, coverNote), cv, errors);
    }

    private static async Task<bool> StartsWithAsync(IFormFile file, byte[][] signatures, CancellationToken ct)
    {
        var head = new byte[8];
        await using var stream = file.OpenReadStream();
        var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        return signatures.Any(s => read >= s.Length && head.AsSpan(0, s.Length).SequenceEqual(s));
    }

    /// <summary>Sets a value only when the tenant still has that field, with a compatible type.</summary>
    private static void Set(JsonObject values, Dictionary<string, FieldDefinition> fields, string key, string? value)
    {
        if (value is null || !fields.TryGetValue(key, out var f)) return;
        var ok = f.Type switch
        {
            FieldType.Email or FieldType.Text or FieldType.TextArea or FieldType.Phone or FieldType.Url or FieldType.Lookup => true,
            FieldType.DateTime => true,
            _ => false,
        };
        if (!ok) return;
        var max = f.MaxLength ?? (f.Type == FieldType.TextArea ? 4000 : 255);
        if (f.Type is not FieldType.Lookup && value.Length > max) value = value[..max];
        values[f.Key] = value;
    }

    private static void SetOption(JsonObject values, Dictionary<string, FieldDefinition> fields, string key, string option)
    {
        if (fields.TryGetValue(key, out var f) && f.Type == FieldType.Picklist && Cf.ParseStrings(f.Options).Contains(option)) values[f.Key] = option;
    }

    private static string Describe(Submission s)
    {
        var lines = new List<string> { $"Name: {s.Name}", $"Email: {s.Email}" };
        if (s.Phone is not null) lines.Add($"Phone: {s.Phone}");
        if (s.Location is not null) lines.Add($"Location: {s.Location}");
        if (s.LinkedIn is not null) lines.Add($"LinkedIn: {s.LinkedIn}");
        if (s.CoverNote is not null) lines.Add($"\nCover note:\n{s.CoverNote}");
        return string.Join('\n', lines);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
