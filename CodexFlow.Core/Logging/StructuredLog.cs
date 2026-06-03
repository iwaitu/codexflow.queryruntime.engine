using System.Collections.Concurrent;

namespace Microsoft.Extensions.Logging;

internal static class StructuredLog
{
    private readonly record struct CacheKey(LogLevel Level, string Message);

    private static class Cache0
    {
        internal static readonly ConcurrentDictionary<CacheKey, Action<ILogger, Exception?>> Values = new();
    }

    private static class Cache1<T1>
    {
        internal static readonly ConcurrentDictionary<CacheKey, Action<ILogger, T1, Exception?>> Values = new();
    }

    private static class Cache2<T1, T2>
    {
        internal static readonly ConcurrentDictionary<CacheKey, Action<ILogger, T1, T2, Exception?>> Values = new();
    }

    private static class Cache3<T1, T2, T3>
    {
        internal static readonly ConcurrentDictionary<CacheKey, Action<ILogger, T1, T2, T3, Exception?>> Values = new();
    }

    private static class Cache4<T1, T2, T3, T4>
    {
        internal static readonly ConcurrentDictionary<CacheKey, Action<ILogger, T1, T2, T3, T4, Exception?>> Values = new();
    }

    private static class Cache5<T1, T2, T3, T4, T5>
    {
        internal static readonly ConcurrentDictionary<CacheKey, Action<ILogger, T1, T2, T3, T4, T5, Exception?>> Values = new();
    }

    private static class Cache6<T1, T2, T3, T4, T5, T6>
    {
        internal static readonly ConcurrentDictionary<CacheKey, Action<ILogger, T1, T2, T3, T4, T5, T6, Exception?>> Values = new();
    }

    private static Action<ILogger, Exception?> Get0(LogLevel level, string message)
        => Cache0.Values.GetOrAdd(new CacheKey(level, message), static key => LoggerMessage.Define(key.Level, default, key.Message));

    private static Action<ILogger, T1, Exception?> Get1<T1>(LogLevel level, string message)
        => Cache1<T1>.Values.GetOrAdd(new CacheKey(level, message), static key => LoggerMessage.Define<T1>(key.Level, default, key.Message));

    private static Action<ILogger, T1, T2, Exception?> Get2<T1, T2>(LogLevel level, string message)
        => Cache2<T1, T2>.Values.GetOrAdd(new CacheKey(level, message), static key => LoggerMessage.Define<T1, T2>(key.Level, default, key.Message));

    private static Action<ILogger, T1, T2, T3, Exception?> Get3<T1, T2, T3>(LogLevel level, string message)
        => Cache3<T1, T2, T3>.Values.GetOrAdd(new CacheKey(level, message), static key => LoggerMessage.Define<T1, T2, T3>(key.Level, default, key.Message));

    private static Action<ILogger, T1, T2, T3, T4, Exception?> Get4<T1, T2, T3, T4>(LogLevel level, string message)
        => Cache4<T1, T2, T3, T4>.Values.GetOrAdd(new CacheKey(level, message), static key => LoggerMessage.Define<T1, T2, T3, T4>(key.Level, default, key.Message));

    private static Action<ILogger, T1, T2, T3, T4, T5, Exception?> Get5<T1, T2, T3, T4, T5>(LogLevel level, string message)
        => Cache5<T1, T2, T3, T4, T5>.Values.GetOrAdd(new CacheKey(level, message), static key => LoggerMessage.Define<T1, T2, T3, T4, T5>(key.Level, default, key.Message));

    private static Action<ILogger, T1, T2, T3, T4, T5, T6, Exception?> Get6<T1, T2, T3, T4, T5, T6>(LogLevel level, string message)
        => Cache6<T1, T2, T3, T4, T5, T6>.Values.GetOrAdd(new CacheKey(level, message), static key => LoggerMessage.Define<T1, T2, T3, T4, T5, T6>(key.Level, default, key.Message));

