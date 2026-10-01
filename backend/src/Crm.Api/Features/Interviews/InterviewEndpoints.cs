using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Crm.Api.Automation;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Messaging;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Crm.Api.Features.Interviews;

public record InterviewRequest(
    Guid ApplicationId,
    DateTime ScheduledAt,
    int DurationMinutes,
    InterviewKind Kind,
    [MaxLength(500)] string? Location,
    [MaxLength(2000)] string? Notes,
    Guid[] InterviewerIds,
    bool EmailCandidate = false);

public record ScorecardRequest(Recommendation Recommendation, Dictionary<string, int>? Ratings, [MaxLength(4000)] string? Notes);

public record InterviewerItem(Guid Id, string Name, bool Submitted);

public record ScorecardItem(Guid Id, Guid InterviewerId, string InterviewerName, Recommendation Recommendation,
    Dictionary<string, int> Ratings, string? Notes, DateTime SubmittedAt);

/// <param name="Scorecards">Only those the caller may see: all once they've submitted theirs (or if they aren't an interviewer and can edit the application).</param>
public record InterviewItem(
    Guid Id, Guid ApplicationId, string ApplicationName, string? JobTitle, DateTime ScheduledAt, int DurationMinutes, InterviewKind Kind,
    string? Location, string? Notes, InterviewStatus Status, string OrganizerName, IReadOnlyList<InterviewerItem> Interviewers,
    bool IAmInterviewer, ScorecardItem? MyScorecard, IReadOnlyList<ScorecardItem> Scorecards, bool CanEdit);

public record InterviewSaved(InterviewItem Interview, string? EmailNote);

public record MyInterviews(IReadOnlyList<InterviewItem> Upcoming, IReadOnlyList<InterviewItem> AwaitingFeedback);

/// <summary>
/// Interviews and scorecards for applications. Seeing an application's interviews needs read access to it; scheduling and
/// changing them needs update access. Interviewers record feedback on their own interviews.
/// </summary>
public static class InterviewEndpoints
{
    public const string ApplicationKey = "application";
    public const string CriteriaPicklist = "interview.criteria";
    public const int MaxInterviewers = 10;

    public static RouteGroupBuilder MapInterviews(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/interviews").WithTags("Interviews");
        g.MapGet("/", List);
        g.MapGet("/mine", Mine);
        g.MapPost("/", Create);
        g.MapPut("/{id:guid}", Update);
        g.MapPost("/{id:guid}/cancel", Cancel);
        g.MapPost("/{id:guid}/complete", Complete);
        g.MapPut("/{id:guid}/scorecard", SubmitScorecard);
        g.MapGet("/{id:guid}/ics", Calendar);
        return api;
    }

    // ---------- Access ----------

    private static Task<CustomRecord?> ApplicationAsync(CrmDbContext db, Guid id, CancellationToken ct) =>
        db.CustomRecords.FirstOrDefaultAsync(r => r.Id == id && r.ObjectDefinition!.Key == ApplicationKey, ct);

    private static bool Can(EffectivePermissions p, CustomRecord app, string action) => p.CanAccess(ApplicationKey, action, app.OwnerId);

    // ---------- Reading ----------

    private static async Task<Results<Ok<List<InterviewItem>>, NotFound>> List(Guid applicationId, Access access, CrmDbContext db, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var app = await ApplicationAsync(db, applicationId, ct);
        if (app is null || !Can(p, app, PermissionActions.Read)) return TypedResults.NotFound();
        var interviews = await Load(db).Where(i => i.ApplicationId == applicationId).OrderByDescending(i => i.ScheduledAt).ToListAsync(ct);
        return TypedResults.Ok(await ToItemsAsync(db, interviews, p, ct));
    }

