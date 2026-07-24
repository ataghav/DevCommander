using DevCommander.Data;
using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.HyperCare;
using DevCommander.HyperCare.Watching;
using DevCommander.Integrations.Telegram;
using DevCommander.Sandbox;
using DevCommander.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace DevCommander.Tests;

public sealed class HyperCareActivationTests
{
    [Fact]
    public async Task HcOn_WithValidConfig_ActivatesAndRegistersHyperCareMenu()
    {
        // FR-HC-002 / FR-HC-031
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig());

        var session = await host.ActivateAsync();

        Assert.Equal(HyperCareSessionStatus.Running, session.Status);
        Assert.Equal(2, session.MaxConcurrency);
        Assert.Equal(HyperCareSeverity.Medium, session.DefaultSeverity);
        var menu = Assert.Single(host.Messenger.CommandMenus);
        Assert.Contains(menu, c => c.Command == "/go");
        Assert.Contains(menu, c => c.Command == "/hc_off");
    }

    [Fact]
    public async Task HcOn_FailsClosed_NamingEveryProblem()
    {
        // FR-HC-003 / BR-HC-009 / BR-HC-015
        using var host = new HyperCareTestHost();
        host.WriteConfig(new
        {
            maxConcurrency = 0,
            budgetUsd = 10m,
            grafana = new { baseUrl = "https://grafana.test/", tokenEnvVar = "HC_UNSET_VAR_XYZ" },
            services = new object[]
            {
                new { id = "svc", repoId = "ghost", include = new[] { "ERROR" } },
                new { id = "svc", repoId = "", include = new[] { "ERROR" } },
            },
        });

        var reply = await host.Commands.ActivateAsync(42, default);

        Assert.Contains("activation failed", reply);
        Assert.Contains("maxConcurrency", reply);
        Assert.Contains("Duplicate service id 'svc'", reply);
        Assert.Contains("unknown repoId 'ghost'", reply);
        Assert.Contains("exactly one repoId", reply);
        Assert.Contains("HC_UNSET_VAR_XYZ", reply);
        await using var db = await host.DbFactory.CreateDbContextAsync();
        Assert.Empty(await db.HyperCareSessions.ToListAsync());
        Assert.Empty(host.Messenger.CommandMenus);
    }

    [Fact]
    public async Task StartMission_RefusedWhileHyperCareActive()
    {
        // FR-HC-001 / BR-HC-001
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig());
        await host.ActivateAsync();

        var reply = await host.MissionCommands.StartAsync("release", 42, default);

        Assert.Contains("Hyper-Care mode is active", reply);
        await using var db = await host.DbFactory.CreateDbContextAsync();
        Assert.Empty(await db.Missions.ToListAsync());
    }

    [Fact]
    public async Task HcOff_FreezesDecisions_AndRestoresNormalMenu()
    {
        // FR-HC-004 / BR-HC-017
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig());
        var session = await host.ActivateAsync();
        var issue = await host.Issues.UpsertOccurrenceAsync(
            session, "checkout-api", "sig-a", "summary", "ERROR sample", "checkout", 1, default);

        var offReply = await host.Commands.DeactivateAsync(42, default);
        var goReply = await host.Commands.GoAsync(issue.ShortId, null, default);

        Assert.Contains("deactivated", offReply);
        Assert.Contains("rejected", goReply);
        var frozen = await host.GetIssueAsync(issue.Id);
        Assert.Equal(HyperCareIssueStatus.AwaitingDecision, frozen.Status);
        var lastMenu = host.Messenger.CommandMenus[^1];
        Assert.Contains(lastMenu, c => c.Command == "/start");
        Assert.DoesNotContain(lastMenu, c => c.Command == "/go");
    }
}

public sealed class HyperCareWatcherTests
{
    private const string GrafanaErrors =
        """
        {"results":{"A":{"frames":[{"data":{"values":[[
            "ERROR NullReferenceException in PaymentService.Capture id=8123",
            "ERROR NullReferenceException in PaymentService.Capture id=9944",
            "INFO request served",
            "healthcheck error probe ok"
        ]]}}]}}}
        """;

