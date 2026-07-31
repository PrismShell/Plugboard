using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Plugboard.Host;

// The .pbapp bundle format (v2) and its build step.
//
// Build: a multi-file HTML project (entry .html + local .css/.js/images) is INLINED into
// one HTML string (styles/scripts inlined, images -> data: URIs), lightly minified, gzipped,
// and written behind a magic header. The result is one small, opaque, portable file.
//
// Serve: Plugboard reads the file; if it carries the magic it decompresses to the HTML
// and serves that. A .pbapp WITHOUT the magic is treated as plain HTML (back-compat).
//
// Honest scope: gzip makes it small and unreadable in an editor at rest. It is NOT
// confidentiality - the browser is served plaintext HTML, so View Source shows the real
// code. No keys, nothing to rotate. Signing/encryption are deliberately not here.
public static class Pbapp
{
    // 7-byte ASCII magic prefixing the gzip payload. Chosen to not collide with '<' (HTML).
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("PBAPP1\n");

    // ── build ──

    // Folders/files never packed into an app archive (build output, VCS, tooling junk).
    private static readonly HashSet<string> SkipDirs =
        new(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", ".vs", ".vscode", "bin", "obj", "dist" };

    private static bool SkipFile(string name) =>
        name.StartsWith(".") || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(name).ToLowerInvariant() is ".pbapp" or ".pdb";

    // Package an app. Default: ZIP the project folder (a portable static site in one opaque
    // file - handles multiple pages, ES modules, fetch of local files, everything, because
    // Plugboard serves each file from the zip by path). flatten=true instead inlines the
    // entry into a single gzipped HTML (single-page only; smaller, but no sub-files).
    //
    // inputPath: a project directory (needs index.html at its root, or exactly one *.html),
    // or a single .html file. Returns the path written.
    public static string Build(string inputPath, string? outPath = null, bool flatten = false)
    {
        var full = Path.GetFullPath(inputPath).TrimEnd(Path.DirectorySeparatorChar);

        string projectDir, entryName;
        bool isDir = Directory.Exists(full);
        if (isDir)
        {
            projectDir = full;
            if (File.Exists(Path.Combine(projectDir, "index.html"))) entryName = "index.html";
            else
            {
                var htmls = Directory.GetFiles(projectDir, "*.htm*", SearchOption.TopDirectoryOnly);
                if (htmls.Length == 1) entryName = Path.GetFileName(htmls[0]);
                else throw new Exception($"A Plugboard app folder needs an index.html at its root (or exactly one *.html). Found {htmls.Length} in {projectDir}");
            }
        }
        else if (File.Exists(full)) { projectDir = Path.GetDirectoryName(full)!; entryName = Path.GetFileName(full); }
        else throw new Exception($"input not found: {full}");

        // Default output lands IN the project folder as <foldername>.pbapp - it's right
        // there next to index.html and easy to find. Safe because *.pbapp is excluded from
        // the pack, so a rebuild never zips the previous output into itself.
        outPath ??= isDir
            ? Path.Combine(projectDir, Path.GetFileName(projectDir) + ".pbapp")
            : Path.ChangeExtension(full, ".pbapp");

        if (flatten)
        {
            File.WriteAllBytes(outPath, Pack(Minify(BundleHtml(Path.Combine(projectDir, entryName)))));
            return outPath;
        }

        // Archive mode: zip the whole project (dir input) or just the file (file input,
        // stored as index.html so the entry always resolves regardless of its name).
        var files = isDir ? EnumerateProjectFiles(projectDir) : new[] { (full, "index.html") };
        if (File.Exists(outPath)) File.Delete(outPath);
        using (var zip = ZipFile.Open(outPath, ZipArchiveMode.Create))
            foreach (var (abs, rel) in files)
                zip.CreateEntryFromFile(abs, rel.Replace('\\', '/'), CompressionLevel.Optimal);
        return outPath;
    }

    // All project files worth shipping, as (absolutePath, relativePath), skipping junk dirs/files.
    private static IEnumerable<(string abs, string rel)> EnumerateProjectFiles(string root)
    {
        foreach (var abs in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, abs);
            if (rel.Split('/', '\\').Any(seg => SkipDirs.Contains(seg))) continue;
            if (SkipFile(Path.GetFileName(abs))) continue;
            yield return (abs, rel);
        }
    }

