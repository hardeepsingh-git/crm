using System.Text;
using Crm.Api.Common;
using Crm.Api.Data;
using Crm.Api.Domain;
using Crm.Api.Files;
using Crm.Api.Security;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using static Crm.Api.Domain.PermissionActions;

namespace Crm.Api.Features.Attachments;

public record AttachmentItem(Guid Id, string FileName, string ContentType, long Size, Guid UploadedById, string UploadedByName, DateTime CreatedAt);

/// <summary>Files on records (resumes, contracts, IDs). Reading needs read access to the record; adding or removing needs update access.</summary>
public static class AttachmentEndpoints
{
    public const int MaxFilesPerRecord = 100;

    /// <summary>Allowed extensions and the content type served for each; the client's declared type is never trusted.</summary>
    public static readonly IReadOnlyDictionary<string, string> AllowedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".rtf"] = "application/rtf",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    public static RouteGroupBuilder MapAttachments(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/attachments").WithTags("Attachments");
        g.MapGet("/", List);
        // Bearer-token auth only (no cookies), so cross-site request forgery doesn't apply to uploads.
        g.MapPost("/", Upload).DisableAntiforgery();
        g.MapGet("/{id:guid}/content", Download);
        g.MapDelete("/{id:guid}", Delete);
        return api;
    }

    private static async Task<bool> CanAsync(string objectKey, Guid recordId, string action, Access access, CrmDbContext db, CancellationToken ct)
    {
        if (objectKey == ObjectKeys.Activity) return false;
        var owner = await RecordOwners.OwnerOfAsync(db, objectKey, recordId, ct);
        return owner is not null && (await access.GetAsync(ct)).CanAccess(objectKey, action, owner.Value);
    }

    private static async Task<Results<Ok<List<AttachmentItem>>, NotFound>> List(
        string objectKey, Guid recordId, Access access, CrmDbContext db, CancellationToken ct)
    {
        if (!await CanAsync(objectKey, recordId, Read, access, db, ct)) return TypedResults.NotFound();
        return TypedResults.Ok(await db.Attachments.AsNoTracking()
            .Where(a => a.ObjectKey == objectKey && a.RecordId == recordId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new AttachmentItem(a.Id, a.FileName, a.ContentType, a.Size, a.UploadedById, a.UploadedByName, a.CreatedAt))
            .ToListAsync(ct));
    }

    private static async Task<Results<Created<AttachmentItem>, NotFound, ForbidHttpResult, ValidationProblem>> Upload(
        string objectKey, Guid recordId, IFormFile? file, Access access, CrmDbContext db, IFileStorage storage, IConfiguration config,
        TimeProvider clock, CancellationToken ct)
    {
        if (!await CanAsync(objectKey, recordId, Read, access, db, ct)) return TypedResults.NotFound();
        if (!await CanAsync(objectKey, recordId, Update, access, db, ct)) return TypedResults.Forbid();

        var maxBytes = config.GetValue("Attachments:MaxFileSizeMb", 10) * 1024L * 1024L;
        if (file is null || file.Length == 0) return Errors.Validation("file", "Choose a file to upload.");
        if (file.Length > maxBytes) return Errors.Validation("file", $"Files can be at most {maxBytes / 1024 / 1024} MB.");
        var name = SafeFileName(file.FileName);
        if (!AllowedTypes.TryGetValue(Path.GetExtension(name), out var contentType))
            return Errors.Validation("file", $"This file type isn't allowed. Allowed: {string.Join(", ", AllowedTypes.Keys)}.");
        if (await db.Attachments.CountAsync(a => a.ObjectKey == objectKey && a.RecordId == recordId, ct) >= MaxFilesPerRecord)
            return Errors.Validation("file", $"A record can have at most {MaxFilesPerRecord} files.");

        var now = clock.GetUtcNow().UtcDateTime;
        var attachment = new Attachment
        {
            ObjectKey = objectKey, RecordId = recordId, FileName = name, ContentType = contentType, Size = file.Length,
            StorageKey = StorageKeys.New(access.TenantId, now), UploadedById = access.UserId,
            UploadedByName = db.TenantContext.UserName ?? "", CreatedAt = now,
        };
        await using (var stream = file.OpenReadStream()) await storage.SaveAsync(attachment.StorageKey, stream, ct);
        try
        {
            db.Attachments.Add(attachment);
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await storage.DeleteAsync(attachment.StorageKey, CancellationToken.None);
            throw;
        }
        return TypedResults.Created($"/api/attachments/{attachment.Id}/content", new AttachmentItem(
            attachment.Id, attachment.FileName, attachment.ContentType, attachment.Size, attachment.UploadedById, attachment.UploadedByName, attachment.CreatedAt));
    }

    private static async Task<Results<FileStreamHttpResult, NotFound>> Download(
        Guid id, Access access, CrmDbContext db, IFileStorage storage, CancellationToken ct)
    {
        var attachment = await db.Attachments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
        if (attachment is null || !await CanAsync(attachment.ObjectKey, attachment.RecordId, Read, access, db, ct)) return TypedResults.NotFound();
        var stream = await storage.OpenReadAsync(attachment.StorageKey, ct);
        return stream is null
            ? TypedResults.NotFound()
            : TypedResults.Stream(stream, attachment.ContentType, attachment.FileName);
    }

    private static async Task<Results<NoContent, NotFound, ForbidHttpResult>> Delete(
        Guid id, Access access, CrmDbContext db, IFileStorage storage, ILoggerFactory loggers, CancellationToken ct)
    {
        var attachment = await db.Attachments.SingleOrDefaultAsync(a => a.Id == id, ct);
        if (attachment is null || !await CanAsync(attachment.ObjectKey, attachment.RecordId, Read, access, db, ct)) return TypedResults.NotFound();
        if (!await CanAsync(attachment.ObjectKey, attachment.RecordId, Update, access, db, ct)) return TypedResults.Forbid();

        db.Attachments.Remove(attachment);
        await db.SaveChangesAsync(ct);
        try
        {
            await storage.DeleteAsync(attachment.StorageKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger("Crm.Api.Attachments").LogError(ex, "Attachment {Id} removed but its file could not be deleted", id);
        }
        return TypedResults.NoContent();
    }

    /// <summary>Keeps only the final path segment and strips control and reserved characters.</summary>
    public static string SafeFileName(string? raw)
    {
        var name = Path.GetFileName((raw ?? "").Replace('\\', '/').Split('/').Last());
        var invalid = Path.GetInvalidFileNameChars().Concat(['"', '<', '>', '|', ':', '*', '?']).ToHashSet();
        var clean = new StringBuilder(name.Length);
        foreach (var c in name) clean.Append(char.IsControl(c) || invalid.Contains(c) ? '_' : c);
        var result = clean.ToString().Trim().TrimStart('.');
        if (result.Length == 0) result = "file";
        if (result.Length > 200)
        {
            var ext = Path.GetExtension(result);
            result = result[..(200 - ext.Length)] + ext;
        }
        return result;
    }
}
