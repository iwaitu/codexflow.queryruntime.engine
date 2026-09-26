using System.Text.Json.Nodes;

namespace CodexFlow.QueryRuntime.UnitTests.Infrastructure;

internal static class TestProcess
{
    public static IReadOnlyList<string> Command(params string[] args)
        => ["dotnet", typeof(TestProcessFixture.FixtureMarker).Assembly.Location, .. args];

    // Keep the manifest under test; only replace the platform-specific executable.
    public static string Manifest(string json, string mode)
    {
        var manifest = JsonNode.Parse(json)!.AsObject();
        var command = Command(mode);
        manifest["command"] = command[0];
        manifest["args"] = new JsonArray(command.Skip(1).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        return manifest.ToJsonString();
    }
}
