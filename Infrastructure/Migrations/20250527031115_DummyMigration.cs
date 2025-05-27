using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DummyMigration : Migration
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
                name: "SignalCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TelegramId = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Level = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    EnableGeneralNotifications = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    EnableVipSignalNotifications = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    EnableRssNewsNotifications = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    PreferredLanguage = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false, defaultValue: "en")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TextReplacement",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EditOptionsForwardingRuleName = table.Column<string>(type: "nvarchar(100)", nullable: false),
                    Find = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ReplaceWith = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsRegex = table.Column<bool>(type: "bit", nullable: false),
                    RegexOptions = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextReplacement", x => new { x.Id, x.EditOptionsForwardingRuleName });
                    table.ForeignKey(
                        name: "FK_TextReplacement_ForwardingRules_EditOptionsForwardingRuleName",
                        column: x => x.EditOptionsForwardingRuleName,
                        principalTable: "ForwardingRules",
                        principalColumn: "RuleName",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RssSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Url = table.Column<string>(type: "nvarchar(2083)", maxLength: 2083, nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false, defaultValueSql: "GETUTCDATE()"),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastModifiedHeader = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ETag = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: true),
                    LastFetchAttemptAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastSuccessfulFetchAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FetchIntervalMinutes = table.Column<int>(type: "int", nullable: true),
                    FetchErrorCount = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    Description = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    DefaultSignalCategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RssSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RssSources_SignalCategories_DefaultSignalCategoryId",
                        column: x => x.DefaultSignalCategoryId,
                        principalTable: "SignalCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Signals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Symbol = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    EntryPrice = table.Column<decimal>(type: "decimal(18,8)", nullable: false),
                    StopLoss = table.Column<decimal>(type: "decimal(18,8)", nullable: false),
                    TakeProfit = table.Column<decimal>(type: "decimal(18,8)", nullable: false),
                    SourceProvider = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false, defaultValue: "Pending"),
                    Timeframe = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    Notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    IsVipOnly = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Signals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Signals_SignalCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "SignalCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Subscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StartDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EndDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    ActivatingTransactionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Subscriptions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TokenWallets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(18,8)", nullable: false, defaultValue: 0m),
                    IsActive = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TokenWallets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TokenWallets_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Transactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PaymentGatewayInvoiceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    PaymentGatewayName = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: "Pending"),
                    PaidAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PaymentGatewayPayload = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PaymentGatewayResponse = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Transactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Transactions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserSignalPreferences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSignalPreferences", x => new { x.UserId, x.CategoryId });
                    table.ForeignKey(
                        name: "FK_UserSignalPreferences_SignalCategories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "SignalCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_UserSignalPreferences_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NewsItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Link = table.Column<string>(type: "nvarchar(2083)", maxLength: 2083, nullable: false),
                    Summary = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FullContent = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ImageUrl = table.Column<string>(type: "nvarchar(2083)", maxLength: 2083, nullable: true),
                    PublishedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastProcessedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SourceName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: true),
                    SourceItemId = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SentimentScore = table.Column<double>(type: "float", nullable: true),
                    SentimentLabel = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    DetectedLanguage = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    AffectedAssets = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    RssSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsVipOnly = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    AssociatedSignalCategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NewsItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NewsItems_RssSources_RssSourceId",
                        column: x => x.RssSourceId,
                        principalTable: "RssSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_NewsItems_SignalCategories_AssociatedSignalCategoryId",
                        column: x => x.AssociatedSignalCategoryId,
                        principalTable: "SignalCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SignalAnalyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SignalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnalystName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    AnalysisText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SentimentScore = table.Column<double>(type: "float", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignalAnalyses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignalAnalyses_Signals_SignalId",
                        column: x => x.SignalId,
                        principalTable: "Signals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_AssociatedSignalCategoryId",
                table: "NewsItems",
                column: "AssociatedSignalCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_Link",
                table: "NewsItems",
                column: "Link");

            migrationBuilder.CreateIndex(
                name: "IX_NewsItems_RssSourceId_SourceItemId",
                table: "NewsItems",
                columns: new[] { "RssSourceId", "SourceItemId" },
                unique: true,
                filter: "[SourceItemId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_RssSources_DefaultSignalCategoryId",
                table: "RssSources",
                column: "DefaultSignalCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_RssSources_SourceName",
                table: "RssSources",
                column: "SourceName");

            migrationBuilder.CreateIndex(
                name: "IX_RssSources_Url",
                table: "RssSources",
                column: "Url",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignalAnalyses_SignalId",
                table: "SignalAnalyses",
                column: "SignalId");

            migrationBuilder.CreateIndex(
                name: "IX_SignalCategories_Name",
                table: "SignalCategories",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Signals_CategoryId",
                table: "Signals",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Signals_Symbol",
                table: "Signals",
                column: "Symbol");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_EndDate",
                table: "Subscriptions",
                column: "EndDate");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_UserId",
                table: "Subscriptions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_TextReplacement_EditOptionsForwardingRuleName",
                table: "TextReplacement",
                column: "EditOptionsForwardingRuleName");

            migrationBuilder.CreateIndex(
                name: "IX_TokenWallets_UserId",
                table: "TokenWallets",
                column: "UserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_PaymentGatewayInvoiceId",
                table: "Transactions",
                column: "PaymentGatewayInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Status",
                table: "Transactions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_Timestamp",
                table: "Transactions",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_Transactions_UserId",
                table: "Transactions",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                table: "Users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_TelegramId",
                table: "Users",
                column: "TelegramId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Username",
                table: "Users",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserSignalPreferences_CategoryId",
                table: "UserSignalPreferences",
                column: "CategoryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NewsItems");

            migrationBuilder.DropTable(
                name: "SignalAnalyses");

            migrationBuilder.DropTable(
                name: "Subscriptions");

            migrationBuilder.DropTable(
                name: "TextReplacement");

            migrationBuilder.DropTable(
                name: "TokenWallets");

            migrationBuilder.DropTable(
                name: "Transactions");

            migrationBuilder.DropTable(
                name: "UserSignalPreferences");

            migrationBuilder.DropTable(
                name: "RssSources");

            migrationBuilder.DropTable(
                name: "Signals");

            migrationBuilder.DropTable(
                name: "ForwardingRules");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "SignalCategories");
        }
    }
}
