using System.Text.Json;
using System.Text.Json.Nodes;

// Test evidence only: never persist request headers or credentials.
internal sealed class LiveEvidenceHandler(string? outputDirectory) : DelegatingHandler(new HttpClientHandler())
{
    public List<JsonObject> Requests { get; } = [];
    public List<int> StatusCodes { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        Requests.Add(body);
        var response = await base.SendAsync(request, ct);
        StatusCodes.Add((int)response.StatusCode);
        Console.WriteLine($"[HTTP] {request.Method} {request.RequestUri!.GetLeftPart(UriPartial.Path)} => {(int)response.StatusCode}");
        // These examples only send the bundled, synthetic review fixture.
        if (outputDirectory != null)
        {
            Directory.CreateDirectory(outputDirectory);
            var evidence = new { timestamp = DateTimeOffset.UtcNow, endpoint = request.RequestUri.GetLeftPart(UriPartial.Path),
                status = (int)response.StatusCode, request = body };
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, $"request-{Requests.Count:D2}.json"),
                JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }), ct);
            // Evidence mode buffers the response for diagnosis, so it is not a latency benchmark.
            await File.WriteAllTextAsync(Path.Combine(outputDirectory, $"response-{Requests.Count:D2}.txt"),
                await response.Content.ReadAsStringAsync(ct), ct);
        }
        return response;
    }
}

internal static class LiveExample
{
    public static string? Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value for {name}.");
        return args[index + 1];
    }

    public static (string? Endpoint, string Model, string Key) Configuration(string[] args)
    {
        var endpoint = Argument(args, "--endpoint");
        if (args.Contains("--live"))
            endpoint ??= Environment.GetEnvironmentVariable("QRE_API_URL")
                ?? throw new ArgumentException("--live requires --endpoint or QRE_API_URL.");
        var model = Argument(args, "--model") ?? Environment.GetEnvironmentVariable("QRE_MODEL");
        if (endpoint != null && string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Live mode requires --model or QRE_MODEL.");
        var key = Argument(args, "--key") ?? Environment.GetEnvironmentVariable("QRE_API_KEY") ?? "placeholder-key";
        Console.WriteLine(endpoint == null ? "Mode: OFFLINE (no API validation)" : $"Mode: LIVE; endpoint={new Uri(endpoint).GetLeftPart(UriPartial.Path)}; model={model}");
        return (endpoint, model ?? "qwen3-coder-30b", key);
    }

    public static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine($"[PASS] {message}");
    }
}
