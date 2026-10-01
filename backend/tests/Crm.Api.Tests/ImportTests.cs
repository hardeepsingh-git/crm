using System.Net;
using Crm.Api.Common;
using Crm.Api.Features.Import;
using Crm.Api.Features.Records;

namespace Crm.Api.Tests;

public class ImportTests(CrmApiFactory factory) : IClassFixture<CrmApiFactory>
{
    private const string Password = "Ats#Pass12345";

    private async Task<HttpClient> RecruitmentTenantAsync()
    {
        var platform = await factory.CreateAuthenticatedClientAsync();
        var slug = $"ats-{Guid.NewGuid():N}"[..20];
        var email = $"admin@{slug}.test";
        (await platform.PostJsonAsync("/api/platform/tenants", new
        {
            name = "ATS Agency", slug, template = "recruitment", currency = "AUD",
            adminEmail = email, adminFirstName = "Ada", adminLastName = "Admin", adminPassword = Password, seedDemoData = false,
        })).EnsureSuccessStatusCode();
        return await factory.CreateAuthenticatedClientAsync(email, Password);
    }

    private static Task<HttpResponseMessage> ImportAsync(HttpClient client, string objectKey, string csv, Dictionary<string, string?> mapping, bool dryRun = false) =>
        client.PostJsonAsync($"/api/import/{objectKey}", new { csv, mapping, dryRun });

    private static async Task<List<RecordItem>> RecordsAsync(HttpClient client, string objectKey) =>
        [.. (await (await client.GetAsync($"/api/records/{objectKey}?pageSize=100")).ReadAsync<PagedResult<RecordItem>>()).Items];

    [Fact]
    public async Task Positions_and_applications_import_with_suggested_mapping_lookups_by_name_and_all_or_nothing()
    {
        var ats = await RecruitmentTenantAsync();
        const string positions = "\uFEFFJob title,Department,Location,Status,Openings,Max salary\r\n" +
                                 "Senior Developer,Engineering,Sydney,open,2,\"$150,000\"\r\n" +
                                 "\r\n" +
                                 "\"Data Analyst, Junior\",Analytics,Remote,On hold,1,95000.50\r\n";

        var analysis = await (await ats.PostJsonAsync("/api/import/position/analyze", new { csv = positions })).ReadAsync<ImportAnalysis>();
        Assert.Equal(2, analysis.RowCount);
        Assert.Equal("name", analysis.SuggestedMapping["Job title"]);
        Assert.Equal("cf.department", analysis.SuggestedMapping["Department"]);
        Assert.Equal("cf.salary_max", analysis.SuggestedMapping["Max salary"]);
        Assert.Equal("Data Analyst, Junior", analysis.Sample[1][0]);

        var dry = await (await ImportAsync(ats, "position", positions, analysis.SuggestedMapping, dryRun: true)).ReadAsync<ImportResult>();
        Assert.Equal((true, 2, 0, 0), (dry.DryRun, dry.Total, dry.Created, dry.ErrorCount));
        Assert.Empty(await RecordsAsync(ats, "position"));

        var done = await (await ImportAsync(ats, "position", positions, analysis.SuggestedMapping)).ReadAsync<ImportResult>();
        Assert.Equal(2, done.Created);
        var senior = (await RecordsAsync(ats, "position")).Single(r => r.Name == "Senior Developer");
        Assert.Equal("Open", senior.CustomFields["status"]!.GetValue<string>());
        Assert.Equal(150000m, senior.CustomFields["salary_max"]!.GetValue<decimal>());

        var people = await (await ImportAsync(ats, "candidate", "Full name,Email\nJane Doe,jane@example.com\nRaj Patel,raj@example.com\n",
            new() { ["Full name"] = "name", ["Email"] = "cf.email" })).ReadAsync<ImportResult>();
        Assert.Equal(2, people.Created);

        var mapping = new Dictionary<string, string?>
        {
            ["Applicant"] = "name", ["Candidate"] = "cf.candidate", ["Role"] = "cf.position", ["Stage"] = "stage", ["Rating"] = "cf.rating", ["Source"] = "cf.source",
        };
        const string withErrors = "Applicant,Candidate,Role,Stage,Rating,Source\n" +
                                  "Jane Doe,jane doe,senior developer,Interview,5,linkedin\n" +
                                  "Bad Row,Nobody Known,Unknown Role,Nope,9,Referral\n";
        var failed = await (await ImportAsync(ats, "application", withErrors, mapping)).ReadAsync<ImportResult>();
        Assert.Equal(0, failed.Created);
        var rowError = Assert.Single(failed.Errors);
        Assert.Equal(3, rowError.Row);
        Assert.Contains(rowError.Messages, m => m.Contains("No record named 'Unknown Role'"));
        Assert.Contains(rowError.Messages, m => m.Contains("No record named 'Nobody Known'"));
        Assert.Contains(rowError.Messages, m => m.Contains("Stage 'Nope'"));
        Assert.Contains(rowError.Messages, m => m.StartsWith("Rating (1-5):"));
        Assert.Empty(await RecordsAsync(ats, "application"));

        var fixedCsv = withErrors.Replace("Bad Row,Nobody Known,Unknown Role,Nope,9", "Raj Patel,Raj Patel,\"Data Analyst, Junior\",,4");
        var imported = await (await ImportAsync(ats, "application", fixedCsv, mapping)).ReadAsync<ImportResult>();
        Assert.Equal(2, imported.Created);
        var applications = await RecordsAsync(ats, "application");
        var jane = applications.Single(a => a.Name == "Jane Doe");
        Assert.Equal("interview", jane.Stage);
        Assert.Equal(senior.Id.ToString(), jane.CustomFields["position"]!.GetValue<string>());
        var janeCandidate = (await RecordsAsync(ats, "candidate")).Single(c => c.Name == "Jane Doe");
        Assert.Equal(janeCandidate.Id.ToString(), jane.CustomFields["candidate"]!.GetValue<string>());
        Assert.Equal("LinkedIn", jane.CustomFields["source"]!.GetValue<string>());
        Assert.Equal("applied", applications.Single(a => a.Name == "Raj Patel").Stage);
    }

