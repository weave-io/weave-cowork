using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Dapper;

namespace CoWork.Server.Tests;

/// <summary>Search (docs/build/brief-6.md): the FTS5 projection, the query's filters, what a person may see, and the rebuild.</summary>
public class SearchTests
{
    sealed record World(TestApp App, Person Alex, Person Rosa, long Ch, long Secret, FakeRunner Runner, long Root) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await Runner.DisposeAsync(); App.Dispose(); }
    }

    /// <summary>Lumen Labs: #checkout-app with a thread where reviewer answers in three segments, a plain message, and #leads (private, Rosa isn't in it).</summary>
    static async Task<World> Make()
    {
        var app = new TestApp();
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa");
        var ch = await alex.Channel("checkout-app");
        var secret = await alex.Channel("leads", priv: true);
        var runner = await FakeRunner.Enrol(app, alex, "reviewer");
        (await alex.Http.PutAsync($"/api/channels/{ch}/members/{runner.AgentId}", null)).EnsureSuccessStatusCode();
        await runner.Connect();

        await rosa.Post(ch, "Finance wants partial refunds by January.");
        var root = (await alex.Post(ch, "@reviewer why is the cart total off by a cent?")).GetProperty("id").GetInt64();
        await runner.NextDelivery();
        await runner.Say(root, "roundTax rounds each line ");          // the first words are stored at once …
        await runner.Say(root, "before summing, so a refund ");        // … the rest arrive as later segments of the same answer
        await runner.Say(root, "drifts by a cent.");
        await runner.End(root);
        await rosa.Post(ch, "Do we round half-up or half-even on refunds?", root);
        await alex.Post(secret, "Rounding of the refund budget: keep this between us.");
        return new World(app, alex, rosa, ch, secret, runner, root);
    }

    static async Task<JsonElement> Find(Person p, string q, string extra = "") =>
        await p.Http.GetFromJsonAsync<JsonElement>($"/api/search?q={Uri.EscapeDataString(q)}{extra}");
    static JsonElement[] Messages(JsonElement r) => r.GetProperty("messages").GetProperty("items").EnumerateArray().ToArray();
    static JsonElement[] Threads(JsonElement r) => r.GetProperty("threads").GetProperty("items").EnumerateArray().ToArray();
    static string Plain(string snippet) => snippet.Replace("\u0002", "").Replace("\u0003", "");
    static string[] Texts(JsonElement r) => Messages(r).Select(m => Plain(m.GetProperty("snippet").GetString()!)).ToArray();

    [Fact]
    public async Task Refund_rounding_finds_threads_first_then_messages_with_stems_and_highlights()
    {
        await using var w = await Make();
        var r = await Find(w.Rosa, "refund rounding");

        // the thread: its opening post, the reply count, the agents in it, and the best match inside it (reviewer's answer)
        var t = Assert.Single(Threads(r));
        Assert.Equal(w.Root, t.GetProperty("rootId").GetInt64());
        Assert.Equal("checkout-app", t.GetProperty("channelName").GetString());
        Assert.Equal(2, t.GetProperty("replyCount").GetInt32());
        Assert.Equal(new[] { "reviewer" }, t.GetProperty("agents").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(2, t.GetProperty("matches").GetInt32());
        Assert.Contains("cart total", t.GetProperty("title").GetString());

        // messages: the agent's answer is ONE document (three segments), found by words from different segments; "rounds" and "round" match "rounding"
        var texts = Texts(r);
        Assert.Contains(texts, s => s.Contains("roundTax rounds each line before summing, so a refund drifts"));
        Assert.Contains(texts, s => s.Contains("half-even on refunds"));
        Assert.Equal(2, r.GetProperty("messages").GetProperty("total").GetInt32());
        Assert.DoesNotContain(texts, s => s.Contains("partial refunds"));      // "rounding" is required too
        var answer = Messages(r).First(m => Plain(m.GetProperty("snippet").GetString()!).StartsWith("roundTax"));
        Assert.Equal(w.Root, answer.GetProperty("threadId").GetInt64());
        Assert.Contains("\u0002rounds\u0003", answer.GetProperty("snippet").GetString());           // the matching words are marked
        Assert.Contains("\u0002refund\u0003", answer.GetProperty("snippet").GetString());
        Assert.Equal(w.Runner.AgentId, answer.GetProperty("authorId").GetInt64());

        // stemming the other way: "rounds" finds "rounding"
        Assert.Contains("Rounding", Texts(await Find(w.Alex, "rounds budget")).Single());
        // as she types: the last word is a prefix
        Assert.Single(Threads(await Find(w.Rosa, "refund roun")));
    }

    [Fact]
    public async Task A_private_channel_she_isnt_in_never_appears_and_the_counts_are_hers()
    {
        await using var w = await Make();
        var rosa = await Find(w.Rosa, "rounding");
        Assert.DoesNotContain(Texts(rosa), s => s.Contains("between us"));
        Assert.Empty(Messages(await Find(w.Rosa, "budget")));
        Assert.Empty(Messages(await Find(w.Rosa, "rounding in:#leads")));
        Assert.Empty(Messages(await Find(w.Rosa, "from:@alex")).Where(m => m.GetProperty("channelId").GetInt64() == w.Secret));

        var alex = await Find(w.Alex, "rounding");
        Assert.Contains(Texts(alex), s => s.Contains("between us"));
        Assert.Equal(rosa.GetProperty("messages").GetProperty("total").GetInt32() + 1, alex.GetProperty("messages").GetProperty("total").GetInt32());

        // added to it, she finds it; removed again, she doesn't
        (await w.Alex.Http.PutAsync($"/api/channels/{w.Secret}/members/{w.Rosa.Id}", null)).EnsureSuccessStatusCode();
        Assert.Single(Messages(await Find(w.Rosa, "budget")));
        (await w.Alex.Http.DeleteAsync($"/api/channels/{w.Secret}/members/{w.Rosa.Id}")).EnsureSuccessStatusCode();
        Assert.Empty(Messages(await Find(w.Rosa, "budget")));

        // paging is over what she may see
        for (var i = 0; i < 5; i++) await w.Alex.Post(w.Secret, $"secret rounding note {i}");
        for (var i = 0; i < 3; i++) await w.Rosa.Post(w.Ch, $"public rounding note {i}");
        var page = await Find(w.Rosa, "rounding note", "&limit=2");
        Assert.Equal(3, page.GetProperty("messages").GetProperty("total").GetInt32());
        Assert.Equal(2, Messages(page).Length);
        Assert.Single(Messages(await Find(w.Rosa, "rounding note", "&limit=2&offset=2")));

        // signed out: no search
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await w.App.CreateClient().GetAsync("/api/search?q=refund")).StatusCode);
    }

    [Fact]
    public async Task Deleted_and_redacted_messages_are_found_by_nothing_they_used_to_say()
    {
        await using var w = await Make();
        var p = (await w.Rosa.Post(w.Ch, "The coupon code is LUMEN-SPRING")).GetProperty("id").GetInt64();
        Assert.Single(Messages(await Find(w.Rosa, "coupon")));
        (await w.Rosa.Http.PostAsync($"/api/posts/{p}/redact", null)).EnsureSuccessStatusCode();
        Assert.Empty(Messages(await Find(w.Rosa, "coupon")));
        Assert.Empty(Messages(await Find(w.Alex, "LUMEN-SPRING")));
        Assert.Empty(Messages(await Find(w.Alex, "redacted")));

        // an agent's answer, redacted by its owner: none of its segments is found
        Assert.NotEmpty(Messages(await Find(w.Alex, "summing")));
        var answer = Messages(await Find(w.Alex, "summing")).Single().GetProperty("postId").GetInt64();
        (await w.Alex.Http.PostAsync($"/api/posts/{answer}/redact", null)).EnsureSuccessStatusCode();
        Assert.Empty(Messages(await Find(w.Alex, "summing")));
        Assert.Empty(Messages(await Find(w.Alex, "drifts")));

        var q = (await w.Rosa.Post(w.Ch, "Partial refunds need a ledger entry.")).GetProperty("id").GetInt64();
        Assert.Single(Messages(await Find(w.Rosa, "ledger")));
        (await w.Rosa.Http.DeleteAsync($"/api/posts/{q}")).EnsureSuccessStatusCode();
        Assert.Empty(Messages(await Find(w.Rosa, "ledger")));

        // a deleted channel takes everything in it, its threads too
        Assert.NotEmpty(Threads(await Find(w.Alex, "cart")));
        (await w.Alex.Http.DeleteAsync($"/api/channels/{w.Ch}")).EnsureSuccessStatusCode();
        Assert.Empty(Threads(await Find(w.Alex, "cart")));
        Assert.DoesNotContain(Messages(await Find(w.Alex, "refunds")), m => m.GetProperty("channelId").GetInt64() == w.Ch);
        using var c = w.App.Sql();
        Assert.Equal(0, c.ExecuteScalar<int>("SELECT COUNT(*) FROM search_docs WHERE channel_id=@ch", new { ch = w.Ch }));
    }

    [Fact]
    public async Task Each_filter_narrows_it()
    {
        await using var w = await Make();
        var other = await w.Alex.Channel("checkout-alerts");
        await w.Rosa.Post(other, "Refund alert: rounding job failed.");

        Assert.Equal(4, (await Find(w.Rosa, "refund")).GetProperty("messages").GetProperty("total").GetInt32());
        Assert.All(Messages(await Find(w.Rosa, "refund in:#checkout-app")), m => Assert.Equal(w.Ch, m.GetProperty("channelId").GetInt64()));
        Assert.Single(Messages(await Find(w.Rosa, "refund in:checkout-alerts")));
        Assert.Single(Messages(await Find(w.Rosa, "refund IN:#Checkout-Alerts")));
        Assert.Empty(Messages(await Find(w.Rosa, "refund in:#nowhere")));

        var byReviewer = Messages(await Find(w.Rosa, "refund from:@reviewer"));
        Assert.Equal(w.Runner.AgentId, Assert.Single(byReviewer).GetProperty("authorId").GetInt64());
        var byRosa = Messages(await Find(w.Rosa, "refund from:@rosa"));
        Assert.Equal(3, byRosa.Length);        // the reply, the message and the alert
        Assert.All(byRosa, m => Assert.Equal(w.Rosa.Id, m.GetProperty("authorId").GetInt64()));
        Assert.Equal(2, Messages(await Find(w.Rosa, "refund from:@rosa in:#checkout-app")).Length);
        Assert.Equal(3, Messages(await Find(w.Rosa, "refund from:@rosa from:@reviewer in:#checkout-app")).Length);     // two of one filter: either

        var threads = await Find(w.Rosa, "refund is:thread");
        Assert.Single(Threads(threads));
        Assert.Empty(Messages(threads));
        Assert.True(threads.GetProperty("query").GetProperty("isThread").GetBoolean());

        // filters alone list the newest first
        var latest = Messages(await Find(w.Rosa, "from:@rosa"));
        Assert.Equal("Refund alert: rounding job failed.", Plain(latest[0].GetProperty("snippet").GetString()!));
        // nothing typed: nothing searched
        Assert.True((await Find(w.Rosa, "  ")).GetProperty("empty").GetBoolean());
        // FTS syntax in the box is just words
        foreach (var q in new[] { "refund\" OR NEAR(", "*", "a:b c\"", "\"half even\"", "refund -rounding", "(" })
            (await w.Rosa.Http.GetAsync($"/api/search?q={Uri.EscapeDataString(q)}")).EnsureSuccessStatusCode();
        Assert.Single(Messages(await Find(w.Rosa, "\"half-even on refunds\"")));
    }

    [Fact]
    public async Task What_you_just_wrote_is_found_at_once_and_the_index_rebuilds_from_zero_to_the_same_rows()
    {
        await using var w = await Make();
        await w.Rosa.Post(w.Ch, "Tallyrate rounds half-even per invoice.");
        Assert.Single(Messages(await Find(w.Rosa, "tallyrate")));           // no wait: the query brings the index to the head first
        var redact = (await w.Rosa.Post(w.Ch, "temporary rounding note")).GetProperty("id").GetInt64();
        (await w.Rosa.Http.PostAsync($"/api/posts/{redact}/redact", null)).EnsureSuccessStatusCode();
        var gone = (await w.Rosa.Post(w.Ch, "another rounding note")).GetProperty("id").GetInt64();
        (await w.Rosa.Http.DeleteAsync($"/api/posts/{gone}")).EnsureSuccessStatusCode();
        var dead = await w.Alex.Channel("old-refunds");
        await w.Alex.Post(dead, "rounding in a channel that goes");
        (await w.Alex.Http.DeleteAsync($"/api/channels/{dead}")).EnsureSuccessStatusCode();

        var views = w.App.Services.GetRequiredService<ProjectionRunner>();
        await views.CatchUpAsync();
        string Dump() { using var c = w.App.Sql(); return string.Join("\n", c.Query("SELECT d.*, f.title, f.body FROM search_docs d LEFT JOIN search_fts f ON f.rowid=d.id ORDER BY d.id").Select(r => JsonSerializer.Serialize((IDictionary<string, object>)r))); }
        var before = Dump();
        var answers = (await Find(w.Rosa, "rounding")).GetRawText();
        Assert.Contains("summing", before);

        (await w.Alex.Http.PostAsync("/api/admin/projections/search/rebuild", null)).EnsureSuccessStatusCode();
        Assert.Equal(before, Dump());
        Assert.Equal(answers, (await Find(w.Rosa, "rounding")).GetRawText());

        // the FTS table itself is consistent after the rebuild (FTS5's own check)
        using (var c = w.App.Sql()) c.Execute("INSERT INTO search_fts(search_fts, rank) VALUES ('integrity-check', 1)");
        var status = await w.Alex.Http.GetFromJsonAsync<JsonElement>("/api/admin/projections");
        Assert.Contains(status.EnumerateArray(), s => s.GetProperty("name").GetString() == "search" && s.GetProperty("title").GetString() == "Search");
    }
}
