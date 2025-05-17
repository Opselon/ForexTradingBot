using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FullDevResetAndInit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SignalCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
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
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RssSources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SourceName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastFetchedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    FetchIntervalMinutes = table.Column<int>(type: "int", nullable: true),
                    FetchErrorCount = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DefaultSignalCategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ETag = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RssSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RssSources_SignalCategories_DefaultSignalCategoryId",
                        column: x => x.DefaultSignalCategoryId,
                        principalTable: "SignalCategories",
                        principalColumn: "Id");
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
                    Source = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
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
                    Balance = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
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
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSignalPreferences", x => x.Id);
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
                name: "SignalAnalyses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SignalId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AnalystName = table.Column<string>(type: "nvarchar(150)", maxLength: 150, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
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
                name: "IX_RssSources_DefaultSignalCategoryId",
                table: "RssSources",
                column: "DefaultSignalCategoryId");

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
                name: "IX_Subscriptions_UserId",
                table: "Subscriptions",
                column: "UserId");

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

            migrationBuilder.CreateIndex(
                name: "IX_UserSignalPreferences_UserId",
                table: "UserSignalPreferences",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RssSources");

            migrationBuilder.DropTable(
                name: "SignalAnalyses");

            migrationBuilder.DropTable(
                name: "Subscriptions");

            migrationBuilder.DropTable(
                name: "TokenWallets");

            migrationBuilder.DropTable(
                name: "Transactions");

            migrationBuilder.DropTable(
                name: "UserSignalPreferences");

            migrationBuilder.DropTable(
                name: "Signals");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "SignalCategories");
        }
    }
}
