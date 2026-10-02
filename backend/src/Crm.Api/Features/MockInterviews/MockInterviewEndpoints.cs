using System.Buffers.Text;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Crm.Api.Automation;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Features.Offers;
using Crm.Api.Security;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.MockInterviews;

public record LibraryQuestion(Guid Id, string Prompt, string? Competency, int? Minutes, bool ForThisJob);

/// <param name="Available">The organisation has the question library object (recruitment template v7).</param>
public record QuestionLibrary(bool Available, bool CanAdd, IReadOnlyList<LibraryQuestion> Questions);

public record StarterQuestionsAdded(int Added);

public record MockInviteRequest(Guid ApplicationId, Guid[]? QuestionIds, int ValidDays = 7, [MaxLength(2000)] string? Message = null);

public record MockQuestionResult(MockQuestion Question, MockAnswer? Answer, AiQuestionFeedback? Ai);

public record MockInterviewItem(
    Guid Id, Guid ApplicationId, MockInterviewStatus Status, string JobTitle, string? Message, DateTime SentAt, string SentByName,
    DateTime ExpiresAt, DateTime? StartedAt, DateTime? SubmittedAt, int QuestionCount, int AnsweredCount, double? AverageSelfRating,
    int? MinutesTaken, IReadOnlyList<MockQuestionResult> Questions,
    int? ReadinessRating, string? ReviewNotes, string? ReviewedByName, DateTime? ReviewedAt,
    AiReviewStatus AiStatus, decimal? AiScore, string? AiSummary, IReadOnlyList<string> AiStrengths, IReadOnlyList<string> AiImprovements,
    string? AiModel, DateTime? AiReviewedAt, string? AiError);

/// <param name="AiAvailable">AI feedback can be requested now; otherwise <paramref name="AiUnavailableReason"/> says why.</param>
public record MockInterviewList(IReadOnlyList<MockInterviewItem> Items, bool AiAvailable, string? AiUnavailableReason);

/// <param name="Link">The candidate's link; shown once, so it can be shared another way if the email can't be sent.</param>
public record MockInviteSent(MockInterviewItem Interview, string Link, string? EmailNote);

public record MockReviewRequest(int? ReadinessRating, [MaxLength(4000)] string? Notes);

public record WithdrawMockRequest([MaxLength(2000)] string? Reason);

public record MockSettings(
    bool AiReviewEnabled, AiReviewTrigger AiTrigger, string? AiInstructions, bool AiConnectionReady, string? AiConnectionProblem, bool LibraryAvailable);

public record MockSettingsRequest(bool AiReviewEnabled, AiReviewTrigger AiTrigger, [MaxLength(4000)] string? AiInstructions);

/// <summary>
/// Mock (practice) interviews on applications. Answers and reviews are about the candidate's performance, so every endpoint
/// needs update access to the application (the hiring team), as for offers.
/// </summary>
public static class MockInterviewEndpoints
{
    private const string ApplicationKey = MockKeys.ApplicationKey;
    public const int MaxQuestions = 20;
    public const int MaxValidDays = 30;
    public const int MaxLibrary = 500;

    public static RouteGroupBuilder MapMockInterviews(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/mock-interviews").WithTags("Mock interviews");
        g.MapGet("/", List);
        g.MapGet("/library", Library);
        g.MapPost("/library/starter", AddStarterQuestions);
        g.MapPost("/", Invite);
        g.MapPost("/{id:guid}/resend", Resend);
        g.MapPost("/{id:guid}/withdraw", Withdraw);
        g.MapPut("/{id:guid}/review", Review);
        g.MapPost("/{id:guid}/ai-review", RequestAiReview);
        g.MapGet("/settings", GetSettings).RequirePermission(AdminResources.Settings, PermissionActions.Manage);
        g.MapPut("/settings", SaveSettings).RequirePermission(AdminResources.Settings, PermissionActions.Manage);
        return api;
    }

    // ---------- Shared ----------

    public static bool IsOpen(MockInterviewStatus status) => status is MockInterviewStatus.Sent or MockInterviewStatus.InProgress;

    /// <summary>Open sessions past their deadline become Expired; checked whenever sessions are read or answered.</summary>
    public static bool ExpireIfDue(MockInterview mock, DateTime now)
    {
        if (!IsOpen(mock.Status) || mock.ExpiresAt > now) return false;
        mock.Status = MockInterviewStatus.Expired;
        return true;
    }

