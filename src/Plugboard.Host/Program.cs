using Plugboard.Contracts;
using Plugboard.Host;

// Build helper: `Plugboard.Host.exe --build <projectDir|html> [-o out.pbapp] [--flatten]`
// packs an app into one .pbapp and exits (no server). Default = zip the project folder (a
// portable static site: multiple pages, modules, local fetch all work). --flatten inlines
// a single-page app into one gzipped HTML instead. The written path (or error) goes to
// stdout so a caller like Tabby2 can capture it.
// Remove the .pbapp association and exit (setup teardown).
if (args.Contains("--unregister")) { FileAssociation.Unregister(); return; }

{
    var bi = Array.IndexOf(args, "--build");
    if (bi >= 0 && bi + 1 < args.Length)
    {
        try
        {
            string? outp = null;
            var oi2 = Array.IndexOf(args, "-o");
            if (oi2 >= 0 && oi2 + 1 < args.Length) outp = args[oi2 + 1];
            var written = Pbapp.Build(args[bi + 1], outp, flatten: args.Contains("--flatten"));
            Console.Out.WriteLine("built " + written);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("build failed: " + e.Message);
            Environment.Exit(1);
        }
        return;
    }
}

// "Open with Gateway" shell helper: `Plugboard.Host.exe --open <file>` registers the
// file with the running gateway (served at localhost, so it's same-origin and its
// capability calls work with no secret). If the gateway isn't running, starts it first.
{
    var oi = Array.IndexOf(args, "--open");
    if (oi >= 0 && oi + 1 < args.Length)
    {
        var full = System.IO.Path.GetFullPath(args[oi + 1]);
        var exePath = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Plugboard.Host.exe");
        
        // Try to contact the running gateway
        bool hostRunning = false;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var health = http.GetAsync("http://localhost:9195/health").GetAwaiter().GetResult();
            hostRunning = health.IsSuccessStatusCode;
        }
        catch { }
        
        // If not running, start it and wait for it to be ready
        if (!hostRunning)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(exePath)
            });
            // Wait up to 10 seconds for the host to start
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            for (int i = 0; i < 20; i++)
            {
                System.Threading.Thread.Sleep(500);
                try
                {
                    var health = http.GetAsync("http://localhost:9195/health").GetAwaiter().GetResult();
                    if (health.IsSuccessStatusCode) { hostRunning = true; break; }
                }
                catch { }
            }
        }
        
        // Register the file and get the /app/{id}/ URL
        string url = "http://localhost:9195/view?path=" + Uri.EscapeDataString(full);
        if (hostRunning)
        {
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                var resp = http.PostAsync("http://localhost:9195/_open?path=" + Uri.EscapeDataString(full), null)
                               .GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode)
                {
                    var json = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    var rel = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("url").GetString();
                    url = "http://localhost:9195" + rel;
                }
            }
            catch { }
        }
        
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch
        {
            // UseShellExecute can fail if no default browser is registered. Try common browsers directly.
            string[] browsers = new[]
            {
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"),
                Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Google\Chrome\Application\chrome.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Mozilla Firefox\firefox.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Mozilla Firefox\firefox.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"),
                Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"),
            };
            foreach (var browser in browsers)
            {
                if (System.IO.File.Exists(browser))
                {
                    try { System.Diagnostics.Process.Start(browser, url); break; }
                    catch { }
                }
            }
        }
        return;
    }
}

// The Plugboard host: a generic, "incomplete" shell. It has no capabilities of
// its own. At startup it scans the plugins directory, verifies each DLL's
// signature against the trusted keys, loads the verified ones, and exposes the
// routes they register under /con/ (connectors) and /svc/ (services). Add a capability = drop a signed DLL in the
// folder and restart. The host binary itself never changes.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
// CORS locked to this gateway's own origin. Apps are SERVED by the gateway (opened via
// the "Open with Gateway" shell verb -> localhost origin), so the legitimate caller is
// same-origin; a cross-origin page can't read responses. This pairs with the server-side
// Sec-Fetch-Site check on capabilities (below) - browser CORS plus an unforgeable
// server check, no secret anywhere.
builder.Services.AddCors(o =>
    o.AddDefaultPolicy(p => p
        .WithOrigins("http://localhost:9195", "http://127.0.0.1:9195")
        .AllowAnyMethod().AllowAnyHeader()));
