using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Hashline.Abstractions;
using CodexFlow.Core.Hashline.Infrastructure;
using CodexFlow.Core.Hashline.Models;
using CodexFlow.Core.Hashline.Services;

namespace CodexFlow.Core.Hashline;

/// <summary>
/// Hashline 服务集合扩展方法。
/// </summary>
public static class HashlineServiceCollectionExtensions
{
    /// <summary>
    /// 注册 Hashline 服务。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="options">配置选项（可选，可通过 IConfiguration 绑定）</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddHashlineServices(
        this IServiceCollection services,
        HashlineOptions? options = null)
    {
        if (options != null)
        {
            NormalizeOptions(options);
        }

        // 注册配置选项
        if (options != null)
        {
            services.AddSingleton(options);
        }
        else
        {
            services.AddSingleton<HashlineOptions>();
        }

        // 注册基础设施服务
        services.AddSingleton<IFileSystemGuard, FileSystemGuard>();
        services.AddSingleton<IEncodingDetector, EncodingDetector>();
        services.AddSingleton<IAuditLogger, AuditLogger>();

        // 注册核心服务
        services.AddSingleton<ITextNormalizer, TextNormalizer>();
        services.AddSingleton<ILineHasher, Sha256LineHasher>();
        services.AddSingleton<IFileFingerprintProvider, Sha256FingerprintProvider>();
        services.AddSingleton<ISnapshotReader, SnapshotReader>();
        services.AddSingleton<IEditRequestValidator, EditRequestValidator>();
        services.AddSingleton<IEditApplier, EditApplier>();
        services.AddSingleton<IDiffRenderer, DiffRenderer>();
        services.AddSingleton<IAtomicFileWriter, AtomicFileWriter>();

        // 注册主服务
        services.AddSingleton<IHashlineFileService, HashlineFileService>();

        return services;
    }

    /// <summary>
    /// 注册 Hashline 服务（带配置委托）。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configure">配置委托</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddHashlineServices(
        this IServiceCollection services,
        Action<HashlineOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var options = new HashlineOptions();
        configure(options);

        return services.AddHashlineServices(options);
    }

    /// <summary>
    /// 注册 Hashline 服务（从 IConfiguration 绑定）。
    /// </summary>
    /// <param name="services">服务集合</param>
    /// <param name="configurationSection">配置节</param>
    /// <returns>服务集合</returns>
    public static IServiceCollection AddHashlineServices(
        this IServiceCollection services,
        IConfigurationSection configurationSection)
    {
        ArgumentNullException.ThrowIfNull(configurationSection);

        var options = new HashlineOptions();
        configurationSection.Bind(options);
        NormalizeOptions(options);

        return services.AddHashlineServices(options);
    }

    private static void NormalizeOptions(HashlineOptions options)
    {
        if (options.UseHashlineByDefaultForExistingFileEdits
            || options.EnabledForReadTool
            || options.EnabledForApplyPatch
            || options.EnabledForSmartPatch
            || options.ForceForHighRiskFiles)
        {
            options.Enabled = true;
        }
    }
}