    [Fact]
    public async Task Watcher_FiltersNoiseImperatively_TriagesOnceWithBoundedContext()
    {
        // FR-HC-011/012, NFR-HC-02/09, BR-HC-010
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig());
        var session = await host.ActivateAsync();
        var watcher = host.CreateWatcher(session);
        host.Grafana.Responses.Enqueue(GrafanaErrors);
        host.Triage.Results.Enqueue(new TriageResult(
            true, "real fault", "nullref.paymentservice.capture", "NullReferenceException in PaymentService.Capture"));

        await watcher.PollOnceAsync(default);

        Assert.Equal(1, host.Triage.Calls);
        var context = Assert.Single(host.Triage.Contexts);
        Assert.Contains("PaymentService.Capture", context);
        Assert.DoesNotContain("INFO request served", context);
        Assert.DoesNotContain("healthcheck", context);
        Assert.True(context.Length <= 32_768);
        var issue = Assert.Single(await host.GetIssuesAsync(session.Id));
        Assert.Equal(2, issue.OccurrenceCount);
        Assert.Equal("nullref.paymentservice.capture", issue.Signature);
    }

    [Fact]
    public async Task Watcher_EmptyWindow_MakesNoTriageCalls()
    {
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig());
        var session = await host.ActivateAsync();
        var watcher = host.CreateWatcher(session);

        await watcher.PollOnceAsync(default);

        Assert.Equal(0, host.Triage.Calls);
        Assert.Empty(await host.GetIssuesAsync(session.Id));
    }

    [Fact]
    public async Task Watcher_CachedSignature_SkipsTriageOnRepeat()
    {
        // NFR-HC-02: repeats of a known signature never re-pay the LLM.
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig());
        var session = await host.ActivateAsync();
        var watcher = host.CreateWatcher(session);
        host.Grafana.Responses.Enqueue(GrafanaErrors);
        host.Grafana.Responses.Enqueue(GrafanaErrors);
        host.Triage.Results.Enqueue(new TriageResult(true, "real", "nullref.capture", "NRE in Capture"));

        await watcher.PollOnceAsync(default);
        await watcher.PollOnceAsync(default);

        Assert.Equal(1, host.Triage.Calls);
        var issue = Assert.Single(await host.GetIssuesAsync(session.Id));
        Assert.Equal(4, issue.OccurrenceCount);
    }

    [Fact]
    public async Task TriageReject_LeavesEventTrailButNoIssue()
    {
        // FR-HC-013
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig());
        var session = await host.ActivateAsync();
        var watcher = host.CreateWatcher(session);
        host.Grafana.Responses.Enqueue(GrafanaErrors);
        host.Triage.Results.Enqueue(new TriageResult(false, "expected transient", "n/a", "noise"));

        await watcher.PollOnceAsync(default);

        Assert.Empty(await host.GetIssuesAsync(session.Id));
        await using var db = await host.DbFactory.CreateDbContextAsync();
        Assert.True(await db.HyperCareEvents.AnyAsync(e => e.Kind == "TriageRejected"));
    }

    [Fact]
    public async Task HcStatus_SourceHealthFromDb_SurvivesRestart()
    {
        // FR-HC-032 / NFR-HC-07: /hc_status source health comes from durable DB state, so an
        // empty in-memory registry (fresh process) still reports the last-known health.
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig());
        var session = await host.ActivateAsync();
        var watcher = host.CreateWatcher(session);
        host.Grafana.QueryFailure = new InvalidOperationException("grafana unreachable");

        await watcher.PollOnceAsync(default);
        host.WatcherDeps.Health.Reset();   // simulate restart: in-memory health is gone

        var degraded = await host.Commands.StatusAsync(default);
        Assert.Contains("checkout-api: degraded", degraded);
        Assert.Contains("grafana unreachable", degraded);

        host.Grafana.QueryFailure = null;
        await watcher.PollOnceAsync(default);
        host.WatcherDeps.Health.Reset();

        var ok = await host.Commands.StatusAsync(default);
        Assert.Contains("checkout-api: ok", ok);
        Assert.DoesNotContain("degraded", ok);
    }

    [Fact]
    public async Task SameSignatureOnTwoServices_CreatesTwoIssues()
    {
        // FR-HC-020 / BR-HC-002: the key includes serviceId.
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("repo-a");
        await host.RegisterRepoAsync("repo-b");
        host.WriteConfig(host.DefaultConfig(servicesOverride: [("svc-a", "repo-a"), ("svc-b", "repo-b")]));
        var session = await host.ActivateAsync();

        await host.Issues.UpsertOccurrenceAsync(session, "svc-a", "same-sig", "s", "e", "repo-a", 1, default);
        await host.Issues.UpsertOccurrenceAsync(session, "svc-b", "same-sig", "s", "e", "repo-b", 1, default);

        Assert.Equal(2, (await host.GetIssuesAsync(session.Id)).Count);
    }
}

