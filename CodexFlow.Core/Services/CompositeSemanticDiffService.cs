using CodexFlow.Core.Abstractions;
using CodexFlow.Core.Models;
using CodexFlow.Core.Utils;
using Microsoft.Extensions.Logging;
using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace CodexFlow.Core.Services;

public class CompositeSemanticDiffService : ISemanticDiffService
{
    private readonly IEnumerable<ILanguageSemanticDiffProvider> _providers;
    private readonly ILogger<CompositeSemanticDiffService> _logger;

    public CompositeSemanticDiffService(IEnumerable<ILanguageSemanticDiffProvider> providers, ILogger<CompositeSemanticDiffService> logger)
    {
        _providers = providers;
        _logger = logger;
    }

    public async Task<SemanticDiffResult> AnalyzeDiffAsync(string mainPath, string shadowPath, CancellationToken ct = default)
    {
        StructuredLog.Information(_logger, "Analyzing diff for path {ShadowPath}", shadowPath);
        var aggregatedResult = new SemanticDiffResult();

        if (string.IsNullOrEmpty(shadowPath) || !Directory.Exists(shadowPath))
        {
            // If it is a file, handle single file
            if (File.Exists(shadowPath))
            {
                var provider = _providers.FirstOrDefault(p => p.CanHandle(Path.GetExtension(shadowPath)));
                if (provider != null)
                {
                    return await provider.AnalyzeAsync(mainPath, shadowPath, ct).ConfigureAwait(false);
                }
            }
            return aggregatedResult;
        }

        var files = Directory.GetFiles(shadowPath, "*.*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            var ext = Path.GetExtension(file);
            var provider = _providers.FirstOrDefault(p => p.CanHandle(ext));

            if (provider != null)
            {
                var relativePath = Path.GetRelativePath(shadowPath, file);
                var mainFile = Path.Combine(mainPath, relativePath);

                try
                {
                    var fileResult = await provider.AnalyzeAsync(mainFile, file, ct).ConfigureAwait(false);
                    if (fileResult.HasChanges)
                    {
                        aggregatedResult.ChangedSymbols.AddRange(fileResult.ChangedSymbols);
                        aggregatedResult.ImpactedFiles.AddRange(fileResult.ImpactedFiles);
                        if (!string.IsNullOrEmpty(fileResult.Recommendations))
                        {
                            aggregatedResult.Recommendations += $"\n[{relativePath}]: {fileResult.Recommendations}";
                        }
                    }
                }
                catch (IOException ex)
                {
                    StructuredLog.Error(_logger, ex, "Error analyzing file {File}", file);
                }
                catch (UnauthorizedAccessException ex)
                {
                    StructuredLog.Error(_logger, ex, "Error analyzing file {File}", file);
                }
                catch (InvalidOperationException ex)
                {
                    StructuredLog.Error(_logger, ex, "Error analyzing file {File}", file);
                }
                catch (JsonException ex)
                {
                    StructuredLog.Error(_logger, ex, "Error analyzing file {File}", file);
                }
                catch (Win32Exception ex)
                {
                    StructuredLog.Error(_logger, ex, "Error analyzing file {File}", file);
                }
            }
        }

        aggregatedResult.HasChanges = aggregatedResult.ChangedSymbols.Count > 0;
        aggregatedResult.ReplaceImpactedFiles(aggregatedResult.ImpactedFiles.Distinct());

        return aggregatedResult;
    }
}

