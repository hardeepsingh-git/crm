using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase6MockInterviews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MockAiInstructions",
                table: "Tenants",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MockAiReviewEnabled",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MockAiTrigger",
                table: "Tenants",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.AddColumn<string>(
                name: "ThemePreference",
                table: "AspNetUsers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "System");

            migrationBuilder.CreateTable(
                name: "MockInterviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApplicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    JobTitle = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Questions = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'"),
                    Answers = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'"),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SentById = table.Column<Guid>(type: "uuid", nullable: false),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedById = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReadinessRating = table.Column<int>(type: "integer", nullable: true),
                    ReviewNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    AiStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AiRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AiReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AiLockedUntil = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AiModel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AiScore = table.Column<decimal>(type: "numeric(3,1)", precision: 3, scale: 1, nullable: true),
                    AiFeedback = table.Column<string>(type: "jsonb", nullable: true),
                    AiError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MockInterviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MockInterviews_CustomRecords_ApplicationId",
                        column: x => x.ApplicationId,
                        principalTable: "CustomRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MockInterviews_AiStatus_AiRequestedAt",
                table: "MockInterviews",
                columns: new[] { "AiStatus", "AiRequestedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MockInterviews_ApplicationId_Open",
                table: "MockInterviews",
                column: "ApplicationId",
                unique: true,
                filter: "\"Status\" IN ('Sent', 'InProgress')");

            migrationBuilder.CreateIndex(
                name: "IX_MockInterviews_TenantId_ApplicationId",
                table: "MockInterviews",
                columns: new[] { "TenantId", "ApplicationId" });

            migrationBuilder.CreateIndex(
                name: "IX_MockInterviews_TokenHash",
                table: "MockInterviews",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MockInterviews");

            migrationBuilder.DropColumn(
                name: "MockAiInstructions",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "MockAiReviewEnabled",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "MockAiTrigger",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "ThemePreference",
                table: "AspNetUsers");
        }
    }
}
