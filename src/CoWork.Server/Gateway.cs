using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

/// <summary>A thread token as the server knows it: which sandbox, agent, project and thread it speaks for.</summary>
public sealed class ThreadTokenRow
{
    public long SandboxId { get; set; }
    public long AgentId { get; set; }
    public long? ProjectId { get; set; }
    public long ThreadId { get; set; }
}

public static class ThreadTokens
{
    /// <summary>A thread token that still works: not revoked, its agent not removed.</summary>
    public static ThreadTokenRow? Find(SqliteConnection c, string hash) =>
        c.QueryFirstOrDefault<ThreadTokenRow>(@"SELECT t.sandbox_id, t.agent_id, t.project_id, t.thread_id FROM thread_tokens t JOIN members m ON m.id=t.agent_id
            WHERE t.token_hash=@hash AND t.revoked_at IS NULL AND m.removed=0", new { hash });
}

/// <summary>
/// The model gateway (docs/architecture/provisioning.html, steps 10 and 11): an OpenAI-compatible endpoint in the server. A sandbox's harness
/// uses its thread token as the API key; the gateway checks it, forwards the call to the provider the admin set in Server settings → Models with
/// the server's own credential, streams the answer back as it comes, and records what the call used against the thread (`ModelUsageRecorded`,
/// in the turn it belongs to). The provider's key never leaves the server.
///
/// Providers: an OpenAI-compatible base URL with a key (OpenRouter, OpenAI, a local model), or GitHub Copilot signed in on the server's own
/// machine, for a server only its owner uses: the server reads the GitHub sign-in OpenCode keeps (`~/.local/share/opencode/auth.json`, read only)
/// and calls Copilot the way OpenCode 2.4.1 does (the GitHub token as the bearer, Copilot's API address from `copilot_internal/user`).
/// </summary>
public sealed class Gateway
{
    public const string OpenAiCompatible = "openai-compatible", Copilot = "github-copilot";
    private const string CopilotApiVersion = "2026-08-01";          // what OpenCode 2.4.1 sends
    private readonly Db _db;
    private readonly IEventStore _store;
    private readonly ILogger<Gateway> _log;
    private readonly HttpClient _http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = Timeout.InfiniteTimeSpan };
    private (string TokenHash, string Api, DateTime At)? _copilotApi;

    /// <summary>Where GitHub's API is (tests point it elsewhere).</summary>
    public static string GitHubApi = "https://api.github.com";

    public Gateway(Db db, IEventStore store, ILogger<Gateway> log) { _db = db; _store = store; _log = log; }

    public static void Map(WebApplication app)
    {
        app.MapPost("/gateway/v1/chat/completions", (HttpContext ctx, Gateway g) => g.Chat(ctx));
        app.MapGet("/gateway/v1/models", (HttpContext ctx, Gateway g) => g.Models(ctx));
    }

    /// <summary>Where the Copilot sign-in is read from: COWORK_COPILOT_AUTH, else OpenCode's data folder of the user the server runs as.</summary>
    public static string CopilotAuthPath()
    {
        var own = Environment.GetEnvironmentVariable("COWORK_COPILOT_AUTH");
        if (!string.IsNullOrEmpty(own)) return own;
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var data = string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share") : xdg;
        return Path.Combine(data, "opencode", "auth.json");
    }

    /// <summary>The GitHub token of the Copilot sign-in, or null when this machine isn't signed in.</summary>
    public static string? CopilotGitHubToken()
    {
        try
        {
            var path = CopilotAuthPath();
            if (!File.Exists(path)) return null;
            var node = JsonNode.Parse(File.ReadAllText(path))?["github-copilot"];
            return (string?)node?["refresh"] is { Length: > 0 } r ? r : (string?)node?["access"];
        }
        catch { return null; }
    }

    private ThreadTokenRow? Caller(HttpContext ctx)
    {
        var h = ctx.Request.Headers.Authorization.ToString();
        var token = h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : ctx.Request.Headers["x-api-key"].ToString();
        if (string.IsNullOrEmpty(token)) return null;
        using var c = _db.Open();
        return ThreadTokens.Find(c, Tokens.Hash(token));
    }

    private static IResult Refuse(int status, string message) => Results.Json(new { error = new { message, type = "cowork_gateway" } }, statusCode: status);

    private async Task<IResult> Models(HttpContext ctx)
    {
        if (Caller(ctx) is not { } t) return Refuse(401, "This gateway takes a sandbox's thread token as its API key.");
        string? model;
        using (var c = _db.Open()) model = c.ExecuteScalar<string?>("SELECT model FROM agents WHERE member_id=@a", new { a = t.AgentId });
        await Task.CompletedTask;
        return Results.Ok(new { @object = "list", data = model is null ? Array.Empty<object>() : new object[] { new { id = model, @object = "model", owned_by = "cowork" } } });
    }

