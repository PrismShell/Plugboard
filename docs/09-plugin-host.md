# 09 - The Plugin Host (current build direction)

This supersedes the "static gateway with fixed endpoints" framing. Plugboard is a
generic host that loads capabilities as signed plugin DLLs. Decided 2026-06-25.

## Shape
- **Host (the shell):** a generic C# / ASP.NET process with NO capabilities of its own.
  At startup it scans a plugins directory, verifies each DLL's signature against its
  trusted keys, loads the verified ones, and exposes the routes they register under
  `/con/<route>` or `/svc/<route>`. The host binary never changes to add a capability.
- **Plugins (the logic):** each DLL implements `IPlugin` and calls
  `registry.Map(method, route, handler)` to attach its endpoints. Excel, Outlook,
  Bloomberg, Filesystem each become their own plugin.
- **Orchestrators (the UI/workflow):** HTML/JS pages call `/con/...` and `/svc/...` over HTTP. They
  hold no capability logic; they compose calls across plugins.

## Decisions (settled)
- **In-process, load-at-init.** Plugins load once at startup via `AssemblyLoadContext`.
  No hot-swap. To reload, restart the host. (Deletes the COM-unload problem.)
- **No fault-isolation requirement.** A load failure is caught and the plugin is
  skipped; the host stays up. A runtime crash is just a normal bug, same as a monolith.
- **Signing is the only trust gate.** Detached `<dll>.sig` = RSA-SHA256 over the DLL
  bytes, verified against trusted public keys before load. The runtime does not do this
  for us and strong-naming is not a trust boundary, so the host verifies explicitly.
  `TrustedKeys` is a list, for rotation. Signing protects against loading unauthorized
  or tampered DLLs; it does not protect an already-compromised machine (accepted).
- **Keyring auth** for `/con/*` and `/svc/*`: callers send `X-Plugboard-Token`; the host checks it against SHA-256 hashes read from a keyring file (`KeyringPath`, typically on the share), cached locally and refreshed periodically. Built-in endpoints are not gated.

## The contract
```csharp
public interface IPlugin {
    string Name { get; }
    void Register(IEndpointRegistry registry);
}
public interface IEndpointRegistry {
    void Map(string method, string route, Func<PluginRequest, Task<object?>> handler, RouteInfo? info = null);
}
public sealed record RouteInfo(string? Summary = null, string? Description = null, object? Sample = null);
```
Handler returns the data object (host wraps as `{ok:true,data}`) or throws (host wraps
as `{ok:false,error}`). Uniform envelope everywhere.

**Discovery / catalog.** `GET /` lists the loaded plugins and routes. `GET /catalog`
returns the self-describing API surface — each route's method, path, and any
`summary`/`description`/`sample` the plugin supplied via the optional `RouteInfo`.
Clients (tabby2, IntelliSense, templates) read `/catalog` live from the gateway
instead of bundling a static snapshot that drifts. `RouteInfo` is optional, so a
route appears uannotated until its plugin fills it in.

## Layout
```
plugboard/src/
  Plugboard.Contracts/                 IPlugin, IEndpointRegistry, PluginRequest
  Plugboard.Host/                      shell: scan -> verify -> load -> register -> serve (:9195)
  Plugboard.Sign/                      .NET signing tool (gen-keys, sign); shares the host's crypto
  plugins/Plugboard.Plugins.Ping/      proof plugin (no deps)
plugboard/tools/
  gen-keys.ps1                           wrapper -> Plugboard.Sign gen-keys
  sign-plugin.ps1                        wrapper -> Plugboard.Sign sign  (DLL -> <dll>.sig)
```

Status (2026-06-25): skeleton built and proven end to end. A signed Ping plugin
loads and serves `/con/ping/hello` + `/con/ping/echo`; with its `.sig` removed the
host refuses it (`/` shows `plugins:[]`, the route 404s). Signing is a .NET tool, not
PowerShell, because Windows PowerShell 5.1 lacks the modern RSA export APIs.

## Deployment (self-contained, runs out of the box)
Publish the host as a self-contained single-file exe so the target needs no .NET install:
```
dotnet publish src\Plugboard.Host\Plugboard.Host.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true -o dist\host
```
The deployable is the whole `dist\host` folder:
```
dist\host\
  Plugboard.Host.exe        ~91 MB (runtime bundled in; no .NET install needed)
  appsettings.json            urls, PluginsDirs, TrustedKeys, RequireSignature, KeyringPath, AllowedHosts
  plugins\
    <Plugin>.dll              a capability plugin
    <Plugin>.dll.sig          its signature (must verify against a TrustedKeys entry)
```
Copy that folder to any Windows machine and run the exe. `web.config` and
`aspnetcorev2_inprocess.dll` may appear in the output; they are harmless IIS-hosting
artifacts and are ignored when the exe self-hosts. Verified 2026-06-25: the
self-contained exe runs and serves the signed Ping plugin standalone.

## Capability plugins (ported from local-gateway, 2026-06-25)
All five local-gateway handlers are recreated as signed plugins under `src/plugins/`,
each its own project that ships its own dependencies:
- `Plugboard.Plugins.Bloomberg` (8 routes; carries BLPAPI; the session self-heal is preserved)
- `Plugboard.Plugins.Excel`     (49 routes; COM automation of a running Excel)
- `Plugboard.Plugins.Outlook`   (17 routes; COM automation of Outlook)
- `Plugboard.Plugins.Pdf`       (12 routes; itext7 + headless Chrome/Edge for HTML->PDF)
- `Plugboard.Plugins.Files`     (2 routes; `files/read` is binary-safe — text as utf8, else base64, signalled by an `encoding` field)

Routes are exposed at `/con/<route>` or `/svc/<route>` (e.g. `/con/bloomberg/bdp`). Each returns its data
object (host wraps `{ ok, data }`); errors throw (host wraps `{ ok:false, error }`).
Status-style routes (`bloomberg/status`, `excel/detect`, `outlook/status`) report their
state as data rather than throwing.

Loader supports two layouts:
- flat: `plugins/<Plugin>.dll` (+ `.sig`) for dependency-free plugins (Ping).
- foldered: `plugins/<Plugin>/<Plugin>.dll` (+ `.sig`) with its deps alongside; the per-plugin
  load context resolves BLPAPI / itext from that folder.

Verified 2026-06-25: the host loaded all six (5 capabilities + Ping), signature-verified,
and registered 90 routes. The COM/Bloomberg routes load and register here; invoking them
needs Office / the Bloomberg Terminal present at runtime.

## Build / run loop
1. `dotnet build Plugboard.Host` and `dotnet build` the plugin.
2. `gen-keys.ps1` once; put the public key in the host appsettings `TrustedKeys`.
3. Copy the plugin DLL into the host's `plugins/` dir; `sign-plugin.ps1` it.
4. Run the host; the verified plugin's routes appear under `/con/` (or `/svc/` for services).

## Sequence
Ping plugin proves the loop. Then port Bloomberg (the existing handler becomes a
plugin), then Excel/Outlook/PDF/File. Keep `local-gateway` running until parity.
The orchestration layer already built there (passthrough, `/apps`, Tabula preview)
is unaffected; it sits in front of whichever backend serves the capabilities.