    public static void Debug(ILogger logger, string message) => Get0(LogLevel.Debug, message)(logger, null);
    public static void Information(ILogger logger, string message) => Get0(LogLevel.Information, message)(logger, null);
    public static void Warning(ILogger logger, string message) => Get0(LogLevel.Warning, message)(logger, null);
    public static void Error(ILogger logger, string message) => Get0(LogLevel.Error, message)(logger, null);
    public static void Critical(ILogger logger, string message) => Get0(LogLevel.Critical, message)(logger, null);

    public static void Debug(ILogger logger, Exception exception, string message) => Get0(LogLevel.Debug, message)(logger, exception);
    public static void Information(ILogger logger, Exception exception, string message) => Get0(LogLevel.Information, message)(logger, exception);
    public static void Warning(ILogger logger, Exception exception, string message) => Get0(LogLevel.Warning, message)(logger, exception);
    public static void Error(ILogger logger, Exception exception, string message) => Get0(LogLevel.Error, message)(logger, exception);
    public static void Critical(ILogger logger, Exception exception, string message) => Get0(LogLevel.Critical, message)(logger, exception);

    public static void Debug<T1>(ILogger logger, string message, T1 arg1) => Get1<T1>(LogLevel.Debug, message)(logger, arg1, null);
    public static void Information<T1>(ILogger logger, string message, T1 arg1) => Get1<T1>(LogLevel.Information, message)(logger, arg1, null);
    public static void Warning<T1>(ILogger logger, string message, T1 arg1) => Get1<T1>(LogLevel.Warning, message)(logger, arg1, null);
    public static void Error<T1>(ILogger logger, string message, T1 arg1) => Get1<T1>(LogLevel.Error, message)(logger, arg1, null);
    public static void Critical<T1>(ILogger logger, string message, T1 arg1) => Get1<T1>(LogLevel.Critical, message)(logger, arg1, null);

    public static void Debug<T1>(ILogger logger, Exception exception, string message, T1 arg1) => Get1<T1>(LogLevel.Debug, message)(logger, arg1, exception);
    public static void Information<T1>(ILogger logger, Exception exception, string message, T1 arg1) => Get1<T1>(LogLevel.Information, message)(logger, arg1, exception);
    public static void Warning<T1>(ILogger logger, Exception exception, string message, T1 arg1) => Get1<T1>(LogLevel.Warning, message)(logger, arg1, exception);
    public static void Error<T1>(ILogger logger, Exception exception, string message, T1 arg1) => Get1<T1>(LogLevel.Error, message)(logger, arg1, exception);
    public static void Critical<T1>(ILogger logger, Exception exception, string message, T1 arg1) => Get1<T1>(LogLevel.Critical, message)(logger, arg1, exception);

    public static void Debug<T1, T2>(ILogger logger, string message, T1 arg1, T2 arg2) => Get2<T1, T2>(LogLevel.Debug, message)(logger, arg1, arg2, null);
    public static void Information<T1, T2>(ILogger logger, string message, T1 arg1, T2 arg2) => Get2<T1, T2>(LogLevel.Information, message)(logger, arg1, arg2, null);
    public static void Warning<T1, T2>(ILogger logger, string message, T1 arg1, T2 arg2) => Get2<T1, T2>(LogLevel.Warning, message)(logger, arg1, arg2, null);
    public static void Error<T1, T2>(ILogger logger, string message, T1 arg1, T2 arg2) => Get2<T1, T2>(LogLevel.Error, message)(logger, arg1, arg2, null);
    public static void Critical<T1, T2>(ILogger logger, string message, T1 arg1, T2 arg2) => Get2<T1, T2>(LogLevel.Critical, message)(logger, arg1, arg2, null);

