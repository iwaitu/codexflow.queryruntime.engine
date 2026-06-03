using System;
using System.Security.Cryptography;
using System.Text;
using CodexFlow.Core.Hashline.Abstractions;

namespace CodexFlow.Core.Hashline.Services;

/// <summary>
/// SHA256 行哈希计算器。
/// 计算规则：
/// 1. 对规范化后的行文本计算 SHA256
/// 2. 取前 8 位大写 hex 作为 AnchorId
/// </summary>
public sealed class Sha256LineHasher : ILineHasher
{
    /// <summary>
    /// 计算行锚点 ID（短哈希）。
    /// </summary>
    public string ComputeAnchorId(string normalizedLineText)
    {
        if (normalizedLineText == null)
        {
            // 空行也要有锚点
            normalizedLineText = string.Empty;
        }

        // 使用 SHA256 计算哈希
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedLineText));

        // 取前 8 位，转为大写 hex
        var hexString = Convert.ToHexString(hashBytes);
        return hexString.Substring(0, 8).ToUpperInvariant();
    }
}