public sealed class HyperCareCardTests
{
    [Fact]
    public async Task TenIdenticalHits_OneIssueOneCard_MessageIdStored()
    {
        // FR-HC-020/021/022, FR-HC-033
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig());
        var session = await host.ActivateAsync();
        for (var i = 0; i < 10; i++)
        {
            await host.Issues.UpsertOccurrenceAsync(session, "checkout-api", "sig", "NRE", "ERROR x", "checkout", 1, default);
        }

        await host.Coordinator.TickAsync(default);

        var issue = Assert.Single(await host.GetIssuesAsync(session.Id));
        Assert.Equal(10, issue.OccurrenceCount);
        Assert.NotNull(issue.TelegramMessageId);
        var card = Assert.Single(host.Messenger.Cards);
        Assert.Contains($"/go_{issue.ShortId}", card.Text);
        Assert.Contains($"/nogo_{issue.ShortId}", card.Text);
        Assert.Contains("medium (default)", card.Text);
    }

    [Fact]
    public async Task RepeatOccurrences_EditAtMostOncePerMinute_NeverASecondCard()
    {
        // NFR-HC-03
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig());
        var session = await host.ActivateAsync();
        var issue = await host.Issues.UpsertOccurrenceAsync(session, "checkout-api", "sig", "NRE", "ERROR x", "checkout", 1, default);
        await host.Coordinator.TickAsync(default);
        Assert.Single(host.Messenger.Cards);

        await host.Issues.UpsertOccurrenceAsync(session, "checkout-api", "sig", "NRE", "ERROR x", "checkout", 5, default);
        await host.Coordinator.TickAsync(default);
        Assert.Empty(host.Messenger.Edits); // throttled: within 60s of the initial card

        host.Time.Advance(TimeSpan.FromSeconds(61));
        await host.Coordinator.TickAsync(default);

        var edit = Assert.Single(host.Messenger.Edits);
        Assert.Contains("Seen 6×", edit.Text);
        Assert.Single(host.Messenger.Cards);
        var updated = await host.GetIssueAsync(issue.Id);
        Assert.Equal(6, updated.CardOccurrenceCount);
    }
}

public sealed class HyperCareDecisionTests
{
    [Fact]
    public async Task Go_IsIdempotent_AndGoAfterNogoIsRejected()
    {
        // FR-HC-034 / BR-HC-004, §12
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig());
        var session = await host.ActivateAsync();
        var a = await host.Issues.UpsertOccurrenceAsync(session, "checkout-api", "sig-a", "A", "e", "checkout", 1, default);
        var b = await host.Issues.UpsertOccurrenceAsync(session, "checkout-api", "sig-b", "B", "e", "checkout", 1, default);

        Assert.Contains("accepted", await host.Commands.GoAsync(a.ShortId, "high", default));
        Assert.Contains("already accepted", await host.Commands.GoAsync(a.ShortId, null, default));
        Assert.Contains("suppressed", await host.Commands.NoGoAsync(b.ShortId, default));
        Assert.Contains("cannot be accepted", await host.Commands.GoAsync(b.ShortId, null, default));

