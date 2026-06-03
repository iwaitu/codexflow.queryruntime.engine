using System;
using System.Security.Cryptography;
using System.Text;
using CodexFlow.Core.Hashline.Abstractions;

namespace CodexFlow.Core.Hashline.Services;

/// <summary>
/// SHA256 文件指纹计算器。
/// 计算规则：
/// 1. 对规范化后的完整文本计算 SHA256
/// 2. 输出完整 hex 作为 Fingerprint（小写）
/// </summary>
public sealed class Sha256FingerprintProvider : IFileFingerprintProvider
{
    /// <summary>
    /// 计算文件指纹（完整哈希）。
    /// </summary>
    public string ComputeFingerprint(string normalizedFullText)
    {
        if (normalizedFullText == null)
        {
            normalizedFullText = string.Empty;
        }

        // 使用 SHA256 计算哈希
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedFullText));

        // 输出完整 hex（小写）
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}