var app = builder.Build();
app.UseCors();

var cfg         = app.Configuration;

// Setup: self-register the .pbapp file association (HKCU, no admin) so double-clicking a
// .pbapp opens it through Plugboard. Idempotent and self-healing; points at the running
// exe. Turn off with "RegisterFileAssociation": false, or remove via --unregister.
if (cfg.GetValue("RegisterFileAssociation", true))
    FileAssociation.EnsureRegistered(
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Plugboard.Host.exe"),
        app.Logger);

// Where plugins load from: a LIST of directories, scanned in list order. By
// default the list is just the local ./plugins folder next to the exe — the
// install-and-done "lite" model (drop a plugin's DLL in, done, like an Excel
// add-in). An admin can add one or more shares for centrally-managed delivery.
// Accepts "PluginsDirs" (array) or the legacy singular "PluginsDir" (string).
var pluginsCfg = cfg.GetSection("PluginsDirs").Get<string[]>()
               ?? (cfg["PluginsDir"] is { } single ? new[] { single } : new[] { "plugins" });
var pluginDirs = pluginsCfg
    .Where(d => !string.IsNullOrWhiteSpace(d))
    .Select(d => Path.GetFullPath(Path.IsPathRooted(d) ? d : Path.Combine(AppContext.BaseDirectory, d)))
    .ToArray();

// Signature enforcement. OFF by default: installing a plugin (dropping its DLL
// in a plugins dir) IS the trust decision, like an Excel add-in — the "lite"
// posture. Set RequireSignature=true (+ TrustedKeys) for the managed model,
// where the host refuses any plugin lacking a valid detached .sig from a
// trusted key. Same binary either way — this is purely a config choice.
var requireSig  = cfg.GetValue("RequireSignature", false);
var trustedKeys = cfg.GetSection("TrustedKeys").Get<string[]>() ?? Array.Empty<string>();

var endpoints = new PluginLoader(pluginDirs, trustedKeys, requireSig, app.Logger).LoadAll();

// Route -> (tier, bare name). A route the author wrote as "svc/..." is a service;
// a "con/..." prefix is honored too; anything else is a connector. Connectors mount
// under /con/, services under /svc/. The bare name (tier prefix stripped) is the
// mesh's addressing key, so Call("bloomberg/bdp") and Call("prices") resolve
// regardless of the URL tier. The tier is a routing/organizational concern only;
// it is not encoded in how one unit addresses another.
static (string tier, string bare) Split(string route)
{
    var r = route.TrimStart('/');
    if (r.StartsWith("svc/", StringComparison.OrdinalIgnoreCase)) return ("svc", r[4..]);
    if (r.StartsWith("con/", StringComparison.OrdinalIgnoreCase)) return ("con", r[4..]);
    return ("con", r);
}

// Project a route's declared ParamInfo[] into a JSON Schema object (what MCP /
// OpenAI tool definitions carry as inputSchema). No params -> an open object so an
// agent knows the inputs are undeclared rather than "none".
static object ToInputSchema(ParamInfo[]? ps)
{
    if (ps is null || ps.Length == 0)
        return new { type = "object", properties = new Dictionary<string, object>(), additionalProperties = true };
    var props = new Dictionary<string, object>();
    var required = new List<string>();
    foreach (var p in ps)
    {
        var prop = new Dictionary<string, object> { ["type"] = p.Type };
        if (!string.IsNullOrEmpty(p.Description)) prop["description"] = p.Description!;
        if (p.Type == "array")                    prop["items"] = new { type = p.Items ?? "string" };
        if (p.Enum is { Length: > 0 })            prop["enum"] = p.Enum;
        if (p.Default is not null)                prop["default"] = p.Default;
        props[p.Name] = prop;
        if (p.Required) required.Add(p.Name);
    }
    return new { type = "object", properties = props, required = required.ToArray() };
}

