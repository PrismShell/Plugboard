using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using Plugboard.Contracts;

namespace Plugboard.Host;

public sealed record LoadedEndpoint(string Method, string Route,
    Func<PluginRequest, Task<object?>> Handler, string Plugin, RouteInfo? Info = null,
    PluginManifest? Manifest = null);

// Optional metadata read from a plugin.json sitting next to the entry DLL. It is
// pure metadata for the catalog/UI: behaviour still comes from the code (IPlugin).
// "Requires" declares external prerequisites the plugin needs to actually work
// (e.g. "Bloomberg Terminal") so a client can show "installed, but needs X".
public sealed record PluginManifest(
    string? Id = null, string? Version = null, string? DisplayName = null,
    string? Description = null, string[]? Requires = null);

// Scans a directory for plugin DLLs, verifies each against the trusted public
// keys, loads the verified ones, and collects the routes they register. A
// plugin that fails verification or throws while loading is logged and SKIPPED;
// it never takes the host down. Loading happens once, at startup (no hot-swap).
public sealed class PluginLoader
{
    private readonly string[] _dirs;
    private readonly bool _requireSig;
    private readonly List<RSA> _trusted = new();
    private readonly ILogger _log;

    public PluginLoader(IEnumerable<string> dirs, IEnumerable<string> trustedPublicKeysB64, bool requireSignature, ILogger log)
    {
        _dirs = dirs.ToArray(); _requireSig = requireSignature; _log = log;
        foreach (var b64 in trustedPublicKeysB64)
        {
            try { var r = RSA.Create(); r.ImportSubjectPublicKeyInfo(Convert.FromBase64String(b64.Trim()), out _); _trusted.Add(r); }
            catch (Exception e) { _log.LogWarning($"[plugboard] ignoring bad trusted key: {e.Message}"); }
        }
    }

    public List<LoadedEndpoint> LoadAll()
    {
        var routes = new List<LoadedEndpoint>();

        // Managed posture only: signatures required but no keys means nothing could
        // ever verify — refuse everything rather than silently load nothing useful.
        // In lite mode (RequireSignature=false) an empty key list is expected.
        if (_requireSig && _trusted.Count == 0)
        {
            _log.LogWarning("[plugboard] RequireSignature is on but no trusted keys configured; refusing every plugin");
            return routes;
        }

        // Candidate entry DLLs across every configured plugins dir (scanned in list
        // order): any DLL directly in a dir (simple, dependency-free plugins like
        // Ping), plus each subfolder's entry DLL named after the folder (a plugin
        // that ships with its own dependencies, e.g.
        // .../Plugboard.Plugins.Pdf/Plugboard.Plugins.Pdf.dll, whose itext/BLPAPI
        // deps sit alongside it and resolve via the load context).
        var candidates = new List<string>();
        foreach (var dir in _dirs)
        {
            if (!Directory.Exists(dir)) { _log.LogWarning($"[plugboard] plugins dir not found (skipped): {dir}"); continue; }
            candidates.AddRange(Directory.GetFiles(dir, "*.dll"));
            foreach (var sub in Directory.GetDirectories(dir))
            {
                var entry = Path.Combine(sub, Path.GetFileName(sub) + ".dll");
                if (File.Exists(entry)) candidates.Add(entry);
            }
        }

        // A plugin present in more than one dir (e.g. a local override of a shared
        // one) loads once: first dir in the list wins. Prevents duplicate routes.
        candidates = candidates
            .GroupBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        foreach (var dll in candidates)
        {
            var name = Path.GetFileName(dll);
            try
            {
                if (_requireSig && !VerifySignature(dll)) { _log.LogWarning($"[plugboard] REFUSED (bad/missing signature): {name}"); continue; }

                // Optional plugin.json sitting next to the entry DLL (pure metadata).
                var manifest = ReadManifest(Path.Combine(Path.GetDirectoryName(dll)!, "plugin.json"), name);

                var asm = new PluginLoadContext(dll).LoadFromAssemblyPath(dll);
                var found = 0;
                foreach (var t in asm.GetTypes().Where(t => typeof(IPlugin).IsAssignableFrom(t) && !t.IsAbstract))
                {
                    var plugin = (IPlugin)Activator.CreateInstance(t)!;
                    var reg = new Registry(plugin.Name);
                    plugin.Register(reg);
                    routes.AddRange(reg.Endpoints.Select(e => e with { Manifest = manifest }));
                    found++;
                    _log.LogInformation($"[plugboard] loaded '{plugin.Name}' ({reg.Endpoints.Count} routes) from {name}");
                }
                if (found == 0) _log.LogWarning($"[plugboard] {name} verified but exposes no IPlugin");
            }
            catch (Exception e) { _log.LogError($"[plugboard] FAILED to load {name}: {e.Message}"); } // catch & skip
        }
        return routes;
    }

    private PluginManifest? ReadManifest(string path, string pluginName)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception e) { _log.LogWarning($"[plugboard] bad plugin.json for {pluginName}: {e.Message}"); return null; }
    }

    // Detached signature: <dll>.sig holds base64(RSA-SHA256 over the DLL bytes).
    // The runtime does NOT do this for us, and strong-naming is not a trust
    // boundary, so we verify explicitly against our own keys.
    private bool VerifySignature(string dll)
    {
        var sigPath = dll + ".sig";
        if (!File.Exists(sigPath)) return false;
        byte[] data, sig;
        try { data = File.ReadAllBytes(dll); sig = Convert.FromBase64String(File.ReadAllText(sigPath).Trim()); }
        catch { return false; }
        foreach (var key in _trusted)
            if (key.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return true;
        return false;
    }

    private sealed class Registry(string plugin) : IEndpointRegistry
    {
        public List<LoadedEndpoint> Endpoints { get; } = new();
        public void Map(string method, string route, Func<PluginRequest, Task<object?>> handler, RouteInfo? info = null)
            => Endpoints.Add(new LoadedEndpoint(method.ToUpperInvariant(), route.Trim('/'), handler, plugin, info));
    }
}

// Per-plugin load context so a plugin can carry its own dependencies. Contracts
// is shared from the host (return null) so the IPlugin type identity matches.
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    public PluginLoadContext(string pluginPath) => _resolver = new AssemblyDependencyResolver(pluginPath);
    protected override Assembly? Load(AssemblyName name)
    {
        // Share the host's copy (return null -> default context) for the contract AND the
        // BLPAPI stack. BLPAPI's native lib is a process singleton, so every plugin MUST
        // bind to the one managed wrapper + one session in Plugboard.Blpapi; a private
        // per-plugin copy would fail with "Session Not Started".
        if (name.Name is "Plugboard.Contracts" or "Plugboard.Blpapi" or "Bloomberglp.Blpapi")
            return null;
        var path = _resolver.ResolveAssemblyToPath(name);
        return path != null ? LoadFromAssemblyPath(path) : null;
    }

    // Native dependencies (e.g. SQLite's e_sqlite3) live in the plugin's own
    // runtimes/<rid>/native folder, described by its deps.json. Without this
    // override the default context probes only next to the host exe, and any
    // plugin carrying a native library fails its type initializer.
    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }
}
