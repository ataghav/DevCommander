using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace DevCommander.Process;

public interface IProcessRunner
{
    Task<IProcessExecution> StartAsync(ProcessStartRequest request, CancellationToken ct);
}

public sealed record ProcessStartRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    int MaxOutputChars = 200_000);

public interface IProcessExecution
{
    int Pid { get; }
    DateTimeOffset StartedAt { get; }
    Task<ProcessCompletion> Completion { get; }
    Task KillTreeAsync(CancellationToken ct = default);
}

public sealed record ProcessCompletion(
    int ExitCode,
    string StdOut,
    string StdErr,
    bool OutputTruncated);

public sealed class ProcessRunner(TimeProvider time) : IProcessRunner
{
    public Task<IProcessExecution> StartAsync(ProcessStartRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment.Clear();
        foreach (var (key, value) in request.Environment)
        {
            if (value is not null)
            {
                psi.Environment[key] = value;
            }
        }

        var process = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{request.FileName}'.");
        }

        var startedAt = time.GetUtcNow();
        var execution = new ProcessExecution(process, startedAt, request.MaxOutputChars);
        return Task.FromResult<IProcessExecution>(execution);
    }
}

internal sealed class ProcessExecution : IProcessExecution
{
    private readonly System.Diagnostics.Process _process;
    private readonly TaskCompletionSource<ProcessCompletion> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly int _maxOutputChars;
    private int _truncated;
    private int _killed;

    public ProcessExecution(System.Diagnostics.Process process, DateTimeOffset startedAt, int maxOutputChars)
    {
        _process = process;
        StartedAt = startedAt;
        _maxOutputChars = maxOutputChars;
        Pid = process.Id;

        process.OutputDataReceived += (_, e) => Append(_stdout, e.Data);
        process.ErrorDataReceived += (_, e) => Append(_stderr, e.Data);
        process.Exited += (_, _) =>
        {
            try
            {
                process.WaitForExit();
                _tcs.TrySetResult(new ProcessCompletion(
                    process.ExitCode,
                    _stdout.ToString(),
                    _stderr.ToString(),
                    Interlocked.CompareExchange(ref _truncated, 0, 0) == 1));
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public int Pid { get; }
    public DateTimeOffset StartedAt { get; }
    public Task<ProcessCompletion> Completion => _tcs.Task;

    public async Task KillTreeAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _killed, 1) == 1)
        {
            await Completion.WaitAsync(ct);
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // already exited
        }

        await Completion.WaitAsync(ct);
    }

    private void Append(StringBuilder sb, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (sb)
        {
            if (sb.Length >= _maxOutputChars)
            {
                Interlocked.Exchange(ref _truncated, 1);
                return;
            }

            var remaining = _maxOutputChars - sb.Length;
            if (line.Length + 1 > remaining)
            {
                sb.Append(line.AsSpan(0, Math.Max(0, remaining - 1)));
                sb.Append('\n');
                Interlocked.Exchange(ref _truncated, 1);
                if (sb.Length < _maxOutputChars + 32)
                {
                    sb.Append("[truncated]");
                }
            }
            else
            {
                sb.AppendLine(line);
            }
        }
    }
}