// Content negotiation: a browser (Accept: text/html) gets the built-in web UI page,
// which routes on its own path and renders that endpoint; everything else
// (fetch/curl/agents) gets JSON. Same URL, two representations - so every built-in
// endpoint has a human interface without needing a second route.
static IResult Negotiate(HttpContext ctx, object json)
    => ctx.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase)
        ? Results.Text(ConsolePage.Html, "text/html")
        : Results.Json(json);

// The composition mesh. Index units by their BARE name so any handler can call
// another in-process via PluginRequest.Call — this is what turns independent mounts
// into a mesh (a service composing connectors and other services). First
// registration wins (matches the loader's dedup). Depth guards cycles (a -> b -> a).
var byRoute = endpoints
    .GroupBy(e => Split(e.Route).bare, StringComparer.OrdinalIgnoreCase)
    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

Func<string, object?, int, Task<object?>> dispatch = null!;
dispatch = async (name, payload, depth) =>
{
    if (depth > 16) throw new Exception($"call depth exceeded at '{name}' (cycle?)");
    var (_, key) = Split(name);   // a con/ or svc/ prefix on the target is optional
    if (!byRoute.TryGetValue(key, out var target))
        throw new Exception($"no unit registered for '{name}'");
    var inner = new PluginRequest
    {
        Method = target.Method,
        Route  = target.Route,
        Body   = payload switch { null => "", string s => s, _ => System.Text.Json.JsonSerializer.Serialize(payload) },
        Query  = new Dictionary<string, string>(),
        Depth  = depth,
        Call   = (n, p) => dispatch!(n, p, depth + 1)
    };
    return await target.Handler(inner);
};

foreach (var ep in endpoints)
{
    var (tier, bare) = Split(ep.Route);
    app.MapMethods($"/{tier}/{bare}", new[] { ep.Method }, async (HttpContext ctx) =>
    {
        // Authorize the capability call. Two callers are allowed:
        //  1. An app SERVED by this gateway (opened via "Serve via Plugboard") runs at
        //     our origin, so its calls carry the browser-set, UNFORGEABLE
        //     Sec-Fetch-Site: same-origin.
        //  2. A local NON-browser process (Tabby2's Node host, a Python/VBA/curl tool)
        //     sends NO Sec-Fetch-* headers at all - a browser can never omit them, so an
        //     absent value is proof the caller is not a web page.
        // A drive-by web page is cross-site (or same-site) and cannot forge same-origin
        // nor omit the header, so it's refused. No secret anywhere. (A local non-browser
        // process could also forge the header, but it already owns the box, and remote
        // callers can't reach loopback.)
        var sfs = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        if (!(sfs.Length == 0 || sfs.Equals("same-origin", StringComparison.OrdinalIgnoreCase)))
            return Results.Json(new { ok = false, error = "unauthorized" }, statusCode: 401);

        var body = "";
        if (ctx.Request.ContentLength > 0)
        {
            using var sr = new StreamReader(ctx.Request.Body);
            body = await sr.ReadToEndAsync();
        }
        var req = new PluginRequest
        {
            Method = ep.Method,
            Route  = ep.Route,
            Body   = body,
            Query  = ctx.Request.Query.ToDictionary(k => k.Key, v => v.Value.ToString()),
            Call   = (n, p) => dispatch(n, p, 1)   // externally-triggered calls can fan out too
        };
        try { return Results.Json(new { ok = true, data = await ep.Handler(req) }); }
        catch (Exception e) { return Results.Json(new { ok = false, error = e.Message }); }
    });
}

// Discovery / home. Browser -> web UI home; machine -> JSON list of capabilities.
app.MapGet("/", (HttpContext ctx) => Negotiate(ctx, new
{
    ok = true,
    data = new
    {
        service = "plugboard-host",
        plugins = endpoints.Select(e => { var (t, b) = Split(e.Route); return new { e.Plugin, e.Method, route = $"/{t}/{b}" }; })
    }
}));