        var issueA = await host.GetIssueAsync(a.Id);
        Assert.Equal(HyperCareIssueStatus.Queued, issueA.Status);
        Assert.Equal(HyperCareSeverity.High, issueA.Severity); // /go severity override
        Assert.Equal(HyperCareIssueStatus.Suppressed, (await host.GetIssueAsync(b.Id)).Status);
    }

    [Fact]
    public async Task UnknownIssueId_ErrorsWithoutStateChange()
    {
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig());
        await host.ActivateAsync();

        Assert.Contains("Unknown issue id", await host.Commands.GoAsync("deadbeef", null, default));
    }

    [Fact]
    public async Task SeverityAndPriority_UpdateNonTerminal_AndReorderQueue()
    {
        // FR-HC-033/035, FR-HC-041 ordering
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("repo-a");
        await host.RegisterRepoAsync("repo-b");
        host.WriteConfig(host.DefaultConfig(maxConcurrency: 1, servicesOverride: [("svc-a", "repo-a"), ("svc-b", "repo-b")]));
        var session = await host.ActivateAsync();
        var older = await host.Issues.UpsertOccurrenceAsync(session, "svc-a", "sig-a", "A", "e", "repo-a", 1, default);
        var newer = await host.Issues.UpsertOccurrenceAsync(session, "svc-b", "sig-b", "B", "e", "repo-b", 1, default);
        await host.Commands.GoAsync(older.ShortId, null, default);
        await host.Commands.GoAsync(newer.ShortId, null, default);
        await host.Commands.SetPriorityAsync(newer.ShortId, "10", default);

        await host.Coordinator.TickAsync(default);

        // Priority beats first-seen: the newer, higher-priority issue claims the single slot.
        Assert.Equal(HyperCareIssueStatus.Running, (await host.GetIssueAsync(newer.Id)).Status);
        Assert.Equal(HyperCareIssueStatus.Queued, (await host.GetIssueAsync(older.Id)).Status);
    }
}

public sealed class HyperCareSchedulerTests
{
    [Fact]
    public async Task SameRepo_SerializesEvenWithFreeCapacity()
    {
        // FR-HC-042 / BR-HC-006, NFR-HC-05
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig(maxConcurrency: 2));
        var session = await host.ActivateAsync();
        var a = await host.Issues.UpsertOccurrenceAsync(session, "checkout-api", "sig-a", "A", "e", "checkout", 1, default);
        var b = await host.Issues.UpsertOccurrenceAsync(session, "checkout-api", "sig-b", "B", "e", "checkout", 1, default);
        await host.Commands.GoAsync(a.ShortId, null, default);
        await host.Commands.GoAsync(b.ShortId, null, default);

        await host.Coordinator.TickAsync(default);

        var issues = await host.GetIssuesAsync(session.Id);
        Assert.Equal(1, issues.Count(i => i.Status == HyperCareIssueStatus.Running));
        Assert.Equal(1, issues.Count(i => i.Status == HyperCareIssueStatus.Queued));
    }

    [Fact]
    public async Task Hold_PreemptsRunningSameRepoTrack_AndUnholdRequeues()
    {
        // FR-HC-042 hold/unhold
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig(maxConcurrency: 1));
        var session = await host.ActivateAsync();
        var missionId = Guid.NewGuid();
        Guid runningId;
        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            var mission = new Mission
            {
                Id = missionId, Slug = "hc-runner", SpecPath = "hypercare://x", SpecHash = "x",
                SpecContent = "x", Status = MissionStatus.Running, ChatId = 42,
            };
            db.Missions.Add(mission);
            db.Squads.Add(new Squad
            {
                Id = Guid.NewGuid(), MissionId = missionId, RepoId = "checkout", WorktreePath = "x",
                Branch = "hypercare/x/y", Status = SquadStatus.Running,
            });
            runningId = Guid.NewGuid();
            db.HyperCareIssues.Add(new HyperCareIssue
            {
                Id = runningId, SessionId = session.Id, ShortId = "aaaa1111", ServiceId = "checkout-api",
                Signature = "sig-run", RepoId = "checkout", Summary = "running", MissionId = missionId,
                Branch = "hypercare/x/y", Status = HyperCareIssueStatus.Running,
                FirstSeenAt = host.Time.GetUtcNow(), LastSeenAt = host.Time.GetUtcNow(), OccurrenceCount = 1,
            });
            await db.SaveChangesAsync();
        }

        var preferred = await host.Issues.UpsertOccurrenceAsync(
            session, "checkout-api", "sig-pref", "preferred", "e", "checkout", 1, default);
        await host.Commands.GoAsync(preferred.ShortId, null, default);

        var holdReply = await host.Commands.HoldAsync(preferred.ShortId, default);

        Assert.Contains("paused", holdReply);
        Assert.Equal(HyperCareIssueStatus.Held, (await host.GetIssueAsync(runningId)).Status);
        Assert.True((await host.GetIssueAsync(preferred.Id)).HoldPreferred);
        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            Assert.Equal(SquadStatus.Stopped, (await db.Squads.SingleAsync(s => s.MissionId == missionId)).Status);
        }

        var unholdReply = await host.Commands.UnholdAsync("aaaa1111", default);
        Assert.Contains("returned to the queue", unholdReply);
        Assert.Equal(HyperCareIssueStatus.Queued, (await host.GetIssueAsync(runningId)).Status);
    }
}