    // ── archive serve ──

    // A zip container (PK\x03\x04) = a packed project served file-by-file.
    public static bool IsArchive(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> b = stackalloc byte[4];
            return fs.Read(b) == 4 && b[0] == 0x50 && b[1] == 0x4B && b[2] == 0x03 && b[3] == 0x04;
        }
        catch { return false; }
    }

    // Read one file from the archive. rel null/empty -> the entry (index.html, else the sole
    // *.html). Returns bytes + the entry's lowercase extension for content typing.
    public static bool TryReadArchiveEntry(string path, string? rel, out byte[] bytes, out string ext)
    {
        bytes = Array.Empty<byte>(); ext = "";
        try
        {
            using var zip = ZipFile.OpenRead(path);
            ZipArchiveEntry? e;
            if (string.IsNullOrEmpty(rel))
                e = zip.GetEntry("index.html")
                    ?? zip.Entries.FirstOrDefault(x => x.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                                                    || x.FullName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase));
            else
                e = zip.GetEntry(rel.Replace('\\', '/').TrimStart('/'));
            if (e is null) return false;
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            bytes = ms.ToArray();
            ext = Path.GetExtension(e.FullName).ToLowerInvariant();
            return true;
        }
        catch { return false; }
    }

    // Inline local stylesheets, scripts, and images into a single self-contained HTML.
    // Remote (http, //, data:) and rooted references are left untouched.
    public static string BundleHtml(string entryFile)
    {
        var dir  = Path.GetDirectoryName(Path.GetFullPath(entryFile))!;
        var html = File.ReadAllText(entryFile);

        // <link rel="stylesheet" href="x.css"> -> <style> ...with url()s inlined... </style>
        html = Regex.Replace(html, @"<link\b[^>]*?>", m =>
        {
            var tag = m.Value;
            if (!Regex.IsMatch(tag, @"rel\s*=\s*[""']?stylesheet", RegexOptions.IgnoreCase)) return tag;
            var href = Attr(tag, "href");
            var css  = ReadTextAsset(dir, href);
            return css == null ? tag : "<style>" + InlineCssUrls(css, Path.GetDirectoryName(Path.Combine(dir, href!))!) + "</style>";
        }, RegexOptions.IgnoreCase);

        // <script src="x.js"></script> -> <script> ... </script>
        html = Regex.Replace(html, @"<script\b([^>]*?)\bsrc\s*=\s*[""']([^""']+)[""']([^>]*?)>\s*</script>", m =>
        {
            var src = m.Groups[2].Value;
            var js  = ReadTextAsset(dir, src);
            if (js == null) return m.Value;
            // preserve any non-src attributes (e.g. type="module")
            var attrs = (m.Groups[1].Value + m.Groups[3].Value).Trim();
            return "<script" + (attrs.Length > 0 ? " " + attrs : "") + ">" + js + "</script>";
        }, RegexOptions.IgnoreCase);

        // <img src="x.png"> -> data: URI
        html = Regex.Replace(html, @"(<img\b[^>]*?\bsrc\s*=\s*[""'])([^""']+)([""'][^>]*?>)", m =>
        {
            var data = DataUri(dir, m.Groups[2].Value);
            return data == null ? m.Value : m.Groups[1].Value + data + m.Groups[3].Value;
        }, RegexOptions.IgnoreCase);

        return html;
    }

    // Replace url(...) in CSS with data: URIs, resolved relative to the CSS file's dir.
    private static string InlineCssUrls(string css, string cssDir) =>
        Regex.Replace(css, @"url\(\s*['""]?([^'""\)]+)['""]?\s*\)", m =>
        {
            var data = DataUri(cssDir, m.Groups[1].Value);
            return data == null ? m.Value : $"url({data})";
        }, RegexOptions.IgnoreCase);

    private static bool IsRemote(string? p) =>
        string.IsNullOrWhiteSpace(p) || p.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
        || p.StartsWith("https:", StringComparison.OrdinalIgnoreCase) || p.StartsWith("//")
        || p.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || p.StartsWith("#") || Path.IsPathRooted(p);

    private static string? ReadTextAsset(string dir, string? rel)
    {
        if (IsRemote(rel)) return null;
        var p = Path.GetFullPath(Path.Combine(dir, rel!.Split('?', '#')[0]));
        return File.Exists(p) ? File.ReadAllText(p) : null;
    }

    private static string? DataUri(string dir, string rel)
    {
        if (IsRemote(rel)) return null;
        var p = Path.GetFullPath(Path.Combine(dir, rel.Split('?', '#')[0]));
        if (!File.Exists(p)) return null;
        var mime = Path.GetExtension(p).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif",
            ".svg" => "image/svg+xml", ".webp" => "image/webp", ".ico" => "image/x-icon",
            ".woff2" => "font/woff2", ".woff" => "font/woff", _ => "application/octet-stream"
        };
        return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(p))}";
    }

    private static string? Attr(string tag, string name)
    {
        var m = Regex.Match(tag, name + @"\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    // Conservative minify: never touches <script>/<pre>/<textarea> content (unsafe to
    // rewrite JS by regex). Collapses CSS + markup whitespace and strips comments. gzip
    // does the real size work, so this stays safe rather than aggressive.
    public static string Minify(string html)
    {
        var stash = new List<string>();
        string Stash(string s) => Regex.Replace(s, @"(<(script|pre|textarea)\b[^>]*>)(.*?)(</\2>)", m =>
        {
            stash.Add(m.Groups[3].Value);
            return m.Groups[1].Value + " " + (stash.Count - 1) + " " + m.Groups[4].Value;
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        html = Stash(html);   // protect JS / preformatted text verbatim

        // minify <style> contents (CSS whitespace is safe to collapse)
        html = Regex.Replace(html, @"(<style\b[^>]*>)(.*?)(</style>)", m =>
        {
            var css = Regex.Replace(m.Groups[2].Value, @"/\*.*?\*/", "", RegexOptions.Singleline);
            css = Regex.Replace(css, @"\s+", " ");
            css = Regex.Replace(css, @"\s*([{}:;,>])\s*", "$1");
            return m.Groups[1].Value + css.Trim() + m.Groups[3].Value;
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);

        html = Regex.Replace(html, @"<!--(?!\[if).*?-->", "", RegexOptions.Singleline);  // HTML comments
        html = Regex.Replace(html, @">\s+<", "><");                                       // inter-tag whitespace
        html = Regex.Replace(html, @"[ \t]{2,}", " ");                                    // runs of spaces

        html = Regex.Replace(html, " (\\d+) ", m => stash[int.Parse(m.Groups[1].Value)]);  // restore
        return html.Trim();
    }

    // ── container ──

    public static byte[] Pack(string html)
    {
        using var ms = new MemoryStream();
        ms.Write(Magic, 0, Magic.Length);
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(html);
            gz.Write(bytes, 0, bytes.Length);
        }
        return ms.ToArray();
    }

    // If the file carries the magic, decompress to its HTML and return true. Otherwise
    // false (caller serves the file as plain content).
    public static bool TryDecodeFile(string path, out string html)
    {
        html = "";
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); } catch { return false; }
        if (bytes.Length < Magic.Length) return false;
        for (int i = 0; i < Magic.Length; i++) if (bytes[i] != Magic[i]) return false;
        try
        {
            using var ms = new MemoryStream(bytes, Magic.Length, bytes.Length - Magic.Length);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            html = Encoding.UTF8.GetString(outMs.ToArray());
            return true;
        }
        catch { return false; }
    }
}