// Shared filter for /catalog and /tools. Reads ?plugin= (alias ?group=), ?method=, and
// ?q= (keyword over route/summary/description/plugin) off the query string and narrows the
// endpoint set. No params -> everything (unchanged behaviour).
static List<LoadedEndpoint> FilterEndpoints(HttpContext ctx, List<LoadedEndpoint> all)
{
    var q    = ctx.Request.Query["q"].ToString();
    var unit = ctx.Request.Query["plugin"].ToString();
    if (string.IsNullOrEmpty(unit)) unit = ctx.Request.Query["group"].ToString();
    var meth = ctx.Request.Query["method"].ToString();

    IEnumerable<LoadedEndpoint> sel = all;
    if (!string.IsNullOrWhiteSpace(unit))
        sel = sel.Where(e => e.Plugin.Equals(unit, StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(meth))
        sel = sel.Where(e => e.Method.Equals(meth, StringComparison.OrdinalIgnoreCase));
    if (!string.IsNullOrWhiteSpace(q))
        sel = sel.Where(e => $"{e.Route} {e.Info?.Summary} {e.Info?.Description} {e.Plugin}"
            .Contains(q, StringComparison.OrdinalIgnoreCase));
    return sel.ToList();
}

// Self-describing catalog: the live API surface with per-route metadata. Clients
// (tabby2, IntelliSense, Rogo guide, templates) read this from the gateway instead
// of bundling a static snapshot that drifts. `routes` carries the optional
// summary/description/sample each plugin supplied via RouteInfo.
app.MapGet("/catalog", (HttpContext ctx) =>
{
    // Optional filtering: ?plugin=<unit> (alias ?group=), ?method=GET|POST, ?q=<keyword>
    // (substring over route/summary/description/plugin). Lets a client pull just the slice
    // it needs instead of the whole surface.
    var eps = FilterEndpoints(ctx, endpoints);
    var q = ctx.Request.Query["q"].ToString();
    var unit = string.IsNullOrEmpty(ctx.Request.Query["plugin"].ToString()) ? ctx.Request.Query["group"].ToString() : ctx.Request.Query["plugin"].ToString();
    var meth = ctx.Request.Query["method"].ToString();
    return Negotiate(ctx, new
    {
        ok = true,
        data = new
        {
            service = "plugboard-host",
            count   = eps.Count,
            filter  = new { q, plugin = unit, method = meth },
            // Per-plugin view with the optional manifest — id/version/displayName and,
            // crucially, `requires` (external prerequisites) so a client can show
            // "installed, but needs a Bloomberg Terminal" instead of a silent failure.
            plugins = eps.GroupBy(e => e.Plugin).Select(g => new
            {
                name        = g.Key,
                routeCount  = g.Count(),
                id          = g.First().Manifest?.Id,
                version     = g.First().Manifest?.Version,
                displayName = g.First().Manifest?.DisplayName,
                description = g.First().Manifest?.Description,
                requires    = g.First().Manifest?.Requires
            }),
            routes  = eps.Select(e =>
            {
                var (t, b) = Split(e.Route);
                return new
                {
                    plugin      = e.Plugin,
                    method      = e.Method,
                    route       = $"/{t}/{b}",
                    summary     = e.Info?.Summary,
                    description = e.Info?.Description,
                    sample      = e.Info?.Sample,
                    parameters  = e.Info?.Params
                };
            })
        }
    });
});

// AI-facing tool definitions: every capability projected into an MCP/OpenAI-style
// tool (name + description + JSON-Schema inputSchema) plus its HTTP binding (method
// + path). An agent reads this and can call any unit with correct arguments - no
// bespoke glue. `name` is sanitized to the function-name charset ([A-Za-z0-9_-]).
app.MapGet("/tools", (HttpContext ctx) =>
{
    // Same filtering as /catalog: ?plugin=/?group=, ?method=, ?q=. Handy for handing an
    // agent only the tools for one unit (e.g. ?plugin=tdm) instead of all ~160.
    var eps = FilterEndpoints(ctx, endpoints);
    return Negotiate(ctx, new
    {
        ok = true,
        data = new
        {
            service = "plugboard-host",
            count   = eps.Count,
            tools   = eps.Select(e =>
            {
                var (t, b) = Split(e.Route);
                var path = $"/{t}/{b}";
                var name = System.Text.RegularExpressions.Regex.Replace($"{t}_{b}", "[^A-Za-z0-9_-]", "_");
                return new
                {
                    name,
                    description = e.Info?.Description ?? e.Info?.Summary ?? $"{e.Method} {path}",
                    method      = e.Method,
                    path,
                    sample      = e.Info?.Sample,
                    inputSchema = ToInputSchema(e.Info?.Params)
                };
            })
        }
    });
});

// Built-in server health: liveness + basic operational stats. Cheap and always
// present (independent of any plugin) so monitors/uptime checks have a stable probe.
app.MapGet("/health", (HttpContext ctx) =>
{
    var proc = System.Diagnostics.Process.GetCurrentProcess();
    var uptime = (DateTime.UtcNow - proc.StartTime.ToUniversalTime()).TotalSeconds;
    return Negotiate(ctx, new
    {
        ok = true,
        data = new
        {
            status        = "ok",
            uptimeSeconds = (long)uptime,
            plugins       = endpoints.Select(e => e.Plugin).Distinct().Count(),
            routes        = endpoints.Count,
            signatures    = requireSig ? "required" : "off",
            startedUtc    = proc.StartTime.ToUniversalTime().ToString("o")
        }
    });
});

// Built-in console: a human-facing dashboard (renders the catalog + health). This
// is the page the tray opens. Machines use /catalog, /info, /health (JSON).
app.MapGet("/console", () => Results.Text(ConsolePage.Html, "text/html"));

// Serve an applet FILE through the gateway so it runs at THIS origin (same-origin) -
// which is what lets its capability calls work with no secret. Reached via the "Serve
// via Plugboard" shell verb, a same-origin launcher link, or a Tabby2 webview iframe.
//
// Two shapes, two security postures:
//  - /view?path=<file>          direct, back-compat. The CALLER controls the path, so a
//                               cross-site navigation here could get an attacker's own
//                               file served at our origin - REFUSED (cross-site -> 403).
//  - POST /_open -> /app/{id}/  clean: register the file, serve it (and its folder, so
//                               relative css/js/images resolve) under a short id. The
//                               caller can't choose the content (ids map to pre-
//                               registered files), so /app allows cross-site loads but
//                               restricts FRAMING via frame-ancestors (see ServeApp).
// The registry survives a restart: it's mirrored to served.json under %LOCALAPPDATA%, so
// an already-open (or bookmarked) /app/{id}/ still resolves after Plugboard restarts.
// The id is a hash of the file's full path, so re-opening the SAME file is idempotent
// (same URL, no duplicate entry). Moving the file changes its path -> a new id/URL when
// re-served; the stale entry self-heals (dropped the next time its dead URL is hit).
var servedApps = new System.Collections.Concurrent.ConcurrentDictionary<string, (string dir, string entry)>();
var servedPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "plugboard", "served.json");
try
{
    if (File.Exists(servedPath))
    {
        var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(servedPath));
        if (saved != null)
            foreach (var kv in saved)
                if (kv.Value is { Length: 2 }) servedApps[kv.Key] = (kv.Value[0], kv.Value[1]);
    }
}
catch { /* corrupt/absent registry is not fatal - start empty */ }
void SaveServed()
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(servedPath)!);
        File.WriteAllText(servedPath, System.Text.Json.JsonSerializer.Serialize(
            servedApps.ToDictionary(kv => kv.Key, kv => new[] { kv.Value.dir, kv.Value.entry })));
    }
    catch { /* best-effort; losing the mirror only costs a re-open after restart */ }
}

