using System.Collections.Frozen;
using System.Text;

namespace CoWork.Server;

/// <summary>
/// Brief 13: what CoWork gives every page an agent shows. A font kit (open-licence families, served at <c>/a/fonts/</c>, declared by
/// <c>/a/fonts/fonts.css</c>), because a page may load nothing from the internet; and a small script added to each page as it's served, so the
/// page's own <c>prefers-color-scheme</c> rules follow the theme chosen in CoWork (the canvas says which in the address's fragment, which never
/// reaches the server). Pages keep writing standard CSS; downloads get the page as the agent wrote it.
/// </summary>
public static class PageKit
{
    const string Prefix = "CoWork.Server.PageKit.";

    /// <summary>The kit's files by name: <c>fonts.css</c> and the <c>.woff2</c> files it declares.</summary>
    public static readonly FrozenDictionary<string, byte[]> Fonts = Load("fonts.");

    public static readonly string SchemeScript = Encoding.UTF8.GetString(Load("").GetValueOrDefault("scheme.js") ?? throw new InvalidOperationException("PageKit/scheme.js isn't embedded."));

    static readonly byte[] Tag = Encoding.UTF8.GetBytes("<script data-cowork=\"scheme\">\n" + SchemeScript + "</script>\n");

    static FrozenDictionary<string, byte[]> Load(string folder)
    {
        var asm = typeof(PageKit).Assembly; var d = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var n in asm.GetManifestResourceNames())
        {
            if (!n.StartsWith(Prefix + folder, StringComparison.Ordinal)) continue;
            var name = n[(Prefix.Length + folder.Length)..];
            if (name.Contains('.') && name.IndexOf('.') != name.LastIndexOf('.')) continue;     // a file in a folder below this one
            using var s = asm.GetManifestResourceStream(n)!; using var ms = new MemoryStream(); s.CopyTo(ms);
            d[name] = ms.ToArray();
        }
        return d.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>The page with the scheme script just before its last <c>&lt;/body&gt;</c>, or at its end. A classic script there runs after the
    /// style sheets above it have loaded. Bytes, not text, so a page in another encoding isn't re-encoded (the script is ASCII).</summary>
    public static byte[] WithScheme(byte[] html)
    {
        var at = LastIndexOfAscii(html, "</body");
        if (at < 0) return [.. html, .. Tag];
        return [.. html.AsSpan(0, at), .. Tag, .. html.AsSpan(at)];
    }

    static int LastIndexOfAscii(byte[] hay, string needle)
    {
        for (var i = hay.Length - needle.Length; i >= 0; i--)
        {
            var j = 0;
            while (j < needle.Length && char.ToLowerInvariant((char)hay[i + j]) == needle[j]) j++;
            if (j == needle.Length) return i;
        }
        return -1;
    }
}
