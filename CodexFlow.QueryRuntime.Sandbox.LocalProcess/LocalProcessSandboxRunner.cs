using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using CodexFlow.QueryRuntime.Abstractions;

namespace CodexFlow.QueryRuntime.Sandbox.LocalProcess;

public sealed class LocalProcessSandboxRunner : ISandboxRunner
{
    public async Task<SandboxResult> RunAsync(
        SandboxJobSpec spec,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Command.Count == 0 || string.IsNullOrWhiteSpace(spec.Command[0]))
        {
            throw new ArgumentException("Sandbox command must include an executable.", nameof(spec));
        }
        if (string.Equals(spec.Network.Mode, SandboxNetworkPolicy.Allow.Mode, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("LocalProcessSandboxRunner does not support network-allowed jobs. Use an isolation-capable runner for Network.Allow.");
        }

        var workingDirectory = Path.GetFullPath(spec.WorkingDirectory);
        if (!Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Sandbox working directory does not exist: {workingDirectory}");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(spec.Limits.Timeout);

        var outputLimit = Math.Max(1, spec.Limits.MaxOutputBytes);
        var stdout = new BoundedOutputBuffer(outputLimit);
        var stderr = new BoundedOutputBuffer(outputLimit);
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.Command[0],
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.Environment.Clear();

        foreach (var arg in spec.Command.Skip(1))
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var pair in spec.Environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            stopwatch.Stop();
            return new SandboxResult(
                127,
                string.Empty,
                $"Failed to start sandbox command '{spec.Command[0]}': {ex.Message}",
                false,
                stopwatch.ElapsedMilliseconds);
        }

        var stdoutTask = DrainAsync(process.StandardOutput, stdout, timeoutCts.Token);
        var stderrTask = DrainAsync(process.StandardError, stderr, timeoutCts.Token);
        var timedOut = false;

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            TryKill(process);
            throw;
        }

        try
        {
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timedOut)
        {
        }

        stopwatch.Stop();
        return new SandboxResult(
            timedOut ? -1 : process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            timedOut,
            stopwatch.ElapsedMilliseconds);
    }

    private static async Task DrainAsync(
        StreamReader reader,
        BoundedOutputBuffer buffer,
        CancellationToken ct)
    {
        var chars = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(chars, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            buffer.Append(chars.AsSpan(0, read));
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
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private sealed class BoundedOutputBuffer(int maxBytes)
    {
        private readonly StringBuilder _builder = new();
        private readonly Encoder _encoder = Encoding.UTF8.GetEncoder();
        private int _bytes;

        public void Append(ReadOnlySpan<char> value)
        {
            if (_bytes >= maxBytes)
            {
                return;
            }

            var byteCount = _encoder.GetByteCount(value, flush: false);
            var remaining = maxBytes - _bytes;
            if (byteCount <= remaining)
            {
                _builder.Append(value);
                _bytes += byteCount;
                return;
            }

            var includedChars = 0;
            var includedBytes = 0;
            foreach (var ch in value)
            {
                var bytes = Encoding.UTF8.GetByteCount([ch]);
                if (includedBytes + bytes > remaining)
                {
                    break;
                }

                includedBytes += bytes;
                includedChars++;
            }

            _builder.Append(value[..includedChars]);
            _bytes += includedBytes;
        }

        public override string ToString() => _builder.ToString();
    }
}