static bool IsCrossSite(HttpContext c) =>
    c.Request.Headers["Sec-Fetch-Site"].ToString().Equals("cross-site", StringComparison.OrdinalIgnoreCase);
static string ShortId(string full) =>
    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Text.Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8].ToLowerInvariant();
static string CTypeFor(string ext) => ext switch
{
    ".html" or ".htm" or ".pbapp" => "text/html",   // .pbapp = a Plugboard app (HTML)
    ".js" or ".mjs" => "text/javascript",
    ".css" => "text/css",
    ".json" => "application/json",
    ".svg" => "image/svg+xml",
    ".png" => "image/png",
    ".jpg" or ".jpeg" => "image/jpeg",
    ".gif" => "image/gif",
    ".webp" => "image/webp",
    ".ico" => "image/x-icon",
    ".woff2" => "font/woff2",
    ".woff" => "font/woff",
    _ => "application/octet-stream",
};

app.MapGet("/view", (HttpContext ctx, string? path) =>
{
    if (IsCrossSite(ctx)) return Results.StatusCode(403);
    if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest();
    var full = Path.GetFullPath(path);
    if (Path.GetExtension(full).ToLowerInvariant() is not (".html" or ".htm" or ".pbapp")) return Results.StatusCode(415);
    if (!File.Exists(full)) return Results.NotFound();
    // Packed (zip) .pbapp -> serve its entry; flattened (gzip) -> decompress; else plain HTML.
    // (/view only serves the entry; sub-files of a packed app are reached via /app/{id}/.)
    if (Pbapp.IsArchive(full))
        return Pbapp.TryReadArchiveEntry(full, null, out var vb, out var vext)
            ? Results.Bytes(vb, CTypeFor(vext.Length > 0 ? vext : ".html")) : Results.NotFound();
    if (Pbapp.TryDecodeFile(full, out var bundled)) return Results.Text(bundled, "text/html");
    return Results.Text(File.ReadAllText(full), "text/html");
});

