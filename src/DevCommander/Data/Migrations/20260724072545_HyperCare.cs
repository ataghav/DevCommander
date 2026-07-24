using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevCommander.Data.Migrations
{
    /// <inheritdoc />
    public partial class HyperCare : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HyperCareEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IssueId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HyperCareEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HyperCareIssues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ShortId = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    ServiceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Signature = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    RepoId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Summary = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AttributesJson = table.Column<string>(type: "TEXT", nullable: false),
                    TelegramMessageId = table.Column<int>(type: "INTEGER", nullable: true),
                    CardOccurrenceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CardStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    LastCardTouchAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SuppressReason = table.Column<string>(type: "TEXT", nullable: true),
                    HoldPreferred = table.Column<bool>(type: "INTEGER", nullable: false),
                    MissionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PrUrl = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HyperCareIssues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HyperCareSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    MaxConcurrency = table.Column<int>(type: "INTEGER", nullable: false),
                    BudgetUsd = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    AccountedCostUsd = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    DefaultSeverity = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultPriority = table.Column<int>(type: "INTEGER", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    StoppedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HyperCareSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HyperCareSourceHealths",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ServiceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastErrorAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HyperCareSourceHealths", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HyperCareEvents_SessionId_At",
                table: "HyperCareEvents",
                columns: new[] { "SessionId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_HyperCareIssues_SessionId_ServiceId_Signature",
                table: "HyperCareIssues",
                columns: new[] { "SessionId", "ServiceId", "Signature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HyperCareIssues_SessionId_ShortId",
                table: "HyperCareIssues",
                columns: new[] { "SessionId", "ShortId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HyperCareIssues_SessionId_Status",
                table: "HyperCareIssues",
                columns: new[] { "SessionId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_HyperCareSessions_Status",
                table: "HyperCareSessions",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_HyperCareSourceHealths_SessionId_ServiceId",
                table: "HyperCareSourceHealths",
                columns: new[] { "SessionId", "ServiceId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HyperCareEvents");

            migrationBuilder.DropTable(
                name: "HyperCareIssues");

            migrationBuilder.DropTable(
                name: "HyperCareSessions");

            migrationBuilder.DropTable(
                name: "HyperCareSourceHealths");
        }
    }
}
