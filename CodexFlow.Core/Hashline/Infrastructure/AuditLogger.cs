using System;
using System.Threading;
using System.Threading.Tasks;
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Hashline.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Hashline.Infrastructure;

/// <summary>
/// 审计日志实现。
/// 记录 Hashline 操作的关键事件。
/// </summary>
public sealed class AuditLogger : IAuditLogger
{
    private readonly ILogger<AuditLogger> _logger;

    public AuditLogger(ILogger<AuditLogger> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 记录读取操作。
    /// </summary>
    public Task LogReadAsync(FileSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ct.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "[Hashline] Read snapshot: SnapshotId={SnapshotId}, FilePath={FilePath}, " +
            "Fingerprint={Fingerprint}, Lines={LineCount}, Encoding={Encoding}, " +
            "NewLine={NewLineStyle}, ReadAt={ReadAtUtc}",
            snapshot.SnapshotId,
            snapshot.FilePath,
            snapshot.FileFingerprint,
            snapshot.Lines.Count,
            snapshot.EncodingName,
            snapshot.NewLineStyle,
            snapshot.ReadAtUtc);

        return Task.CompletedTask;
    }

    /// <summary>
    /// 记录编辑请求。
    /// </summary>
    public Task LogEditRequestAsync(HashlineEditRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        _logger.LogInformation(
            "[Hashline] Edit request: FilePath={FilePath}, SnapshotId={SnapshotId}, " +
            "Fingerprint={Fingerprint}, DryRun={DryRun}, Operations={OperationCount}",
            request.FilePath,
            request.SnapshotId,
            request.FileFingerprint,
            request.DryRun,
            request.Operations.Count);

        // 记录每个操作的详细信息
        foreach (var op in request.Operations)
        {
            _logger.LogDebug(
                "[Hashline] Operation: OpId={OpId}, Type={Type}",
                op.OpId,
                op.Type);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 记录编辑结果。
    /// </summary>
    public Task LogEditResultAsync(HashlineEditResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ct.ThrowIfCancellationRequested();

        if (result.Success)
        {
            _logger.LogInformation(
                "[Hashline] Edit success: OldFingerprint={OldFingerprint}, " +
                "NewFingerprint={NewFingerprint}, Hunks={HunkCount}",
                result.OldFingerprint,
                result.NewFingerprint,
                result.Hunks.Count);
        }
        else
        {
            _logger.LogWarning(
                "[Hashline] Edit failed: ErrorCode={ErrorCode}, ErrorMessage={ErrorMessage}, " +
                "OldFingerprint={OldFingerprint}",
                result.ErrorCode,
                result.ErrorMessage,
                result.OldFingerprint);
        }

        return Task.CompletedTask;
    }
}