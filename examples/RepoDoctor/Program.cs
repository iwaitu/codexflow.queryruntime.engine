// RepoDoctor - a cross-platform example of calling the `qre` CLI as a local
// agent runtime from a .NET app.
//
// It registers a custom stdio tool, streams the model's answer from
// `qre run --stream` to the host app console, then follows up with a recorded
// replay of the latest run.
//
// Usage:
//   dotnet run -- /path/to/repo
//   dotnet run -- --offline /path/to/repo
//
// The `qre` binary is found on PATH, or via QRE_BIN. Provider configuration is
// read from QRE_API_URL / QRE_API_KEY / QRE_MODEL / QRE_API_MODE, or from the
// sibling CodexFlow appsettings.json VllmAgent section.

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

const string ToolName = "repodoctor_workspace_summary";

if (args.Length > 0 && args[0] == "--stdio-tool")
{
    return await RunWorkspaceSummaryToolAsync();
}

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
var provider = LoadProviderSettings(options);
if (!options.Offline && !provider.HasRequiredSettings)
{
    Console.Error.WriteLine("Missing real-provider configuration: QRE_API_URL, QRE_API_KEY, QRE_MODEL.");
    Console.Error.WriteLine("Set environment variables, or pass --appsettings /path/to/codexflow/CodexFlow/appsettings.json.");
    return 2;
}

var prompt = options.Prompt ??
    "Use the RepoDoctor custom tool result below to list the top three repository risks.";
var response = options.OfflineResponse ??
    "RepoDoctor can stream a QRE answer, keep the run trace, and replay it without another provider call.";
var toolResult = string.Empty;

Console.WriteLine("RepoDoctor");
Console.WriteLine($"workspace: {workspace}");
Console.WriteLine($"mode: {(options.Offline ? "offline smoke" : "live provider")}");
if (!options.Offline)
{
    Console.WriteLine($"provider: {provider.Source}");
    Console.WriteLine($"model: {provider.Model}");
    if (!string.IsNullOrWhiteSpace(provider.ApiMode))
    {
        Console.WriteLine($"api_mode: {provider.ApiMode}");
    }
}
Console.WriteLine();

if (!options.Offline && !options.SkipCustomTool)
{
    var manifestPath = WriteCustomToolManifest(workspace);
    var registerResult = await RunQreBufferedAsync(qrePath, [
        "tool", "register",
        "--workspace", workspace,
        "--manifest", manifestPath,
        "--force",
        "--json",
    ], provider.Environment);

    if (registerResult.ExitCode != 0)
    {
        Console.Error.WriteLine(registerResult.StdErr);
        return registerResult.ExitCode;
    }

    Console.WriteLine($"registered tool: {ToolName}");
    var invokeResult = await RunQreBufferedAsync(qrePath, [
        "tool", "invoke",
        "--workspace", workspace,
        "--name", ToolName,
        "--arguments", """{"extension":".cs","maxFiles":1000}""",
        "--json",
    ], provider.Environment);

    if (invokeResult.ExitCode != 0)
    {
        Console.Error.WriteLine(invokeResult.StdErr);
        return invokeResult.ExitCode;
    }

    var invokeJson = LastJsonLine(invokeResult.StdOut);
    if (invokeJson is null)
    {
        Console.Error.WriteLine("qre tool invoke did not produce a JSON result.");
        Console.Error.WriteLine(invokeResult.StdOut);
        return 1;
    }

    using var invokeDoc = JsonDocument.Parse(invokeJson);
    toolResult = GetString(invokeDoc.RootElement, "result");
    Console.WriteLine($"invoked tool: {ToolName}");
    Console.WriteLine();
}

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
runArgs.Add(string.IsNullOrWhiteSpace(toolResult)
    ? prompt
    : $"{prompt}{Environment.NewLine}{Environment.NewLine}RepoDoctor custom tool result:{Environment.NewLine}{toolResult}");

var runResult = await RunQreStreamingAsync(qrePath, runArgs, provider.Environment, CancellationToken.None);
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
], provider.Environment);

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
    IReadOnlyDictionary<string, string> environment,
    CancellationToken ct)
{
    var startInfo = BuildStartInfo(fileName, arguments, environment);
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
    IReadOnlyList<string> arguments,
    IReadOnlyDictionary<string, string> environment)
{
    var startInfo = BuildStartInfo(fileName, arguments, environment);
    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();

    return (process.ExitCode, await stdoutTask, await stderrTask);
}

static ProcessStartInfo BuildStartInfo(
    string fileName,
    IReadOnlyList<string> arguments,
    IReadOnlyDictionary<string, string> environment)
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

    foreach (var pair in environment)
    {
        startInfo.Environment[pair.Key] = pair.Value;
    }

    return startInfo;
}

