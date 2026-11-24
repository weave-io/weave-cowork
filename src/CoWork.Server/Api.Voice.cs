using Microsoft.AspNetCore.Routing;

namespace CoWork.Server;

public static partial class Api
{
    static void MapVoice(RouteGroupBuilder authed)
    {
        // a room token for one join; only for people who can see the voice channel (private: only its members)
        authed.MapPost("/channels/{id:long}/voice/token", (long id, HttpContext ctx, Db db, VoiceOptions vo) =>
        {
            if (!vo.Enabled) return Err(503, "Voice isn't set up on this server.");
            var me = Me(ctx);
            using var c = db.Open();
            var ch = Q.Channel(c, id);
            if (ch is null || !Access.CanSee(c, id, me)) return Err(404, "No such channel.");
            if (ch.Kind != "voice") return Err(400, "That's not a voice channel.");
            var m = Q.Member(c, me)!;
            var token = LiveKitToken.Create(vo.ApiKey!, vo.ApiSecret!, $"channel-{id}", me.ToString(), m.DisplayName, TimeSpan.FromHours(1));
            return Results.Ok(new { token, url = vo.Url });
        });
    }
}
