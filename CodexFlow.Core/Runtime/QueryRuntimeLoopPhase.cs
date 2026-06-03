namespace CodexFlow.Core.Runtime;

/// <summary>
/// Coarse-grained stages of the agentic query loop.
/// </summary>
public enum QueryRuntimeLoopPhase
{
    PromptAssembly,
    ModelSampling,
    ToolPlanExtraction,
    ToolPlanValidation,
    ToolArgumentNormalization,
    ToolExecution,
    Observation,
    RecoveryDecision,
    ContextCompaction,
    ContinuationDecision,
    StopDecision
}
