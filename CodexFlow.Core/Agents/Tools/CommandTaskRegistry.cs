using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace CodexFlow.Core.Agents.Tools;

public enum CommandTaskStatus
{
    Running,
    Completed,
    Failed,
    TimedOut,
    Stopped
}

public sealed record CommandTaskEvent(
    long Seq,
    string Stream,
    string Text,
    DateTimeOffset OccurredAtUtc);

public sealed record CommandTaskSnapshot(
    string CommandTaskId,
    CommandTaskStatus Status,
    string Command,
    string WorkingDirectory,
    int? ExitCode,
    long LatestSeq,
    IReadOnlyList<CommandTaskEvent> Events,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);

public static class CommandTaskRegistry
{
    private static readonly ConcurrentDictionary<string, CommandTaskState> Tasks = new(StringComparer.OrdinalIgnoreCase);

    public static string Start(ProcessStartInfo startInfo, string command, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        var id = "cmd_" + Guid.NewGuid().ToString("N");
        var state = new CommandTaskState(id, command, startInfo.WorkingDirectory, timeout);
        if (!Tasks.TryAdd(id, state))
        {
            throw new InvalidOperationException($"Duplicate command task id: {id}");
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        state.Process = process;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                state.AddEvent("stdout", e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                state.AddEvent("stderr", e.Data);
            }
        };

        try
        {
            process.Start();
            state.AddEvent("system", $"started: {command}");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _ = WaitForExitAsync(state);
            return id;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            state.MarkCompleted(CommandTaskStatus.Failed, null);
            state.AddEvent("stderr", ex.Message);
            return id;
        }
    }

    public static bool TryGet(string commandTaskId, long? afterSeq, int maxEvents, out CommandTaskSnapshot snapshot)
    {
        snapshot = null!;
        if (!Tasks.TryGetValue(commandTaskId, out var state))
        {
            return false;
        }

        snapshot = state.Snapshot(afterSeq, maxEvents);
        return true;
    }

    public static async Task<CommandTaskSnapshot?> WaitForCompletionAsync(string commandTaskId, CancellationToken ct = default)
    {
        if (!Tasks.TryGetValue(commandTaskId, out var state))
        {
            return null;
        }

        await state.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
        return state.Snapshot(afterSeq: null, maxEvents: 500);
    }

    public static bool Stop(string commandTaskId, out CommandTaskSnapshot? snapshot)
    {
        snapshot = null;
        if (!Tasks.TryGetValue(commandTaskId, out var state))
        {
            return false;
        }

        state.Stop();
        snapshot = state.Snapshot(afterSeq: null, maxEvents: 200);
        return true;
    }

    private static async Task WaitForExitAsync(CommandTaskState state)
    {
        var process = state.Process!;
        using var timeoutCts = new CancellationTokenSource(state.Timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var status = process.ExitCode == 0 ? CommandTaskStatus.Completed : CommandTaskStatus.Failed;
            state.MarkCompleted(status, process.ExitCode);
            state.AddEvent("system", $"exited: {process.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            state.MarkCompleted(CommandTaskStatus.TimedOut, null);
            state.AddEvent("system", $"timed out after {(int)state.Timeout.TotalSeconds}s");
        }
        finally
        {
            process.Dispose();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        catch (NotSupportedException) { }
    }

    private sealed class CommandTaskState
    {
        private readonly object _gate = new();
        private readonly List<CommandTaskEvent> _events = [];
        private long _latestSeq;

        public CommandTaskState(string id, string command, string workingDirectory, TimeSpan timeout)
        {
            Id = id;
            Command = command;
            WorkingDirectory = workingDirectory;
            Timeout = timeout;
        }

        public string Id { get; }
        public string Command { get; }
        public string WorkingDirectory { get; }
        public TimeSpan Timeout { get; }
        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? CompletedAtUtc { get; private set; }
        public CommandTaskStatus Status { get; private set; } = CommandTaskStatus.Running;
        public int? ExitCode { get; private set; }
        public Process? Process { get; set; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AddEvent(string stream, string text)
        {
            lock (_gate)
            {
                _events.Add(new CommandTaskEvent(++_latestSeq, stream, text, DateTimeOffset.UtcNow));
            }
        }

        public void MarkCompleted(CommandTaskStatus status, int? exitCode)
        {
            lock (_gate)
            {
                if (Status != CommandTaskStatus.Running)
                {
                    return;
                }

                Status = status;
                ExitCode = exitCode;
                CompletedAtUtc = DateTimeOffset.UtcNow;
            }

            Completion.TrySetResult();
        }

        public void Stop()
        {
            var process = Process;
            if (process != null)
            {
                TryKill(process);
            }

            MarkCompleted(CommandTaskStatus.Stopped, null);
            AddEvent("system", "stopped by task_stop");
        }

        public CommandTaskSnapshot Snapshot(long? afterSeq, int maxEvents)
        {
            lock (_gate)
            {
                var events = _events
                    .Where(evt => !afterSeq.HasValue || evt.Seq > afterSeq.Value)
                    .Take(Math.Clamp(maxEvents, 1, 500))
                    .ToArray();

                return new CommandTaskSnapshot(
                    Id,
                    Status,
                    Command,
                    WorkingDirectory,
                    ExitCode,
                    _latestSeq,
                    events,
                    StartedAtUtc,
                    CompletedAtUtc);
            }
        }
    }
}
