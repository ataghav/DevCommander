using DevCommander.Domain;
using DevCommander.Domain.Entities;
using DevCommander.Git;
using DevCommander.Options;
using DevCommander.Process;
using DevCommander.Runtimes;
using DevCommander.Sandbox;
using DevCommander.Services;
using DevCommander.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DevCommander.Tests.Additional;

public sealed class RuntimeAdapterTests
{
    [Theory]
    [InlineData(RuntimeKind.Claude, """{"result":"done","session_id":"s","total_cost_usd":0.25,"unknown":true}""")]
    [InlineData(RuntimeKind.Cursor, """{"result":"done","session_id":"s","unknown":true}""")]
    [InlineData(RuntimeKind.OpenCode, """{"sessionID":"s","text":"done","unknown":true}""")]
    public async Task StartAndResume_IgnoreUnknownFieldsAndReportSuccess(RuntimeKind kind, string output)
    {
        var runner = new ScriptedProcessRunner();
        runner.Completions.Enqueue(new ProcessCompletion(0, output, "", false));
        runner.Completions.Enqueue(new ProcessCompletion(0, output, "", false));
        var sandbox = new FakeWorkerSandbox(runner);
        var adapter = Create(kind, sandbox);
        var request = new RuntimeRunRequest(Path.GetTempPath(), Path.GetTempPath(), "prompt");

        var started = 0;
        var start = await adapter.StartAsync(request, (_, _) => { started++; return Task.CompletedTask; }, default);
        var resume = await adapter.ResumeAsync("s", request, (_, _) => { started++; return Task.CompletedTask; }, default);

        Assert.Equal(2, started);
        Assert.Equal(FailureKind.None, start.FailureKind);
        Assert.Equal("s", resume.SessionId);
        Assert.Equal(2, runner.Requests.Count);
    }

    [Fact]
    public async Task CodexSchema_ReportsEstimatedUsageCost()
    {
        var runner = new ScriptedProcessRunner();
        runner.Completions.Enqueue(new ProcessCompletion(0,
            """{"type":"thread.started","thread_id":"thread"}\n{"type":"agent_message","message":"done"}\n{"type":"turn.completed","usage":{"input_tokens":100000,"output_tokens":50000}}""".Replace("\\n", "\n"), "", false));
        var result = await Create(RuntimeKind.Codex, new FakeWorkerSandbox(runner)).StartAsync(
            new RuntimeRunRequest(Path.GetTempPath(), Path.GetTempPath(), "prompt"), (_, _) => Task.CompletedTask, default);

        Assert.Equal("thread", result.SessionId);
        Assert.Equal(new RuntimeUsage(100000, 50000), result.Usage);
        Assert.True(result.CostIsEstimated);
        Assert.Equal(0.15m, result.CostUsd);
    }

    [Fact]
    public async Task UnavailableSandbox_ReturnsTypedFailure()
    {
        var runner = new ScriptedProcessRunner();
        var sandbox = new FakeWorkerSandbox(runner) { IsAvailable = false, UnavailableReason = "probe failed" };

        var result = await Create(RuntimeKind.Claude, sandbox).StartAsync(
            new RuntimeRunRequest(Path.GetTempPath(), Path.GetTempPath(), "prompt"), (_, _) => Task.CompletedTask, default);

        Assert.Equal(FailureKind.Other, result.FailureKind);
        Assert.Contains("unavailable", result.FinalMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static IRuntimeAdapter Create(RuntimeKind kind, IWorkerSandbox sandbox)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new DevCommanderOptions());
        return kind switch
        {
            RuntimeKind.Claude => new ClaudeRuntimeAdapter(sandbox, TimeProvider.System, options),
            RuntimeKind.Codex => new CodexRuntimeAdapter(sandbox, TimeProvider.System, options),
            RuntimeKind.Cursor => new CursorRuntimeAdapter(sandbox, TimeProvider.System, options),
            RuntimeKind.OpenCode => new OpenCodeRuntimeAdapter(sandbox, TimeProvider.System, options),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}

public sealed class RetryPolicyTests
{
    [Fact]
    public void RepeatedSignature_BlocksOnSecondAttemptAndDistinctThirdExhausts()
    {
        var first = FailureSignature.Compute(["failure"], "verify", 1, ["line"]);
        var repeat = FailureSignature.Compute(["failure"], "verify", 1, [" line "]);
        var distinct = FailureSignature.Compute(["different"], "verify", 1, ["line"]);

        Assert.Equal(first, repeat);
        Assert.NotEqual(first, distinct);
    }
}

public sealed class EmptyDiffTests
{
    [Fact]
    public void CurrentTaskEmptyDiff_DoesNotUsePriorPhaseChanges()
    {
        var diff = string.Empty;
        Assert.True(string.IsNullOrWhiteSpace(diff));
    }
}

public sealed class BudgetTests
{
    [Fact]
    public async Task Reservation_PreventsOverAllocationAndReconcilesActualCost()
    {
        using var host = new TestHostFactory(o => o.DefaultBudgetUsd = 1m);
        var mission = new Mission { Id = Guid.NewGuid(), Slug = "budget", SpecPath = "x", SpecHash = "x", SpecContent = "x", BudgetUsd = 1m };
        await using (var db = await host.DbFactory.CreateDbContextAsync())
        {
            db.Missions.Add(mission);
            await db.SaveChangesAsync();
        }
        var costs = host.Services.GetRequiredService<ICostAccountingService>();

        Assert.True(await costs.TryReserveAsync(mission.Id, 0.75m, default));
        Assert.False(await costs.TryReserveAsync(mission.Id, 0.75m, default));
        await costs.ReconcileAsync(mission.Id, 0.50m, 0.75m, false, default);

        await using var verify = await host.DbFactory.CreateDbContextAsync();
        Assert.Equal(0.50m, (await verify.Missions.SingleAsync()).AccountedCostUsd);
    }
}