// Register a file for clean serving; returns { id, url = /app/{id}/ }. Called by the
// --open shell helper. Not reachable cross-site (registering is harmless anyway - the
// /app serve still refuses cross-site - but keep the surface tight).
app.MapPost("/_open", (HttpContext ctx, string? path) =>
{
    if (IsCrossSite(ctx)) return Results.StatusCode(403);
    if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest();
    var full = Path.GetFullPath(path);
    if (Path.GetExtension(full).ToLowerInvariant() is not (".html" or ".htm" or ".pbapp")) return Results.StatusCode(415);
    if (!File.Exists(full)) return Results.NotFound();
    var id = ShortId(full);
    servedApps[id] = (Path.GetDirectoryName(full)!, Path.GetFileName(full));
    SaveServed();
    return Results.Json(new { ok = true, id, url = $"/app/{id}/" });
});

IResult ServeApp(HttpContext ctx, string id, string? rest)
{
    // Unlike /view, /app does NOT block cross-site: the content is a pre-registered file
    // the caller cannot choose (registration via /_open is locked), so serving it cross-
    // site leaks nothing (CORS still hides fetch responses from other origins). Framing is
    // controlled precisely by frame-ancestors below: only same-origin pages and Tabby2
    // webviews (vscode-webview: scheme) may embed a served app; a website (evil.com) is
    // refused by the browser. This is what lets Tabby2 preview an app in an <iframe> while
    // a drive-by page still cannot frame-and-drive it.
    ctx.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'self' vscode-webview:";
    if (!servedApps.TryGetValue(id, out var entry)) return Results.NotFound();
    var dir = Path.GetFullPath(entry.dir);
    var appFile = Path.GetFullPath(Path.Combine(dir, entry.entry));   // the registered .pbapp/.html
    if (!File.Exists(appFile))
    {
        // self-heal: the app file itself is gone (moved/deleted) -> forget this registration.
        if (servedApps.TryRemove(id, out _)) SaveServed();
        return Results.NotFound();
    }

    // A packed (zip) .pbapp: serve the requested path from INSIDE it. This is what makes
    // multi-page links, ES modules, and fetch() of local files work - every file the page
    // asks for (/app/{id}/page2.html, /js/x.js, /data.json) comes out of the archive.
    if (Pbapp.IsArchive(appFile))
        return Pbapp.TryReadArchiveEntry(appFile, rest, out var ab, out var aext)
            ? Results.Bytes(ab, CTypeFor(aext.Length > 0 ? aext : ".html"))
            : Results.NotFound();

    // A flattened (PBAPP1 gzip) single-HTML bundle: only the entry.
    if (string.IsNullOrEmpty(rest) && Pbapp.TryDecodeFile(appFile, out var bundled))
        return Results.Text(bundled, "text/html");

    // Plain HTML, or a legacy on-disk multi-file app: serve the file from the folder.
    var full = Path.GetFullPath(Path.Combine(dir, string.IsNullOrEmpty(rest) ? entry.entry : rest));
    if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        return Results.StatusCode(403);
    if (!File.Exists(full)) return Results.NotFound();
    return Results.File(full, CTypeFor(Path.GetExtension(full).ToLowerInvariant()));
}
app.MapGet("/app/{id}", (HttpContext ctx, string id) => ServeApp(ctx, id, null));
app.MapGet("/app/{id}/{**rest}", (HttpContext ctx, string id, string? rest) => ServeApp(ctx, id, rest));

