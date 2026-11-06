using System.Diagnostics;
using System.Net;
using System.Text;

namespace CodexFlow.QueryRuntime.IntegrationTests.Infrastructure;

/// <summary>
/// Opt-in transport (<c>QRE_TEST_VIA_CURL=1</c>) that sends each request through <c>curl.exe</c>. It exists for
/// hosts where only curl can reach the live endpoint (for example per-process network routing); the QRE adapter
/// and the SDK still build the request and parse the response. Responses are buffered, not streamed.
/// </summary>
internal sealed class CurlTransportHandler : HttpMessageHandler
{
    public static bool Enabled
        => string.Equals(Environment.GetEnvironmentVariable("QRE_TEST_VIA_CURL"), "1", StringComparison.Ordinal);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var bodyFile = Path.GetTempFileName();
        try
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            await File.WriteAllTextAsync(bodyFile, body, cancellationToken);

            var psi = new ProcessStartInfo("curl.exe") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var arg in new[] { "-s", "-m", "150", "-X", request.Method.Method, "-w", "\n%{http_code}" })
            {
                psi.ArgumentList.Add(arg);
            }
            foreach (var header in request.Headers)
            {
                psi.ArgumentList.Add("-H");
                psi.ArgumentList.Add($"{header.Key}: {string.Join(",", header.Value)}");
            }
            psi.ArgumentList.Add("-H");
            psi.ArgumentList.Add("Content-Type: application/json");
            psi.ArgumentList.Add("--data-binary");
            psi.ArgumentList.Add("@" + bodyFile);
            psi.ArgumentList.Add(request.RequestUri!.ToString());

            using var process = Process.Start(psi)!;
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var split = output.LastIndexOf('\n');
            var status = int.Parse(output[(split + 1)..]);
            var content = new StringContent(output[..split], Encoding.UTF8, body.Contains("\"stream\":true", StringComparison.Ordinal) ? "text/event-stream" : "application/json");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = content };
        }
        finally
        {
            File.Delete(bodyFile);
        }
    }
}