    private static async Task<(CustomRecord? App, IResult? Denied)> ApplicationAsync(CrmDbContext db, EffectivePermissions p, Guid id, CancellationToken ct)
    {
        var app = await db.CustomRecords.FirstOrDefaultAsync(r => r.Id == id && r.ObjectDefinition!.Key == ApplicationKey, ct);
        if (app is null || !p.CanAccess(ApplicationKey, PermissionActions.Read, app.OwnerId)) return (null, TypedResults.NotFound());
        if (!p.CanAccess(ApplicationKey, PermissionActions.Update, app.OwnerId)) return (null, TypedResults.Forbid());
        return (app, null);
    }

    private static async Task<(MockInterview? Mock, CustomRecord? App, IResult? Denied)> MockAsync(CrmDbContext db, EffectivePermissions p, Guid id, CancellationToken ct)
    {
        var mock = await db.MockInterviews.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (mock is null) return (null, null, TypedResults.NotFound());
        var (app, denied) = await ApplicationAsync(db, p, mock.ApplicationId, ct);
        return (mock, app, denied);
    }

    private static async Task<List<MockInterviewItem>> ToItemsAsync(CrmDbContext db, List<MockInterview> mocks, Guid tenantId, CancellationToken ct)
    {
        var userIds = mocks.Select(m => m.SentById).Concat(mocks.Where(m => m.ReviewedById is not null).Select(m => m.ReviewedById!.Value)).Distinct().ToList();
        var names = await db.Users.Where(u => userIds.Contains(u.Id) && u.TenantId == tenantId)
            .ToDictionaryAsync(u => u.Id, u => u.FirstName + " " + u.LastName, ct);
        return mocks.Select(m => ToItem(m, names)).ToList();
    }

    private static MockInterviewItem ToItem(MockInterview m, Dictionary<Guid, string> names)
    {
        var questions = MockJson.Questions(m);
        var answers = MockJson.Answers(m);
        var ai = m.AiStatus == AiReviewStatus.Completed ? MockJson.Feedback(m) : null;
        var ratings = answers.Values.Where(a => a.Rating is not null).Select(a => a.Rating!.Value).ToList();
        return new MockInterviewItem(
            m.Id, m.ApplicationId, m.Status, m.JobTitle, m.Message, m.SentAt, names.GetValueOrDefault(m.SentById, ""), m.ExpiresAt,
            m.StartedAt, m.SubmittedAt, questions.Count, questions.Count(q => answers.ContainsKey(q.Id)),
            ratings.Count == 0 ? null : Math.Round(ratings.Average(), 1),
            m.StartedAt is { } s && m.SubmittedAt is { } e ? (int)Math.Ceiling((e - s).TotalMinutes) : null,
            questions.Select(q => new MockQuestionResult(q, answers.GetValueOrDefault(q.Id), ai?.Questions.FirstOrDefault(f => f.Id == q.Id))).ToList(),
            m.ReadinessRating, m.ReviewNotes, m.ReviewedById is { } r ? names.GetValueOrDefault(r) : null, m.ReviewedAt,
            m.AiStatus, m.AiStatus == AiReviewStatus.Completed ? m.AiScore : null, ai?.Summary, ai?.Strengths ?? [], ai?.Improvements ?? [],
            m.AiModel, m.AiReviewedAt, m.AiStatus == AiReviewStatus.Failed ? m.AiError : null);
    }

    private static async Task<MockInterviewItem> ItemAsync(CrmDbContext db, MockInterview mock, EffectivePermissions p, CancellationToken ct) =>
        (await ToItemsAsync(db, [mock], p.TenantId, ct))[0];

    /// <summary>Whether this organisation can ask for AI feedback now, and if not, why.</summary>
    public static async Task<(bool Ready, string? Problem)> AiStateAsync(CrmDbContext db, AiReviewConfig ai, CancellationToken ct)
    {
        var tenantId = db.CurrentTenantId;
        var enabled = await db.Tenants.Where(t => t.Id == tenantId).Select(t => t.MockAiReviewEnabled).FirstOrDefaultAsync(ct);
        var (connection, problem) = await ai.ResolveAsync(ct);
        if (connection is null) return (false, problem);
        return enabled ? (true, null) : (false, "AI feedback is switched off for your organisation (Settings > Mock interviews).");
    }

    // ---------- Reading ----------

