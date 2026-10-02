using System.Globalization;
using System.Text.Json;
using Crm.Api.Automation;
using Crm.Api.Domain;

namespace Crm.Api.Features.MockInterviews;

/// <summary>A question as sent to the candidate; ids are short (q1, q2…) so no internal ids leave the organisation.</summary>
public sealed record MockQuestion(string Id, string Prompt, string? Details, string? Competency, string? Guidance, int? Minutes);

public sealed record MockAnswer(string Text, int? Rating, int? Seconds, DateTime AnsweredAt);

public static class MockJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static List<MockQuestion> Questions(MockInterview m) => Read<List<MockQuestion>>(m.Questions) ?? [];

    public static Dictionary<string, MockAnswer> Answers(MockInterview m) => Read<Dictionary<string, MockAnswer>>(m.Answers) ?? [];

    public static AiFeedback? Feedback(MockInterview m) => m.AiFeedback is null ? null : Read<AiFeedback>(m.AiFeedback);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static T? Read<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return default; }
    }
}

/// <summary>Mock interview placeholders, the invitation template and the starter question library.</summary>
public static class MockKeys
{
    public const string ApplicationKey = "application";
    public const string QuestionObject = "mock_question";
    public const string EmailTemplate = "Mock interview invitation";
    public const string LinkKey = "mock.link";

    public static readonly EmailField[] Placeholders =
    [
        new("mock.job_title", "Mock interview → Job title"),
        new("mock.questions", "Mock interview → Number of questions"),
        new("mock.expires", "Mock interview → Complete by"),
        new(LinkKey, "Mock interview → Link (invitation email only)"),
    ];

    public static IEnumerable<KeyValuePair<string, object?>> Values(MockInterview? mock)
    {
        yield return new("mock.job_title", mock?.JobTitle ?? "");
        yield return new("mock.questions", mock is null ? "" : MockJson.Questions(mock).Count.ToString(CultureInfo.InvariantCulture));
        yield return new("mock.expires", mock is null ? "" : mock.ExpiresAt.ToString("d MMMM yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC");
        yield return new(LinkKey, "");
    }

    public const string DefaultEmailSubject = "Practice interview for the {{mock.job_title}} role";

    public const string DefaultEmailBody =
        "Hi {{name}},\n\nTo help you prepare for your interview for the {{mock.job_title}} role, we've put together a short practice " +
        "interview with {{mock.questions}} questions. Take it in your own time; after each answer you'll see tips on what a strong answer covers.\n\n" +
        "{{mock.link}}\n\nThe link works until {{mock.expires}}. Your answers are shared with the hiring team to help them prepare for your interview." +
        "\n\nGood luck!\n{{owner.name}}\n{{organisation.name}}";

    public static readonly string[] Competencies =
        ["Communication", "Problem solving", "Teamwork", "Leadership", "Technical skills", "Planning & organisation", "Motivation", "Self-awareness"];

    public static readonly (string Prompt, string Competency, int Minutes, string Guidance)[] StarterQuestions =
    [
        ("Tell us about yourself and why you're interested in this role.", "Communication", 3,
            "Keep it to about two minutes: your current role, two or three achievements relevant to this job, and why this role is the right next step. Finish with what draws you to the organisation."),
        ("Describe a difficult problem you solved at work. What did you do, and what was the result?", "Problem solving", 4,
            "Use the STAR method: Situation, Task, Action, Result. Spend most of the answer on your own actions and quantify the result if you can."),
        ("Tell us about a time you disagreed with a colleague. How did you handle it?", "Teamwork", 3,
            "Show that you listened, kept to the shared goal and found a way forward together. Say what you learned."),
        ("Tell us about a time you led a piece of work or influenced people without formal authority.", "Leadership", 4,
            "Explain how you set direction, brought people with you and handled resistance. Close with the outcome and its impact."),
        ("How do you prioritise when several urgent tasks land at once?", "Planning & organisation", 3,
            "Describe a clear method (impact versus urgency, deadlines, stakeholders) and give a real example. Mention how you communicate trade-offs."),
        ("Describe a time you had to learn something new quickly.", "Problem solving", 3,
            "Name what you learned, how you approached it (sources, practice, asking for help) and how quickly you applied it."),
        ("Tell us about a mistake you made and what you learned from it.", "Self-awareness", 3,
            "Pick a real but recoverable mistake, own it without blaming others, explain how you fixed it and what you do differently now."),
        ("What achievement are you most proud of, and why?", "Motivation", 3,
            "Choose something relevant to this role. Explain the challenge, your contribution and why it matters to you."),
        ("Where would you like your career to be in three years, and how does this role fit?", "Motivation", 2,
            "Show ambition that is realistic and connected to this role. Avoid sounding as if you'd leave quickly."),
        ("What questions do you have for us?", "Communication", 2,
            "Prepare two or three thoughtful questions about the team, the role's challenges or how success is measured."),
    ];
}
