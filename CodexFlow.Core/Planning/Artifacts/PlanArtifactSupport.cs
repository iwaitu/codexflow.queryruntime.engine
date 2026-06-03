using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexFlow.Core.Planning.Artifacts;

public static class PlanArtifactSupport
{
    private static readonly Regex UnsafeSlugChars = new("[^a-zA-Z0-9_.-]+", RegexOptions.Compiled);

    public static string ComputeMarkdownHash(string markdown)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(markdown ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string CreateSessionSlug(string sessionId)
    {
        var normalized = UnsafeSlugChars.Replace(sessionId ?? string.Empty, "-").Trim('-', '.', '_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "session";
        }

        return normalized.Length <= 80 ? normalized : normalized[..80];
    }
}
