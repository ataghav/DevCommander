using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DevCommander.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_memories",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    scope_key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    idx = table.Column<int>(type: "INTEGER", nullable: false),
                    title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    content = table.Column<string>(type: "TEXT", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_memories", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "agent_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    last_activity_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    summary_text = table.Column<string>(type: "TEXT", nullable: true),
                    summary_up_to_turn = table.Column<long>(type: "INTEGER", nullable: true),
                    summary_updated_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ApprovalRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SquadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Attempt = table.Column<int>(type: "INTEGER", nullable: false),
                    CommandIndex = table.Column<int>(type: "INTEGER", nullable: false),
                    CommandHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Operation = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DecidedByChatId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApprovalRequests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Missions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Slug = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    SpecPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    SpecHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SpecContent = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    BudgetUsd = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    AccountedCostUsd = table.Column<decimal>(type: "TEXT", precision: 18, scale: 6, nullable: false),
                    Deadline = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PhaseSummariesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Missions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    LogicalKey = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Repos",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    DefaultBranch = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    DefaultRuntime = table.Column<int>(type: "INTEGER", nullable: false),
                    VerifyCommandsJson = table.Column<string>(type: "TEXT", nullable: false),
                    GatedOpsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Repos", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "TelegramUpdates",
                columns: table => new
                {
                    UpdateId = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramUpdates", x => x.UpdateId);
                });

            migrationBuilder.CreateTable(
                name: "agent_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    session_id = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    turn_number = table.Column<long>(type: "INTEGER", nullable: false),
                    role = table.Column<int>(type: "INTEGER", nullable: false),
                    parts_json = table.Column<string>(type: "TEXT", nullable: false),
                    tool_call_id = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_messages", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_messages_agent_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "agent_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Squads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RepoId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WorktreePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Branch = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    BaseCommit = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Runtime = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    LastPid = table.Column<int>(type: "INTEGER", nullable: true),
                    ProcessStartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SessionId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CurrentTaskId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    RunGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    LastCommittedSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Pushed = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastGuidance = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Squads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Squads_Missions_MissionId",
                        column: x => x.MissionId,
                        principalTable: "Missions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SquadEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SquadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: false),
                    At = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SquadEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SquadEvents_Squads_SquadId",
                        column: x => x.SquadId,
                        principalTable: "Squads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MissionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SquadId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Phase = table.Column<int>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastErrorSignature = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BaselineCommit = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Evidence = table.Column<string>(type: "TEXT", nullable: true),
                    CompletedCommitSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PhaseSummary = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tasks_Missions_MissionId",
                        column: x => x.MissionId,
                        principalTable: "Missions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Tasks_Squads_SquadId",
                        column: x => x.SquadId,
                        principalTable: "Squads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_memories_created_at",
                table: "agent_memories",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "IX_agent_memories_scope_key_idx",
                table: "agent_memories",
                columns: new[] { "scope_key", "idx" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_messages_session_id_turn_number",
                table: "agent_messages",
                columns: new[] { "session_id", "turn_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_sessions_last_activity_at",
                table: "agent_sessions",
                column: "last_activity_at");

            migrationBuilder.CreateIndex(
                name: "IX_ApprovalRequests_MissionId_SquadId_TaskId_Attempt_CommandIndex_CommandHash",
                table: "ApprovalRequests",
                columns: new[] { "MissionId", "SquadId", "TaskId", "Attempt", "CommandIndex", "CommandHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Missions_Slug",
                table: "Missions",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_LogicalKey",
                table: "Notifications",
                column: "LogicalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_State_NextAttemptAt",
                table: "Notifications",
                columns: new[] { "State", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SquadEvents_SquadId_At",
                table: "SquadEvents",
                columns: new[] { "SquadId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Squads_MissionId_RepoId",
                table: "Squads",
                columns: new[] { "MissionId", "RepoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_MissionId",
                table: "Tasks",
                column: "MissionId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_SquadId_Phase_Id",
                table: "Tasks",
                columns: new[] { "SquadId", "Phase", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramUpdates_ChatId_UpdateId",
                table: "TelegramUpdates",
                columns: new[] { "ChatId", "UpdateId" });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramUpdates_State_ReceivedAt",
                table: "TelegramUpdates",
                columns: new[] { "State", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_memories");

            migrationBuilder.DropTable(
                name: "agent_messages");

            migrationBuilder.DropTable(
                name: "ApprovalRequests");

            migrationBuilder.DropTable(
                name: "Notifications");

            migrationBuilder.DropTable(
                name: "Repos");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "SquadEvents");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "TelegramUpdates");

            migrationBuilder.DropTable(
                name: "agent_sessions");

            migrationBuilder.DropTable(
                name: "Squads");

            migrationBuilder.DropTable(
                name: "Missions");
        }
    }
}
