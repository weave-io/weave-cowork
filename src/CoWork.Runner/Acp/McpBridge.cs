using System.Net.Http.Headers;
using System.Text;

namespace CoWork.Runner.Acp;

/// <summary>
/// For a harness that takes MCP servers only over stdio: `cowork-runner mcp &lt;url&gt;` reads one JSON-RPC message per line, posts it to CoWork's
/// MCP endpoint with the token and thread from its environment (COWORK_MCP_TOKEN, COWORK_MCP_THREAD), and writes the answer back as a line.
/// </summary>
public static class McpBridge
{
    /// <summary>How to start this runner again as the bridge (as a single file, or as `dotnet cowork-runner.dll`).</summary>
    public static (string Command, string[] Args) Command(string url)
    {
        var exe = Environment.ProcessPath ?? "cowork-runner";
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
            return (exe, new[] { typeof(McpBridge).Assembly.Location, "mcp", url });
        return (exe, new[] { "mcp", url });
    }

    public static async Task<int> Run(string url, TextReader input, TextWriter output, HttpMessageHandler? handler = null)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler);
        var token = Environment.GetEnvironmentVariable("COWORK_MCP_TOKEN") ?? "";
        var thread = Environment.GetEnvironmentVariable("COWORK_MCP_THREAD") ?? "";
        string? line;
        while ((line = await input.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(line, Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("X-Cowork-Thread", thread);
            req.Headers.Accept.ParseAdd("application/json");
            try
            {
                using var res = await http.SendAsync(req);
                var body = await res.Content.ReadAsStringAsync();
                if (res.StatusCode == System.Net.HttpStatusCode.Accepted || body.Length == 0) continue;     // a notification
                await output.WriteLineAsync(body.ReplaceLineEndings(" ")); await output.FlushAsync();
            }
            catch (Exception e) { await Console.Error.WriteLineAsync("cowork mcp: " + e.Message); }
        }
        return 0;
    }
}
