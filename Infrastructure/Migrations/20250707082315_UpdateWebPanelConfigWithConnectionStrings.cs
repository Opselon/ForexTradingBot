using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateWebPanelConfigWithConnectionStrings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DatabaseConnectionString",
                table: "WebPanelConfigs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RedisConnectionString",
                table: "WebPanelConfigs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TelegramBotToken",
                table: "WebPanelConfigs",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DatabaseConnectionString",
                table: "WebPanelConfigs");

            migrationBuilder.DropColumn(
                name: "RedisConnectionString",
                table: "WebPanelConfigs");

            migrationBuilder.DropColumn(
                name: "TelegramBotToken",
                table: "WebPanelConfigs");
        }
    }
}