public sealed class HyperCareFixTrackTests
{
    [Fact]
    public async Task FullFixTrack_InvestigateNotPlanner_PushedHyperCareBranch_PrStored_HandedOver()
    {
        // FR-HC-040/043/044/046, BR-HC-007/014, FR-HC-024 recurrence
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig());
        var session = await host.ActivateAsync();
        var issue = await host.Issues.UpsertOccurrenceAsync(
            session, "checkout-api", "nullref.capture", "NRE in PaymentService.Capture", "ERROR x", "checkout", 3, default);
        await host.Commands.GoAsync(issue.ShortId, null, default);

        await host.TickUntilAsync(async () =>
            (await host.GetIssueAsync(issue.Id)).Status == HyperCareIssueStatus.HandedOver);

        var done = await host.GetIssueAsync(issue.Id);
        Assert.Equal(host.GitHub.PrUrl, done.PrUrl);
        Assert.Equal($"hypercare/{session.ShortId}/{issue.ShortId}", done.Branch);
        Assert.Equal(0, host.Planner.Calls);              // BR-HC-014: planner never runs
        Assert.Equal(1, host.Investigate.Calls);
        var push = Assert.Single(host.Git.Pushes);
        Assert.Equal(done.Branch, push.Branch);
        var pr = Assert.Single(host.GitHub.CreatedPrs);
        Assert.Equal(done.Branch, pr.Head);
        Assert.Equal("main", pr.Base);

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            var mission = await db.Missions.AsNoTracking().SingleAsync(m => m.Id == done.MissionId);
            Assert.Equal($"hc-{issue.ShortId}", mission.Slug);
            Assert.Equal(MissionStatus.Completed, mission.Status);
            Assert.Contains("Root cause:", mission.SpecContent);
            Assert.True(await db.Notifications.AnyAsync(n => n.LogicalKey == $"hc-handover:{issue.Id:N}"));
            foreach (var kind in new[] { "IssueCreated", "IssueQueued", "IssueClaimed", "FixTrackStarted", "IssueHandedOver" })
            {
                Assert.True(await db.HyperCareEvents.AnyAsync(e => e.Kind == kind), $"missing event {kind}");
            }
        }

        // Recurrence after HandedOver: count-only, no reopen, no second track (FR-HC-024 / BR-HC-013).
        var cardsBefore = host.Messenger.Cards.Count;
        await host.Issues.UpsertOccurrenceAsync(
            session, "checkout-api", "nullref.capture", "NRE in PaymentService.Capture", "ERROR x", "checkout", 2, default);
        await host.Coordinator.TickAsync(default);
        var after = await host.GetIssueAsync(issue.Id);
        Assert.Equal(HyperCareIssueStatus.HandedOver, after.Status);
        Assert.Equal(5, after.OccurrenceCount);
        Assert.Equal(done.MissionId, after.MissionId);
        Assert.Equal(cardsBefore, host.Messenger.Cards.Count);
    }

    [Fact]
    public async Task GhFailureAfterPush_BlocksIssueNamingBranch()
    {
        // Workflow 11.5: gh fails after push ⇒ Blocked + human notification.
        using var host = new HyperCareTestHost();
        host.GitHub.CreateFailure = new InvalidOperationException("gh exploded");
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(host.DefaultConfig());
        var session = await host.ActivateAsync();
        var issue = await host.Issues.UpsertOccurrenceAsync(
            session, "checkout-api", "sig", "NRE", "ERROR x", "checkout", 1, default);
        await host.Commands.GoAsync(issue.ShortId, null, default);

        await host.TickUntilAsync(async () =>
            (await host.GetIssueAsync(issue.Id)).Status == HyperCareIssueStatus.Blocked);

        var blocked = await host.GetIssueAsync(issue.Id);
        Assert.Single(host.Git.Pushes);
        Assert.Null(blocked.PrUrl);
        await using var db = await host.DbFactory.CreateDbContextAsync();
        var notification = await db.Notifications.SingleAsync(n => n.LogicalKey == $"hc-blocked:{issue.Id:N}");
        Assert.Contains(blocked.Branch!, notification.Body);
    }
}