    private static async Task<Ok<MyInterviews>> Mine(Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var mine = await Load(db)
            .Where(i => i.Status != InterviewStatus.Cancelled && i.Participants.Any(x => x.UserId == p.UserId))
            .Where(i => i.ScheduledAt >= now.AddHours(-1) || !i.Scorecards.Any(s => s.InterviewerId == p.UserId))
            .OrderBy(i => i.ScheduledAt).Take(200).ToListAsync(ct);
        var apps = await AppsAsync(db, mine, ct);
        mine = mine.Where(i => apps.TryGetValue(i.ApplicationId, out var a) && Can(p, a, PermissionActions.Read)).ToList();
        var items = await ToItemsAsync(db, mine, p, ct);
        return TypedResults.Ok(new MyInterviews(
            items.Where(i => i.Status == InterviewStatus.Scheduled && i.ScheduledAt >= now.AddHours(-1)).ToList(),
            items.Where(i => i.ScheduledAt < now && i.MyScorecard is null).OrderByDescending(i => i.ScheduledAt).ToList()));
    }

    private static IQueryable<Interview> Load(CrmDbContext db) =>
        db.Interviews.AsNoTracking().Include(i => i.Participants).ThenInclude(x => x.User).Include(i => i.Scorecards);

    private static async Task<Dictionary<Guid, CustomRecord>> AppsAsync(CrmDbContext db, IEnumerable<Interview> interviews, CancellationToken ct)
    {
        var ids = interviews.Select(i => i.ApplicationId).Distinct().ToList();
        return await db.CustomRecords.AsNoTracking().Where(r => ids.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
    }

    private static async Task<List<InterviewItem>> ToItemsAsync(CrmDbContext db, List<Interview> interviews, EffectivePermissions p, CancellationToken ct)
    {
        var apps = await AppsAsync(db, interviews, ct);
        var jobIds = apps.Values.Select(a => Guid.TryParse(Cf.AsText(Cf.Parse(a.CustomFields)["position"]), out var j) ? j : Guid.Empty)
            .Where(j => j != Guid.Empty).Distinct().ToList();
        var jobs = await db.CustomRecords.AsNoTracking().Where(r => jobIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        var userIds = interviews.Select(i => i.OrganizerId).Concat(interviews.SelectMany(i => i.Scorecards.Select(s => s.InterviewerId))).Distinct().ToList();
        var tenantId = p.TenantId;
        var names = await db.Users.Where(u => userIds.Contains(u.Id) && u.TenantId == tenantId)
            .ToDictionaryAsync(u => u.Id, u => u.FirstName + " " + u.LastName, ct);

        return interviews.Select(i =>
        {
            var app = apps.GetValueOrDefault(i.ApplicationId);
            var job = app is not null && Guid.TryParse(Cf.AsText(Cf.Parse(app.CustomFields)["position"]), out var jobId) ? jobs.GetValueOrDefault(jobId) : null;
            var mine = i.Scorecards.FirstOrDefault(s => s.InterviewerId == p.UserId);
            var isInterviewer = i.Participants.Any(x => x.UserId == p.UserId);
            var canEdit = app is not null && Can(p, app, PermissionActions.Update);
            // Blind feedback: interviewers see colleagues' scorecards only after submitting their own.
            var seeAll = isInterviewer ? mine is not null : canEdit;
            ScorecardItem Card(Scorecard s) => new(s.Id, s.InterviewerId, names.GetValueOrDefault(s.InterviewerId, ""), s.Recommendation,
                RatingsOf(s.Ratings), s.Notes, s.SubmittedAt);
            return new InterviewItem(
                i.Id, i.ApplicationId, app?.Name ?? "", job, i.ScheduledAt, i.DurationMinutes, i.Kind, i.Location, i.Notes, i.Status,
                names.GetValueOrDefault(i.OrganizerId, ""),
                i.Participants.Select(x => new InterviewerItem(x.UserId, x.User is null ? "" : $"{x.User.FirstName} {x.User.LastName}".Trim(),
                    i.Scorecards.Any(s => s.InterviewerId == x.UserId))).OrderBy(x => x.Name).ToList(),
                isInterviewer, mine is null ? null : Card(mine),
                seeAll ? i.Scorecards.OrderBy(s => s.SubmittedAt).Select(Card).ToList() : [], canEdit);
        }).ToList();
    }

    private static Dictionary<string, int> RatingsOf(string json) =>
        Cf.Parse(json).Where(kv => kv.Value is JsonValue v && v.TryGetValue<int>(out _)).ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<int>());

    // ---------- Scheduling ----------

    private static async Task<IResult> Create(
        InterviewRequest req, Access access, CrmDbContext db, EmailComposer emails, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var app = await ApplicationAsync(db, req.ApplicationId, ct);
        if (app is null || !Can(p, app, PermissionActions.Read)) return TypedResults.NotFound();
        if (!Can(p, app, PermissionActions.Update)) return TypedResults.Forbid();
        var now = clock.GetUtcNow().UtcDateTime;
        if (await ValidateAsync(req, db, p, now, ct) is { } problem) return problem;

        var interview = new Interview { ApplicationId = app.Id, OrganizerId = p.UserId };
        Apply(interview, req);
        db.Interviews.Add(interview);
        var job = await JobTitleAsync(db, app, ct);
        foreach (var userId in req.InterviewerIds.Distinct())
            AddInterviewer(db, interview, app, job, userId, p.UserId, now);
        await SyncNextInterviewAsync(db, app, now, interview, ct);
        await db.SaveChangesAsync(ct);

        var emailNote = req.EmailCandidate ? await EmailCandidateAsync(db, emails, app, p, now, ct) : null;
        var saved = await Load(db).SingleAsync(i => i.Id == interview.Id, ct);
        return TypedResults.Created($"/api/interviews/{interview.Id}", new InterviewSaved((await ToItemsAsync(db, [saved], p, ct))[0], emailNote));
    }

    private static async Task<IResult> Update(
        Guid id, InterviewRequest req, Access access, CrmDbContext db, EmailComposer emails, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var interview = await db.Interviews.Include(i => i.Participants).SingleOrDefaultAsync(i => i.Id == id, ct);
        var app = interview is null ? null : await ApplicationAsync(db, interview.ApplicationId, ct);
        if (interview is null || app is null || !Can(p, app, PermissionActions.Read)) return TypedResults.NotFound();
        if (!Can(p, app, PermissionActions.Update)) return TypedResults.Forbid();
        if (interview.Status == InterviewStatus.Cancelled) return Errors.Validation("status", "This interview was cancelled. Schedule a new one instead.");
        if (req.ApplicationId != interview.ApplicationId) return Errors.Validation("applicationId", "An interview can't move to another application.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (await ValidateAsync(req, db, p, now, ct) is { } problem) return problem;

        var moved = interview.ScheduledAt != req.ScheduledAt || interview.DurationMinutes != req.DurationMinutes;
        Apply(interview, req);
        var job = await JobTitleAsync(db, app, ct);
        var wanted = req.InterviewerIds.Distinct().ToHashSet();
        foreach (var removed in interview.Participants.Where(x => !wanted.Contains(x.UserId)).ToList())
        {
            await RemoveActivityAsync(db, removed, ct);
            interview.Participants.Remove(removed);
            Notify(db, removed.UserId, p.UserId, "Removed from interview", $"You're no longer interviewing {app.Name}.", app, now);
        }
        foreach (var participant in interview.Participants)
        {
            if (participant.ActivityId is { } activityId && await db.Activities.FirstOrDefaultAsync(a => a.Id == activityId, ct) is { } activity)
                Describe(activity, interview, app, job);
            if (moved) Notify(db, participant.UserId, p.UserId, "Interview rescheduled",
                $"Interview with {app.Name} moved to {interview.ScheduledAt:d MMM yyyy HH:mm} UTC.", app, now);
        }
        foreach (var userId in wanted.Where(u => interview.Participants.All(x => x.UserId != u)))
            AddInterviewer(db, interview, app, job, userId, p.UserId, now);
        await SyncNextInterviewAsync(db, app, now, interview, ct);
        await db.SaveChangesAsync(ct);

        var emailNote = req.EmailCandidate ? await EmailCandidateAsync(db, emails, app, p, now, ct) : null;
        var saved = await Load(db).SingleAsync(i => i.Id == interview.Id, ct);
        return TypedResults.Ok(new InterviewSaved((await ToItemsAsync(db, [saved], p, ct))[0], emailNote));
    }

    private static async Task<IResult> Cancel(Guid id, Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct) =>
        await ChangeStatusAsync(id, InterviewStatus.Cancelled, access, db, clock, ct);

    private static async Task<IResult> Complete(Guid id, Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct) =>
        await ChangeStatusAsync(id, InterviewStatus.Completed, access, db, clock, ct);

    private static async Task<IResult> ChangeStatusAsync(
        Guid id, InterviewStatus status, Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var interview = await db.Interviews.Include(i => i.Participants).SingleOrDefaultAsync(i => i.Id == id, ct);
        var app = interview is null ? null : await ApplicationAsync(db, interview.ApplicationId, ct);
        if (interview is null || app is null || !Can(p, app, PermissionActions.Read)) return TypedResults.NotFound();
        if (!Can(p, app, PermissionActions.Update)) return TypedResults.Forbid();
        if (interview.Status != InterviewStatus.Scheduled) return Errors.Validation("status", $"This interview is already {interview.Status.ToString().ToLowerInvariant()}.");

        var now = clock.GetUtcNow().UtcDateTime;
        interview.Status = status;
        foreach (var x in interview.Participants)
        {
            if (status == InterviewStatus.Cancelled)
            {
                await RemoveActivityAsync(db, x, ct);
                Notify(db, x.UserId, p.UserId, "Interview cancelled", $"The interview with {app.Name} on {interview.ScheduledAt:d MMM yyyy HH:mm} UTC was cancelled.", app, now);
            }
        }
        await SyncNextInterviewAsync(db, app, now, interview, ct);
        await db.SaveChangesAsync(ct);
        var saved = await Load(db).SingleAsync(i => i.Id == interview.Id, ct);
        return TypedResults.Ok((await ToItemsAsync(db, [saved], p, ct))[0]);
    }

    private static async Task<ValidationProblem?> ValidateAsync(InterviewRequest req, CrmDbContext db, EffectivePermissions p, DateTime now, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var at = DateTime.SpecifyKind(req.ScheduledAt.Kind == DateTimeKind.Local ? req.ScheduledAt.ToUniversalTime() : req.ScheduledAt, DateTimeKind.Utc);
        if (at < now.AddDays(-30) || at > now.AddDays(365)) errors["scheduledAt"] = ["Choose a time between 30 days ago and a year from now."];
        if (req.DurationMinutes is < 15 or > 480) errors["durationMinutes"] = ["Interviews last between 15 minutes and 8 hours."];
        var ids = req.InterviewerIds.Distinct().ToList();
        if (ids.Count == 0) errors["interviewerIds"] = ["Add at least one interviewer."];
        else if (ids.Count > MaxInterviewers) errors["interviewerIds"] = [$"An interview can have at most {MaxInterviewers} interviewers."];
        else
        {
            var tenantId = p.TenantId;
            var active = await db.Users.CountAsync(u => ids.Contains(u.Id) && u.TenantId == tenantId && u.IsActive, ct);
            if (active != ids.Count) errors["interviewerIds"] = ["Interviewers must be active users in your organisation."];
        }
        if (req.Kind == InterviewKind.Video && !string.IsNullOrWhiteSpace(req.Location) &&
            !(Uri.TryCreate(req.Location.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
            errors["location"] = ["For a video interview, enter the meeting link (https://…)."];
        return errors.Count > 0 ? Errors.Validation(errors) : null;
    }

    private static void Apply(Interview interview, InterviewRequest req)
    {
        interview.ScheduledAt = DateTime.SpecifyKind(req.ScheduledAt.Kind == DateTimeKind.Local ? req.ScheduledAt.ToUniversalTime() : req.ScheduledAt, DateTimeKind.Utc);
        interview.DurationMinutes = req.DurationMinutes;
        interview.Kind = req.Kind;
        interview.Location = string.IsNullOrWhiteSpace(req.Location) ? null : req.Location.Trim();
        interview.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
    }

    /// <summary>Adds an interviewer with a Meeting on their timeline (and dashboard) and a notification.</summary>
    private static void AddInterviewer(CrmDbContext db, Interview interview, CustomRecord app, string? job, Guid userId, Guid organizerId, DateTime now)
    {
        var activity = new Activity { Type = ActivityType.Meeting, OwnerId = userId, CustomRecordId = app.Id, Priority = ActivityPriority.High };
        Describe(activity, interview, app, job);
        db.Activities.Add(activity);
        interview.Participants.Add(new InterviewParticipant { UserId = userId, ActivityId = activity.Id });
        Notify(db, userId, organizerId, "Interview scheduled",
            $"You're interviewing {app.Name}{(job is null ? "" : $" for {job}")} on {interview.ScheduledAt:d MMM yyyy HH:mm} UTC.", app, now);
    }

    private static void Describe(Activity activity, Interview interview, CustomRecord app, string? job)
    {
        activity.Subject = Truncate($"{interview.Kind} interview: {app.Name}{(job is null ? "" : $" ({job})")}", 200);
        activity.DueAt = interview.ScheduledAt;
        activity.Description = Truncate(string.Join('\n', new[]
        {
            $"{interview.DurationMinutes} minutes, {interview.Kind.ToString().ToLowerInvariant()}",
            interview.Location is null ? null : $"Where: {interview.Location}",
            interview.Notes,
            "Add your scorecard from the application's Interviews panel or My interviews.",
        }.Where(l => l is not null)), 4000);
    }

    private static async Task RemoveActivityAsync(CrmDbContext db, InterviewParticipant participant, CancellationToken ct)
    {
        if (participant.ActivityId is { } activityId && await db.Activities.FirstOrDefaultAsync(a => a.Id == activityId, ct) is { } activity)
            db.Activities.Remove(activity);
        participant.ActivityId = null;
    }

    private static void Notify(CrmDbContext db, Guid userId, Guid actorId, string title, string message, CustomRecord app, DateTime now)
    {
        if (userId == actorId) return;
        db.Notifications.Add(new Notification { UserId = userId, Title = title, Message = Truncate(message, 1000), Link = $"/o/{ApplicationKey}/{app.Id}", CreatedAt = now });
    }

    /// <summary>Keeps the application's "Next interview" field on its next scheduled interview, so date-based reminders follow it.</summary>
    private static async Task SyncNextInterviewAsync(CrmDbContext db, CustomRecord app, DateTime now, Interview changed, CancellationToken ct)
    {
        if (!await db.FieldDefinitions.AnyAsync(f => f.ObjectKey == ApplicationKey && f.Key == "interview_date" && f.Type == FieldType.DateTime, ct)) return;
        var others = await db.Interviews.Where(i => i.ApplicationId == app.Id && i.Id != changed.Id && i.Status == InterviewStatus.Scheduled && i.ScheduledAt >= now)
            .Select(i => i.ScheduledAt).ToListAsync(ct);
        if (changed.Status == InterviewStatus.Scheduled && changed.ScheduledAt >= now) others.Add(changed.ScheduledAt);
        var values = Cf.Parse(app.CustomFields);
        if (others.Count == 0) values.Remove("interview_date");
        else values["interview_date"] = others.Min().ToString("o", CultureInfo.InvariantCulture);
        var json = values.ToJsonString();
        if (json != app.CustomFields) app.CustomFields = json;
    }

    private static async Task<string?> JobTitleAsync(CrmDbContext db, CustomRecord app, CancellationToken ct) =>
        Guid.TryParse(Cf.AsText(Cf.Parse(app.CustomFields)["position"]), out var jobId)
            ? await db.CustomRecords.Where(r => r.Id == jobId).Select(r => r.Name).FirstOrDefaultAsync(ct)
            : null;

    /// <summary>Sends the "Interview invitation" template (or a plain invitation) to the candidate via the outbox.</summary>
    private static async Task<string?> EmailCandidateAsync(CrmDbContext db, EmailComposer emails, CustomRecord app, EffectivePermissions p, DateTime now, CancellationToken ct)
    {
        var fields = await emails.FieldsAsync(db, ApplicationKey, ct);
        if (fields is null) return "The candidate wasn't emailed: email isn't available for applications.";
        var fresh = await db.CustomRecords.AsNoTracking().SingleAsync(r => r.Id == app.Id, ct);
        var context = await emails.ContextAsync(db, fresh, ApplicationKey, p, ct);
        var to = EmailComposer.RecipientsOf(fields, context).FirstOrDefault()?.Address;
        if (to is null) return "The candidate wasn't emailed: they have no email address.";
        if (await emails.DailyLimitReachedAsync(db, now, ct)) return "The candidate wasn't emailed: your organisation's daily email limit was reached.";
        var template = await db.EmailTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.ObjectKey == ApplicationKey && t.Name == "Interview invitation", ct);
        var subject = template?.Subject ?? "Interview for {{cf.position.name}}";
        var body = template?.Body ?? "Hi {{name}},\n\nWe'd like to invite you to an interview on {{cf.interview_date}}.\n\nKind regards,\n{{owner.name}}";
        var sender = await db.Users.AsNoTracking().Where(u => u.Id == p.UserId).Select(u => new { u.Email, u.FirstName, u.LastName }).SingleAsync(ct);
        await emails.QueueAsync(db, new QueueEmail(fresh, ApplicationKey, to, EmailComposer.Render(subject, context), EmailComposer.Render(body, context),
            TemplateId: template?.Id, SentById: p.UserId, SentByName: $"{sender.FirstName} {sender.LastName}".Trim(),
            ReplyTo: EmailText.Address(sender.Email)), now, ct);
        await db.SaveChangesAsync(ct);
        return null;
    }

    // ---------- Scorecards ----------

    private static async Task<IResult> SubmitScorecard(
        Guid id, ScorecardRequest req, Access access, CrmDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var interview = await db.Interviews.Include(i => i.Participants).SingleOrDefaultAsync(i => i.Id == id, ct);
        var app = interview is null ? null : await ApplicationAsync(db, interview.ApplicationId, ct);
        if (interview is null || app is null || !Can(p, app, PermissionActions.Read)) return TypedResults.NotFound();
        var me = interview.Participants.FirstOrDefault(x => x.UserId == p.UserId);
        if (me is null) return TypedResults.Forbid();
        if (interview.Status == InterviewStatus.Cancelled) return Errors.Validation("status", "This interview was cancelled.");

        var criteria = Cf.ParseStrings(await db.Picklists.Where(x => x.Key == CriteriaPicklist).Select(x => x.Options).FirstOrDefaultAsync(ct));
        var ratings = req.Ratings ?? [];
        if (ratings.Values.Any(v => v is < 1 or > 5)) return Errors.Validation("ratings", "Ratings go from 1 to 5.");
        if (criteria.Length > 0 && ratings.Keys.FirstOrDefault(k => !criteria.Contains(k)) is { } unknown)
            return Errors.Validation("ratings", $"'{unknown}' isn't one of the interview criteria ({string.Join(", ", criteria)}).");

        var now = clock.GetUtcNow().UtcDateTime;
        var card = await db.Scorecards.SingleOrDefaultAsync(s => s.InterviewId == id && s.InterviewerId == p.UserId, ct);
        var first = card is null;
        if (card is null) db.Scorecards.Add(card = new Scorecard { InterviewId = id, InterviewerId = p.UserId });
        card.Recommendation = req.Recommendation;
        card.Ratings = new JsonObject(ratings.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))).ToJsonString();
        card.Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
        card.SubmittedAt = now;
        if (me.ActivityId is { } activityId && await db.Activities.FirstOrDefaultAsync(a => a.Id == activityId, ct) is { CompletedAt: null } activity)
            activity.CompletedAt = now;
        if (first)
            Notify(db, interview.OrganizerId, p.UserId, "Interview feedback",
                $"{db.TenantContext.UserName ?? "An interviewer"} recommends {Label(req.Recommendation)} for {app.Name}.", app, now);
        await db.SaveChangesAsync(ct);
        var saved = await Load(db).SingleAsync(i => i.Id == id, ct);
        return TypedResults.Ok((await ToItemsAsync(db, [saved], p, ct))[0]);
    }

    public static string Label(Recommendation r) => r switch
    {
        Recommendation.StrongYes => "a strong yes",
        Recommendation.Yes => "yes",
        Recommendation.No => "no",
        _ => "a strong no",
    };

    // ---------- Calendar ----------

    /// <summary>An iCalendar (.ics) file for the interview, so interviewers can add it to Outlook or Google Calendar.</summary>
    private static async Task<IResult> Calendar(Guid id, Access access, CrmDbContext db, TimeProvider clock, IConfiguration config, CancellationToken ct)
    {
        var p = await access.GetAsync(ct);
        var interview = await db.Interviews.AsNoTracking().Include(i => i.Participants).SingleOrDefaultAsync(i => i.Id == id, ct);
        var app = interview is null ? null : await ApplicationAsync(db, interview.ApplicationId, ct);
        if (interview is null || app is null || !Can(p, app, PermissionActions.Read)) return TypedResults.NotFound();
        var job = await JobTitleAsync(db, app, ct);
        var link = config["App:PublicUrl"]?.TrimEnd('/') is { Length: > 0 } url ? $"\n{url}/o/{ApplicationKey}/{app.Id}" : "";
        var ics = Ics(interview, $"{interview.Kind} interview: {app.Name}{(job is null ? "" : $" ({job})")}",
            (interview.Notes ?? "") + link, clock.GetUtcNow().UtcDateTime);
        return TypedResults.File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8", $"interview-{interview.ScheduledAt:yyyyMMdd-HHmm}.ics");
    }

    public static string Ics(Interview i, string summary, string description, DateTime now)
    {
        static string Stamp(DateTime d) => d.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//CRM Platform//Interviews//EN", "METHOD:PUBLISH", "BEGIN:VEVENT",
            $"UID:{i.Id}@crm-platform", $"DTSTAMP:{Stamp(now)}", $"DTSTART:{Stamp(i.ScheduledAt)}",
            $"DTEND:{Stamp(i.ScheduledAt.AddMinutes(i.DurationMinutes))}", $"SUMMARY:{Escape(summary)}",
            $"STATUS:{(i.Status == InterviewStatus.Cancelled ? "CANCELLED" : "CONFIRMED")}",
        };
        if (i.Location is not null) lines.Add($"LOCATION:{Escape(i.Location)}");
        if (description.Trim().Length > 0) lines.Add($"DESCRIPTION:{Escape(description.Trim())}");
        lines.AddRange(["END:VEVENT", "END:VCALENDAR"]);
        return string.Concat(lines.Select(l => Fold(l) + "\r\n"));
    }

    /// <summary>RFC 5545 text escaping; line breaks and control characters can't break out of the property.</summary>
    private static string Escape(string text) =>
        new string(text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\n").Replace("\n", "\\n")
            .Where(c => !char.IsControl(c)).ToArray());

    /// <summary>RFC 5545 lines are at most 75 octets; longer ones continue on lines starting with a space.</summary>
    private static string Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= 75) return line;
        var result = new StringBuilder();
        var octets = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (octets + rune.Utf8SequenceLength > 75)
            {
                result.Append("\r\n ");
                octets = 1;
            }
            result.Append(rune.ToString());
            octets += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
