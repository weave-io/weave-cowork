using System.Text.Json.Nodes;
using CoWork.Contracts;
using CoWork.Runner;
using CoWork.Runner.Acp;

namespace CoWork.Runner.Tests;

public class AcpMappingTests
{
    static JsonNode J(string s) => JsonNode.Parse(s)!;

    [Fact]
    public void Message_chunks_become_text_events()
    {
        var e = AcpAdapter.Map(7, J("""{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"Hi"}}""")).Single();
        Assert.Equal((7L, StreamKinds.Text, "Hi"), (e.ThreadId, e.Kind, e.Text));
    }

    [Fact]
    public void Tool_rows_say_what_was_touched()
    {
        var e = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call","toolCallId":"c","title":"read","kind":"read","locations":[{"path":"src/Api.cs"}]}""")).First();
        Assert.Equal("src/Api.cs", e.Title);          // the row already says "read"
        e = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call","toolCallId":"c","title":"glob","kind":"search","rawInput":{"pattern":"**/*.csproj"}}""")).First();
        Assert.Equal("glob **/*.csproj", e.Title);
    }

    [Fact]
    public void A_tool_call_with_no_locations_is_still_described()
    {
        var e = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call","toolCallId":"c","title":"bash","locations":[],"rawInput":{"command":"ls"}}""")).First();
        Assert.Equal("bash ls", e.Title);
    }

    // Real OpenCode 2.4.1 output (docs/build/oc2-tool-calls.jsonl): the first tool_call is bare, the detail comes in the in_progress update
    [Fact]
    public void OpenCode2_detail_arrives_in_the_update_after_a_bare_tool_call()
    {
        const string ws = "/home/alex/checkout-app";
        var start = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call","toolCallId":"a","name":"grep","title":"grep","kind":"search","status":"pending","locations":[],"rawInput":{}}"""), ws).Single();
        Assert.Equal("grep", start.Title);
        var up = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call_update","toolCallId":"a","status":"in_progress","kind":"search","title":"grep","locations":[],"rawInput":{"pattern":"roundTax","caseSensitive":false}}"""), ws).Single();
        Assert.Equal((StreamKinds.ToolUpdate, "a", "grep roundTax", null), (up.Kind, up.ToolId, up.Title, up.Status));
        up = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call_update","toolCallId":"b","status":"in_progress","kind":"read","title":"read","locations":[{"path":"/home/alex/checkout-app/src/Api.cs"}],"rawInput":{"path":"/home/alex/checkout-app/src/Api.cs","limit":2000}}"""), ws).Single();
        Assert.Equal("src/Api.cs", up.Title);
        up = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call_update","toolCallId":"e","status":"in_progress","kind":"read","title":"read","rawInput":{"path":"/home/alex/checkout-app"}}"""), ws).Single();
        Assert.Equal(".", up.Title);          // the workspace itself
        // a search's "path" is where it looked, not what it looked for
        up = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call_update","toolCallId":"d","status":"in_progress","kind":"search","title":"grep","rawInput":{"pattern":"roundTax","path":"/home/alex/checkout-app/src"}}"""), ws).Single();
        Assert.Equal("grep roundTax", up.Title);
        // a shell tool's only location is its working folder: the command is the detail
        var sh = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call_update","toolCallId":"c","status":"in_progress","kind":"execute","title":"ls src","locations":[{"path":"/home/alex/checkout-app"}],"rawInput":{"command":"ls src","cwd":"/home/alex/checkout-app"}}"""), ws).Single();
        Assert.Equal("ls src", sh.Title);
        // the completed update carries neither title nor input: it must not blank the row
        var end = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call_update","toolCallId":"b","status":"completed","locations":[{"path":"/home/alex/checkout-app/src/Api.cs"}]}"""), ws).Single();
        Assert.Equal((StreamKinds.ToolEnd, null), (end.Kind, end.Title));
    }

    [Fact]
    public void A_finished_tool_reports_how_much_it_printed_and_a_hash_and_the_text_only_when_the_agent_records_output()
    {
        var u = J("""{"sessionUpdate":"tool_call_update","toolCallId":"a","status":"completed","rawOutput":{"output":"hello"}}""");
        var off = AcpAdapter.Map(1, u).Single();
        Assert.Equal((5L, "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", null), (off.OutputBytes, off.OutputSha256, off.Output));
        Assert.Equal("hello", AcpAdapter.Map(1, u, null, true).Single().Output);
        // content as ACP sends it: a list of text blocks
        var c = J("""{"sessionUpdate":"tool_call_update","toolCallId":"a","status":"completed","content":[{"type":"content","content":{"type":"text","text":"a"}},{"type":"content","content":{"type":"text","text":"b"}}]}""");
        Assert.Equal("a\nb", AcpAdapter.Map(1, c, null, true).Single().Output);
        Assert.Null(AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call_update","toolCallId":"a","status":"completed"}""")).Single().OutputBytes);
        Assert.Equal(64 * 1024, System.Text.Encoding.UTF8.GetByteCount(AcpAdapter.Cap(new string('x', 70_000))!));
    }

    [Fact]
    public void Tool_calls_start_then_end_and_thoughts_are_ignored()
    {
        var start = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call","toolCallId":"c1","title":"Read a.txt","kind":"read","status":"in_progress"}""")).Single();
        Assert.Equal((StreamKinds.ToolStart, "c1", "Read a.txt", "read", "running"), (start.Kind, start.ToolId, start.Title, start.ToolKind, start.Status));
        var end = AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"completed"}""")).Single();
        Assert.Equal((StreamKinds.ToolEnd, "c1", "completed"), (end.Kind, end.ToolId, end.Status));
        Assert.Empty(AcpAdapter.Map(1, J("""{"sessionUpdate":"tool_call_update","toolCallId":"c1","status":"in_progress"}""")));
        Assert.Empty(AcpAdapter.Map(1, J("""{"sessionUpdate":"agent_thought_chunk","content":{"type":"text","text":"hmm"}}""")));
    }
}

public class AcpAdapterTests
{
    sealed class Sink : IRunnerSink
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<StreamEvent> Events = new();
        public readonly TaskCompletionSource<PermissionRequestMsg> Asked = new();
        public string? Answer = "allow";
        public Task Stream(StreamEvent e) { Events.Enqueue(e); return Task.CompletedTask; }
        public Task<string?> AskPermission(PermissionRequestMsg r) { Asked.TrySetResult(r); return Task.FromResult(Answer); }
        public async Task WaitTurnEnd(long thread, int already = 0)
        {
            for (var i = 0; i < 600 && Events.Count(e => e.ThreadId == thread && e.Kind == StreamKinds.TurnEnded) <= already; i++) await Task.Delay(50);
        }
    }

    static DeliverMessage Msg(long thread, string text, string instructions = "Be brief.") =>
        new(1, thread, 1, "reviewer", instructions, new[] { "python3", "-I", Path.Combine(AppContext.BaseDirectory, "fake_acp_agent.py") }, Path.GetTempPath(), text);

    [Fact]
    public async Task A_turn_streams_text_and_tool_rows_then_ends()
    {
        var sink = new Sink();
        await using var adapter = new AcpAdapter(sink);
        await adapter.Deliver(Msg(5, "hello"));
        await sink.WaitTurnEnd(5);
        var kinds = sink.Events.Select(e => e.Kind).ToArray();
        Assert.Equal(new[] { StreamKinds.Text, StreamKinds.ToolStart, StreamKinds.ToolEnd, StreamKinds.Text, StreamKinds.TurnEnded }, kinds);
    }

    [Fact]
    public async Task Instructions_go_in_the_first_prompt_of_a_session_only_and_threads_get_own_sessions()
    {
        var sink = new Sink();
        await using var adapter = new AcpAdapter(sink);
        await adapter.Deliver(Msg(1, "ECHO one"));
        await adapter.Deliver(Msg(1, "ECHO two"));
        await adapter.Deliver(Msg(2, "ECHO three"));
        await sink.WaitTurnEnd(2);
        string Said(long t, int i) => sink.Events.Where(e => e.ThreadId == t && e.Kind == StreamKinds.Text).ElementAt(i).Text!;
        Assert.Contains("Be brief.", Said(1, 0));
        Assert.DoesNotContain("Be brief.", Said(1, 1));
        Assert.Contains("Be brief.", Said(2, 0));      // a new thread is a new session
    }

    [Fact]
    public async Task A_permission_request_goes_to_the_sink_and_the_answer_goes_back_to_the_agent()
    {
        var sink = new Sink { Answer = "allow" };
        await using var adapter = new AcpAdapter(sink);
        await adapter.Deliver(Msg(3, "PERMISSION please"));
        var asked = await sink.Asked.Task;
        Assert.Equal(("Run ls", 2), (asked.Title, asked.Options.Length));
        Assert.Contains(sink.Events, e => e.Kind == StreamKinds.Text && e.Text!.Contains("\"selected\"") && e.Text.Contains("\"allow\""));

        var deny = new Sink { Answer = null };
        await using var adapter2 = new AcpAdapter(deny);
        await adapter2.Deliver(Msg(4, "PERMISSION please"));
        await deny.WaitTurnEnd(4);
        Assert.Contains(deny.Events, e => e.Kind == StreamKinds.Text && e.Text!.Contains("cancelled"));
    }

    static string StorePath() => Path.Combine(Path.GetTempPath(), $"cw-{Guid.NewGuid():N}", "sessions.json");

    [Fact]
    public async Task A_restarted_runner_loads_each_threads_saved_session_without_replaying_it()
    {
        var path = StorePath();
        var first = new Sink();
        await using (var a1 = new AcpAdapter(first, new SessionStore(path))) await a1.Deliver(Msg(1, "ECHO before"));
        var firstText = first.Events.Single(e => e.Kind == StreamKinds.Text).Text!;
        Assert.Contains("Be brief.", firstText);
        var savedId = new SessionStore(path).Get(1);
        Assert.NotNull(savedId);

        var second = new Sink();                                  // a "restart": a new adapter and a new harness process, the same state file
        await using (var a2 = new AcpAdapter(second, new SessionStore(path))) await a2.Deliver(Msg(1, "ECHO after"));
        await second.WaitTurnEnd(1);
        var text = Assert.Single(second.Events, e => e.Kind == StreamKinds.Text).Text!;     // the harness's REPLAYED chunk was dropped
        Assert.Contains("LOADED:" + savedId, text);
        Assert.DoesNotContain("Be brief.", text);                  // it's the same session: no instructions again
        Assert.Equal(savedId, new SessionStore(path).Get(1));
    }

    [Fact]
    public async Task If_a_saved_session_will_not_load_a_new_one_starts_and_is_saved()
    {
        var path = StorePath();
        new SessionStore(path).Set(1, "bad-session");
        var sink = new Sink();
        await using (var a = new AcpAdapter(sink, new SessionStore(path))) await a.Deliver(Msg(1, "ECHO hi"));
        var text = Assert.Single(sink.Events, e => e.Kind == StreamKinds.Text).Text!;
        Assert.Contains("Be brief.", text);
        Assert.DoesNotContain("LOADED", text);
        Assert.NotEqual("bad-session", new SessionStore(path).Get(1));
    }

    [Fact]
    public async Task A_harness_that_cannot_start_ends_the_turn_with_an_error()
    {
        var sink = new Sink();
        await using var adapter = new AcpAdapter(sink);
        var m = Msg(9, "hi") with { HarnessCommand = new[] { "definitely-not-a-real-command-xyz" } };
        await adapter.Deliver(m);
        await sink.WaitTurnEnd(9);
        Assert.Contains(sink.Events, e => e.Kind == StreamKinds.Error);
        Assert.Contains(sink.Events, e => e.Kind == StreamKinds.TurnEnded);
    }
}

public class ConfigTests
{
    [Fact]
    public void Saved_agents_round_trip_and_the_file_is_private()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cw-{Guid.NewGuid():N}", "agents.json");
        var cfg = new RunnerConfig(path);
        cfg.Add(new SavedAgent("http://localhost:5880", 3, "reviewer", "tok"));
        cfg.Add(new SavedAgent("http://localhost:5880", 3, "reviewer", "tok2"));     // re-enrolling replaces
        var all = cfg.Load();
        Assert.Equal("tok2", Assert.Single(all).Token);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        cfg.Remove(all[0]);
        Assert.Empty(cfg.Load());
    }

    [Fact]
    public void An_mcp_tool_called_through_opencodes_code_tool_is_named_by_the_tool_and_its_arguments()
    {
        // what OpenCode 2.4.1 sends for an MCP call (checked 25 Nov 2026): a bare "execute", then the code that calls it
        static System.Text.Json.Nodes.JsonObject Update(string code) => new()
        {
            ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "t1", ["status"] = "in_progress", ["kind"] = "other", ["title"] = "execute",
            ["locations"] = new System.Text.Json.Nodes.JsonArray(), ["rawInput"] = new System.Text.Json.Nodes.JsonObject { ["code"] = code },
        };
        Assert.Equal("cowork_read_channel #checkout-app", AcpAdapter.Describe(Update("\nconst result = await tools.cowork.cowork_read_channel({ channel: '#checkout-app' });\nreturn result;\n")));
        // and as it arrives: parsed from the line OpenCode wrote
        var line = System.Text.Json.Nodes.JsonNode.Parse("{\"sessionUpdate\":\"tool_call_update\",\"toolCallId\":\"t2\",\"status\":\"in_progress\",\"kind\":\"other\",\"title\":\"execute\",\"locations\":[],\"rawInput\":{\"code\":\"\\nconst result = await tools.cowork.cowork_search({ query: 'Q4 refund budget' });\\nreturn result;\\n\"}}")!;
        Assert.Equal("cowork_search Q4 refund budget", AcpAdapter.Describe(line));
        Assert.Equal(StreamKinds.ToolUpdate, AcpAdapter.Map(42, line).Single().Kind);
        Assert.Equal("cowork_search Q4 refund budget", AcpAdapter.Map(42, line, "/work/repo").Single().Title);
        Assert.Equal("cowork_resolve_thread Rounding (per line) fixed",
            AcpAdapter.Describe(Update("await tools.cowork.cowork_resolve_thread({ title: \"Rounding (per line) fixed\", summary: \"See (1) and (2).\" })")));
        Assert.Equal("cowork_search refund rounding, cowork_resolve_thread done",
            AcpAdapter.Describe(Update("await tools.cowork.cowork_search({ query: \"refund rounding\" }); await tools.cowork.cowork_resolve_thread({ summary: 'done' })")));
    }

    /// <summary>Replays OpenCode 2.4.1's tool updates through the adapter and keeps each row's title as the server does: a later non-empty
    /// title (a toolUpdate, then the toolEnd) replaces the one before.</summary>
    static Dictionary<string, (string Title, string? Status)> Rows(IEnumerable<string> lines)
    {
        var rows = new Dictionary<string, (string Title, string? Status)>();
        foreach (var line in lines)
        {
            var u = System.Text.Json.Nodes.JsonNode.Parse(line)!;
            foreach (var e in AcpAdapter.Map(42, u, "/work/repo"))
                rows[e.ToolId!] = e.Kind switch
                {
                    StreamKinds.ToolStart => (e.Title!, e.Status),
                    _ => (string.IsNullOrEmpty(e.Title) ? rows[e.ToolId!].Title : e.Title!, e.Kind == StreamKinds.ToolEnd ? e.Status : rows[e.ToolId!].Status),
                };
        }
        return rows;
    }

    [Fact]
    public void Mcp_calls_are_named_from_the_real_update_sequence_whatever_shape_the_models_code_takes()
    {
        // recorded from a sandbox on 25 Nov 2026: a bare "execute", three in_progress updates with the model's code (here
        // tools["cowork"].cowork_read_channel(…), which the first parser didn't match), and the completed update with OpenCode's own record
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "oc2-mcp-tool-calls.jsonl"));
        var rows = Rows(lines).Values.ToList();
        Assert.Equal(new[] { ("cowork_read_channel #checkout-app", "completed"), ("cowork_resolve_thread Finance partial refunds requirement", "completed") }, rows.Select(r => (r.Title, r.Status)));
        // while it runs, the bracket form is named too
        var running = AcpAdapter.Map(42, System.Text.Json.Nodes.JsonNode.Parse(lines[1])!, "/work/repo").Single();
        Assert.Equal((StreamKinds.ToolUpdate, "cowork_read_channel #checkout-app"), (running.Kind, running.Title));
        // code the parser can't read at all (the model destructured): the row says execute while it runs, and is named when it finishes
        var odd = lines.Select(l =>
        {
            var n = System.Text.Json.Nodes.JsonNode.Parse(l)!;
            if (n["rawInput"]?["code"] is System.Text.Json.Nodes.JsonValue c)
                n["rawInput"]!["code"] = c.GetValue<string>().Replace("tools[\"cowork\"].", "(({ cowork }) => cowork)(tools).");
            return n.ToJsonString();
        }).ToList();
        Assert.NotEqual(lines, odd);
        Assert.Equal("execute", AcpAdapter.Map(42, System.Text.Json.Nodes.JsonNode.Parse(odd[1])!, "/work/repo").Single().Title);
        Assert.Equal(new[] { "cowork_read_channel #checkout-app", "cowork_resolve_thread Finance partial refunds requirement" }, Rows(odd).Values.Select(r => r.Title));
    }
}