    private sealed record Upstream(string Kind, string BaseUrl, string Key);

    private async Task<Upstream?> Resolve(CancellationToken ct)
    {
        string? kind, baseUrl, key;
        using (var c = _db.Open())
        {
            kind = c.ExecuteScalar<string?>("SELECT value FROM settings WHERE key='model_provider'");
            baseUrl = c.ExecuteScalar<string?>("SELECT value FROM settings WHERE key='model_base_url'");
            key = c.ExecuteScalar<string?>("SELECT value FROM server_secrets WHERE key='model_api_key'");
        }
        if (kind == OpenAiCompatible && !string.IsNullOrWhiteSpace(baseUrl)) return new(kind, baseUrl.TrimEnd('/'), key ?? "");
        if (kind == Copilot && CopilotGitHubToken() is { } gh) return new(kind, await CopilotApi(gh, ct), gh);
        return null;
    }

    /// <summary>Copilot's API address for this sign-in (an individual, business or enterprise plan each have their own), asked once an hour.</summary>
    private async Task<string> CopilotApi(string gh, CancellationToken ct)
    {
        var hash = Tokens.Hash(gh);
        if (_copilotApi is { } cached && cached.TokenHash == hash && DateTime.UtcNow - cached.At < TimeSpan.FromHours(1)) return cached.Api;
        var api = "https://api.githubcopilot.com";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, GitHubApi.TrimEnd('/') + "/copilot_internal/user");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gh);
            req.Headers.TryAddWithoutValidation("User-Agent", "cowork-gateway/0.1");
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2025-04-01");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var res = await _http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode && JsonNode.Parse(await res.Content.ReadAsStringAsync(ct))?["endpoints"]?["api"] is JsonValue v && v.TryGetValue<string>(out var a) && a.Length > 0) api = a.TrimEnd('/');
        }
        catch (Exception e) { _log.LogWarning("couldn't ask GitHub where Copilot's API is ({Message}); using {Api}", e.Message, api); }
        _copilotApi = (hash, api, DateTime.UtcNow);
        return api;
    }

    private async Task Chat(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        if (Caller(ctx) is not { } t) { await Refuse(401, "This gateway takes a sandbox's thread token as its API key.").ExecuteAsync(ctx); return; }
        JsonObject body;
        try { body = (await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct))!.AsObject(); }
        catch { await Refuse(400, "The body isn't a chat completion request.").ExecuteAsync(ctx); return; }
        // the project's budget, before the call: a thread or a month at its cap gets no more model calls, and the thread says why
        if (t.ProjectId is { } project)
        {
            Budgets.Reached? reached;
            using (var c = _db.Open()) reached = Budgets.Check(c, t.ThreadId, project, Db.Now());
            if (reached is not null)
            {
                RecordReached(t, project, reached);
                await Results.Json(new { error = new { message = reached.Text, type = "budget_exceeded", code = "budget_exceeded" } }, statusCode: 402).ExecuteAsync(ctx);
                return;
            }
        }
        var up = await Resolve(ct);
        if (up is null) { await Refuse(503, "No model provider is set up on this server. An admin sets one in Server settings → Models.").ExecuteAsync(ctx); return; }

        var stream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var st) && st;
        if (stream)
        {
            // ask for the usage in the last chunk, so the call can be counted
            var opts = body["stream_options"] as JsonObject ?? new JsonObject();
            opts["include_usage"] = true; body["stream_options"] = opts;
        }
        var model = (string?)body["model"];
        using var req = new HttpRequestMessage(HttpMethod.Post, up.BaseUrl + "/chat/completions") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (up.Key.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", up.Key);
        req.Headers.TryAddWithoutValidation("User-Agent", "cowork-gateway/0.1");
        if (up.Kind == Copilot)
        {
            // as OpenCode 2.4.1 calls Copilot: an agent's follow-up (a tool result, not a person's message) is marked as the agent's
            var last = (body["messages"] as JsonArray)?.LastOrDefault();
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", CopilotApiVersion);
            req.Headers.TryAddWithoutValidation("Openai-Intent", "conversation-edits");
            req.Headers.TryAddWithoutValidation("x-initiator", (string?)last?["role"] == "user" ? "user" : "agent");
        }

        HttpResponseMessage res;
        try { res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { await Refuse(502, "The model provider couldn't be reached: " + e.Message).ExecuteAsync(ctx); return; }
        using (res)
        {
            ctx.Response.StatusCode = (int)res.StatusCode;
            ctx.Response.ContentType = res.Content.Headers.ContentType?.ToString() ?? (stream ? "text/event-stream" : "application/json");
            ctx.Response.Headers.CacheControl = "no-cache";
            JsonNode? usage = null;
            await using var from = await res.Content.ReadAsStreamAsync(ct);
            if (stream && res.IsSuccessStatusCode)
            {
                // pass the server-sent events through line by line as they arrive, and keep the last usage seen
                using var reader = new StreamReader(from, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync(ct)) is not null)
                {
                    await ctx.Response.WriteAsync(line + "\n", ct);
                    if (line.Length == 0) await ctx.Response.Body.FlushAsync(ct);
                    else if (line.StartsWith("data:") && line.Contains("\"usage\""))
                        try { if (JsonNode.Parse(line[5..].Trim())?["usage"] is JsonObject u) usage = u; } catch { /* not JSON: pass it on */ }
                }
                await ctx.Response.Body.FlushAsync(ct);
            }
            else
            {
                using var ms = new MemoryStream();
                await from.CopyToAsync(ms, ct);
                if (res.IsSuccessStatusCode) try { usage = JsonNode.Parse(ms.ToArray())?["usage"]; } catch { }
                ms.Position = 0;
                await ms.CopyToAsync(ctx.Response.Body, ct);
            }
            if (res.IsSuccessStatusCode) Record(t, model, usage);
            else _log.LogWarning("the model provider said {Status} for thread {Thread}", (int)res.StatusCode, t.ThreadId);
        }
    }

    /// <summary>The thread says once per turn that it stopped at a cap (`BudgetReached`, a notice with a Raise button for leads).</summary>
    private void RecordReached(ThreadTokenRow t, long project, Budgets.Reached r)
    {
        try
        {
            _store.Append(ctx =>
            {
                var turn = ctx.Query<(string? Turn, long? Behalf)>("SELECT turn, behalf FROM open_turns WHERE thread_id=@t AND agent_id=@a", new { t = t.ThreadId, a = t.AgentId }).FirstOrDefault();
                var stream = $"thread-{t.ThreadId}";
                if (turn.Turn is not null && ctx.Scalar<int>("SELECT COUNT(*) FROM events WHERE stream_id=@stream AND type='BudgetReached' AND correlation=@c", new { stream, c = turn.Turn }) > 0)
                    return Array.Empty<NewEvent>();
                var leads = ctx.Query<long>("SELECT member_id FROM project_members WHERE project_id=@project AND role='lead' ORDER BY member_id", new { project }).ToArray();
                return new[] { new NewEvent(stream, "BudgetReached", new
                {
                    agentId = t.AgentId, projectId = project, channelId = ctx.Scalar<long>("SELECT channel_id FROM threads WHERE root_id=@t", new { t = t.ThreadId }),
                    postId = ctx.NextId("posts"), scope = r.Scope, unit = r.Unit, limit = r.Limit, used = r.Used, text = r.Text, leadIds = leads,
                }, t.AgentId, turn.Behalf, turn.Turn) };
            });
        }
        catch (Exception e) { _log.LogWarning(e, "couldn't record a budget stop for thread {Thread}", t.ThreadId); }
    }

    /// <summary>One call, counted against the thread and the turn it belongs to (the one in flight, else the thread's last), for whoever asked.</summary>
    private void Record(ThreadTokenRow t, string? model, JsonNode? usage)
    {
        long? Num(string k) => usage?[k] is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
        double? cost = usage?["cost"] is JsonValue cv && cv.TryGetValue<double>(out var d) ? d : null;
        try
        {
            _store.Append(ctx =>
            {
                var turn = ctx.Query<(string? Turn, long? Behalf)>("SELECT turn, behalf FROM open_turns WHERE thread_id=@t AND agent_id=@a", new { t = t.ThreadId, a = t.AgentId }).FirstOrDefault();
                if (turn.Turn is null)
                    turn = ctx.Query<(string? Turn, long? Behalf)>("SELECT correlation, on_behalf_of FROM events WHERE stream_id=@s AND type='TurnStarted' AND json_extract(data,'$.agentId')=@a ORDER BY position DESC LIMIT 1",
                        new { s = $"thread-{t.ThreadId}", a = t.AgentId }).FirstOrDefault();
                return new[] { new NewEvent($"thread-{t.ThreadId}", "ModelUsageRecorded", new
                {
                    agentId = t.AgentId, sandboxId = t.SandboxId, projectId = t.ProjectId, model,
                    promptTokens = Num("prompt_tokens"), completionTokens = Num("completion_tokens"), totalTokens = Num("total_tokens"), cost,
                }, t.AgentId, turn.Behalf, turn.Turn ?? "between-turns") };
            });
        }
        catch (Exception e) { _log.LogWarning(e, "couldn't record a model call for thread {Thread}", t.ThreadId); }
    }
}
