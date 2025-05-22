using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddForwardingRuleOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ForwardingRules",
                columns: table => new
                {
                    RuleName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SourceChannelId = table.Column<long>(type: "bigint", nullable: false),
                    TargetChannelIds = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    EditOptions_PrependText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EditOptions_AppendText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EditOptions_RemoveSourceForwardHeader = table.Column<bool>(type: "bit", nullable: false),
                    EditOptions_RemoveLinks = table.Column<bool>(type: "bit", nullable: false),
                    EditOptions_StripFormatting = table.Column<bool>(type: "bit", nullable: false),
                    EditOptions_CustomFooter = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EditOptions_DropAuthor = table.Column<bool>(type: "bit", nullable: false),
                    EditOptions_DropMediaCaptions = table.Column<bool>(type: "bit", nullable: false),
                    EditOptions_NoForwards = table.Column<bool>(type: "bit", nullable: false),
                    FilterOptions_AllowedMessageTypes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FilterOptions_AllowedMimeTypes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FilterOptions_ContainsText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FilterOptions_ContainsTextIsRegex = table.Column<bool>(type: "bit", nullable: false),
                    FilterOptions_ContainsTextRegexOptions = table.Column<int>(type: "int", nullable: false),
                    FilterOptions_AllowedSenderUserIds = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FilterOptions_BlockedSenderUserIds = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FilterOptions_IgnoreEditedMessages = table.Column<bool>(type: "bit", nullable: false),
                    FilterOptions_IgnoreServiceMessages = table.Column<bool>(type: "bit", nullable: false),
                    FilterOptions_MinMessageLength = table.Column<int>(type: "int", nullable: true),
                    FilterOptions_MaxMessageLength = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForwardingRules", x => x.RuleName);
                });

            migrationBuilder.CreateTable(
                name: "TextReplacementRule",
                columns: table => new
                {
                    MessageEditOptionsForwardingRuleRuleName = table.Column<string>(type: "nvarchar(100)", nullable: false),
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Find = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReplaceWith = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsRegex = table.Column<bool>(type: "bit", nullable: false),
                    RegexOptions = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextReplacementRule", x => new { x.MessageEditOptionsForwardingRuleRuleName, x.Id });
                    table.ForeignKey(
                        name: "FK_TextReplacementRule_ForwardingRules_MessageEditOptionsForwardingRuleRuleName",
                        column: x => x.MessageEditOptionsForwardingRuleRuleName,
                        principalTable: "ForwardingRules",
                        principalColumn: "RuleName",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TextReplacementRule");

            migrationBuilder.DropTable(
                name: "ForwardingRules");
        }
    }
}
