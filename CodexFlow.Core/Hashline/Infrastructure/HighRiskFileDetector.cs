using System;
using System.Collections.Generic;
using System.IO;

namespace CodexFlow.Core.Hashline.Infrastructure;

/// <summary>
/// 高风险文件检测器。
/// 用于识别需要特殊保护的配置文件和核心文件。
/// </summary>
public static class HighRiskFileDetector
{
    // 高风险文件模式列表
    private static readonly HashSet<string> HighRiskFilePatterns = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program.cs",
        "appsettings.json",
        "appsettings.Development.json",
        "appsettings.Production.json",
        "Startup.cs",
        "GlobalUsings.cs",
        "*.csproj",
        "*.sln",
        "Directory.Build.props",
        "Directory.Packages.props",
        ".env",
        ".env.*",
        "appsettings.local.json",
        "settings.local.json",
        "secrets.json",
        "launchSettings.json",
        "launchsettings.json",
        "Controllers/AuthController.cs",
        "Controllers/AccountController.cs",
        "Middleware/*.cs",
        "Services/AuthService.cs",
        "Services/IdentityService.cs",
        "Program.*.cs"
    };

    /// <summary>
    /// 检查文件是否为高风险文件。
    /// </summary>
    /// <param name="filePath">文件路径（可以是相对路径或绝对路径）</param>
    /// <returns>如果是高风险文件返回 true</returns>
    public static bool IsHighRiskFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return false;

        var fileName = Path.GetFileName(filePath);
        var dirName = Path.GetDirectoryName(filePath) ?? string.Empty;

        foreach (var pattern in HighRiskFilePatterns)
        {
            if (pattern.Contains('*'))
            {
                // Glob-style matching
                if (pattern.StartsWith("*."))
                {
                    // *.csproj, *.sln, etc.
                    var suffix = pattern[1..]; // includes the dot
                    if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (pattern.EndsWith("/*.cs"))
                {
                    // Middleware/*.cs, etc.
                    var dirPattern = pattern[..^5]; // remove "/*.cs"
                    if (dirName.EndsWith(dirPattern, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else if (pattern.EndsWith(".*"))
                {
                    // .env.* style
                    var prefix = pattern[..^2]; // remove ".*"
                    if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            else
            {
                // Exact match
                if (string.Equals(fileName, pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 获取所有高风险文件模式。
    /// </summary>
    public static IReadOnlySet<string> Patterns => HighRiskFilePatterns;
}