static ProviderSettings LoadProviderSettings(RepoDoctorOptions options)
{
    var environment = new Dictionary<string, string>(GetEnvironmentComparer());
    CopyEnvironment("QRE_API_URL", environment);
    CopyEnvironment("QRE_API_KEY", environment);
    CopyEnvironment("QRE_MODEL", environment);
    CopyEnvironment("QRE_API_MODE", environment);

    var source = HasRequiredProviderSettings(environment)
        ? "environment"
        : "environment";

    if (!HasRequiredProviderSettings(environment))
    {
        var appsettingsPath = Path.GetFullPath(options.AppsettingsPath ?? ResolveDefaultAppsettingsPath());
        if (File.Exists(appsettingsPath))
        {
            using var doc = JsonDocument.Parse(
                File.ReadAllText(appsettingsPath),
                new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            if (doc.RootElement.TryGetProperty(options.ProviderSection, out var section) &&
                section.ValueKind == JsonValueKind.Object)
            {
                CopyProviderSetting(section, "ApiUrl", "QRE_API_URL", environment);
                CopyProviderSetting(section, "ApiKey", "QRE_API_KEY", environment);
                CopyProviderSetting(section, "Model", "QRE_MODEL", environment);
                CopyProviderSetting(section, "ApiMode", "QRE_API_MODE", environment);
                source = $"{appsettingsPath}#{options.ProviderSection}";
            }
            else
            {
                source = $"missing section {options.ProviderSection} in {appsettingsPath}";
            }
        }
        else
        {
            source = $"missing appsettings ({appsettingsPath})";
        }
    }

    return new ProviderSettings(
        environment,
        source,
        environment.GetValueOrDefault("QRE_MODEL") ?? string.Empty,
        environment.GetValueOrDefault("QRE_API_MODE") ?? string.Empty,
        HasRequiredProviderSettings(environment));
}

static void CopyProviderSetting(
    JsonElement section,
    string configName,
    string environmentName,
    IDictionary<string, string> environment)
{
    if (environment.ContainsKey(environmentName) ||
        !section.TryGetProperty(configName, out var value) ||
        value.ValueKind != JsonValueKind.String)
    {
        return;
    }

    var text = value.GetString();
    if (!string.IsNullOrWhiteSpace(text))
    {
        environment[environmentName] = text;
    }
}

static void CopyEnvironment(string name, IDictionary<string, string> environment)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (!string.IsNullOrWhiteSpace(value))
    {
        environment[name] = value;
    }
}

static bool HasRequiredProviderSettings(IReadOnlyDictionary<string, string> environment)
    => environment.ContainsKey("QRE_API_URL") &&
       environment.ContainsKey("QRE_API_KEY") &&
       environment.ContainsKey("QRE_MODEL");

static string ResolveDefaultAppsettingsPath()
{
    var current = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (current != null)
    {
        if (File.Exists(Path.Combine(current.FullName, "CodexFlow.QueryRuntime.slnx")))
        {
            return Path.Combine(
                current.Parent?.FullName ?? current.FullName,
                "codexflow",
                "CodexFlow",
                "appsettings.json");
        }

        current = current.Parent;
    }

    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "github",
        "codexflow",
        "CodexFlow",
        "appsettings.json");
}

static StringComparer GetEnvironmentComparer()
    => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

static string WriteCustomToolManifest(string workspace)
{
    var assemblyPath = Assembly.GetEntryAssembly()?.Location;
    if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
    {
        throw new InvalidOperationException("Cannot resolve the RepoDoctor assembly path for the custom tool manifest.");
    }

    var manifestDirectory = Path.Combine(workspace, ".qre", "repodoctor");
    Directory.CreateDirectory(manifestDirectory);
    var manifestPath = Path.Combine(manifestDirectory, ToolName + ".manifest.json");
    using var stream = File.Create(manifestPath);
    using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

    writer.WriteStartObject();
    writer.WriteString("name", ToolName);
    writer.WriteString("description", "Summarize the target repository from the RepoDoctor .NET host.");
    writer.WriteString("transport", "stdio");
    writer.WriteString("command", "dotnet");
    writer.WritePropertyName("args");
    writer.WriteStartArray();
    writer.WriteStringValue(assemblyPath);
    writer.WriteStringValue("--stdio-tool");
    writer.WriteEndArray();
    writer.WritePropertyName("capabilities");
    writer.WriteStartArray();
    writer.WriteStringValue("read_fs");
    writer.WriteEndArray();
    writer.WriteNumber("timeoutSeconds", 30);
    writer.WriteNumber("maxOutputBytes", 200_000);
    writer.WritePropertyName("inputSchema");
    writer.WriteStartObject();
    writer.WriteString("type", "object");
    writer.WritePropertyName("properties");
    writer.WriteStartObject();
    writer.WritePropertyName("extension");
    writer.WriteStartObject();
    writer.WriteString("type", "string");
    writer.WriteString("description", "Optional file extension filter, such as .cs or .md.");
    writer.WriteEndObject();
    writer.WritePropertyName("maxFiles");
    writer.WriteStartObject();
    writer.WriteString("type", "integer");
    writer.WriteNumber("minimum", 1);
    writer.WriteNumber("maximum", 5000);
    writer.WriteString("description", "Maximum number of files to inspect.");
    writer.WriteEndObject();
    writer.WriteEndObject();
    writer.WriteBoolean("additionalProperties", false);
    writer.WriteEndObject();
    writer.WriteEndObject();

    return manifestPath;
}

