using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase2EmailAndScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScheduleField",
                table: "AutomationRules",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ScheduleOffsetMinutes",
                table: "AutomationRules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "EmailTemplates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboundEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    To = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    ReplyTo = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "character varying(10000)", maxLength: 10000, nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecordName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: true),
                    TemplateId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActivityId = table.Column<Guid>(type: "uuid", nullable: true),
                    SentById = table.Column<Guid>(type: "uuid", nullable: true),
                    SentByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboundEmails", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledRuleRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RanAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Matched = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledRuleRuns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AutomationRules_Trigger_IsActive",
                table: "AutomationRules",
                columns: new[] { "Trigger", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_EmailTemplates_TenantId_Name",
                table: "EmailTemplates",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_Status_NextAttemptAt",
                table: "OutboundEmails",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_TenantId_CreatedAt",
                table: "OutboundEmails",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboundEmails_TenantId_RuleId_RecordId",
                table: "OutboundEmails",
                columns: new[] { "TenantId", "RuleId", "RecordId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledRuleRuns_RuleId_RecordId_DueAt",
                table: "ScheduledRuleRuns",
                columns: new[] { "RuleId", "RecordId", "DueAt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledRuleRuns_TenantId",
                table: "ScheduledRuleRuns",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailTemplates");

            migrationBuilder.DropTable(
                name: "OutboundEmails");

            migrationBuilder.DropTable(
                name: "ScheduledRuleRuns");

            migrationBuilder.DropIndex(
                name: "IX_AutomationRules_Trigger_IsActive",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "ScheduleField",
                table: "AutomationRules");

            migrationBuilder.DropColumn(
                name: "ScheduleOffsetMinutes",
                table: "AutomationRules");
        }
    }
}
