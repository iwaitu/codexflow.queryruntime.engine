using CodexFlow.QueryRuntime.Abstractions;

namespace CodexFlow.QueryRuntime.Experimental;

public static class ExperimentalCommandCapabilityClassifier
{
    public static IReadOnlySet<string> Classify(
        IReadOnlyList<string> command,
        SandboxMountPolicy mounts)
    {
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        if (command.Count == 0)
        {
            return capabilities;
        }

        var executable = NormalizeExecutable(command[0]);
        switch (executable)
        {
            case "rg":
            case "grep":
            case "find":
            case "ls":
            case "pwd":
            case "cat":
                capabilities.Add(QueryRuntimeCommandCapabilities.ReadWorkspace);
                break;
            case "git":
                ClassifyGit(command, capabilities);
                break;
            case "dotnet":
                ClassifyDotnet(command, capabilities);
                break;
            case "npm":
            case "pnpm":
            case "yarn":
                ClassifyNodePackageManager(command, capabilities);
                break;
            case "pip":
            case "pip3":
                ClassifyPythonPackageManager(command, capabilities);
                break;
            case "rm":
                capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
                capabilities.Add(QueryRuntimeCommandCapabilities.Destructive);
                break;
            case "sh":
            case "bash":
            case "zsh":
                ClassifyShell(command, capabilities);
                break;
            case "curl":
            case "wget":
                capabilities.Add(QueryRuntimeCommandCapabilities.NetworkAccess);
                break;
            case "docker":
            case "kubectl":
            case "helm":
            case "terraform":
            case "wrangler":
            case "vercel":
            case "netlify":
                ClassifyDeploymentCommand(command, capabilities);
                break;
            default:
                capabilities.Add(QueryRuntimeCommandCapabilities.UnknownProcess);
                break;
        }

        if (string.Equals(mounts.Mode, SandboxMountPolicy.WorkspaceReadWrite.Mode, StringComparison.OrdinalIgnoreCase) &&
            !capabilities.Contains(QueryRuntimeCommandCapabilities.ReadWorkspace))
        {
            capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
        }

        return capabilities;
    }

    public static bool HasAny(
        IReadOnlySet<string> capabilities,
        params string[] expected)
        => expected.Any(capabilities.Contains);

    private static void ClassifyGit(IReadOnlyList<string> command, ISet<string> capabilities)
    {
        capabilities.Add(QueryRuntimeCommandCapabilities.ReadWorkspace);
        if (command.Count < 2)
        {
            capabilities.Add(QueryRuntimeCommandCapabilities.UnknownProcess);
            return;
        }

        switch (command[1])
        {
            case "status":
            case "diff":
            case "log":
            case "show":
            case "ls-files":
            case "rev-parse":
                break;
            case "push":
                capabilities.Add(QueryRuntimeCommandCapabilities.GitPush);
                capabilities.Add(QueryRuntimeCommandCapabilities.NetworkAccess);
                break;
            case "pull":
            case "fetch":
            case "clone":
                capabilities.Add(QueryRuntimeCommandCapabilities.NetworkAccess);
                capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
                break;
            case "add":
            case "commit":
            case "merge":
            case "rebase":
            case "switch":
            case "restore":
            case "branch":
            case "tag":
            case "stash":
                capabilities.Add(QueryRuntimeCommandCapabilities.GitWrite);
                capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
                break;
            case "reset":
            case "checkout":
            case "clean":
            case "rm":
                capabilities.Add(QueryRuntimeCommandCapabilities.GitWrite);
                capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
                capabilities.Add(QueryRuntimeCommandCapabilities.Destructive);
                break;
            default:
                capabilities.Add(QueryRuntimeCommandCapabilities.UnknownProcess);
                break;
        }
    }

    private static void ClassifyDotnet(IReadOnlyList<string> command, ISet<string> capabilities)
    {
        capabilities.Add(QueryRuntimeCommandCapabilities.ReadWorkspace);
        if (command.Count < 2)
        {
            return;
        }

        switch (command[1])
        {
            case "restore":
                capabilities.Add(QueryRuntimeCommandCapabilities.PackageRestore);
                capabilities.Add(QueryRuntimeCommandCapabilities.NetworkAccess);
                capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
                break;
            case "test":
            case "build":
                capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
                if (!command.Contains("--no-restore", StringComparer.Ordinal))
                {
                    capabilities.Add(QueryRuntimeCommandCapabilities.PackageRestore);
                    capabilities.Add(QueryRuntimeCommandCapabilities.NetworkAccess);
                }
                break;
            case "publish":
                capabilities.Add(QueryRuntimeCommandCapabilities.PackagePublish);
                capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
                break;
            case "run":
                capabilities.Add(QueryRuntimeCommandCapabilities.ArbitraryExecution);
                capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
                break;
            default:
                capabilities.Add(QueryRuntimeCommandCapabilities.UnknownProcess);
                break;
        }
    }

