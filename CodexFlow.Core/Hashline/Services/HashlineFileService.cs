using System;
using System.Threading;
using System.Threading.Tasks;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Hashline.Constants;
using CodexFlow.Core.Hashline.Models;
using Microsoft.Extensions.Logging;

namespace CodexFlow.Core.Hashline.Services;

/// <summary>
/// Hashline 文件服务实现。
/// 作为总协调器，整合所有 Hashline 子组件。
/// </summary>
public sealed class HashlineFileService : IHashlineFileService
{
    private readonly ISnapshotReader _snapshotReader;
    private readonly IEditRequestValidator _validator;
    private readonly IEditApplier _applier;
    private readonly IDiffRenderer _diffRenderer;
    private readonly IAtomicFileWriter _fileWriter;
    private readonly IFileFingerprintProvider _fingerprintProvider;
    private readonly IAuditLogger _auditLogger;
    private readonly ITextNormalizer _normalizer;
    private readonly HashlineOptions _options;
    private readonly ILogger<HashlineFileService> _logger;

    public HashlineFileService(
        ISnapshotReader snapshotReader,
        IEditRequestValidator validator,
        IEditApplier applier,
        IDiffRenderer diffRenderer,
        IAtomicFileWriter fileWriter,
        IFileFingerprintProvider fingerprintProvider,
        IAuditLogger auditLogger,
        ITextNormalizer normalizer,
        HashlineOptions options,
        ILogger<HashlineFileService> logger)
    {
        _snapshotReader = snapshotReader ?? throw new ArgumentNullException(nameof(snapshotReader));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _diffRenderer = diffRenderer ?? throw new ArgumentNullException(nameof(diffRenderer));
        _fileWriter = fileWriter ?? throw new ArgumentNullException(nameof(fileWriter));
        _fingerprintProvider = fingerprintProvider ?? throw new ArgumentNullException(nameof(fingerprintProvider));
        _auditLogger = auditLogger ?? throw new ArgumentNullException(nameof(auditLogger));
        _normalizer = normalizer ?? throw new ArgumentNullException(nameof(normalizer));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// 读取文件并生成带行锚点的快照。
    /// </summary>
    public async Task<FileSnapshot> ReadAsync(
        string filePath,
        string? workspaceRoot = null,
        int? windowStartLine = null,
        int? windowLineCount = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            var snapshot = await _snapshotReader
                .ReadAsync(filePath, workspaceRoot, windowStartLine, windowLineCount, ct)
                .ConfigureAwait(false);
            return snapshot;
        }
        catch (HashlineException ex)
        {
            // 转换为结果对象返回错误
            _logger.LogError(ex, "[Hashline] Read failed: {FilePath}, ErrorCode={ErrorCode}", filePath, ex.ErrorCode);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hashline] Read failed with unexpected error: {FilePath}", filePath);
            throw new HashlineException(HashlineErrorCodes.UnknownError, $"读取文件时发生未知错误: {filePath}", ex);
        }
    }

    /// <summary>
    /// 验证编辑请求但不落盘。返回 diff 和校验结果。
    /// </summary>
    public async Task<HashlineEditResult> ValidateAsync(
        HashlineEditRequest request,
        string? workspaceRoot = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (request == null)
        {
            return CreateErrorResult(HashlineErrorCodes.InvalidRequest, "请求不能为 null");
        }

        try
        {
            // 1. 记录编辑请求日志
            if (_options.EnableAuditLogging)
            {
                await _auditLogger.LogEditRequestAsync(request, ct).ConfigureAwait(false);
            }

            // 2. 重新读取文件生成当前快照
            var currentSnapshot = await _snapshotReader.ReadAsync(
                request.FilePath,
                workspaceRoot,
                ct: ct).ConfigureAwait(false);

            // 3. 校验请求
            var validation = await _validator.ValidateAsync(request, currentSnapshot, workspaceRoot, ct).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                var firstError = validation.Errors[0];
                var result = CreateErrorResult(firstError.Code, firstError.Message, currentSnapshot.FileFingerprint);

                if (_options.EnableAuditLogging)
                {
                    await _auditLogger.LogEditResultAsync(result, ct).ConfigureAwait(false);
                }

                return result;
            }

            // 4. 在内存中应用操作
            var applyResult = _applier.Apply(currentSnapshot, request.Operations);

            // 5. 计算新 fingerprint
            var normalized = _normalizer.Normalize(applyResult.NewContent);
            var newFingerprint = _fingerprintProvider.ComputeFingerprint(normalized.NormalizedText);

            // 6. 生成 diff
            var diff = _diffRenderer.RenderUnifiedDiff(
                request.FilePath,
                applyResult.OldContent,
                applyResult.NewContent);

            // 7. 返回成功结果
            var successResult = new HashlineEditResult
            {
                Success = true,
                OldFingerprint = currentSnapshot.FileFingerprint,
                NewFingerprint = newFingerprint,
                UnifiedDiff = diff,
                Hunks = applyResult.Hunks
            };

            if (_options.EnableAuditLogging)
            {
                await _auditLogger.LogEditResultAsync(successResult, ct).ConfigureAwait(false);
            }

            return successResult;
        }
        catch (HashlineException ex)
        {
            var result = CreateErrorResult(ex.ErrorCode, ex.Message);
            if (_options.EnableAuditLogging)
            {
                await _auditLogger.LogEditResultAsync(result, ct).ConfigureAwait(false);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hashline] Validate failed with unexpected error: {FilePath}", request.FilePath);
            var result = CreateErrorResult(HashlineErrorCodes.UnknownError, $"验证时发生未知错误: {ex.Message}");
            if (_options.EnableAuditLogging)
            {
                await _auditLogger.LogEditResultAsync(result, ct).ConfigureAwait(false);
            }
            return result;
        }
    }

    /// <summary>
    /// 执行编辑请求。若 DryRun=true 则仅验证不落盘。
    /// </summary>
    public async Task<HashlineEditResult> EditAsync(
        HashlineEditRequest request,
        string? workspaceRoot = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (request == null)
        {
            return CreateErrorResult(HashlineErrorCodes.InvalidRequest, "请求不能为 null");
        }

        try
        {
            // 1. 先执行验证
            var result = await ValidateAsync(request, workspaceRoot, ct).ConfigureAwait(false);

            if (!result.Success)
            {
                return result;
            }

            // 2. 如果是 DryRun，直接返回
            if (request.DryRun)
            {
                result.DryRun = true;
                _logger.LogInformation("[Hashline] DryRun completed: {FilePath}", request.FilePath);
                return result;
            }

            // 3. 再次读取文件进行写前校验（乐观并发控制）
            var currentSnapshot = await _snapshotReader.ReadAsync(
                request.FilePath,
                workspaceRoot,
                ct: ct).ConfigureAwait(false);

            if (!string.Equals(request.FileFingerprint, currentSnapshot.FileFingerprint, StringComparison.Ordinal))
            {
                var mismatchResult = CreateErrorResult(
                    HashlineErrorCodes.FileFingerprintMismatch,
                    $"文件 fingerprint 不匹配。请求: {request.FileFingerprint}, 当前: {currentSnapshot.FileFingerprint}",
                    currentSnapshot.FileFingerprint);

                if (_options.EnableAuditLogging)
                {
                    await _auditLogger.LogEditResultAsync(mismatchResult, ct).ConfigureAwait(false);
                }

                return mismatchResult;
            }

            // 4. 在内存中应用操作（再次）
            var applyResult = _applier.Apply(currentSnapshot, request.Operations);

            // 5. 原子写入文件
            await _fileWriter.WriteAsync(
                request.FilePath,
                applyResult.NewContent,
                currentSnapshot.EncodingName,
                currentSnapshot.HasBom,
                currentSnapshot.NewLineStyle,
                currentSnapshot.HasTrailingNewline,
                ct).ConfigureAwait(false);

            // 6. 读取最终文件获取新 fingerprint
            var finalSnapshot = await _snapshotReader.ReadAsync(
                request.FilePath,
                workspaceRoot,
                ct: ct).ConfigureAwait(false);
            result.NewFingerprint = finalSnapshot.FileFingerprint;
            result.DryRun = false;

            // 7. 记录审计日志
            if (_options.EnableAuditLogging)
            {
                await _auditLogger.LogEditResultAsync(result, ct).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "[Hashline] Edit completed: {FilePath}, OldFingerprint={OldFingerprint}, NewFingerprint={NewFingerprint}",
                request.FilePath,
                result.OldFingerprint,
                result.NewFingerprint);

            return result;
        }
        catch (HashlineException ex)
        {
            var errorResult = CreateErrorResult(ex.ErrorCode, ex.Message);
            if (_options.EnableAuditLogging)
            {
                await _auditLogger.LogEditResultAsync(errorResult, ct).ConfigureAwait(false);
            }
            return errorResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Hashline] Edit failed with unexpected error: {FilePath}", request.FilePath);
            var errorResult = CreateErrorResult(HashlineErrorCodes.UnknownError, $"编辑时发生未知错误: {ex.Message}");
            if (_options.EnableAuditLogging)
            {
                await _auditLogger.LogEditResultAsync(errorResult, ct).ConfigureAwait(false);
            }
            return errorResult;
        }
    }

    /// <summary>
    /// 创建错误结果对象。
    /// </summary>
    private static HashlineEditResult CreateErrorResult(
        string errorCode,
        string errorMessage,
        string? oldFingerprint = null)
    {
        return new HashlineEditResult
        {
            Success = false,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            OldFingerprint = oldFingerprint,
            NewFingerprint = null,
            UnifiedDiff = null,
            Hunks = new System.Collections.Generic.List<ChangedHunk>()
        };
    }
}