    public static void Debug<T1, T2>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2) => Get2<T1, T2>(LogLevel.Debug, message)(logger, arg1, arg2, exception);
    public static void Information<T1, T2>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2) => Get2<T1, T2>(LogLevel.Information, message)(logger, arg1, arg2, exception);
    public static void Warning<T1, T2>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2) => Get2<T1, T2>(LogLevel.Warning, message)(logger, arg1, arg2, exception);
    public static void Error<T1, T2>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2) => Get2<T1, T2>(LogLevel.Error, message)(logger, arg1, arg2, exception);
    public static void Critical<T1, T2>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2) => Get2<T1, T2>(LogLevel.Critical, message)(logger, arg1, arg2, exception);

    public static void Debug<T1, T2, T3>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3) => Get3<T1, T2, T3>(LogLevel.Debug, message)(logger, arg1, arg2, arg3, null);
    public static void Information<T1, T2, T3>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3) => Get3<T1, T2, T3>(LogLevel.Information, message)(logger, arg1, arg2, arg3, null);
    public static void Warning<T1, T2, T3>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3) => Get3<T1, T2, T3>(LogLevel.Warning, message)(logger, arg1, arg2, arg3, null);
    public static void Error<T1, T2, T3>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3) => Get3<T1, T2, T3>(LogLevel.Error, message)(logger, arg1, arg2, arg3, null);
    public static void Critical<T1, T2, T3>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3) => Get3<T1, T2, T3>(LogLevel.Critical, message)(logger, arg1, arg2, arg3, null);

    public static void Debug<T1, T2, T3>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3) => Get3<T1, T2, T3>(LogLevel.Debug, message)(logger, arg1, arg2, arg3, exception);
    public static void Information<T1, T2, T3>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3) => Get3<T1, T2, T3>(LogLevel.Information, message)(logger, arg1, arg2, arg3, exception);
    public static void Warning<T1, T2, T3>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3) => Get3<T1, T2, T3>(LogLevel.Warning, message)(logger, arg1, arg2, arg3, exception);
    public static void Error<T1, T2, T3>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3) => Get3<T1, T2, T3>(LogLevel.Error, message)(logger, arg1, arg2, arg3, exception);
    public static void Critical<T1, T2, T3>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3) => Get3<T1, T2, T3>(LogLevel.Critical, message)(logger, arg1, arg2, arg3, exception);

    public static void Debug<T1, T2, T3, T4>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4) => Get4<T1, T2, T3, T4>(LogLevel.Debug, message)(logger, arg1, arg2, arg3, arg4, null);
    public static void Information<T1, T2, T3, T4>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4) => Get4<T1, T2, T3, T4>(LogLevel.Information, message)(logger, arg1, arg2, arg3, arg4, null);
    public static void Warning<T1, T2, T3, T4>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4) => Get4<T1, T2, T3, T4>(LogLevel.Warning, message)(logger, arg1, arg2, arg3, arg4, null);
    public static void Error<T1, T2, T3, T4>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4) => Get4<T1, T2, T3, T4>(LogLevel.Error, message)(logger, arg1, arg2, arg3, arg4, null);
    public static void Critical<T1, T2, T3, T4>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4) => Get4<T1, T2, T3, T4>(LogLevel.Critical, message)(logger, arg1, arg2, arg3, arg4, null);

    public static void Debug<T1, T2, T3, T4>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4) => Get4<T1, T2, T3, T4>(LogLevel.Debug, message)(logger, arg1, arg2, arg3, arg4, exception);
    public static void Information<T1, T2, T3, T4>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4) => Get4<T1, T2, T3, T4>(LogLevel.Information, message)(logger, arg1, arg2, arg3, arg4, exception);
    public static void Warning<T1, T2, T3, T4>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4) => Get4<T1, T2, T3, T4>(LogLevel.Warning, message)(logger, arg1, arg2, arg3, arg4, exception);
    public static void Error<T1, T2, T3, T4>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4) => Get4<T1, T2, T3, T4>(LogLevel.Error, message)(logger, arg1, arg2, arg3, arg4, exception);
    public static void Critical<T1, T2, T3, T4>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4) => Get4<T1, T2, T3, T4>(LogLevel.Critical, message)(logger, arg1, arg2, arg3, arg4, exception);

    public static void Debug<T1, T2, T3, T4, T5>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5) => Get5<T1, T2, T3, T4, T5>(LogLevel.Debug, message)(logger, arg1, arg2, arg3, arg4, arg5, null);
    public static void Information<T1, T2, T3, T4, T5>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5) => Get5<T1, T2, T3, T4, T5>(LogLevel.Information, message)(logger, arg1, arg2, arg3, arg4, arg5, null);
    public static void Warning<T1, T2, T3, T4, T5>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5) => Get5<T1, T2, T3, T4, T5>(LogLevel.Warning, message)(logger, arg1, arg2, arg3, arg4, arg5, null);
    public static void Error<T1, T2, T3, T4, T5>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5) => Get5<T1, T2, T3, T4, T5>(LogLevel.Error, message)(logger, arg1, arg2, arg3, arg4, arg5, null);
    public static void Critical<T1, T2, T3, T4, T5>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5) => Get5<T1, T2, T3, T4, T5>(LogLevel.Critical, message)(logger, arg1, arg2, arg3, arg4, arg5, null);

    public static void Debug<T1, T2, T3, T4, T5>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5) => Get5<T1, T2, T3, T4, T5>(LogLevel.Debug, message)(logger, arg1, arg2, arg3, arg4, arg5, exception);
    public static void Information<T1, T2, T3, T4, T5>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5) => Get5<T1, T2, T3, T4, T5>(LogLevel.Information, message)(logger, arg1, arg2, arg3, arg4, arg5, exception);
    public static void Warning<T1, T2, T3, T4, T5>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5) => Get5<T1, T2, T3, T4, T5>(LogLevel.Warning, message)(logger, arg1, arg2, arg3, arg4, arg5, exception);
    public static void Error<T1, T2, T3, T4, T5>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5) => Get5<T1, T2, T3, T4, T5>(LogLevel.Error, message)(logger, arg1, arg2, arg3, arg4, arg5, exception);
    public static void Critical<T1, T2, T3, T4, T5>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5) => Get5<T1, T2, T3, T4, T5>(LogLevel.Critical, message)(logger, arg1, arg2, arg3, arg4, arg5, exception);

    public static void Debug<T1, T2, T3, T4, T5, T6>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6) => Get6<T1, T2, T3, T4, T5, T6>(LogLevel.Debug, message)(logger, arg1, arg2, arg3, arg4, arg5, arg6, null);
    public static void Information<T1, T2, T3, T4, T5, T6>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6) => Get6<T1, T2, T3, T4, T5, T6>(LogLevel.Information, message)(logger, arg1, arg2, arg3, arg4, arg5, arg6, null);
    public static void Warning<T1, T2, T3, T4, T5, T6>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6) => Get6<T1, T2, T3, T4, T5, T6>(LogLevel.Warning, message)(logger, arg1, arg2, arg3, arg4, arg5, arg6, null);
    public static void Error<T1, T2, T3, T4, T5, T6>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6) => Get6<T1, T2, T3, T4, T5, T6>(LogLevel.Error, message)(logger, arg1, arg2, arg3, arg4, arg5, arg6, null);
    public static void Critical<T1, T2, T3, T4, T5, T6>(ILogger logger, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6) => Get6<T1, T2, T3, T4, T5, T6>(LogLevel.Critical, message)(logger, arg1, arg2, arg3, arg4, arg5, arg6, null);

    public static void Debug<T1, T2, T3, T4, T5, T6>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6) => Get6<T1, T2, T3, T4, T5, T6>(LogLevel.Debug, message)(logger, arg1, arg2, arg3, arg4, arg5, arg6, exception);
    public static void Information<T1, T2, T3, T4, T5, T6>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6) => Get6<T1, T2, T3, T4, T5, T6>(LogLevel.Information, message)(logger, arg1, arg2, arg3, arg4, arg5, arg6, exception);
    public static void Warning<T1, T2, T3, T4, T5, T6>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6) => Get6<T1, T2, T3, T4, T5, T6>(LogLevel.Warning, message)(logger, arg1, arg2, arg3, arg4, arg5, arg6, exception);
    public static void Error<T1, T2, T3, T4, T5, T6>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6) => Get6<T1, T2, T3, T4, T5, T6>(LogLevel.Error, message)(logger, arg1, arg2, arg3, arg4, arg5, arg6, exception);
    public static void Critical<T1, T2, T3, T4, T5, T6>(ILogger logger, Exception exception, string message, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6) => Get6<T1, T2, T3, T4, T5, T6>(LogLevel.Critical, message)(logger, arg1, arg2, arg3, arg4, arg5, arg6, exception);
}
