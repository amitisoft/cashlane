using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cashlane.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CashlaneV2Features : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Categories_UserId",
                table: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_Budgets_UserId_CategoryId_Month_Year",
                table: "Budgets");

            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "Categories",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "Budgets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AccountId",
                table: "AuditLogs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AccountBalanceSnapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    CapturedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountBalanceSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountBalanceSnapshots_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AccountMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountMembers_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AccountMembers_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConditionJson = table.Column<string>(type: "text", nullable: false),
                    ActionJson = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Rules_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Rules_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_AccountId",
                table: "Categories",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_UserId_AccountId_Name_Type",
                table: "Categories",
                columns: new[] { "UserId", "AccountId", "Name", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_Budgets_AccountId_CategoryId_Month_Year",
                table: "Budgets",
                columns: new[] { "AccountId", "CategoryId", "Month", "Year" },
                unique: true,
                filter: "\"AccountId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Budgets_UserId_CategoryId_Month_Year",
                table: "Budgets",
                columns: new[] { "UserId", "CategoryId", "Month", "Year" },
                unique: true,
                filter: "\"AccountId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_AccountId_CreatedAtUtc",
                table: "AuditLogs",
                columns: new[] { "AccountId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountBalanceSnapshots_AccountId_CapturedAtUtc",
                table: "AccountBalanceSnapshots",
                columns: new[] { "AccountId", "CapturedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountMembers_AccountId_UserId",
                table: "AccountMembers",
                columns: new[] { "AccountId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountMembers_UserId_Role",
                table: "AccountMembers",
                columns: new[] { "UserId", "Role" });

            migrationBuilder.CreateIndex(
                name: "IX_Rules_AccountId",
                table: "Rules",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Rules_UserId_AccountId_Priority",
                table: "Rules",
                columns: new[] { "UserId", "AccountId", "Priority" });

            migrationBuilder.AddForeignKey(
                name: "FK_Budgets_Accounts_AccountId",
                table: "Budgets",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_Categories_Accounts_AccountId",
                table: "Categories",
                column: "AccountId",
                principalTable: "Accounts",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Budgets_Accounts_AccountId",
                table: "Budgets");

            migrationBuilder.DropForeignKey(
                name: "FK_Categories_Accounts_AccountId",
                table: "Categories");

            migrationBuilder.DropTable(
                name: "AccountBalanceSnapshots");

            migrationBuilder.DropTable(
                name: "AccountMembers");

            migrationBuilder.DropTable(
                name: "Rules");

            migrationBuilder.DropIndex(
                name: "IX_Categories_AccountId",
                table: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_Categories_UserId_AccountId_Name_Type",
                table: "Categories");

            migrationBuilder.DropIndex(
                name: "IX_Budgets_AccountId_CategoryId_Month_Year",
                table: "Budgets");

            migrationBuilder.DropIndex(
                name: "IX_Budgets_UserId_CategoryId_Month_Year",
                table: "Budgets");

            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_AccountId_CreatedAtUtc",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Categories");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "Budgets");

            migrationBuilder.DropColumn(
                name: "AccountId",
                table: "AuditLogs");

            migrationBuilder.CreateIndex(
                name: "IX_Categories_UserId",
                table: "Categories",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Budgets_UserId_CategoryId_Month_Year",
                table: "Budgets",
                columns: new[] { "UserId", "CategoryId", "Month", "Year" },
                unique: true);
        }
    }
}