    private static async Task<IResult> List(Guid applicationId, Access access, CrmDbContext db, AiReviewConfig ai, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (_, denied) = await ApplicationAsync(db, p, applicationId, ct);
        if (denied is not null) return denied;
        var mocks = await db.MockInterviews.Where(m => m.ApplicationId == applicationId).OrderByDescending(m => m.CreatedAt).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (mocks.Count(m => ExpireIfDue(m, now)) > 0) await db.SaveChangesAsync(ct);
        var (ready, problem) = await AiStateAsync(db, ai, ct);
        return TypedResults.Ok(new MockInterviewList(await ToItemsAsync(db, mocks, p.TenantId, ct), ready, problem));
    }

    /// <summary>The organisation's questions for this application's job: those for the job and those for every job, in library order.</summary>
    private static async Task<IResult> Library(Guid applicationId, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (app, denied) = await ApplicationAsync(db, p, applicationId, ct);
        if (denied is not null) return denied;
        var definition = await db.ObjectDefinitions.AsNoTracking().FirstOrDefaultAsync(o => o.Key == MockKeys.QuestionObject && o.IsActive, ct);
        if (definition is null || !p.Can(MockKeys.QuestionObject, PermissionActions.Read))
            return TypedResults.Ok(new QuestionLibrary(definition is not null, false, []));

        var job = Cf.AsText(Cf.Parse(app!.CustomFields)["position"]);
        var records = await p.Filter(db.CustomRecords.AsNoTracking().Where(r => r.ObjectDefinitionId == definition.Id), MockKeys.QuestionObject)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Name).Take(MaxLibrary).ToListAsync(ct);
        var questions = records
            .Select(r => (Record: r, Values: Cf.Parse(r.CustomFields)))
            .Where(x => Cf.AsText(x.Values["retired"]) != "true")
            .Select(x => (x.Record, x.Values, For: Cf.AsText(x.Values["position"])))
            .Where(x => string.IsNullOrEmpty(x.For) || x.For == job)
            .Select(x => new LibraryQuestion(x.Record.Id, x.Record.Name, Cf.AsText(x.Values["competency"]), Minutes(x.Values), !string.IsNullOrEmpty(x.For)))
            .OrderByDescending(q => q.ForThisJob)
            .ToList();
        return TypedResults.Ok(new QuestionLibrary(true, p.Can(MockKeys.QuestionObject, PermissionActions.Create), questions));
    }

    private static int? Minutes(JsonObject values) =>
        decimal.TryParse(Cf.AsText(values["minutes"]), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var m)
            ? (int)Math.Clamp(Math.Round(m), 1, 30) : null;

    /// <summary>Adds the starter questions the library doesn't have yet (matched by wording), owned by the caller.</summary>
    private static async Task<IResult> AddStarterQuestions(Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var definition = await db.ObjectDefinitions.FirstOrDefaultAsync(o => o.Key == MockKeys.QuestionObject && o.IsActive, ct);
        if (definition is null) return TypedResults.NotFound();
        if (!p.Can(MockKeys.QuestionObject, PermissionActions.Create)) return TypedResults.Forbid();
        var existing = (await db.CustomRecords.Where(r => r.ObjectDefinitionId == definition.Id).Select(r => r.Name).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = StarterRecords(definition, p.UserId).Where(r => !existing.Contains(r.Name)).ToList();
        db.CustomRecords.AddRange(added);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new StarterQuestionsAdded(added.Count));
    }

    public static IEnumerable<CustomRecord> StarterRecords(ObjectDefinition definition, Guid ownerId) =>
        MockKeys.StarterQuestions.Select(q => new CustomRecord
        {
            ObjectDefinitionId = definition.Id, Name = q.Prompt, OwnerId = ownerId,
            CustomFields = new JsonObject { ["competency"] = q.Competency, ["minutes"] = q.Minutes, ["guidance"] = q.Guidance }.ToJsonString(),
        });

    // ---------- Inviting ----------

    private static async Task<IResult> Invite(MockInviteRequest req, Access access, CrmDbContext db, EmailComposer emails, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (app, denied) = await ApplicationAsync(db, p, req.ApplicationId, ct);
        if (denied is not null) return denied;
        var ids = (req.QuestionIds ?? []).Distinct().ToList();
        if (ids.Count is 0 or > MaxQuestions) return Errors.Validation("questionIds", $"Choose between 1 and {MaxQuestions} questions.");
        if (req.ValidDays is < 1 or > MaxValidDays) return Errors.Validation("validDays", $"Give the candidate between 1 and {MaxValidDays} days.");
        if (emails.PublicUrl() is not { } publicUrl) return Errors.Validation("link", "The site address (App:PublicUrl) isn't configured, so the candidate's link can't be created.");

        var records = p.Can(MockKeys.QuestionObject, PermissionActions.Read)
            ? await db.CustomRecords.AsNoTracking().Where(r => ids.Contains(r.Id) && r.ObjectDefinition!.Key == MockKeys.QuestionObject).ToListAsync(ct)
            : [];
        records = records.Where(r => p.CanAccess(MockKeys.QuestionObject, PermissionActions.Read, r.OwnerId)).ToList();
        if (records.Count != ids.Count) return Errors.Validation("questionIds", "Some of the chosen questions no longer exist. Refresh and try again.");

        var now = clock.GetUtcNow().UtcDateTime;
        var current = await db.MockInterviews.Where(m => m.ApplicationId == app!.Id && (m.Status == MockInterviewStatus.Sent || m.Status == MockInterviewStatus.InProgress)).ToListAsync(ct);
        if (current.Count(m => ExpireIfDue(m, now)) > 0) await db.SaveChangesAsync(ct);
        if (current.Any(m => IsOpen(m.Status))) return OpenExists();

        var byId = records.ToDictionary(r => r.Id);
        var questions = ids.Select((id, i) =>
        {
            var values = Cf.Parse(byId[id].CustomFields);
            return new MockQuestion($"q{i + 1}", byId[id].Name, Text(values, "details"), Text(values, "competency"), Text(values, "guidance"), Minutes(values));
        }).ToList();
        var token = NewToken();
        var mock = new MockInterview
        {
            ApplicationId = app!.Id, JobTitle = await JobTitleAsync(db, app, ct), Message = string.IsNullOrWhiteSpace(req.Message) ? null : req.Message.Trim(),
            Questions = MockJson.Serialize(questions), ExpiresAt = now.AddDays(req.ValidDays), TokenHash = OfferEndpoints.HashToken(token),
            SentById = p.UserId, SentAt = now,
        };
        db.MockInterviews.Add(mock);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with another invitation for the same application (filtered unique index).
            return OpenExists();
        }
        var link = $"{publicUrl}/mock/{token}";
        var note = await EmailCandidateAsync(db, emails, app, mock, p, link, now, ct);
        return TypedResults.Ok(new MockInviteSent(await ItemAsync(db, mock, p, ct), link, note));
    }

    private static string? Text(JsonObject values, string key) => Cf.AsText(values[key]) is { Length: > 0 } s ? s : null;

    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private static async Task<string> JobTitleAsync(CrmDbContext db, CustomRecord app, CancellationToken ct) =>
        Guid.TryParse(Cf.AsText(Cf.Parse(app.CustomFields)["position"]), out var jobId)
            ? await db.CustomRecords.Where(r => r.Id == jobId).Select(r => r.Name).FirstOrDefaultAsync(ct) ?? "the role"
            : "the role";

    private static Task<string?> EmailCandidateAsync(
        CrmDbContext db, EmailComposer emails, CustomRecord app, MockInterview mock, EffectivePermissions p, string link, DateTime now, CancellationToken ct) =>
        emails.QueueCandidateLinkAsync(db, app, ApplicationKey, p, new CandidateLinkEmail(
            MockKeys.EmailTemplate, MockKeys.DefaultEmailSubject, MockKeys.DefaultEmailBody, MockKeys.LinkKey, link,
            "Start your practice interview", MockKeys.Values(mock), "invitation"), now, ct);

    private static Microsoft.AspNetCore.Http.HttpResults.ValidationProblem OpenExists() =>
        Errors.Validation("applicationId", "This application already has an open mock interview. Withdraw it before sending another.");

    /// <summary>A new link (the old one stops working), emailed again; answers given so far are kept.</summary>
    private static async Task<IResult> Resend(Guid id, Access access, CrmDbContext db, EmailComposer emails, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (mock, app, denied) = await MockAsync(db, p, id, ct);
        if (denied is not null) return denied;
        var now = clock.GetUtcNow().UtcDateTime;
        if (ExpireIfDue(mock!, now)) await db.SaveChangesAsync(ct);
        if (!IsOpen(mock!.Status)) return Errors.Validation("status", $"This mock interview is {Describe(mock.Status)} and can't be sent again.");
        if (emails.PublicUrl() is not { } publicUrl) return Errors.Validation("link", "The site address (App:PublicUrl) isn't configured, so the candidate's link can't be created.");
        var token = NewToken();
        mock.TokenHash = OfferEndpoints.HashToken(token);
        mock.SentAt = now;
        mock.SentById = p.UserId;
        await db.SaveChangesAsync(ct);
        var link = $"{publicUrl}/mock/{token}";
        var note = await EmailCandidateAsync(db, emails, app!, mock, p, link, now, ct);
        return TypedResults.Ok(new MockInviteSent(await ItemAsync(db, mock, p, ct), link, note));
    }

    private static string Describe(MockInterviewStatus status) => status switch
    {
        MockInterviewStatus.InProgress => "in progress",
        _ => status.ToString().ToLowerInvariant(),
    };

    private static async Task<IResult> Withdraw(Guid id, WithdrawMockRequest req, Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (mock, app, denied) = await MockAsync(db, p, id, ct);
        if (denied is not null) return denied;
        var now = clock.GetUtcNow().UtcDateTime;
        ExpireIfDue(mock!, now);
        if (!IsOpen(mock!.Status)) return Errors.Validation("status", $"This mock interview is already {Describe(mock.Status)}.");
        mock.Status = MockInterviewStatus.Withdrawn;
        mock.TokenHash = null;
        db.Activities.Add(new Activity
        {
            Type = ActivityType.Note, OwnerId = p.UserId, CustomRecordId = app!.Id, CompletedAt = now, Subject = "Mock interview withdrawn",
            Description = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim(),
        });
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ItemAsync(db, mock, p, ct));
    }

    // ---------- Reviewing ----------

    private static async Task<IResult> Review(Guid id, MockReviewRequest req, Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (mock, _, denied) = await MockAsync(db, p, id, ct);
        if (denied is not null) return denied;
        if (mock!.Status != MockInterviewStatus.Submitted) return Errors.Validation("status", "You can review a mock interview once the candidate has submitted it.");
        if (req.ReadinessRating is < 1 or > 5) return Errors.Validation("readinessRating", "Rate readiness from 1 to 5.");
        var notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
        if (req.ReadinessRating is null && notes is null) return Errors.Validation("readinessRating", "Give a readiness rating or write some notes.");
        mock.ReadinessRating = req.ReadinessRating;
        mock.ReviewNotes = notes;
        mock.ReviewedById = p.UserId;
        mock.ReviewedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ItemAsync(db, mock, p, ct));
    }

    private static async Task<IResult> RequestAiReview(Guid id, Access access, CrmDbContext db, AiReviewConfig ai, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var (mock, _, denied) = await MockAsync(db, p, id, ct);
        if (denied is not null) return denied;
        if (mock!.Status != MockInterviewStatus.Submitted) return Errors.Validation("status", "AI feedback is available once the candidate has submitted their answers.");
        if (mock.AiStatus == AiReviewStatus.Pending) return Errors.Validation("aiStatus", "AI feedback has already been requested and will appear shortly.");
        var (ready, problem) = await AiStateAsync(db, ai, ct);
        if (!ready) return Errors.Validation("ai", problem!);
        Queue(mock, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await ItemAsync(db, mock, p, ct));
    }

    public static void Queue(MockInterview mock, DateTime now)
    {
        mock.AiStatus = AiReviewStatus.Pending;
        mock.AiRequestedAt = now;
        mock.AiError = null;
        mock.AiLockedUntil = null;
    }

    // ---------- Organisation settings ----------

    private static async Task<IResult> GetSettings(CrmDbContext db, AiReviewConfig ai, CancellationToken ct) =>
        TypedResults.Ok(await SettingsAsync(db, ai, ct));

    private static async Task<IResult> SaveSettings(MockSettingsRequest req, CrmDbContext db, AiReviewConfig ai, CancellationToken ct)
    {
        var tenantId = db.CurrentTenantId!.Value;
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId, ct);
        tenant.MockAiReviewEnabled = req.AiReviewEnabled;
        tenant.MockAiTrigger = req.AiTrigger;
        tenant.MockAiInstructions = string.IsNullOrWhiteSpace(req.AiInstructions) ? null : req.AiInstructions.Trim();
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await SettingsAsync(db, ai, ct));
    }

    private static async Task<MockSettings> SettingsAsync(CrmDbContext db, AiReviewConfig ai, CancellationToken ct)
    {
        var tenantId = db.CurrentTenantId!.Value;
        var t = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == tenantId, ct);
        var (connection, problem) = await ai.ResolveAsync(ct);
        var library = await db.ObjectDefinitions.AnyAsync(o => o.Key == MockKeys.QuestionObject, ct);
        return new MockSettings(t.MockAiReviewEnabled, t.MockAiTrigger, t.MockAiInstructions, connection is not null, problem, library);
    }
}
