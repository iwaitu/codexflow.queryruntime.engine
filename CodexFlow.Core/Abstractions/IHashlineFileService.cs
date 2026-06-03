using CodexFlow.Core.Hashline.Models;

namespace CodexFlow.Core.Abstractions;

/// <summary>
/// Hashline 文件服务接口。
/// 提供基于快照版本和行级锚点的文件读写能力。
/// </summary>
public interface IHashlineFileService
{
    /// <summary>
    /// 读取文件并生成带行锚点的快照。
    /// </summary>
    /// <param name="filePath">文件路径（绝对路径）</param>
    /// <param name="workspaceRoot">工作区根目录（用于路径安全检查，可选）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>文件快照，包含所有行的锚点信息</returns>
    Task<FileSnapshot> ReadAsync(
        string filePath,
        string? workspaceRoot = null,
        int? windowStartLine = null,
        int? windowLineCount = null,
        CancellationToken ct = default);

    /// <summary>
    /// 验证编辑请求但不落盘。返回 diff 和校验结果。
    /// </summary>
    /// <param name="request">编辑请求</param>
    /// <param name="workspaceRoot">工作区根目录（用于路径安全检查，可选）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>验证结果，包含 diff 信息</returns>
    Task<HashlineEditResult> ValidateAsync(
        HashlineEditRequest request,
        string? workspaceRoot = null,
        CancellationToken ct = default);

    /// <summary>
    /// 执行编辑请求。若 DryRun=true 则仅验证不落盘。
    /// </summary>
    /// <param name="request">编辑请求</param>
    /// <param name="workspaceRoot">工作区根目录（用于路径安全检查，可选）</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>编辑结果</returns>
    Task<HashlineEditResult> EditAsync(
        HashlineEditRequest request,
        string? workspaceRoot = null,
        CancellationToken ct = default);
}
