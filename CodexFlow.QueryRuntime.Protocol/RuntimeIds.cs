using System.Text.Json.Serialization;

namespace CodexFlow.QueryRuntime.Protocol;

public readonly record struct RuntimeSessionId
{
    [JsonConstructor]
    public RuntimeSessionId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct RuntimeTurnId
{
    [JsonConstructor]
    public RuntimeTurnId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct RuntimeStepId
{
    [JsonConstructor]
    public RuntimeStepId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct RuntimeInvocationId
{
    [JsonConstructor]
    public RuntimeInvocationId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct RuntimeEventId
{
    [JsonConstructor]
    public RuntimeEventId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value ?? string.Empty;
}
