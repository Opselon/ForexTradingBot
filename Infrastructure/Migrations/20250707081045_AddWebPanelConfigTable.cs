using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWebPanelConfigTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WebPanelConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SiteTitle = table.Column<string>(type: "text", nullable: true),
                    DefaultTheme = table.Column<string>(type: "text", nullable: true),
                    MaxLogEntriesToDisplay = table.Column<int>(type: "integer", nullable: false),
                    IsFeatureXEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsFeatureYEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ContactEmail = table.Column<string>(type: "text", nullable: true),
                    DefaultPageSize = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastModifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WebPanelConfigs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WebPanelConfigs");
        }
    }
}