// Host info / self-location: where this host lives and how to reach/relaunch it.
// Reports whether auth is required (never the token itself).
app.MapGet("/info", (HttpContext ctx) =>
{
    var proc = System.Diagnostics.Process.GetCurrentProcess();
    // Network-facing identity only - no filesystem paths or username. Path info for
    // relaunch lives in the user-only location.json on disk instead.
    return Negotiate(ctx, new
    {
        ok = true,
        data = new
        {
            service         = "plugboard-host",
            url             = "http://localhost:9195",
            version         = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            pid             = Environment.ProcessId,
            trustedKeyCount = trustedKeys.Length,
            authRequired    = true,                    // capabilities require same-origin (served apps)
            startedUtc      = proc.StartTime.ToUniversalTime().ToString("o")
        }
    });
});

// Location file at a FIXED per-user path so a client (Tabby2) can find the exe to
// relaunch the host even when it's down. User-only (%LOCALAPPDATA%), so the paths
// here are not exposed over the network the way /info would be.
try
{
    var locDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "plugboard");
    Directory.CreateDirectory(locDir);
    var loc = new
    {
        service    = "plugboard-host",
        baseUrl    = "http://localhost:9195",
        catalogUrl = "http://localhost:9195/catalog",
        infoUrl    = "http://localhost:9195/info",
        exePath    = Environment.ProcessPath,
        baseDir    = AppContext.BaseDirectory,
        pluginDirs,
        requireSignature = requireSig,
        pid        = Environment.ProcessId,
        version    = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
        startedUtc = DateTime.UtcNow.ToString("o")
    };
    File.WriteAllText(Path.Combine(locDir, "location.json"),
        System.Text.Json.JsonSerializer.Serialize(loc, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    app.Logger.LogInformation($"[plugboard] wrote location file: {Path.Combine(locDir, "location.json")}");
}
catch (Exception e) { app.Logger.LogWarning($"[plugboard] could not write location.json: {e.Message}"); }

app.Logger.LogInformation($"[plugboard] host on http://localhost:9195 | plugins: {string.Join("; ", pluginDirs)} | signatures: {(requireSig ? "required" : "off (lite)")} | routes: {endpoints.Count}");

// System-tray icon: visual "it's up" signal + start/stop/restart controls. The
// plugin count is distinct because a plugin can register several routes.
var pluginCount = endpoints.Select(e => e.Plugin).Distinct().Count();
try
{
    Plugboard.Host.TrayIcon.Start(
        baseUrl: "http://localhost:9195",
        start:   () => System.Diagnostics.Process.Start(Environment.ProcessPath!),
        restart: () => { System.Diagnostics.Process.Start(Environment.ProcessPath!); Environment.Exit(0); },
        stop:    () => { Plugboard.Host.TrayIcon.SetStopped(); app.StopAsync(); });

    Plugboard.Host.TrayIcon.SetRunning($"localhost:9195 · {pluginCount} plugin{(pluginCount == 1 ? "" : "s")}");
    Plugboard.Host.TrayIcon.Notify(
        "Plugboard is running",
        $"{pluginCount} plugin{(pluginCount == 1 ? "" : "s")}, {endpoints.Count} route{(endpoints.Count == 1 ? "" : "s")} on localhost:9195"
            + (requireSig ? " · signed" : " · lite"));
}
catch (Exception e) { app.Logger.LogWarning($"[plugboard] tray icon unavailable: {e.Message}"); }

app.Run();
