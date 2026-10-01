using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Crm.Api.Features.Accounts;
using Crm.Api.Features.Attachments;

namespace Crm.Api.Tests;

public class AttachmentTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    private static readonly byte[] Pdf = "%PDF-1.4 test resume"u8.ToArray();

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string objectKey, Guid recordId, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await client.PostAsync($"/api/attachments?objectKey={objectKey}&recordId={recordId}", form);
    }

    private static async Task<Guid> CreateAccountAsync(HttpClient client, string name) =>
        (await (await client.PostJsonAsync("/api/accounts", new { name })).ReadAsync<AccountDetail>()).Id;

    private int StoredFileCount() =>
        Directory.Exists(factory.StoragePath) ? Directory.GetFiles(factory.StoragePath, "*", SearchOption.AllDirectories).Length : 0;

    [Fact]
    public async Task Files_upload_download_show_in_history_and_are_removed_with_their_record()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var accountId = await CreateAccountAsync(admin, "Attachment Pty Ltd");
        var before = StoredFileCount();

        var created = await UploadAsync(admin, "account", accountId, @"..\..\secret\Jane Resume.pdf", Pdf);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var item = await created.ReadAsync<AttachmentItem>();
        Assert.Equal("Jane Resume.pdf", item.FileName);
        Assert.Equal("application/pdf", item.ContentType);
        Assert.Equal(before + 1, StoredFileCount());

        var list = await (await admin.GetAsync($"/api/attachments?objectKey=account&recordId={accountId}")).ReadAsync<List<AttachmentItem>>();
        Assert.Equal(item.Id, Assert.Single(list).Id);

        var download = await admin.GetAsync($"/api/attachments/{item.Id}/content");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/pdf", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal(Pdf, await download.Content.ReadAsByteArrayAsync());

        var history = JsonNode.Parse(await (await admin.GetAsync($"/api/audit/account/{accountId}")).Content.ReadAsStringAsync())!.AsArray();
        Assert.Contains(history, h => h!["changes"]?["attachment"]?[1]?.GetValue<string>() == "Jane Resume.pdf");

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/attachments/{item.Id}")).StatusCode);
        Assert.Equal(before, StoredFileCount());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/attachments/{item.Id}/content")).StatusCode);

        (await UploadAsync(admin, "account", accountId, "contract.docx", Pdf)).EnsureSuccessStatusCode();
        Assert.Equal(before + 1, StoredFileCount());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/accounts/{accountId}")).StatusCode);
        Assert.Equal(before, StoredFileCount());
    }

    [Fact]
    public async Task Unsafe_or_oversized_files_are_rejected()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var accountId = await CreateAccountAsync(admin, "Validation Pty Ltd");

        foreach (var name in new[] { "payload.exe", "page.html", "image.svg", "noextension" })
            Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, "account", accountId, name, Pdf)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, "account", accountId, "empty.pdf", [])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, "account", accountId, "huge.pdf", new byte[11 * 1024 * 1024])).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(admin, "account", Guid.NewGuid(), "cv.pdf", Pdf)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(admin, "nonexistent", accountId, "cv.pdf", Pdf)).StatusCode);
    }

    [Fact]
    public async Task Access_follows_the_record_permissions()
    {
        var admin = await factory.CreateAuthenticatedClientAsync();
        var accountId = await CreateAccountAsync(admin, "Managers Account");
        var item = await (await UploadAsync(admin, "account", accountId, "notes.txt", "hello"u8.ToArray())).ReadAsync<AttachmentItem>();

        // Standard User: reads every account but updates only their own.
        var rep = await factory.CreateUserClientAsync("Standard User");
        Assert.Equal(HttpStatusCode.OK, (await rep.GetAsync($"/api/attachments?objectKey=account&recordId={accountId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await rep.GetAsync($"/api/attachments/{item.Id}/content")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(rep, "account", accountId, "cv.pdf", Pdf)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await rep.DeleteAsync($"/api/attachments/{item.Id}")).StatusCode);

        var ownId = await CreateAccountAsync(rep, "Reps Account");
        Assert.Equal(HttpStatusCode.Created, (await UploadAsync(rep, "account", ownId, "cv.pdf", Pdf)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync($"/api/attachments/{item.Id}/content")).StatusCode);
    }

    [Theory]
    [InlineData("cv.pdf", "cv.pdf")]
    [InlineData("../../etc/passwd.txt", "passwd.txt")]
    [InlineData("C:\\temp\\a\u0000b.pdf", "a_b.pdf")]
    [InlineData("...hidden.pdf", "hidden.pdf")]
    [InlineData("", "file")]
    public void File_names_are_sanitised(string raw, string expected) =>
        Assert.Equal(expected, AttachmentEndpoints.SafeFileName(raw));
}
