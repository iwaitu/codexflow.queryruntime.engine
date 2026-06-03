using System.Diagnostics;

namespace CodexFlow.Core.Skills.Java;

public class JavaScaffolderSkill : ISkill
{
    public string Name => "java-scaffolder";
    public string Description => "Initialize a new Java (Spring Boot) project with Maven or Gradle and a layered package structure.";

    public async Task<SkillResult> ExecuteAsync(SkillContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var projectName = context.ProjectName;
        var workspace = context.WorkspacePath;
        var groupId = context.Parameters.GetValueOrDefault("group_id", "com.example")?.ToString() ?? "com.example";
        var buildTool = context.Parameters.GetValueOrDefault("build_tool", "maven")?.ToString() ?? "maven";

        if (string.IsNullOrEmpty(projectName))
            return new SkillResult { Success = false, Error = "Project name is required." };

        try
        {
            var scriptPath = ResolveScriptPath();
            if (!File.Exists(scriptPath))
                return new SkillResult { Success = false, Error = $"Scaffold script not found at {scriptPath}" };

            var args = $"\"{scriptPath}\" \"{projectName}\" --output \"{workspace}\" --group-id {groupId} --build-tool {buildTool}";
            var result = await RunCommandAsync("python3", args, workspace).ConfigureAwait(false);

            return new SkillResult
            {
                Success = true,
                Output = result.Output,
                Data = new { ProjectPath = Path.Combine(workspace, projectName) }
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

    private static string ResolveScriptPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDir, "skills", "java-scaffolder", "scripts", "scaffold.py");
        if (File.Exists(candidate)) return candidate;

        var current = new DirectoryInfo(baseDir);
        while (current != null)
        {
            if (current.GetFiles("*.sln").Length > 0 || current.GetFiles("*.slnx").Length > 0)
            {
                var slnCandidate = Path.Combine(current.FullName, "CodexFlow", "skills", "java-scaffolder", "scripts", "scaffold.py");
                if (File.Exists(slnCandidate)) return slnCandidate;
            }
            current = current.Parent;
        }
        return candidate;
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
