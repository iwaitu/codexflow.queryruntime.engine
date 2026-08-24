using System.Reflection;
using System.Text.Json;
using CodexFlow.QueryRuntime.Engine.V2;
using CodexFlow.QueryRuntime.Protocol;
using Xunit;

namespace CodexFlow.QueryRuntime.UnitTests.Protocol;

public sealed class ProtocolContractTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TypedIds_RejectMissingValues(string? value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new RuntimeSessionId(value!));
        Assert.ThrowsAny<ArgumentException>(() => new RuntimeTurnId(value!));
        Assert.ThrowsAny<ArgumentException>(() => new RuntimeStepId(value!));
        Assert.ThrowsAny<ArgumentException>(() => new RuntimeInvocationId(value!));
        Assert.ThrowsAny<ArgumentException>(() => new RuntimeEventId(value!));
    }

    [Fact]
    public void ProtocolAssembly_HasNoProviderHostOrSandboxDependencies()
    {
        var forbiddenPrefixes = new[]
        {
            "Microsoft.Extensions.AI",
            "VllmChatClient",
            "CodexFlow.QueryRuntime.Cli",
            "CodexFlow.QueryRuntime.Models",
            "CodexFlow.QueryRuntime.Experimental",
            "CodexFlow.QueryRuntime.Sandbox"
        };
        var references = typeof(RuntimeItem).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(references, reference => forbiddenPrefixes.Any(prefix =>
            reference.Name?.StartsWith(prefix, StringComparison.Ordinal) == true));
    }

    [Fact]
    public void VersionedFixture_DeserializesAndRoundTripsWithSourceGeneration()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Protocol", "Fixtures", "protocol-v1.json");
        var json = File.ReadAllText(fixturePath);
        var fixture = JsonSerializer.Deserialize(
            json,
            QueryRuntimeProtocolJsonContext.Default.RuntimeProtocolFixture);

        Assert.NotNull(fixture);
        Assert.Equal(QueryRuntimeProtocolSchema.CurrentVersion, fixture.SchemaVersion);
        var message = Assert.Single(fixture.Messages);
        Assert.Equal(RuntimeMessageRole.User, message.Role);
        Assert.IsType<RuntimeTextItem>(Assert.Single(message.Items));
        Assert.Collection(
            fixture.Events,
            item => Assert.IsType<RuntimeReasoningDeltaEvent>(item),
            item => Assert.IsType<RuntimeTextDeltaEvent>(item),
            item => Assert.Equal(RuntimeModelStopReason.EndTurn, Assert.IsType<RuntimeModelCompletedEvent>(item).StopReason));

        var roundTrip = JsonSerializer.Serialize(
            fixture,
            QueryRuntimeProtocolJsonContext.Default.RuntimeProtocolFixture);
        using var originalDocument = JsonDocument.Parse(json);
        using var roundTripDocument = JsonDocument.Parse(roundTrip);
        Assert.True(JsonElement.DeepEquals(originalDocument.RootElement, roundTripDocument.RootElement));
    }

    [Fact]
    public void ProtocolPublicTypes_RemainInProtocolAssembly()
    {
        var protocolAssembly = typeof(RuntimeItem).Assembly;
        var publicTypes = protocolAssembly.GetExportedTypes();

        Assert.All(publicTypes, type => Assert.Equal("CodexFlow.QueryRuntime.Protocol", type.Namespace));
        Assert.Contains(typeof(RuntimeModelRequest), publicTypes);
        Assert.Contains(typeof(RuntimeToolCall), publicTypes);
        Assert.Contains(typeof(RuntimeTerminationReason), publicTypes);
    }

    [Fact]
    public void V2EnginePublicSurface_IsProviderAndSandboxNeutral()
    {
        var forbiddenPrefixes = new[]
        {
            "Microsoft.Extensions.AI",
            "VllmChatClient",
            "CodexFlow.QueryRuntime.Models",
            "CodexFlow.QueryRuntime.Cli",
            "CodexFlow.QueryRuntime.Experimental",
            "CodexFlow.QueryRuntime.Sandbox"
        };
        var v2Types = typeof(RuntimeAgentLoop).Assembly
            .GetExportedTypes()
            .Where(static type => type.Namespace == "CodexFlow.QueryRuntime.Engine.V2")
            .ToArray();
        var signatureTypes = v2Types.SelectMany(GetSignatureTypes).SelectMany(ExpandType).Distinct();

        Assert.DoesNotContain(signatureTypes, type => forbiddenPrefixes.Any(prefix =>
            type.FullName?.StartsWith(prefix, StringComparison.Ordinal) == true ||
            type.Assembly.GetName().Name?.StartsWith(prefix, StringComparison.Ordinal) == true));
    }

    [Fact]
    public void ModelStreamValidator_AcceptsOrderedTypedStream()
    {
        var validator = new RuntimeModelStreamValidator();
        validator.Apply(new RuntimeTextDeltaEvent("hello"));
        validator.Apply(new RuntimeUsageEvent(new RuntimeUsage(2, 1, 3)));
        validator.Apply(new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn));

        validator.Complete();

        Assert.Equal(3, validator.EventCount);
        Assert.Equal(RuntimeModelStopReason.EndTurn, validator.StopReason);
    }

    [Fact]
    public void ModelStreamValidator_RejectsMalformedToolArguments()
    {
        using var document = JsonDocument.Parse("[]");
        var validator = new RuntimeModelStreamValidator();

        var error = Assert.Throws<RuntimeModelStreamValidationException>(() => validator.Apply(
            new RuntimeToolCallEvent(new RuntimeToolCall(
                new RuntimeInvocationId("call-1"),
                "read_file",
                document.RootElement.Clone()))));

        Assert.Equal("malformed_tool_arguments", error.Error.Code);
    }

    [Fact]
    public void ModelStreamValidator_RejectsMissingOrTrailingCompletion()
    {
        var missing = new RuntimeModelStreamValidator();
        missing.Apply(new RuntimeTextDeltaEvent("partial"));
        Assert.Equal(
            "missing_model_completion",
            Assert.Throws<RuntimeModelStreamValidationException>(missing.Complete).Error.Code);

        var trailing = new RuntimeModelStreamValidator();
        trailing.Apply(new RuntimeModelCompletedEvent(RuntimeModelStopReason.EndTurn));
        Assert.Equal(
            "model_event_after_completion",
            Assert.Throws<RuntimeModelStreamValidationException>(() =>
                trailing.Apply(new RuntimeTextDeltaEvent("late"))).Error.Code);
    }

    private static IEnumerable<Type> GetSignatureTypes(Type type)
    {
        if (type.BaseType != null)
        {
            yield return type.BaseType;
        }
        foreach (var interfaceType in type.GetInterfaces())
        {
            yield return interfaceType;
        }
        foreach (var constructor in type.GetConstructors())
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            yield return method.ReturnType;
            foreach (var parameter in method.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            yield return property.PropertyType;
        }
    }

    private static IEnumerable<Type> ExpandType(Type type)
    {
        yield return type;
        if (type.HasElementType && type.GetElementType() is { } elementType)
        {
            foreach (var expanded in ExpandType(elementType))
            {
                yield return expanded;
            }
        }
        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var expanded in ExpandType(argument))
            {
                yield return expanded;
            }
        }
    }
}
