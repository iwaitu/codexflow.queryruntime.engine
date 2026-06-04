// RepoDoctor - a cross-platform example of calling the `qre` CLI as a local
// agent runtime from a .NET app.
//
// It streams the model's answer from `qre run --stream` to the host app console,
// then follows up with a recorded replay of the latest run.
//
// Usage:
//   dotnet run -- /path/to/repo
//   dotnet run -- --offline /path/to/repo
//
// The `qre` binary is found on PATH, or via QRE_BIN. Provider configuration is
// read by `qre` from QRE_API_URL / QRE_API_KEY / QRE_MODEL / QRE_API_MODE.

using System.Diagnostics;
using System.Text;
using System.Text.Json;

var options = ParseArgs(args);
if (options.ShowHelp)
{
    PrintHelp();
    return 0;
}

var workspace = Path.GetFullPath(options.Workspace ?? Directory.GetCurrentDirectory());
if (!Directory.Exists(workspace))
{
    Console.Error.WriteLine($"Workspace does not exist: {workspace}");
    return 1;
}

var qrePath = options.QrePath ??
    Environment.GetEnvironmentVariable("QRE_BIN") ??
    "qre";
var prompt = options.Prompt ??
    "Analyze this repository and list the top three risks.";
var response = options.OfflineResponse ??
    "RepoDoctor can stream a QRE answer, keep the run trace, and replay it without another provider call.";

Console.WriteLine("RepoDoctor");
Console.WriteLine($"workspace: {workspace}");
Console.WriteLine($"mode: {(options.Offline ? "offline smoke" : "live provider")}");
Console.WriteLine();
Console.WriteLine("Streaming model answer:");

var runArgs = new List<string>
{
    "run",
    "--workspace", workspace,
    "--profile", "readonly",
    "--stream",
};
if (options.Offline)
{
    runArgs.Add("--response");
    runArgs.Add(response);
}
runArgs.Add(prompt);

var runResult = await RunQreStreamingAsync(qrePath, runArgs, CancellationToken.None);
if (runResult.ExitCode != 0)
{
    Console.Error.WriteLine(runResult.StdErr);
    return runResult.ExitCode;
}

Console.WriteLine();
Console.WriteLine("Recorded replay:");
var replayResult = await RunQreBufferedAsync(qrePath, [
    "replay", "latest",
    "--workspace", workspace,
    "--json",
]);

if (replayResult.ExitCode != 0)
{
    Console.Error.WriteLine(replayResult.StdErr);
    return replayResult.ExitCode;
}

var replayJson = LastJsonLine(replayResult.StdOut);
if (replayJson is null)
{
    Console.Error.WriteLine("qre replay did not produce a JSON result.");
    Console.Error.WriteLine(replayResult.StdOut);
    return 1;
}

using var doc = JsonDocument.Parse(replayJson);
var root = doc.RootElement;
Console.WriteLine($"runner: {GetString(root, "runner")}");
Console.WriteLine($"finalText: {GetString(root, "finalText")}");
Console.WriteLine($"trace: {GetString(root, "traceFilePath")}");

return 0;

static async Task<(int ExitCode, string StdOut, string StdErr)> RunQreStreamingAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    CancellationToken ct)
{
    var startInfo = BuildStartInfo(fileName, arguments);
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

    var stdout = new StringBuilder();
    var stderrTask = process.StandardError.ReadToEndAsync(ct);
    var buffer = new char[256];
    while (true)
    {
        var read = await process.StandardOutput.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
        if (read == 0)
        {
            break;
        }

        var chunk = new string(buffer, 0, read);
        stdout.Append(chunk);
        Console.Write(chunk);
    }

    await process.WaitForExitAsync(ct);
    return (process.ExitCode, stdout.ToString(), await stderrTask);
}

static async Task<(int ExitCode, string StdOut, string StdErr)> RunQreBufferedAsync(
    string fileName,
    IReadOnlyList<string> arguments)
{
    var startInfo = BuildStartInfo(fileName, arguments);
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    return (process.ExitCode, await stdoutTask, await stderrTask);
}

static ProcessStartInfo BuildStartInfo(string fileName, IReadOnlyList<string> arguments)
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

    return startInfo;
}

static RepoDoctorOptions ParseArgs(string[] args)
{
    var options = new RepoDoctorOptions();
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "-h" or "--help":
                options.ShowHelp = true;
                break;
            case "--offline":
                options.Offline = true;
                break;
            case "--qre":
                options.QrePath = ReadValue(args, ref i, "--qre");
                break;
            case "--prompt":
                options.Prompt = ReadValue(args, ref i, "--prompt");
                break;
            case "--response":
                options.OfflineResponse = ReadValue(args, ref i, "--response");
                options.Offline = true;
                break;
            default:
                if (args[i].StartsWith("-", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"Unknown option: {args[i]}");
                }

                options.Workspace = args[i];
                break;
        }
    }

    return options;
}

static string ReadValue(string[] args, ref int index, string option)
{
    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
    {
        throw new ArgumentException($"{option} requires a value.");
    }

    return args[index];
}

static string? LastJsonLine(string stdout) => stdout
    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
    .Select(line => line.Trim())
    .LastOrDefault(line => line.StartsWith('{'));

static string GetString(JsonElement element, string property) =>
    element.TryGetProperty(property, out var value)
        ? value.GetString() ?? string.Empty
        : string.Empty;

static void PrintHelp()
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  RepoDoctor [--offline] [--qre /path/to/qre] [--prompt text] [workspace]");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --offline          Use --response so no provider key is required.");
    Console.WriteLine("  --response <text>  Offline response text; implies --offline.");
    Console.WriteLine("  --qre <path>       qre binary path. Defaults to QRE_BIN or PATH.");
    Console.WriteLine("  --prompt <text>    Prompt passed to qre run.");
}

internal sealed record RepoDoctorOptions
{
    public string? Workspace { get; set; }

    public string? QrePath { get; set; }

    public string? Prompt { get; set; }

    public string? OfflineResponse { get; set; }

    public bool Offline { get; set; }

    public bool ShowHelp { get; set; }
}