static async Task<int> RunWorkspaceSummaryToolAsync()
{
    using var request = await JsonDocument.ParseAsync(Console.OpenStandardInput());
    var root = request.RootElement;
    var workspace = root.TryGetProperty("workspacePath", out var workspaceElement) &&
        workspaceElement.ValueKind == JsonValueKind.String
            ? workspaceElement.GetString()
            : Directory.GetCurrentDirectory();

    if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
    {
        Console.Error.WriteLine("Tool request did not include a valid workspacePath.");
        return 1;
    }

    var arguments = root.TryGetProperty("arguments", out var argsElement) &&
        argsElement.ValueKind == JsonValueKind.Object
            ? argsElement
            : default;
    var extension = TryGetString(arguments, "extension");
    if (!string.IsNullOrWhiteSpace(extension) && !extension.StartsWith(".", StringComparison.Ordinal))
    {
        extension = "." + extension;
    }

    var maxFiles = Math.Clamp(TryGetInt32(arguments, "maxFiles") ?? 1000, 1, 5000);
    var summary = SummarizeWorkspace(workspace, extension, maxFiles);
    Console.WriteLine(JsonSerializer.Serialize(new { result = summary }));
    return 0;
}

static object SummarizeWorkspace(string workspace, string? extension, int maxFiles)
{
    var root = new DirectoryInfo(workspace);
    var files = SafeEnumerateFiles(root.FullName)
        .Where(path => ShouldInclude(path, extension))
        .Take(maxFiles)
        .Select(path => Path.GetRelativePath(root.FullName, path))
        .Order(StringComparer.Ordinal)
        .ToArray();

    var topLevelDirectories = files
        .Select(path => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0])
        .Where(segment => !string.IsNullOrWhiteSpace(segment) && segment != ".")
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .Take(12)
        .ToArray();

    return new
    {
        workspace = root.Name,
        extension,
        inspectedFileCount = files.Length,
        topLevelDirectories,
        sampleFiles = files.Take(20).ToArray()
    };
}

static IEnumerable<string> SafeEnumerateFiles(string root)
{
    var pending = new Stack<string>();
    pending.Push(root);
    while (pending.Count > 0)
    {
        var directory = pending.Pop();
        string[] childDirectories;
        string[] childFiles;
        try
        {
            childDirectories = Directory.EnumerateDirectories(directory).ToArray();
            childFiles = Directory.EnumerateFiles(directory).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            continue;
        }

        foreach (var file in childFiles)
        {
            yield return file;
        }

        foreach (var child in childDirectories)
        {
            var name = Path.GetFileName(child);
            if (name is ".git" or ".qre" or "bin" or "obj")
            {
                continue;
            }

            pending.Push(child);
        }
    }
}

static bool ShouldInclude(string path, string? extension)
    => string.IsNullOrWhiteSpace(extension) ||
       string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

static string? TryGetString(JsonElement element, string propertyName)
    => element.ValueKind == JsonValueKind.Object &&
       element.TryGetProperty(propertyName, out var property) &&
       property.ValueKind == JsonValueKind.String
        ? property.GetString()
        : null;

static int? TryGetInt32(JsonElement element, string propertyName)
    => element.ValueKind == JsonValueKind.Object &&
       element.TryGetProperty(propertyName, out var property) &&
       property.ValueKind == JsonValueKind.Number &&
       property.TryGetInt32(out var value)
        ? value
        : null;

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
            case "--appsettings":
                options.AppsettingsPath = ReadValue(args, ref i, "--appsettings");
                break;
            case "--provider-section":
                options.ProviderSection = ReadValue(args, ref i, "--provider-section");
                break;
            case "--skip-custom-tool":
                options.SkipCustomTool = true;
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
    Console.WriteLine("  --appsettings <p>  CodexFlow appsettings.json provider source.");
    Console.WriteLine("  --provider-section Provider section name. Defaults to VllmAgent.");
    Console.WriteLine("  --skip-custom-tool Do not register or require the RepoDoctor custom tool.");
}

internal sealed record RepoDoctorOptions
{
    public string? Workspace { get; set; }

    public string? QrePath { get; set; }

    public string? Prompt { get; set; }

    public string? OfflineResponse { get; set; }

    public string? AppsettingsPath { get; set; }

    public string ProviderSection { get; set; } = "VllmAgent";

    public bool Offline { get; set; }

    public bool SkipCustomTool { get; set; }

    public bool ShowHelp { get; set; }
}

internal sealed record ProviderSettings(
    IReadOnlyDictionary<string, string> Environment,
    string Source,
    string Model,
    string ApiMode,
    bool HasRequiredSettings);