    private static void ClassifyNodePackageManager(IReadOnlyList<string> command, ISet<string> capabilities)
    {
        capabilities.Add(QueryRuntimeCommandCapabilities.ReadWorkspace);
        if (command.Count < 2)
        {
            capabilities.Add(QueryRuntimeCommandCapabilities.PackageInstall);
            capabilities.Add(QueryRuntimeCommandCapabilities.NetworkAccess);
            capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
            return;
        }

        switch (command[1])
        {
            case "install":
            case "i":
            case "ci":
            case "add":
            case "update":
            case "remove":
                capabilities.Add(QueryRuntimeCommandCapabilities.PackageInstall);
                capabilities.Add(QueryRuntimeCommandCapabilities.NetworkAccess);
                capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
                break;
            case "publish":
                capabilities.Add(QueryRuntimeCommandCapabilities.PackagePublish);
                capabilities.Add(QueryRuntimeCommandCapabilities.NetworkAccess);
                break;
            default:
                capabilities.Add(QueryRuntimeCommandCapabilities.UnknownProcess);
                break;
        }
    }

    private static void ClassifyPythonPackageManager(IReadOnlyList<string> command, ISet<string> capabilities)
    {
        capabilities.Add(QueryRuntimeCommandCapabilities.ReadWorkspace);
        if (command.Any(static argument => argument is "install" or "uninstall"))
        {
            capabilities.Add(QueryRuntimeCommandCapabilities.PackageInstall);
            capabilities.Add(QueryRuntimeCommandCapabilities.NetworkAccess);
            capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
            return;
        }

        capabilities.Add(QueryRuntimeCommandCapabilities.UnknownProcess);
    }

    private static void ClassifyShell(IReadOnlyList<string> command, ISet<string> capabilities)
    {
        capabilities.Add(QueryRuntimeCommandCapabilities.ReadWorkspace);
        capabilities.Add(QueryRuntimeCommandCapabilities.UnknownProcess);
        var script = ExtractShellScript(command);
        if (string.IsNullOrWhiteSpace(script))
        {
            return;
        }

        if (script.Contains('>') ||
            ContainsToken(script, "touch") ||
            ContainsToken(script, "mv") ||
            ContainsToken(script, "cp"))
        {
            capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
        }

        if (ContainsToken(script, "rm") || script.Contains("git reset", StringComparison.Ordinal))
        {
            capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
            capabilities.Add(QueryRuntimeCommandCapabilities.Destructive);
        }

        if (script.Contains("git push", StringComparison.Ordinal))
        {
            capabilities.Add(QueryRuntimeCommandCapabilities.GitPush);
            capabilities.Add(QueryRuntimeCommandCapabilities.NetworkAccess);
        }

        if (ContainsToken(script, "curl") || ContainsToken(script, "wget"))
        {
            capabilities.Add(QueryRuntimeCommandCapabilities.NetworkAccess);
        }

        if (script.Contains("npm install", StringComparison.Ordinal) ||
            script.Contains("npm i", StringComparison.Ordinal) ||
            script.Contains("pip install", StringComparison.Ordinal) ||
            script.Contains("pip3 install", StringComparison.Ordinal))
        {
            capabilities.Add(QueryRuntimeCommandCapabilities.PackageInstall);
            capabilities.Add(QueryRuntimeCommandCapabilities.NetworkAccess);
            capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
        }

        if (script.Contains("dotnet restore", StringComparison.Ordinal))
        {
            capabilities.Add(QueryRuntimeCommandCapabilities.PackageRestore);
            capabilities.Add(QueryRuntimeCommandCapabilities.NetworkAccess);
            capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
        }
    }

    private static void ClassifyDeploymentCommand(IReadOnlyList<string> command, ISet<string> capabilities)
    {
        capabilities.Add(QueryRuntimeCommandCapabilities.ReadWorkspace);
        if (command.Any(static argument => string.Equals(argument, "deploy", StringComparison.Ordinal) ||
                                           string.Equals(argument, "apply", StringComparison.Ordinal) ||
                                           string.Equals(argument, "push", StringComparison.Ordinal)))
        {
            capabilities.Add(QueryRuntimeCommandCapabilities.Deploy);
            capabilities.Add(QueryRuntimeCommandCapabilities.NetworkAccess);
            capabilities.Add(QueryRuntimeCommandCapabilities.WriteWorkspace);
        }
    }

    private static string NormalizeExecutable(string executable)
        => Path.GetFileName(executable).Trim().ToLowerInvariant();

    private static string? ExtractShellScript(IReadOnlyList<string> command)
    {
        for (var i = 1; i < command.Count - 1; i++)
        {
            if (string.Equals(command[i], "-c", StringComparison.Ordinal))
            {
                return command[i + 1];
            }
        }

        return null;
    }

    private static bool ContainsToken(string value, string token)
        => value.Split([' ', '\t', '\r', '\n', ';', '&', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, token, StringComparison.Ordinal));
}
