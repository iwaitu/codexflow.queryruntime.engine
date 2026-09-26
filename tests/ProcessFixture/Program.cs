using System.Diagnostics;
using System.Globalization;

namespace CodexFlow.QueryRuntime.TestProcessFixture;

/// <summary>Locates the real child executable from the test output directory.</summary>
public sealed class FixtureMarker;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        switch (args[0])
        {
            case "environment":
                foreach (System.Collections.DictionaryEntry pair in Environment.GetEnvironmentVariables())
                    Console.WriteLine($"{pair.Key}={pair.Value}");
                break;
            case "print":
                Console.Write(args[1]);
                break;
            case "sleep":
                await Task.Delay(TimeSpan.FromSeconds(10));
                break;
            case "stdio":
                await File.WriteAllTextAsync("external-request.json", await Console.In.ReadToEndAsync());
                Console.Write("{\"result\":\"external-ok\"}");
                break;
            case "mcp":
                await File.WriteAllTextAsync("mcp-request.json", await Console.In.ReadToEndAsync());
                Console.WriteLine("{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"mcp-ok\"}]}}");
                break;
            case "large-output":
                await Console.In.ReadToEndAsync();
                Console.Write(new string('x', 50_000));
                break;
            case "process-tree":
                await Console.In.ReadToEndAsync();
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                start.ArgumentList.Add(typeof(FixtureMarker).Assembly.Location);
                start.ArgumentList.Add("delayed-write");
                using (var child = Process.Start(start)!)
                {
                    await File.WriteAllTextAsync("timeout-child.pid", child.Id.ToString(CultureInfo.InvariantCulture));
                    await child.WaitForExitAsync();
                }
                break;
            case "delayed-write":
                await Task.Delay(TimeSpan.FromSeconds(2));
                await File.WriteAllTextAsync("leaked-timeout.txt", "child survived");
                break;
            default:
                return 2;
        }
        return 0;
    }
}
