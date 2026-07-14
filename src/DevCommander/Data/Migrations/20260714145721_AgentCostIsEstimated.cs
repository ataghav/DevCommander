using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevCommander.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentCostIsEstimated : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEstimated",
                table: "AgentCostEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsEstimated",
                table: "AgentCostEntries");
        }
    }
}
