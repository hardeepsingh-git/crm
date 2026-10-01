using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crm.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase1RecruitmentCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing tenants are at revision 1 of their template; only templates with a newer revision upgrade at startup.
            migrationBuilder.AddColumn<int>(
                name: "TemplateVersion",
                table: "Tenants",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<bool>(
                name: "IsUnique",
                table: "FieldDefinitions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TemplateVersion",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "IsUnique",
                table: "FieldDefinitions");
        }
    }
}
