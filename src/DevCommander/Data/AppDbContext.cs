using DevCommander.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using NovaCore.Agents.Persistence.EntityFramework;

namespace DevCommander.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Repo> Repos => Set<Repo>();
    public DbSet<Mission> Missions => Set<Mission>();
    public DbSet<Squad> Squads => Set<Squad>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<SquadEvent> SquadEvents => Set<SquadEvent>();
    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<TelegramUpdate> TelegramUpdates => Set<TelegramUpdate>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ConfigureNovaAgents(schema: null);

        modelBuilder.Entity<Repo>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(128);
            e.Property(x => x.Source).HasMaxLength(1024).IsRequired();
            e.Property(x => x.DefaultBranch).HasMaxLength(256).IsRequired();
            e.Property(x => x.VerifyCommandsJson).IsRequired();
            e.Property(x => x.GatedOpsJson).IsRequired();
        });

        modelBuilder.Entity<Mission>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(128).IsRequired();
            e.Property(x => x.SpecPath).HasMaxLength(1024).IsRequired();
            e.Property(x => x.SpecHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.SpecContent).IsRequired();
            e.Property(x => x.BudgetUsd).HasPrecision(18, 6);
            e.Property(x => x.AccountedCostUsd).HasPrecision(18, 6);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasMany(x => x.Squads).WithOne(x => x.Mission).HasForeignKey(x => x.MissionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Tasks).WithOne(x => x.Mission).HasForeignKey(x => x.MissionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Squad>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.MissionId, x.RepoId }).IsUnique();
            e.Property(x => x.RepoId).HasMaxLength(128).IsRequired();
            e.Property(x => x.WorktreePath).HasMaxLength(1024).IsRequired();
            e.Property(x => x.Branch).HasMaxLength(256).IsRequired();
            e.Property(x => x.BaseCommit).HasMaxLength(64);
            e.Property(x => x.SessionId).HasMaxLength(256);
            e.Property(x => x.LastCommittedSha).HasMaxLength(64);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasMany(x => x.Tasks).WithOne(x => x.Squad).HasForeignKey(x => x.SquadId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasMany(x => x.Events).WithOne(x => x.Squad).HasForeignKey(x => x.SquadId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskItem>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SquadId, x.Phase, x.Id });
            e.Property(x => x.Description).IsRequired();
            e.Property(x => x.LastErrorSignature).HasMaxLength(128);
            e.Property(x => x.BaselineCommit).HasMaxLength(64);
            e.Property(x => x.CompletedCommitSha).HasMaxLength(64);
        });

        modelBuilder.Entity<SquadEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.SquadId, x.At });
            e.Property(x => x.Kind).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<ApprovalRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new
            {
                x.MissionId,
                x.SquadId,
                x.TaskId,
                x.Attempt,
                x.CommandIndex,
                x.CommandHash,
            }).IsUnique();
            e.Property(x => x.CommandHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.Operation).IsRequired();
        });

        modelBuilder.Entity<Notification>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.LogicalKey).IsUnique();
            e.Property(x => x.LogicalKey).HasMaxLength(256).IsRequired();
            e.Property(x => x.Body).IsRequired();
            e.Property(x => x.LeaseOwner).HasMaxLength(64);
            e.HasIndex(x => new { x.State, x.NextAttemptAt });
        });

        modelBuilder.Entity<TelegramUpdate>(e =>
        {
            e.HasKey(x => x.UpdateId);
            e.Property(x => x.Payload).IsRequired();
            e.Property(x => x.LeaseOwner).HasMaxLength(64);
            e.HasIndex(x => new { x.ChatId, x.UpdateId });
            e.HasIndex(x => new { x.State, x.ReceivedAt });
        });

        modelBuilder.Entity<AppSetting>(e =>
        {
            e.HasKey(x => x.Key);
            e.Property(x => x.Key).HasMaxLength(128);
        });
    }
}
