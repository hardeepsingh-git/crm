using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Crm.Api.Messaging;

namespace Crm.Api.Tests;

/// <summary>Records outgoing email and SMS instead of sending them, so tests can read verification codes.</summary>
public sealed partial class CapturedMessages : IEmailSender, ISmsSender
{
    public sealed record Message(string Channel, string To, string Subject, string Body);

    private readonly ConcurrentQueue<Message> _messages = new();

    public IReadOnlyList<Message> All => [.. _messages];

    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        _messages.Enqueue(new Message("Email", to, subject, body));
        return Task.CompletedTask;
    }

    public Task SendAsync(string to, string message, CancellationToken ct)
    {
        _messages.Enqueue(new Message("Sms", to, "", message));
        return Task.CompletedTask;
    }

    public IReadOnlyList<Message> To(string to) => All.Where(m => m.To == to).ToList();

    /// <summary>The code from the most recent message of the channel sent to the address or number.</summary>
    public string LatestCode(string to, string channel) =>
        CodePattern().Match(All.Last(m => m.To == to && m.Channel == channel).Body).Groups[1].Value;

    [GeneratedRegex(@"code is (\d+)")]
    private static partial Regex CodePattern();
}
