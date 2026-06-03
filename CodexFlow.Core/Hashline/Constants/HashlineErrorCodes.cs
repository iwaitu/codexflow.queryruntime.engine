namespace CodexFlow.Core.Hashline.Constants;

/// <summary>
/// Hashline 错误码常量。
/// </summary>
public static class HashlineErrorCodes
{
    // 文件级错误
    public const string FileNotFound = "FILE_NOT_FOUND";
    public const string FileAccessDenied = "FILE_ACCESS_DENIED";
    public const string FilePathNotAllowed = "FILE_PATH_NOT_ALLOWED";
    public const string FileTooLarge = "FILE_TOO_LARGE";
    public const string FileBinaryNotSupported = "FILE_BINARY_NOT_SUPPORTED";
    public const string UnsupportedEncoding = "UNSUPPORTED_ENCODING";

    // 快照/版本错误
    public const string FileFingerprintMismatch = "FILE_FINGERPRINT_MISMATCH";
    public const string SnapshotIdRequired = "SNAPSHOT_ID_REQUIRED";
    public const string InvalidRequest = "INVALID_REQUEST";

    // 行级错误
    public const string LineOutOfRange = "LINE_OUT_OF_RANGE";
    public const string WindowOutOfRange = "WINDOW_OUT_OF_RANGE";
    public const string AnchorNotFound = "ANCHOR_NOT_FOUND";
    public const string AnchorMismatch = "ANCHOR_MISMATCH";
    public const string InvalidRange = "INVALID_RANGE";
    public const string OverlappingOperations = "OVERLAPPING_OPERATIONS";
    public const string RewriteWithOtherOperations = "REWRITE_WITH_OTHER_OPERATIONS";

    // 操作级错误
    public const string InvalidOperationType = "INVALID_OPERATION_TYPE";
    public const string InvalidOperationPayload = "INVALID_OPERATION_PAYLOAD";

    // 写入错误
    public const string WriteConflict = "WRITE_CONFLICT";
    public const string WriteFailed = "WRITE_FAILED";
    public const string DiffRenderFailed = "DIFF_RENDER_FAILED";
    public const string UnknownError = "UNKNOWN_ERROR";

    // 安全/权限错误
    public const string HighRiskFileRequiresGuardedPath = "HIGH_RISK_FILE_REQUIRES_GUARDED_PATH";
    public const string StageNotAllowed = "STAGE_NOT_ALLOWED";
    public const string ToolRouteNotAllowed = "TOOL_ROUTE_NOT_ALLOWED";

    /// <summary>
    /// 错误码对应的修复指引。
    /// </summary>
    public static IReadOnlyDictionary<string, string> RepairGuidance => new Dictionary<string, string>
    {
        [FileFingerprintMismatch] = "必须重新 ivilson_read({\"path\":\"...\", \"mode\":\"hashline\"}) 获取最新快照，禁止复述旧文本。",
        [AnchorMismatch] = "必须重新读取快照获取正确 anchorId，禁止猜测锚点。",
        [AnchorNotFound] = "锚点未找到，检查 anchorId 是否正确，重新读取获取。",
        [LineOutOfRange] = "重新读取获取正确行号范围。",
        [WindowOutOfRange] = "目标范围超出当前读取窗口，重新读取覆盖目标范围的新窗口。",
        [InvalidOperationType] = "使用 replace_range/insert_after/insert_before/delete_range/rewrite_whole_file。",
        [OverlappingOperations] = "按行号顺序排列操作，确保区间不重叠。",
        [FilePathNotAllowed] = "文件路径超出工作区范围，检查 workspace_path。",
        [FileNotFound] = "文件不存在，检查文件路径。",
        [FileTooLarge] = "文件超过大小限制，考虑分片读取或使用其他策略。",
        [FileBinaryNotSupported] = "不支持二进制文件，Hashline 仅支持文本文件。",
        [HighRiskFileRequiresGuardedPath] = "高风险文件必须使用 ivilson_smart_patch({\"edit_mode\":\"hashline\", ...})。"
    };

    /// <summary>
    /// 获取错误码对应的修复指引。
    /// </summary>
    public static string GetGuidance(string errorCode) =>
        RepairGuidance.TryGetValue(errorCode, out var guidance) ? guidance : "检查错误详情并修正操作。";
}

/// <summary>
/// Hashline 编辑操作类型常量。
/// </summary>
public static class HashlineOperationTypes
{
    public const string ReplaceRange = "replace_range";
    public const string InsertAfter = "insert_after";
    public const string InsertBefore = "insert_before";
    public const string DeleteRange = "delete_range";
    public const string RewriteWholeFile = "rewrite_whole_file";
}
