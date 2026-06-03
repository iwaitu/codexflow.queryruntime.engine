namespace CodexFlow.QueryRuntime.Abstractions;

public sealed record QueryRuntimeProviderOptions
{
    public string? StaticResponse { get; set; }

    public string? ApiUrl { get; set; }

    public string? ApiKey { get; set; }

    public string? Model { get; set; }

    public string? ApiMode { get; set; }
}

public sealed record QueryRuntimeToolProfile(string Name)
{
    public static QueryRuntimeToolProfile None { get; } = new("none");

    public static QueryRuntimeToolProfile ReadOnly { get; } = new("readonly");

    public static QueryRuntimeToolProfile Verify { get; } = new("verify");

    public static QueryRuntimeToolProfile Repair { get; } = new("repair");

    public bool IsNone => string.Equals(Name, None.Name, StringComparison.OrdinalIgnoreCase);
}

public sealed record QueryRuntimeModelPolicyOptions
{
    public QreThinkingPolicy ThinkingPolicy { get; set; } = QreThinkingPolicy.Auto;
}

public sealed record QueryRuntimeOutputOptions
{
    public bool RequestJson { get; set; }

    public bool Json { get; set; }
}

public sealed record QueryRuntimeExecutionOptions
{
    public int MaxRounds { get; set; } = 3;
}

public enum QreThinkingPolicy
{
    Auto = 0,
    Preserve = 1,
    ForceDisabled = 2,
    ForceEnabled = 3
}
