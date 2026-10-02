using System.ComponentModel.DataAnnotations;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Features.Offers;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.MockInterviews;

/// <param name="Guidance">The model answer and tips; only revealed once the candidate has answered the question.</param>
public record PublicMockQuestion(string Id, string Prompt, string? Details, string? Competency, int? Minutes, string? Answer, int? Rating, string? Guidance);

/// <summary>What the candidate sees. Questions are hidden once the link has expired or the session was withdrawn.</summary>
public record PublicMockInterview(
    string Organisation, string CandidateName, string JobTitle, string? Message, MockInterviewStatus Status, DateTime ExpiresAt,
    DateTime? SubmittedAt, IReadOnlyList<PublicMockQuestion> Questions);

public record MockAnswerRequest([MaxLength(MockResponseEndpoints.MaxAnswer)] string? Text, int? Rating, int? Seconds);

public record MockAnswerSaved(string? Guidance, int Answered, int Total);

public record MockSubmitted(string Message);

/// <summary>
/// Anonymous endpoints behind the candidate's practice link. The token is the only credential: it enters the session's tenant
/// and allows nothing but reading and answering that one session. Nothing the hiring team writes about it is ever returned.
/// </summary>
public static class MockResponseEndpoints
{
    public const string RateLimitPolicy = "mock-response";
    public const string Actor = "Candidate";
    public const int MaxAnswer = 5000;

    public static RouteGroupBuilder MapMockResponses(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/mock-response/{token}").WithTags("Mock interviews").AllowAnonymous().RequireRateLimiting(RateLimitPolicy);
        g.MapGet("/", Get);
        // Bearer-only API (no cookies), so cross-site request forgery doesn't apply.
        g.MapPut("/answers/{questionId}", Answer).DisableAntiforgery();
        g.MapPost("/submit", Submit).DisableAntiforgery();
        return api;
    }

    private sealed record Entry(MockInterview Mock, CustomRecord Application, Tenant Organisation);

