using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace CoWork.Server;

/// <summary>The server's sign-in provider: plain standard OpenID Connect, set with three settings (environment variables
/// <c>COWORK_OIDC__AUTHORITY</c>, <c>COWORK_OIDC__CLIENTID</c>, <c>COWORK_OIDC__CLIENTSECRET</c>). Nothing here knows any one vendor:
/// the authority's discovery document says where everything is.</summary>
public sealed record SsoOptions(string? Authority, string? ClientId, string? ClientSecret)
{
    public bool Configured => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);

    public static SsoOptions From(IConfiguration cfg)
    {
        var s = cfg.GetSection("COWORK_OIDC");
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        return new(Clean(s["AUTHORITY"]), Clean(s["CLIENTID"]), Clean(s["CLIENTSECRET"]));
    }
}

public static class Sso
{
    /// <summary>The OpenID Connect handler.</summary>
    public const string Scheme = "oidc";
    /// <summary>Who the provider said someone is, from its callback until they are a member (or turned away): a short-lived cookie of its
    /// own, so nobody is signed in to CoWork until they are a member.</summary>
    public const string PendingScheme = "cowork-sso";
    /// <summary>The redirect URL to register with the provider is the server's own address plus this.</summary>
    public const string CallbackPath = "/signin-oidc";

    public static AuthenticationBuilder AddSso(this AuthenticationBuilder auth, SsoOptions o)
    {
        auth.AddCookie(PendingScheme, c =>
        {
            c.Cookie.Name = "cowork-sso";
            c.Cookie.HttpOnly = true;
            c.Cookie.SameSite = SameSiteMode.Lax;
            c.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            c.ExpireTimeSpan = TimeSpan.FromMinutes(15);       // long enough to choose a handle, short enough not to linger
            c.SlidingExpiration = false;
            c.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = 401; return Task.CompletedTask; };
        });
        if (!o.Configured) return auth;
        return auth.AddOpenIdConnect(Scheme, x =>
        {
            x.Authority = o.Authority;
            x.ClientId = o.ClientId;
            x.ClientSecret = o.ClientSecret;                    // none: a public client, PKCE alone
            x.RequireHttpsMetadata = !IsLoopbackHttp(o.Authority!);   // plain http only for a provider on this machine (development, the live checks)
            x.ResponseType = "code";
            x.UsePkce = true;
            x.ResponseMode = "query";                           // back with a redirect, not a cross-site form post, so its cookies can be SameSite=Lax
            x.CallbackPath = CallbackPath;
            x.SignInScheme = PendingScheme;
            x.Scope.Clear();
            foreach (var s in new[] { "openid", "profile", "email" }) x.Scope.Add(s);
            x.MapInboundClaims = false;                         // keep the standard claim names: sub, iss, email, name, preferred_username
            x.GetClaimsFromUserInfoEndpoint = true;             // some providers put email and name only there
            x.ClaimActions.Remove("iss");                       // the issuer is half of who someone is: keep it
            x.ClaimActions.MapUniqueJsonKey("preferred_username", "preferred_username");
            x.SaveTokens = false;                               // CoWork never calls the provider for anyone: no tokens kept
            x.CorrelationCookie.SameSite = SameSiteMode.Lax;
            x.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            x.NonceCookie.SameSite = SameSiteMode.Lax;
            x.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            x.Events.OnRemoteFailure = ctx =>
            {
                ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("CoWork.Sso")
                    .LogWarning(ctx.Failure, "Single sign-on didn't complete");
                ctx.Response.Redirect("/?sso=failed");
                ctx.HandleResponse();
                return Task.CompletedTask;
            };
        });
    }

    static bool IsLoopbackHttp(string authority) =>
        Uri.TryCreate(authority, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttp && u.IsLoopback;

    public static OpenIdConnectChallengeProperties Challenge(string intent, string? code, string? serverName, bool again)
    {
        var p = new OpenIdConnectChallengeProperties { RedirectUri = "/auth/sso/done" };
        p.Items["intent"] = intent;
        if (!string.IsNullOrEmpty(code)) p.Items["code"] = code;
        if (!string.IsNullOrWhiteSpace(serverName)) p.Items["serverName"] = serverName.Trim();
        if (again) p.Prompt = "login";                          // "Use another account": ask the provider to sign in afresh
        return p;
    }
}
