using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using Crm.Api.Platform;

namespace Crm.Api.Messaging;

public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

public interface ISmsSender
{
    Task SendAsync(string to, string message, CancellationToken ct);
}

/// <summary>
/// Sends via the provider chosen in platform settings. "Log" is the unconfigured default: it writes the message to the
/// log in Development (so flows can be tested without a gateway) and only a notice elsewhere, so codes never leak into production logs.
/// </summary>
public sealed class ConfiguredEmailSender(
    PlatformSettingsService settings, IConfiguration config, IHostEnvironment env, ILogger<ConfiguredEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        if (s.EmailProvider == EmailProvider.Log)
        {
            if (env.IsDevelopment()) logger.LogWarning("[Email:Log] To {To} | {Subject} | {Body}", to, subject, body);
            else logger.LogWarning("Email to {To} not sent: no email provider is configured (Platform settings)", to);
            return;
        }

        using var message = new MailMessage(new MailAddress(s.EmailFromAddress!, s.EmailFromName), new MailAddress(to))
        {
            Subject = subject,
            Body = body,
        };
        using var client = new SmtpClient(s.SmtpHost, s.SmtpPort) { EnableSsl = s.SmtpUseStartTls, DeliveryMethod = SmtpDeliveryMethod.Network };
        if (!string.IsNullOrWhiteSpace(s.SmtpUsername))
            client.Credentials = new NetworkCredential(s.SmtpUsername, config["Email:SmtpPassword"]);
        await client.SendMailAsync(message, ct);
    }
}

/// <summary>
/// "Http" posts <c>{ to, from, message }</c> as JSON to the configured gateway, authenticating with <c>Sms:ApiKey</c>.
/// Gateways with a different contract get their own <see cref="ISmsSender"/> implementation.
/// </summary>
public sealed class ConfiguredSmsSender(
    PlatformSettingsService settings, IHttpClientFactory http, IConfiguration config, IHostEnvironment env, ILogger<ConfiguredSmsSender> logger) : ISmsSender
{
    public const string HttpClientName = "sms-gateway";

    public async Task SendAsync(string to, string message, CancellationToken ct)
    {
        var s = await settings.GetAsync(ct);
        if (s.SmsProvider == SmsProvider.Log)
        {
            if (env.IsDevelopment()) logger.LogWarning("[Sms:Log] To {To} | {Message}", to, message);
            else logger.LogWarning("SMS to {To} not sent: no SMS provider is configured (Platform settings)", to);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, s.SmsGatewayUrl)
        {
            Content = JsonContent.Create(new { to, from = s.SmsSenderId, message }),
        };
        if (config["Sms:ApiKey"] is { Length: > 0 } key && !string.IsNullOrWhiteSpace(s.SmsAuthHeader))
            request.Headers.TryAddWithoutValidation(s.SmsAuthHeader, string.IsNullOrWhiteSpace(s.SmsAuthScheme) ? key : $"{s.SmsAuthScheme} {key}");

        using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"SMS gateway returned {(int)response.StatusCode} {response.ReasonPhrase}.");
    }
}
