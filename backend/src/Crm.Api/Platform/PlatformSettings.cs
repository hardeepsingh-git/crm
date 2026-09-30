using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Crm.Api.Data;
using Crm.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Crm.Api.Platform;

public enum EmailProvider { Log, Smtp }

public enum SmsProvider { Log, Http }

/// <summary>
/// Platform-wide, UI-editable configuration persisted one row per property in <c>AppSettings</c>.
/// Secrets (SMTP password, SMS API key) are deliberately absent and come from configuration.
/// </summary>
public sealed record PlatformSettings
{
    public bool SelfRegistrationEnabled { get; init; }
    public bool EmailVerificationEnabled { get; init; }
    public bool SmsVerificationEnabled { get; init; }

    [Range(4, 10)] public int CodeLength { get; init; } = 6;
    [Range(1, 60)] public int CodeTtlMinutes { get; init; } = 10;
    [Range(1, 10)] public int MaxAttempts { get; init; } = 5;
    [Range(0, 600)] public int ResendCooldownSeconds { get; init; } = 60;

    public EmailProvider EmailProvider { get; init; } = EmailProvider.Log;
    [MaxLength(255)] public string? SmtpHost { get; init; }
    [Range(1, 65535)] public int SmtpPort { get; init; } = 587;
    [MaxLength(255)] public string? SmtpUsername { get; init; }
    public bool SmtpUseStartTls { get; init; } = true;
    [MaxLength(256), EmailAddress] public string? EmailFromAddress { get; init; }
    [MaxLength(100)] public string EmailFromName { get; init; } = "CRM";

    public SmsProvider SmsProvider { get; init; } = SmsProvider.Log;
    [MaxLength(500)] public string? SmsGatewayUrl { get; init; }
    [MaxLength(32)] public string? SmsSenderId { get; init; }
    [MaxLength(64)] public string SmsAuthHeader { get; init; } = "Authorization";
    [MaxLength(32)] public string SmsAuthScheme { get; init; } = "Bearer";

    public IReadOnlyList<VerificationChannel> RequiredChannels() =>
        [.. EmailVerificationEnabled ? [VerificationChannel.Email] : Array.Empty<VerificationChannel>(),
         .. SmsVerificationEnabled ? [VerificationChannel.Sms] : Array.Empty<VerificationChannel>()];

    /// <summary>Cross-field rules that attributes can't express.</summary>
    public Dictionary<string, string[]> Validate()
    {
        var errors = new Dictionary<string, string[]>();
        if (EmailProvider == EmailProvider.Smtp)
        {
            if (string.IsNullOrWhiteSpace(SmtpHost)) errors[nameof(SmtpHost)] = ["SMTP host is required for the SMTP provider."];
            if (string.IsNullOrWhiteSpace(EmailFromAddress)) errors[nameof(EmailFromAddress)] = ["A from address is required for the SMTP provider."];
        }
        if (SmsProvider == SmsProvider.Http &&
            !(Uri.TryCreate(SmsGatewayUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)))
            errors[nameof(SmsGatewayUrl)] = ["An absolute http(s) gateway URL is required for the HTTP provider."];
        if (!string.IsNullOrEmpty(SmsAuthHeader) && SmsAuthHeader.Any(c => char.IsWhiteSpace(c) || c == ':'))
            errors[nameof(SmsAuthHeader)] = ["Header names cannot contain spaces or colons."];
        return errors;
    }
}

/// <summary>Reads and writes <see cref="PlatformSettings"/>; cached briefly because every anonymous registration call reads it.</summary>
public sealed class PlatformSettingsService(CrmDbContext db, IMemoryCache cache, TimeProvider clock, ILogger<PlatformSettingsService> logger)
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly object CacheKey = new();
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Tokens = new();

    public async Task<PlatformSettings> GetAsync(CancellationToken ct = default)
    {
        if (cache.TryGetValue(CacheKey, out PlatformSettings? cached) && cached is not null) return cached;

        var rows = await db.AppSettings.AsNoTracking().ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        var settings = Deserialize(rows);
        var token = Tokens.GetOrAdd("settings", _ => new CancellationTokenSource());
        cache.Set(CacheKey, settings, new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(1))
            .AddExpirationToken(new CancellationChangeToken(token.Token)));
        return settings;
    }

    public async Task SaveAsync(PlatformSettings settings, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await db.AppSettings.ToDictionaryAsync(s => s.Key, ct);
        foreach (var (key, value) in Serialize(settings))
        {
            if (existing.TryGetValue(key, out var row))
            {
                if (row.Value == value) continue;
                row.Value = value;
                row.UpdatedAt = now;
            }
            else db.AppSettings.Add(new AppSetting { Key = key, Value = value, UpdatedAt = now });
        }
        await db.SaveChangesAsync(ct);
        Invalidate();
    }

    /// <summary>Writes rows for any settings that don't exist yet, so DBAs can see every key with its default.</summary>
    public async Task EnsureDefaultsAsync(CancellationToken ct = default)
    {
        var keys = await db.AppSettings.Select(s => s.Key).ToListAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var missing = Serialize(new PlatformSettings()).Where(kv => !keys.Contains(kv.Key)).ToList();
        if (missing.Count == 0) return;
        db.AppSettings.AddRange(missing.Select(kv => new AppSetting { Key = kv.Key, Value = kv.Value, UpdatedAt = now }));
        await db.SaveChangesAsync(ct);
        Invalidate();
    }

    public static void Invalidate()
    {
        if (Tokens.TryRemove("settings", out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> Serialize(PlatformSettings settings) =>
        JsonSerializer.SerializeToNode(settings, Json)!.AsObject()
            .Select(p => KeyValuePair.Create(p.Key, p.Value?.ToJsonString() ?? "null"));

    private PlatformSettings Deserialize(Dictionary<string, string> rows)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in rows)
        {
            try { obj[key] = JsonNode.Parse(value); }
            catch (JsonException) { logger.LogWarning("Ignoring malformed setting {Key}", key); }
        }
        try { return obj.Deserialize<PlatformSettings>(Json) ?? new PlatformSettings(); }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Platform settings are invalid; using defaults until they are saved again");
            return new PlatformSettings();
        }
    }
}