public sealed class HyperCareBudgetTests
{
    [Fact]
    public async Task BudgetBelowReservation_HaltsSession_OneNotification_CountingContinues()
    {
        // FR-HC-051/052 / BR-HC-016
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        host.WriteConfig(new
        {
            maxConcurrency = 1,
            budgetUsd = 0.04m,
            fixTrackBudgetUsd = 0.04m,
            triageEstimateUsd = 0.05m,
            investigateEstimateUsd = 0.05m,
            grafana = new { baseUrl = "https://grafana.test/", tokenEnvVar = HyperCareTestHost.GrafanaTokenEnvVar },
            services = new[]
            {
                new
                {
                    id = "checkout-api",
                    repoId = "checkout",
                    grafanaQueries = new[] { new { name = "errors", method = "POST", path = "api/ds/query", bodyTemplate = "{}" } },
                    include = new[] { "ERROR" },
                },
            },
        });
        var session = await host.ActivateAsync();
        var preexisting = await host.Issues.UpsertOccurrenceAsync(
            session, "checkout-api", "known-sig", "known", "e", "checkout", 1, default);
        var watcher = host.CreateWatcher(session);
        host.Grafana.Responses.Enqueue("""{"line":"ERROR something new broke"}""");
        host.Grafana.Responses.Enqueue("""{"line":"ERROR another new thing"}""");

        await watcher.PollOnceAsync(default);   // triage reservation fails ⇒ BudgetHalted
        await watcher.PollOnceAsync(default);   // already halted ⇒ skipped, no second notification

        await using var db = await host.DbFactory.CreateDbContextAsync();
        var halted = await db.HyperCareSessions.AsNoTracking().SingleAsync(s => s.Id == session.Id);
        Assert.Equal(HyperCareSessionStatus.BudgetHalted, halted.Status);
        Assert.Equal(0, host.Triage.Calls);
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.LogicalKey.StartsWith("hc-budget:")));

        // Occurrence counting survives the halt (no LLM involved).
        await host.Issues.UpsertOccurrenceAsync(session, "checkout-api", "known-sig", "known", "e", "checkout", 4, default);
        Assert.Equal(5, (await host.GetIssueAsync(preexisting.Id)).OccurrenceCount);
    }
}