    [Fact]
    public async Task Bad_files_and_mappings_are_rejected_before_anything_is_saved()
    {
        var ats = await RecruitmentTenantAsync();
        const string csv = "Job title,Status\nTester,Open\n";

        Assert.Equal(HttpStatusCode.BadRequest, (await ImportAsync(ats, "position", csv, new() { ["Status"] = "cf.status" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ImportAsync(ats, "position", csv, new() { ["Job title"] = "name" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await ImportAsync(ats, "position", csv, new() { ["Job title"] = "name", ["Status"] = "cf.nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await ImportAsync(ats, "position", "Job title,Status\n\"Unclosed,Open\n", new() { ["Job title"] = "name", ["Status"] = "cf.status" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ImportAsync(ats, "position", "Job title,Status\n", new() { ["Job title"] = "name" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ImportAsync(ats, "account", csv, new() { ["Job title"] = "name" })).StatusCode);
        Assert.Empty(await RecordsAsync(ats, "position"));
    }

    [Fact]
    public void Csv_parser_handles_quotes_line_breaks_delimiters_and_reports_line_numbers()
    {
        var rows = Csv.Parse("a;b;c\r\n\"x;1\";\"say \"\"hi\"\"\";\"multi\nline\"\n\n;;\nlast;row;3");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["x;1", "say \"hi\"", "multi\nline"], rows[1].Cells);
        Assert.Equal(2, rows[1].Line);
        Assert.Equal(6, rows[2].Line);
        Assert.Equal('\t', Csv.DetectDelimiter("a\tb\tc\n1,2\t3"));
        Assert.Throws<FormatException>(() => Csv.Parse("a,b\n\"open,2"));
    }

    [Theory]
    [InlineData("1234.5", "1234.5")]
    [InlineData("$ 95,000", "95000")]
    [InlineData("1,234,567.89", "1234567.89")]
    [InlineData("-42", "-42")]
    [InlineData("1.234,50", null)]
    [InlineData("12,34", null)]
    [InlineData("abc", null)]
    public void Numbers_are_parsed_strictly(string text, string? expected) =>
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), ImportEndpoints.ParseNumber(text));
}
