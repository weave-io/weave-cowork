using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CoWork.Server.Tests;

/// <summary>An OpenID Connect provider inside the test process: discovery, keys, the token and userinfo endpoints (the server's backchannel
/// talks to it), and <see cref="SignInAt"/> for what the provider's sign-in page would do in a browser. It checks what a real one checks:
/// the client's id and secret, a code used once, PKCE. The server's real OpenID Connect handler is what's under test.</summary>
public sealed class FakeIssuer : HttpMessageHandler
{
    public const string Authority = "https://id.lumen.test";
    public const string ClientId = "cowork-test", ClientSecret = "test-client-secret";

    public sealed record Identity(string Subject, string? Email, string? Name, string? PreferredUsername);
    /// <summary>The identity the tests give a person: subject "sub-rosa", rosa@lumen.test, "Rosa".</summary>
    public static Identity For(string handle, string? name = null) =>
        new($"sub-{handle}", $"{handle}@lumen.test", name ?? char.ToUpperInvariant(handle[0]) + handle[1..], handle);

    readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "test-key" };
    readonly ConcurrentDictionary<string, (Identity Who, string Nonce, string Challenge, string RedirectUri)> _codes = new();
    readonly ConcurrentDictionary<string, Identity> _access = new();
    public int TokenRequests;
    /// <summary>The issuer this provider puts in its tokens (a test changes it to act as a second provider).</summary>
    public string Issuer { get; set; } = Authority;

    /// <summary>The person signs in at the provider: returns where it sends the browser back to (the server's callback, with a code).</summary>
    public string SignInAt(Uri authorize, Identity who)
    {
        var q = QueryHelpers.ParseQuery(authorize.Query);
        Assert.Equal(ClientId, q["client_id"].ToString());
        Assert.Equal("code", q["response_type"].ToString());
        Assert.Equal("S256", q["code_challenge_method"].ToString());
        Assert.Contains("openid", q["scope"].ToString().Split(' '));
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _codes[code] = (who, q["nonce"].ToString(), q["code_challenge"].ToString(), q["redirect_uri"].ToString());
        return $"{q["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(q["state"].ToString())}";
    }

    /// <summary>The person (or the provider's admin) says no.</summary>
    public static string Refuse(Uri authorize)
    {
        var q = QueryHelpers.ParseQuery(authorize.Query);
        return $"{q["redirect_uri"]}?error=access_denied&error_description=Not+assigned&state={Uri.EscapeDataString(q["state"].ToString())}";
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        switch (req.RequestUri!.AbsolutePath)
        {
            case "/.well-known/openid-configuration":
                return Json(new
                {
                    issuer = Issuer, authorization_endpoint = $"{Authority}/authorize", token_endpoint = $"{Authority}/token",
                    userinfo_endpoint = $"{Authority}/userinfo", jwks_uri = $"{Authority}/jwks",
                    response_types_supported = new[] { "code" }, subject_types_supported = new[] { "public" },
                    id_token_signing_alg_values_supported = new[] { "RS256" }, code_challenge_methods_supported = new[] { "S256" },
                });
            case "/jwks":
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
                return Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = jwk.Kid, n = jwk.N, e = jwk.E } } });
            case "/token":
            {
                Interlocked.Increment(ref TokenRequests);
                var form = QueryHelpers.ParseQuery(await req.Content!.ReadAsStringAsync(ct));
                if (form["client_id"] != ClientId || form["client_secret"] != ClientSecret) return Error("invalid_client");
                if (!_codes.TryRemove(form["code"].ToString(), out var grant)) return Error("invalid_grant");           // a code works once
                if (form["redirect_uri"] != grant.RedirectUri) return Error("invalid_grant");
                var challenge = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"].ToString())));
                if (challenge != grant.Challenge) return Error("invalid_grant");                                        // PKCE
                var access = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                _access[access] = grant.Who;
                var claims = new Dictionary<string, object> { ["sub"] = grant.Who.Subject, ["nonce"] = grant.Nonce };
                if (grant.Who.Name is { } n) claims["name"] = n;
                if (grant.Who.PreferredUsername is { } u) claims["preferred_username"] = u;
                var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = Issuer, Audience = ClientId, Claims = claims, IssuedAt = DateTime.UtcNow, NotBefore = DateTime.UtcNow.AddMinutes(-1),
                    Expires = DateTime.UtcNow.AddMinutes(5), SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
                });
                return Json(new { access_token = access, token_type = "Bearer", expires_in = 300, id_token = idToken });
            }
            case "/userinfo":
                // the email only comes from here, as with providers that keep the id token small
                if (req.Headers.Authorization?.Parameter is not { } bearer || !_access.TryGetValue(bearer, out var who)) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                return Json(new { sub = who.Subject, email = who.Email, name = who.Name, preferred_username = who.PreferredUsername });
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    static HttpResponseMessage Json(object o) => new(HttpStatusCode.OK) { Content = JsonContent.Create(o) };
    static HttpResponseMessage Error(string e) => new(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = e }) };
}
