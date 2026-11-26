using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Data.Sqlite;

namespace CoWork.Server;

public record SetupBody(string ServerName, string Handle, string DisplayName);
public record JoinBody(string Handle, string DisplayName);

/// <summary>Who the provider said someone is, while they are between its callback and being a member.</summary>
public sealed record SsoIdentity(string Issuer, string Subject, string? Email, string? Name, string? PreferredUsername, string Intent, string? Code, string? ServerName);

public static partial class Api
{
    /// <summary>Setup, joining with an invite, signing in and out. Nobody types a password: the server's OpenID Connect provider says who
    /// someone is (its issuer and subject id), and the invite link is still the only way in. Until someone is a member, who they are lives in
    /// the short-lived pending cookie; once they are, they get the usual session cookie.</summary>
    static void MapAuth(WebApplication app, RouteGroupBuilder api)
    {
        // the browser comes here (a page load, not a fetch) to go to the provider
        app.MapGet("/auth/sso", async (HttpContext ctx, SsoOptions sso, string? intent, string? code, string? serverName, bool? again) =>
        {
            if (!sso.Configured) return Results.Redirect("/");
            await ctx.SignOutAsync(Sso.PendingScheme);
            intent = intent is "setup" or "invite" ? intent : "signin";
            return Results.Challenge(Sso.Challenge(intent, code, serverName, again == true), new[] { Sso.Scheme });
        });

        // ... and back here once the provider has said who they are: a member is signed in; anyone else goes on to choose a handle (setup,
        // an invite) or is told they aren't a member
        app.MapGet("/auth/sso/done", async (HttpContext ctx, Db db) =>
        {
            var who = await Pending(ctx);
            if (who is null) return Results.Redirect("/?sso=failed");
            long? member;
            using (var c = db.Open()) member = MemberFor(c, who);
            if (member is { } id)
            {
                await ctx.SignOutAsync(Sso.PendingScheme);
                await SignIn(ctx, id);
                return Results.Redirect("/");
            }
            return Results.Redirect(who.Intent == "invite" && who.Code is { } code ? $"/invite/{Uri.EscapeDataString(code)}" : "/");
        });

        api.MapGet("/setup/status", (Db db, SsoOptions sso) =>
        {
            using var c = db.Open();
            var people = c.ExecuteScalar<int>("SELECT COUNT(*) FROM people");
            var name = c.ExecuteScalar<string>("SELECT value FROM settings WHERE key='server_name'");
            // people but no sign-ins: a server from before single sign-on, whose password accounts can't sign in any more
            var passwordAccounts = people > 0 && c.ExecuteScalar<int>("SELECT COUNT(*) FROM logins") == 0;
            return Results.Ok(new { needsSetup = people == 0, serverName = name, sso = sso.Configured, passwordAccounts });
        });

        api.MapGet("/auth/pending", async (HttpContext ctx) =>
        {
            var w = await Pending(ctx);
            if (w is null) return Results.Ok(new { pending = (object?)null });
            return Results.Ok(new
            {
                pending = new
                {
                    email = w.Email, name = w.Name, intent = w.Intent, code = w.Code, serverName = w.ServerName,
                    handle = SuggestHandle(w), displayName = SuggestDisplayName(w),
                },
            });
        });
        api.MapDelete("/auth/pending", async (HttpContext ctx) => { await ctx.SignOutAsync(Sso.PendingScheme); return Results.Ok(new { ok = true }); });

        api.MapPost("/setup", async (SetupBody b, HttpContext ctx, Db db, IEventStore store) =>
        {
            var w = await Pending(ctx);
            if (w is null) return Err(401, "Sign in with single sign-on first.");
            using (var c = db.Open())
            {
                if (c.ExecuteScalar<int>("SELECT COUNT(*) FROM people") > 0) return Err(409, "This server is already set up.");
                if (string.IsNullOrWhiteSpace(b.ServerName) || b.ServerName.Trim().Length > 60) return Err(400, "Name the server (up to 60 characters).");
                var bad = ValidateMember(b.Handle, b.DisplayName);
                if (bad is not null) return Err(400, bad);
            }
            long id = 0; var already = false;
            store.Append(d =>
            {
                already = d.Scalar<int>("SELECT COUNT(*) FROM people") > 0;            // two setups at once: the second one loses
                if (already) return Array.Empty<NewEvent>();
                id = d.NextId("members"); var general = d.NextId("channels");
                d.Execute(InsertLogin, new { w.Issuer, w.Subject, id, w.Email, now = Db.Now() });       // who they are at the provider stays out of the event
                return new[]
                {
                    new NewEvent("server", "ServerSetUp", new { serverName = b.ServerName.Trim() }, id),
                    new NewEvent("server", "PersonJoined", new { memberId = id, handle = b.Handle, displayName = b.DisplayName.Trim(), role = "admin" }, id, CausedByIndex: 0),
                    new NewEvent($"channel-{general}", "ChannelCreated", new { channelId = general, name = "general", topic = "Everything that doesn't have a channel yet", isPrivate = false, kind = "text", creatorId = id }, id, CausedByIndex: 0),
                };
            });
            if (already) return Err(409, "This server is already set up.");
            await ctx.SignOutAsync(Sso.PendingScheme);
            await SignIn(ctx, id);
            return Results.Ok(new { ok = true });
        });

        api.MapPost("/invites/{code}/accept", async (string code, JoinBody b, HttpContext ctx, Db db, Realtime rt, IEventStore store) =>
        {
            var w = await Pending(ctx);
            if (w is null) return Err(401, "Sign in with single sign-on first.");
            using (var c = db.Open())
            {
                var (_, reason) = CheckInvite(c, code);
                if (reason is not null) return Err(410, reason);
                var bad = ValidateMember(b.Handle, b.DisplayName);
                if (bad is not null) return Err(400, bad);
                if (HandleTaken(c, b.Handle)) return Err(409, Taken(b.Handle));
            }
            long id = 0; string? refused = null; var status = 0;
            store.Append(d =>
            {
                refused = null;
                var inv = d.Query<InviteRow>(InviteSql, new { code }).FirstOrDefault();       // again, inside the write: two people may be using the last place
                var (_, reason) = CheckInvite(inv);
                if (reason is not null) { refused = reason; status = 410; return Array.Empty<NewEvent>(); }
                if (d.Scalar<int>("SELECT COUNT(*) FROM members WHERE handle=@h", new { h = b.Handle }) > 0) { refused = Taken(b.Handle); status = 409; return Array.Empty<NewEvent>(); }
                if (d.Scalar<int>("SELECT COUNT(*) FROM logins WHERE issuer=@Issuer AND subject=@Subject", new { w.Issuer, w.Subject }) > 0) { refused = "You're already a member here."; status = 409; return Array.Empty<NewEvent>(); }
                id = d.NextId("members");
                d.Execute(InsertLogin, new { w.Issuer, w.Subject, id, w.Email, now = Db.Now() });
                return new[]
                {
                    new NewEvent("server", "PersonJoined", new { memberId = id, handle = b.Handle, displayName = b.DisplayName.Trim(), role = "member", inviteId = inv!.Id }, id),
                    new NewEvent("server", "InviteUsed", new { inviteId = inv.Id, memberId = id }, id, CausedByIndex: 0),
                };
            });
            if (refused is not null) return Err(status, refused);
            await ctx.SignOutAsync(Sso.PendingScheme);
            await SignIn(ctx, id);
            await rt.ToAll("bootstrap-changed");
            return Results.Ok(new { ok = true });
        });

        // signing out ends the CoWork session only, not the one at the provider
        api.MapPost("/auth/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync();
            await ctx.SignOutAsync(Sso.PendingScheme);
            return Results.Ok(new { ok = true });
        });
    }

    const string InsertLogin = "INSERT INTO logins(issuer, subject, member_id, email, created_at) VALUES (@Issuer, @Subject, @id, @Email, @now)";

    static string? ValidateMember(string handle, string displayName)
    {
        if (handle is null || !HandleRx.IsMatch(handle)) return "Handles are 2–31 characters: lowercase letters, numbers, - and _.";
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 60) return "Add a display name (up to 60 characters).";
        return null;
    }
    static bool HandleTaken(SqliteConnection c, string handle) => c.ExecuteScalar<int>("SELECT COUNT(*) FROM members WHERE handle=@handle", new { handle }) > 0;
    static string Taken(string handle) => $"Someone here is already @{handle}. Try another.";

    static async Task<SsoIdentity?> Pending(HttpContext ctx)
    {
        var r = await ctx.AuthenticateAsync(Sso.PendingScheme);
        if (!r.Succeeded || r.Principal.FindFirst("sub") is not { } sub) return null;
        string? V(string type) => r.Principal.FindFirst(type)?.Value is { Length: > 0 } v ? v : null;
        string? Item(string key) => r.Properties?.Items.TryGetValue(key, out var v) == true ? v : null;
        return new(V("iss") ?? sub.Issuer, sub.Value, V("email"), V("name"), V("preferred_username"), Item("intent") ?? "signin", Item("code"), Item("serverName"));
    }

    /// <summary>The member this provider identity signs in as: by issuer and subject, never by email; a removed member is nobody.</summary>
    static long? MemberFor(SqliteConnection c, SsoIdentity w) => c.QueryFirstOrDefault<long?>(
        "SELECT l.member_id FROM logins l JOIN people p ON p.member_id=l.member_id JOIN members m ON m.id=l.member_id WHERE l.issuer=@Issuer AND l.subject=@Subject AND m.removed=0",
        new { w.Issuer, w.Subject });

    /// <summary>A handle to start from: the provider's username, else the email's local part, else the name, made to fit the handle rules.</summary>
    static string SuggestHandle(SsoIdentity w)
    {
        foreach (var raw in new[] { w.PreferredUsername, w.Email, w.Name })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var s = raw.Split('@')[0].ToLowerInvariant();
            s = Regex.Replace(s, "[^a-z0-9_-]+", "-").Trim('-', '_');
            if (s.Length > 31) s = s[..31].TrimEnd('-', '_');
            if (HandleRx.IsMatch(s)) return s;
        }
        return "";
    }
    static string SuggestDisplayName(SsoIdentity w)
    {
        var s = w.Name ?? w.PreferredUsername?.Split('@')[0] ?? w.Email?.Split('@')[0] ?? "";
        return s.Length > 60 ? s[..60] : s;
    }
}
