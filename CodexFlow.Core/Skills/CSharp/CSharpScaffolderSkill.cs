using System.Diagnostics;
using System.Text.Json;
using CodexFlow.Core.Skills;

namespace CodexFlow.Core.Skills.CSharp;

public class CSharpScaffolderSkill : ISkill
{
    public string Name => "csharp-scaffolder";
    public string Description => "Initialize a new .NET solution with Clean Architecture layers (Api, Domain, Infrastructure, Application, Contracts).";

    public async Task<SkillResult> ExecuteAsync(SkillContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var projectName = context.ProjectName;
        var workspace = context.WorkspacePath;
        var targetFramework = context.Parameters.GetValueOrDefault("targetFramework", "net8.0")?.ToString() ?? "net8.0";

        if (string.IsNullOrEmpty(projectName))
            return new SkillResult { Success = false, Error = "Project name is required." };

        try
        {
            var projectRoot = Path.Combine(workspace, projectName);
            if (!Directory.Exists(projectRoot))
                Directory.CreateDirectory(projectRoot);

            // 1. Check SDK
            var sdkCheck = await RunCommandAsync("dotnet", "--list-sdks", workspace).ConfigureAwait(false);
            if (!sdkCheck.Output.Contains("8.0", StringComparison.Ordinal) && targetFramework == "net8.0")
            {
                // Fallback logic could be added here, but for now we warn or fail
                // For robustness, we might just proceed if we trust the environment or auto-downgrade
                // Let's log it but proceed for now.
            }

            // 2. Create Solution
            await RunCommandAsync("dotnet", $"new sln -n {projectName}", projectRoot).ConfigureAwait(false);

            // 3. Create Layers
            var layers = new[] { "Api", "Domain", "Infrastructure", "Application", "Contracts" };
            foreach (var layer in layers)
            {
                var projectType = layer == "Api" ? "webapi" : "classlib";
                var layerName = $"{projectName}.{layer}";
                var layerPath = Path.Combine(projectRoot, layerName);

                // Create project
                await RunCommandAsync("dotnet", $"new {projectType} -n {layerName} -f {targetFramework} --force", projectRoot).ConfigureAwait(false);

                // Add to SLN
                await RunCommandAsync("dotnet", $"sln add {layerName}/{layerName}.csproj", projectRoot).ConfigureAwait(false);
            }

            // 4. Add References (Clean Architecture)
            // Api -> Application, Infrastructure
            await AddReferenceAsync(projectRoot, $"{projectName}.Api", $"{projectName}.Application").ConfigureAwait(false);
            await AddReferenceAsync(projectRoot, $"{projectName}.Api", $"{projectName}.Infrastructure").ConfigureAwait(false);

            // Infrastructure -> Application, Domain
            await AddReferenceAsync(projectRoot, $"{projectName}.Infrastructure", $"{projectName}.Application").ConfigureAwait(false);
            await AddReferenceAsync(projectRoot, $"{projectName}.Infrastructure", $"{projectName}.Domain").ConfigureAwait(false);

            // Application -> Domain, Contracts
            await AddReferenceAsync(projectRoot, $"{projectName}.Application", $"{projectName}.Domain").ConfigureAwait(false);
            await AddReferenceAsync(projectRoot, $"{projectName}.Application", $"{projectName}.Contracts").ConfigureAwait(false);

            // 5. Install Basic NuGets (EF Core, etc.) -> Can be moved to a separate step or parameter driven
            // For now, let's keep it minimal scaffolding.

            return new SkillResult
            {
                Success = true,
                Output = $"Successfully scaffolded Clean Architecture solution for {projectName} in {projectRoot}",
                Data = new { ProjectPath = projectRoot }
            };
        }
        catch (IOException ex)
        {
            return new SkillResult { Success = false, Error = ex.Message };
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SkillResult { Success = false, Error = ex.Message };
        }
        catch (InvalidOperationException ex)
        {
            return new SkillResult { Success = false, Error = ex.Message };
        }
    }

    private static async Task AddReferenceAsync(string root, string fromProject, string toProject)
    {
        // dotnet add Api/Api.csproj reference Application/Application.csproj
        await RunCommandAsync("dotnet", $"add {fromProject}/{fromProject}.csproj reference {toProject}/{toProject}.csproj", root).ConfigureAwait(false);
    }

    private static async Task<(bool Success, string Output)> RunCommandAsync(string command, string args, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var error = await proc.StandardError.ReadToEndAsync().ConfigureAwait(false);
        await proc.WaitForExitAsync().ConfigureAwait(false);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Command failed: {command} {args}\nError: {error}");

        return (true, output);
    }
}
