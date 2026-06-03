// RepoDoctor — a minimal, cross-platform example of calling the `qre` CLI as a
// local agent runtime from a .NET app.
//
// It runs a read-only analysis over a repository, parses the single-line JSON
// result emitted by `qre run --json`, prints the final text and trace path, then
// follows up with a recorded replay of the same run.
//
// Usage:
//   dotnet run -- /path/to/repo
//
// The `qre` binary is found on PATH, or via the QRE_BIN environment variable.
// Provider configuration is read by `qre` itself from QRE_API_URL / QRE_API_KEY /
// QRE_MODEL / QRE_API_MODE. For an offline smoke that needs no LLM key, replace
// "--profile readonly" with "--response \"offline smoke\"" below.

using System.Diagnostics;
using System.Text.Json;

var workspace = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Directory.GetCurrentDirectory();

if (!Directory.Exists(workspace))
{
    Console.Error.WriteLine($"Workspace does not exist: {workspace}");
    return 1;
}

var qrePath = Environment.GetEnvironmentVariable("QRE_BIN");
if (string.IsNullOrWhiteSpace(qrePath))
{
    qrePath = "qre";
}

// 1. Run a read-only analysis and capture the JSON result.
var runArgs = new[]
{
    "run",
    "--workspace", workspace,
    "--profile", "readonly",
    "--json",
    "Analyze this repository and list the top three risks.",
};

var runResult = await RunQreAsync(qrePath, runArgs);
if (runResult.ExitCode != 0)
{
    Console.Error.WriteLine(runResult.StdErr);
    return runResult.ExitCode;
}

var runJson = LastJsonLine(runResult.StdOut);
if (runJson is null)
{
    Console.Error.WriteLine("qre did not produce a JSON result.");
    Console.Error.WriteLine(runResult.StdOut);
    return 1;
}

using (var doc = JsonDocument.Parse(runJson))
{
    var root = doc.RootElement;
    Console.WriteLine("Result:");
    Console.WriteLine(GetString(root, "finalText"));
    Console.WriteLine();
    Console.WriteLine("Trace:");
    Console.WriteLine(GetString(root, "traceFilePath"));
}

// 2. Follow up with a recorded replay of the latest run.
var replayResult = await RunQreAsync(qrePath, new[]
{
    "replay", "latest",
    "--workspace", workspace,
    "--json",
});

if (replayResult.ExitCode == 0)
{
    var replayJson = LastJsonLine(replayResult.StdOut);
    Console.WriteLine();
    Console.WriteLine("Replay summary:");
    Console.WriteLine(replayJson);
}

return 0;

static async Task<(int ExitCode, string StdOut, string StdErr)> RunQreAsync(
    string fileName,
    IReadOnlyList<string> arguments)
{
    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    foreach (var arg in arguments)
    {
        startInfo.ArgumentList.Add(arg);
    }

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    return (process.ExitCode, await stdoutTask, await stderrTask);
}

static string? LastJsonLine(string stdout) => stdout
    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
    .Select(line => line.Trim())
    .LastOrDefault(line => line.StartsWith('{'));

static string GetString(JsonElement element, string property) =>
    element.TryGetProperty(property, out var value)
        ? value.GetString() ?? string.Empty
        : string.Empty;
