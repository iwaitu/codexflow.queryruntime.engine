using System;
using System.Text;
using CodexFlow.Core.Hashline.Abstractions;

namespace CodexFlow.Core.Hashline.Infrastructure;

/// <summary>
/// 编码检测器实现。
/// 支持检测 UTF-8、UTF-8 BOM、UTF-16 LE/BE 等编码。
/// </summary>
public sealed class EncodingDetector : IEncodingDetector
{
    /// <summary>
    /// 检测文件编码。
    /// </summary>
    public HashlineEncodingInfo DetectEncoding(byte[] content)
    {
        if (content == null || content.Length == 0)
        {
            return new HashlineEncodingInfo
            {
                Name = "utf-8",
                HasBom = false,
                Encoding = Encoding.UTF8
            };
        }

        // 检查 BOM
        if (content.Length >= 3)
        {
            // UTF-8 BOM: EF BB BF
            if (content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
            {
                return new HashlineEncodingInfo
                {
                    Name = "utf-8",
                    HasBom = true,
                    Encoding = Encoding.UTF8
                };
            }
        }

        if (content.Length >= 2)
        {
            // UTF-16 LE BOM: FF FE
            if (content[0] == 0xFF && content[1] == 0xFE)
            {
                return new HashlineEncodingInfo
                {
                    Name = "utf-16",
                    HasBom = true,
                    Encoding = Encoding.Unicode // Little Endian
                };
            }

            // UTF-16 BE BOM: FE FF
            if (content[0] == 0xFE && content[1] == 0xFF)
            {
                return new HashlineEncodingInfo
                {
                    Name = "utf-16be",
                    HasBom = true,
                    Encoding = Encoding.BigEndianUnicode
                };
            }
        }

        // 无 BOM，默认 UTF-8
        return new HashlineEncodingInfo
        {
            Name = "utf-8",
            HasBom = false,
            Encoding = Encoding.UTF8
        };
    }

    /// <summary>
    /// 获取编码名称（用于存储）。
    /// </summary>
    public static string GetEncodingName(Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(encoding);

        if (encoding == Encoding.UTF8 || encoding == Encoding.Default)
        {
            return "utf-8";
        }
        if (encoding == Encoding.Unicode)
        {
            return "utf-16";
        }
        if (encoding == Encoding.BigEndianUnicode)
        {
            return "utf-16be";
        }
        return encoding.WebName;
    }

    /// <summary>
    /// 根据名称获取编码。
    /// </summary>
    public static Encoding GetEncodingFromName(string name, bool hasBom)
    {
        ArgumentNullException.ThrowIfNull(name);

        switch (name.ToLowerInvariant())
        {
            case "utf-8":
                return hasBom ? Encoding.UTF8 : new UTF8Encoding(false);
            case "utf-16":
                return Encoding.Unicode;
            case "utf-16be":
                return Encoding.BigEndianUnicode;
            default:
                return Encoding.UTF8;
        }
    }
}