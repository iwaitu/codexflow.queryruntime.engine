using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace CodexFlow.Core.Agents.Tools;

/// <summary>
/// 多语言代码沙箱执行器。支持 Python / Node.js / Java（单文件编译运行）。
/// 通过子进程运行，限制执行时间。
/// </summary>
public class ExecCodeTool(ILogger<ExecCodeTool> logger) : ICodexTool
{
    public string Name => "exec_code";
    public string Description => "在安全的沙箱环境中执行代码片段。参数：code (要执行的代码), language (可选：python|node|java, 默认 python)。Few-shot: exec_code({\"code\":\"print(1+1)\",\"language\":\"python\"})。";
    public ToolCategory Category => ToolCategory.Forge;
    public IReadOnlyList<int> AllowedStages => [3, 4];

    private const int DefaultTimeoutSeconds = 30;

    public async Task<CodexToolResult> ExecuteAsync(Dictionary<string, object?> arguments, CancellationToken ct = default)
    {
        var code = arguments.GetValueOrDefault("code")?.ToString();
        var workspacePath = arguments.GetValueOrDefault("workspace_path")?.ToString();
        var projectRoot = arguments.GetValueOrDefault("project_root")?.ToString();
        var language = (arguments.GetValueOrDefault("language")?.ToString() ?? "python").ToUpperInvariant();

        if (string.IsNullOrEmpty(code))
            return CodexToolResult.Error("Missing code parameter.");

        // [Path Normalization Fix] 使用 ToolPathResolver 解析正确的工作区根目录
        var baseRoot = ToolPathResolver.ResolveBaseRoot(workspacePath, projectRoot);
        var workDir = string.IsNullOrEmpty(baseRoot) ? (workspacePath ?? Directory.GetCurrentDirectory()) : baseRoot;

        var (interpreterPath, interpreterArgs, stdinMode) = ResolveInterpreter(language, workDir);
        if (interpreterPath is null)
            return CodexToolResult.Error($"Runtime for '{language}' not found. Ensure the corresponding runtime (python3/node/java) is installed.");

        try
        {
            string? tempFile = null;
            var psi = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir
            };

            if (stdinMode)
            {
                psi.FileName = interpreterPath;
                psi.Arguments = interpreterArgs;
                psi.RedirectStandardInput = true;
            }
            else
            {
                // Java: write to temp file, compile & run
                tempFile = Path.Combine(Path.GetTempPath(), $"CodexExec_{Guid.NewGuid():N}.java");
                await File.WriteAllTextAsync(tempFile, code, ct).ConfigureAwait(false);
                psi.FileName = interpreterPath;
                psi.Arguments = $"{interpreterArgs} \"{tempFile}\"";
            }

            using var process = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (stdinMode)
            {
                await process.StandardInput.WriteAsync(code).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(DefaultTimeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
                catch (NotSupportedException) { }
                return CodexToolResult.Error($"代码执行超时（{DefaultTimeoutSeconds}s）。");
            }
            finally
            {
                if (tempFile != null)
                {
                    try { File.Delete(tempFile); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }

            var result = new
            {
                Stdout = stdout.ToString(),
                Stderr = stderr.ToString(),
                ExitCode = process.ExitCode
            };

            if (process.ExitCode != 0)
            {
                return CodexToolResult.Error(
                    $"⚠️ 代码执行失败（exit code {process.ExitCode}）\n[stdout]\n{result.Stdout}\n[stderr]\n{result.Stderr}",
                    result);
            }

            return CodexToolResult.Succeeded(
                string.IsNullOrWhiteSpace(result.Stdout) ? "✅ 代码执行成功（无输出）" : $"✅ 执行结果:\n{result.Stdout}",
                result);
        }
        catch (IOException ex)
        {
            StructuredLog.Error(logger, ex, "exec_code failed for language {Language}", language);
            return CodexToolResult.Error($"exec_code 异常：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            StructuredLog.Error(logger, ex, "exec_code failed for language {Language}", language);
            return CodexToolResult.Error($"exec_code 异常：{ex.Message}");
        }
        catch (Win32Exception ex)
        {
            StructuredLog.Error(logger, ex, "exec_code failed for language {Language}", language);
            return CodexToolResult.Error($"exec_code 异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 返回 (可执行文件，参数，是否通过 stdin 传递代码)。
    /// </summary>
    private static (string? Executable, string Args, bool StdinMode) ResolveInterpreter(string language, string? workspace)
    {
        return language switch
        {
            "python" or "python3" => (FindExecutable(["python3", "python"]), "-", true),
            "node" or "nodejs" or "javascript" or "typescript" =>
                FindExecutable(["node"]) is string nodePath
                    ? (nodePath, "--input-type=module", true)
                    : ((string?)null, "", false),
            "java" => (FindExecutable(["java"]), "--source 21", false),
            _ => (FindExecutable(["python3", "python"]), "-", true) // default to python
        };
    }

    private static string? FindExecutable(string[] candidates)
    {
        foreach (var cmd in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(cmd, "--version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(3000);
                if (p?.ExitCode == 0) return cmd;
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            catch (NotSupportedException) { }
        }
        return null;
    }
}