public sealed class HyperCareRecoveryTests
{
    [Fact]
    public async Task RestartWithRunningSession_ResumesAndSummarizesOnce()
    {
        // FR-HC-006 / NFR-HC-11
        using var host = new HyperCareTestHost();
        await host.RegisterRepoAsync("checkout");
        var snapshot = System.Text.Json.JsonSerializer.Serialize(host.DefaultConfig());
        var sessionId = Guid.NewGuid();
        var missionId = Guid.NewGuid();
        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.HyperCareSessions.Add(new HyperCareSession
            {
                Id = sessionId, Status = HyperCareSessionStatus.Running, ConfigSnapshot = snapshot,
                ConfigHash = "x", MaxConcurrency = 2, BudgetUsd = 25m, ChatId = 42,
                StartedAt = host.Time.GetUtcNow() - TimeSpan.FromHours(1), // predates this "boot"
            });
            db.Missions.Add(new Mission
            {
                Id = missionId, Slug = "hc-old", SpecPath = "hypercare://x", SpecHash = "x",
                SpecContent = "x", Status = MissionStatus.Blocked, ChatId = 42,
            });
            db.HyperCareIssues.Add(new HyperCareIssue
            {
                Id = Guid.NewGuid(), SessionId = sessionId, ShortId = "bbbb2222", ServiceId = "checkout-api",
                Signature = "sig", RepoId = "checkout", Summary = "s", Status = HyperCareIssueStatus.Running,
                MissionId = missionId, FirstSeenAt = host.Time.GetUtcNow(), LastSeenAt = host.Time.GetUtcNow(),
            });
            await db.SaveChangesAsync();
        }

        await host.Coordinator.TickAsync(default);

        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            var recovery = await db.Notifications.SingleAsync(n => n.LogicalKey.StartsWith($"hc-recovery:{sessionId:N}"));
            Assert.Contains("recovered after restart", recovery.Body);
            Assert.True(await db.HyperCareEvents.AnyAsync(e => e.Kind == "SessionRecovered"));
        }

        // A second tick of the same boot must not enqueue another summary.
        await host.Coordinator.TickAsync(default);
        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            Assert.Equal(1, await db.Notifications.CountAsync(n => n.LogicalKey.StartsWith("hc-recovery:")));
        }
    }
}

public sealed class HyperCareUnitTests
{
    [Fact]
    public void Sandbox_StripsGrafanaAndCliConfigEnv()
    {
        // BR-HC-008
        var environment = BubblewrapWorkerSandbox.SanitizeEnvironment(new Dictionary<string, string?>
        {
            ["HC_GRAFANA_TOKEN"] = "secret",
            ["MY_GRAFANA_KEY"] = "secret",
            ["GH_CONFIG_DIR"] = "/root/.config/gh",
            ["AZURE_CONFIG_DIR"] = "/root/.azure",
            ["SAFE_VALUE"] = "ok",
        });

        Assert.DoesNotContain("HC_GRAFANA_TOKEN", environment.Keys);
        Assert.DoesNotContain("MY_GRAFANA_KEY", environment.Keys);
        Assert.DoesNotContain("GH_CONFIG_DIR", environment.Keys);
        Assert.DoesNotContain("AZURE_CONFIG_DIR", environment.Keys);
        Assert.Equal("ok", environment["SAFE_VALUE"]);
    }

    [Fact]
    public void NormalizeCta_RewritesUnderscoreCommands()
    {
        // FR-HC-030 / ADR-HC-006
        Assert.Equal("/go ab12cd34", CommanderDispatcher.NormalizeCta("/go_ab12cd34"));
        Assert.Equal("/nogo ab12cd34", CommanderDispatcher.NormalizeCta("/nogo_ab12cd34"));
        Assert.Equal("/hold ab12cd34", CommanderDispatcher.NormalizeCta("/hold_ab12cd34"));
        Assert.Equal("/unhold ab12cd34", CommanderDispatcher.NormalizeCta("/unhold_ab12cd34"));
        Assert.Equal("/hc_on", CommanderDispatcher.NormalizeCta("/hc_on"));
        Assert.Equal("/go ab12 high", CommanderDispatcher.NormalizeCta("/go ab12 high"));
    }

    [Fact]
    public void LocalSignature_CollapsesVolatileValues()
    {
        var a = CandidateFilter.LocalSignature("ERROR NullReference id=8123 at 0xDEADBEEF11223344");
        var b = CandidateFilter.LocalSignature("ERROR NullReference id=999 at 0xFFFF000011112222");
        var c = CandidateFilter.LocalSignature("ERROR Timeout calling payments");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Redact_MasksConfiguredPatterns()
    {
        var patterns = CandidateFilter.CompileAll(["(?i)bearer [a-z0-9._-]+"]);
        Assert.Equal("auth [REDACTED] failed", CandidateFilter.Redact("auth Bearer abc.def-123 failed", patterns));
    }
}
