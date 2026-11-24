using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;

namespace CoWork.Server.Tests;

public class VoiceTests
{
    static byte[] Unb64(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight((s.Length + 3) / 4 * 4, '='));

    [Fact]
    public void A_room_token_is_an_HS256_JWT_with_the_video_grant()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var jwt = LiveKitToken.Create("apikey", "a-secret-that-is-long-enough-for-livekit", "channel-7", "42", "Alex", TimeSpan.FromHours(1), now);
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.DoesNotContain(jwt, c => c is '=' or '+' or '/');          // base64url, unpadded
        Assert.Equal("""{"alg":"HS256","typ":"JWT"}""", Encoding.UTF8.GetString(Unb64(parts[0])));
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes("a-secret-that-is-long-enough-for-livekit"), Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
        Assert.Equal(expected, Unb64(parts[2]));
        var p = JsonDocument.Parse(Unb64(parts[1])).RootElement;
        Assert.Equal(("apikey", "42", "Alex"), (p.GetProperty("iss").GetString(), p.GetProperty("sub").GetString(), p.GetProperty("name").GetString()));
        Assert.Equal(1_800_000_000 + 3600, p.GetProperty("exp").GetInt64());
        Assert.True(p.GetProperty("nbf").GetInt64() <= 1_800_000_000);
        var v = p.GetProperty("video");
        Assert.Equal("channel-7", v.GetProperty("room").GetString());
        Assert.True(v.GetProperty("roomJoin").GetBoolean() && v.GetProperty("canPublish").GetBoolean() && v.GetProperty("canSubscribe").GetBoolean());
    }

    [Fact]
    public async Task Without_voice_configured_a_voice_channel_exists_but_gives_no_token()
    {
        using var app = new TestApp();
        var alex = await app.Setup();
        var ch = await alex.VoiceChannel("standup");
        var boot = await alex.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        Assert.False(boot.GetProperty("voice").GetProperty("enabled").GetBoolean());
        Assert.Equal("voice", boot.GetProperty("channels").EnumerateArray().First(c => c.GetProperty("id").GetInt64() == ch).GetProperty("kind").GetString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await alex.Http.PostAsync($"/api/channels/{ch}/voice/token", null)).StatusCode);
        var hub = alex.Hub(); await hub.StartAsync();
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync("JoinVoice", ch));
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task Tokens_go_only_to_people_who_can_see_a_voice_channel()
    {
        using var app = new TestApp(voice: true);
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa");
        var open = await alex.VoiceChannel("standup");
        var priv = await alex.VoiceChannel("leads-call", priv: true);
        var text = await alex.Channel("general2");
        var boot = await rosa.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        Assert.Equal("ws://livekit.test:7880", boot.GetProperty("voice").GetProperty("url").GetString());

        var r = await rosa.Http.PostAsync($"/api/channels/{open}/voice/token", null);
        r.EnsureSuccessStatusCode();
        var token = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        var claims = JsonDocument.Parse(Unb64(token.Split('.')[1])).RootElement;
        Assert.Equal(rosa.Id.ToString(), claims.GetProperty("sub").GetString());
        Assert.Equal("Rosa", claims.GetProperty("name").GetString());
        Assert.Equal($"channel-{open}", claims.GetProperty("video").GetProperty("room").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await rosa.Http.PostAsync($"/api/channels/{priv}/voice/token", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await alex.Http.PostAsync($"/api/channels/{priv}/voice/token", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await rosa.Http.PostAsync($"/api/channels/{text}/voice/token", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().PostAsync($"/api/channels/{open}/voice/token", null)).StatusCode);
    }

    [Fact]
    public async Task Who_is_in_a_voice_channel_is_pushed_live_and_a_dropped_connection_leaves()
    {
        using var app = new TestApp(voice: true);
        var alex = await app.Setup(); var rosa = await app.Join(alex, "rosa", "Rosa");
        var ch = await alex.VoiceChannel("standup");
        var priv = await alex.VoiceChannel("leads-call", priv: true);
        var seen = System.Threading.Channels.Channel.CreateUnbounded<long[]>();
        var rosaHub = rosa.Hub(); rosaHub.On<JsonElement>("voice-presence", p => { if (p.GetProperty("channelId").GetInt64() == ch) seen.Writer.TryWrite(p.GetProperty("memberIds").EnumerateArray().Select(x => x.GetInt64()).ToArray()); });
        await rosaHub.StartAsync();
        var alexHub = alex.Hub(); await alexHub.StartAsync();
        async Task<long[]> Next() { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); return await seen.Reader.ReadAsync(cts.Token); }

        await alexHub.InvokeAsync("JoinVoice", ch);
        Assert.Equal(new[] { alex.Id }, await Next());
        await rosaHub.InvokeAsync("JoinVoice", ch);
        Assert.Equal(new[] { alex.Id, rosa.Id }.Order().ToArray(), await Next());
        var boot = await rosa.Http.GetFromJsonAsync<JsonElement>("/api/bootstrap");
        Assert.Equal(2, boot.GetProperty("voicePresence").GetProperty(ch.ToString()).GetArrayLength());

        await Assert.ThrowsAsync<HubException>(() => rosaHub.InvokeAsync("JoinVoice", priv));          // can't see it
        await alexHub.DisposeAsync();                                                                   // Alex's tab closes
        Assert.Equal(new[] { rosa.Id }, await Next());
        await rosaHub.InvokeAsync("LeaveVoice");
        Assert.Empty(await Next());
        await rosaHub.DisposeAsync();
    }
}
