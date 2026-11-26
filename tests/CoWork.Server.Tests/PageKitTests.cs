using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CoWork.Contracts;
using Dapper;

namespace CoWork.Server.Tests;

/// <summary>Brief 12: what CoWork gives every page an agent shows: the font kit at /a/fonts/, and the script that makes a page's own
/// prefers-color-scheme rules follow the theme chosen in CoWork.</summary>
public class PageKitTests
{
    [Fact]
    public void The_scheme_script_goes_just_before_the_last_body_close_or_at_the_end()
    {
        string With(string html) => Encoding.UTF8.GetString(PageKit.WithScheme(Encoding.UTF8.GetBytes(html)));
        var page = With("<html><body><p>a</body> in text</p></BODY></html>");
        var at = page.IndexOf("<script data-cowork=\"scheme\">", StringComparison.Ordinal);
        Assert.True(at > page.IndexOf("in text", StringComparison.Ordinal) && at < page.IndexOf("</BODY>", StringComparison.Ordinal));
        Assert.EndsWith("</script>\n</BODY></html>", page);
        Assert.StartsWith("<p>no body</p><script data-cowork=\"scheme\">", With("<p>no body</p>"));
        // a page in another encoding keeps its bytes: the script is ASCII and nothing is re-encoded
        var latin1 = new byte[] { 0x3C, 0x70, 0x3E, 0xE9, 0x3C, 0x2F, 0x62, 0x6F, 0x64, 0x79, 0x3E };   // <p>é</body> in ISO-8859-1
        var out1 = PageKit.WithScheme(latin1);
        Assert.Equal(latin1[..4], out1[..4]);
        Assert.True(PageKit.SchemeScript.All(c => c < 128));
        Assert.Contains("hashchange", PageKit.SchemeScript);
    }

    [Fact]
    public async Task The_font_kit_is_served_to_any_origin_and_every_file_its_stylesheet_names_is_there()
    {
        await using var app = new TestApp();
        var anon = app.CreateClient();
        var css = await anon.GetAsync("/a/fonts/fonts.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);
        Assert.Equal("*", css.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var text = await css.Content.ReadAsStringAsync();
        foreach (var family in new[] { "Fraunces", "Instrument Serif", "Source Serif 4", "IBM Plex Sans", "Bricolage Grotesque", "IBM Plex Mono" })
            Assert.Contains($"font-family: \"{family}\"", text);
        // the guide names the same families
        foreach (var family in new[] { "Fraunces", "Instrument Serif", "Source Serif 4", "IBM Plex Sans", "Bricolage Grotesque", "IBM Plex Mono" })
            Assert.Contains($"`\"{family}\"`", Skills.Design.Body);
        var files = Regex.Matches(text, @"url\(([^)]+)\)").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(13, files.Count);
        foreach (var f in files)
        {
            var r = await anon.GetAsync("/a/fonts/" + f);
            Assert.Equal((HttpStatusCode.OK, "font/woff2", "*"), (r.StatusCode, r.Content.Headers.ContentType!.MediaType, r.Headers.GetValues("Access-Control-Allow-Origin").Single()));
            Assert.Equal("wOF2", Encoding.ASCII.GetString((await r.Content.ReadAsByteArrayAsync())[..4]));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/a/fonts/missing.woff2")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/a/fonts/scheme.js")).StatusCode);
    }

    [Fact]
    public void The_design_skill_is_a_standard_skill_file()
    {
        var text = Skills.Design.Text;
        Assert.StartsWith("---\nname: cowork-design\ndescription: ", text);
        Assert.DoesNotContain("---", Skills.Design.Body[..20]);
        Assert.DoesNotContain("fleet_", text);                      // Weave Fleet's own tools don't exist here
        Assert.DoesNotContain("...", text);
        Assert.Contains("cowork_show", text);
    }
}
