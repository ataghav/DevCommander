using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevCommander.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentCostEntries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentCostEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentRole = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    MissionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TotalCostUsd = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    LlmCostUsd = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCostEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCostEntries_AgentRole_At",
                table: "AgentCostEntries",
                columns: new[] { "AgentRole", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentCostEntries_MissionId",
                table: "AgentCostEntries",
                column: "MissionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentCostEntries");
        }
    }
}