    private static async Task<Entry?> EnterAsync(string token, CrmDbContext db, TenantContext tenant, CancellationToken ct)
    {
        if (token.Length is < 32 or > 64) return null;
        var hash = OfferEndpoints.HashToken(token);
        var mock = await db.MockInterviews.IgnoreQueryFilters().FirstOrDefaultAsync(m => m.TokenHash == hash, ct);
        if (mock is null) return null;
        var org = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == mock.TenantId && t.Status == TenantStatus.Active, ct);
        if (org is null) return null;
        tenant.Set(org.Id, userName: Actor);
        var app = await db.CustomRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == mock.ApplicationId, ct);
        return app is null ? null : new Entry(mock, app, org);
    }

    private static PublicMockInterview ToPublic(Entry e, DateTime now)
    {
        var m = e.Mock;
        var visible = m.Status is not (MockInterviewStatus.Expired or MockInterviewStatus.Withdrawn) && m.ExpiresAt > now;
        var answers = MockJson.Answers(m);
        var questions = visible
            ? MockJson.Questions(m).Select(q =>
            {
                var a = answers.GetValueOrDefault(q.Id);
                return new PublicMockQuestion(q.Id, q.Prompt, q.Details, q.Competency, q.Minutes, a?.Text, a?.Rating, a is null ? null : q.Guidance);
            }).ToList()
            : [];
        return new PublicMockInterview(e.Organisation.Name, e.Application.Name, m.JobTitle, visible ? m.Message : null, m.Status, m.ExpiresAt,
            m.SubmittedAt, questions);
    }

    private static async Task<IResult> Get(string token, CrmDbContext db, TenantContext tenant, TimeProvider clock, CancellationToken ct)
    {
        if (await EnterAsync(token, db, tenant, ct) is not { } e) return TypedResults.NotFound();
        var now = clock.GetUtcNow().UtcDateTime;
        if (MockInterviewEndpoints.ExpireIfDue(e.Mock, now)) await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToPublic(e, now));
    }

    private static async Task<IResult?> NotOpenAsync(Entry e, CrmDbContext db, DateTime now, CancellationToken ct)
    {
        if (MockInterviewEndpoints.ExpireIfDue(e.Mock, now)) await db.SaveChangesAsync(ct);
        return e.Mock.Status switch
        {
            MockInterviewStatus.Sent or MockInterviewStatus.InProgress => null,
            MockInterviewStatus.Submitted => Errors.Validation("status", "You've already submitted this practice interview."),
            _ => Errors.Validation("status", "This practice interview has expired. Please contact the team if you'd like a new link."),
        };
    }

    /// <summary>Saves (or, with blank text, clears) one answer; the first answer starts the session. Returns the question's tips.</summary>
    private static async Task<IResult> Answer(
        string token, string questionId, MockAnswerRequest req, CrmDbContext db, TenantContext tenant, TimeProvider clock, CancellationToken ct)
    {
        if (await EnterAsync(token, db, tenant, ct) is not { } e) return TypedResults.NotFound();
        var now = clock.GetUtcNow().UtcDateTime;
        if (await NotOpenAsync(e, db, now, ct) is { } closed) return closed;
        var questions = MockJson.Questions(e.Mock);
        var question = questions.FirstOrDefault(q => q.Id == questionId);
        if (question is null) return TypedResults.NotFound();
        if (req.Rating is < 1 or > 5) return Errors.Validation("rating", "Rate your confidence from 1 to 5.");

        var answers = MockJson.Answers(e.Mock);
        var text = req.Text?.Trim() ?? "";
        if (text.Length == 0) answers.Remove(questionId);
        else answers[questionId] = new MockAnswer(text, req.Rating, req.Seconds is { } s ? Math.Clamp(s, 0, 7200) : null, now);
        e.Mock.Answers = MockJson.Serialize(answers);
        if (e.Mock.Status == MockInterviewStatus.Sent)
        {
            e.Mock.Status = MockInterviewStatus.InProgress;
            e.Mock.StartedAt = now;
        }
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new MockAnswerSaved(text.Length == 0 ? null : question.Guidance, questions.Count(q => answers.ContainsKey(q.Id)), questions.Count));
    }

    private static async Task<IResult> Submit(
        string token, CrmDbContext db, TenantContext tenant, AiReviewConfig ai, TimeProvider clock, ILoggerFactory loggers, CancellationToken ct)
    {
        if (await EnterAsync(token, db, tenant, ct) is not { } e) return TypedResults.NotFound();
        var now = clock.GetUtcNow().UtcDateTime;
        if (await NotOpenAsync(e, db, now, ct) is { } closed) return closed;
        var questions = MockJson.Questions(e.Mock);
        var answers = MockJson.Answers(e.Mock);
        var answered = questions.Count(q => answers.ContainsKey(q.Id));
        if (answered == 0) return Errors.Validation("answers", "Answer at least one question before you submit.");

        var m = e.Mock;
        m.Status = MockInterviewStatus.Submitted;
        m.SubmittedAt = now;
        m.StartedAt ??= now;
        if (e.Organisation is { MockAiReviewEnabled: true, MockAiTrigger: AiReviewTrigger.OnSubmit } && (await ai.ResolveAsync(ct)).Connection is not null)
            MockInterviewEndpoints.Queue(m, now);

        var minutes = Math.Max(1, (int)Math.Ceiling((now - m.StartedAt.Value).TotalMinutes));
        var ratings = answers.Values.Where(a => a.Rating is not null).Select(a => a.Rating!.Value).ToList();
        var details = $"Answered {answered} of {questions.Count} questions in {minutes} minute{(minutes == 1 ? "" : "s")}." +
            (ratings.Count == 0 ? "" : $" Average self-rated confidence {ratings.Average():0.#}/5.");
        db.Activities.Add(new Activity
        {
            Type = ActivityType.Note, OwnerId = e.Application.OwnerId, CustomRecordId = e.Application.Id, CompletedAt = now,
            Subject = "Mock interview completed", Description = details,
        });
        foreach (var userId in new[] { m.SentById, e.Application.OwnerId }.Distinct())
            db.Notifications.Add(new Notification
            {
                UserId = userId, Title = "Mock interview completed",
                Message = $"{e.Application.Name} completed the mock interview for {m.JobTitle}. Review their answers on the application.",
                Link = $"/o/{MockKeys.ApplicationKey}/{e.Application.Id}", CreatedAt = now,
            });
        await db.SaveChangesAsync(ct);
        loggers.CreateLogger("Crm.Api.MockInterviews").LogInformation("Mock interview {MockId} submitted in tenant {Slug}", m.Id, e.Organisation.Slug);
        return TypedResults.Ok(new MockSubmitted("Thank you. Your answers have been sent to the team. You can come back to this page to review the tips until the link expires."));
    }
}
