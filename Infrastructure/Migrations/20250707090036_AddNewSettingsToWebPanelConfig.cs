using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddNewSettingsToWebPanelConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdminChatId",
                table: "WebPanelConfigs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppName",
                table: "WebPanelConfigs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MaintenanceMode",
                table: "WebPanelConfigs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SessionTimeoutMinutes",
                table: "WebPanelConfigs",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdminChatId",
                table: "WebPanelConfigs");

            migrationBuilder.DropColumn(
                name: "AppName",
                table: "WebPanelConfigs");

            migrationBuilder.DropColumn(
                name: "MaintenanceMode",
                table: "WebPanelConfigs");

            migrationBuilder.DropColumn(
                name: "SessionTimeoutMinutes",
                table: "WebPanelConfigs");
        }
    }
}
