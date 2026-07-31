# Plugboard

A local web host that turns a trading desktop's capabilities — Bloomberg, Excel, Outlook, PDF, the filesystem — into a single, uniform, self-describing HTTP surface on `http://localhost:9195`, and lets you build and run browser "apps" against it with **no install and no secret**.

Drop a signed (or unsigned, in lite mode) DLL in the plugins folder to add a capability; double-click a `.pbapp` file to run an app that uses them.

---

## The mental model: connectors, services, views

Everything in Plugboard is one of three things.

| Tier | What it is | Lives at | Example |
|------|------------|----------|---------|
| **Connector** | A resource/adapter. A signed C# DLL loaded in-process; the only tier that touches the outside world (Bloomberg session, Excel COM, Outlook, disk). | `/con/<name>/…` | `/con/bloomberg/bdp` |
| **Service** | Composition. A plugin whose handler *calls other units* to combine them - no external I/O of its own. | `/svc/<name>/…` | `/svc/prices` |
| **View** | An HTML app (a `.pbapp` file) that calls services and connectors over HTTP and renders the result. | served at `/app/{id}/` | `examples/mesh-demo.pbapp` |

A connector exposes a resource. A service wires connectors together. A view is the UI.
The host itself is empty - it only mounts what plugins register.

### The composition mesh

Services compose other units **in-process**, not over the network. A plugin handler
receives a `PluginRequest` with a `Call` delegate:

```csharp
// inside the "prices" service
var quote = await req.Call("bloomberg/bdp", new { securities, fields = new[]{"PX_LAST"} });
```

`req.Call(name, payload)` dispatches to another registered unit by its **bare name** (no
`con/`/`svc/` prefix), returns whatever that unit returned, and is cycle-guarded by depth.
That is the mesh: a view calls one service, which fans out to several connectors, all
without leaving the process.

---

## How you actually use it

The headline feature: **an app is just a file you open.**

1. **Author** an app as a `.pbapp` file - plain HTML/CSS/JS (see `examples/mesh-demo.pbapp`).
2. **Double-click it.** Windows hands it to Plugboard (`Plugboard.Host.exe --open`),
   which serves it at `http://localhost:9195/app/{id}/` and opens your browser there.
3. The app's `fetch('/con/…')` / `fetch('/svc/…')` calls **just work** - see the security
   model below for why no key is involved.

`.pbapp` is registered (per-user, no admin) as a file type whose default program is
Plugboard, via `tools/register-open-with-gateway.ps1`. Apps can live on a network
share; opening one is the whole "install".

---

## Security model (why there is no key)

The threat is not a remote host - loopback isn't reachable off-box. It's a **web page the
user visits** trying to drive the local gateway, and **untrusted files** being opened.
The design makes the *origin* the boundary, using signals the browser sets and page script
cannot forge.

- **Served, same-origin.** Apps are opened *through* Plugboard (`/app/{id}/` or
  `/view?path=`), so they run at Plugboard's own origin. Their capability calls are
  therefore `Sec-Fetch-Site: same-origin` - a browser-set, unforgeable header.
- **The capability gate.** `/con/*` and `/svc/*` are allowed only when
  `Sec-Fetch-Site` is `same-origin` **or absent**. A browser can never omit that header,
  so a drive-by page (always `cross-site`/`same-site`) is refused `401`; an *absent*
  value means a local non-browser caller (a Python/VBA/CLI tool, or Tabby2's Node host),
  which is fine on loopback. **No secret lives anywhere.**
- **CORS is locked to the gateway origin** (`localhost:9195`), so even the built-in reads
  can't be scraped cross-origin.
- **`/view?path=`** (caller controls the path) refuses `cross-site` (`403`) so a drive-by
  can't get its own file served at our origin.
- **`/app/{id}/`** (content is a pre-registered file, not caller-chosen) allows cross-site
  loads but sets `Content-Security-Policy: frame-ancestors 'self' vscode-webview:`, so
  only same-origin pages and the Tabby2 editor's webview may *frame* an app - a website
  cannot.
- **Loopback bind + host filtering.** Kestrel listens on `127.0.0.1`/`::1` only;
  `AllowedHosts` rejects non-loopback `Host` headers (kills DNS-rebinding).

Residual risk is honest and documented: a user *socially engineered* into serving a
malicious `.pbapp` runs it same-origin (higher bar than a drive-by, still bounded by the
connector set and Bloomberg/Outlook entitlements); a local process that already owns the
box could forge the header (out of scope). Full reasoning: `docs/03-security-model.md`.

---

## Built-in endpoints (the host itself)

These are ungated (they expose API *shape*, never desk data), so dashboards and agents can
read them freely.

| Endpoint | Purpose |
|----------|---------|
| `GET /catalog` | Every mounted route, with summaries/params - the live API surface. |
| `GET /tools` | The same surface as MCP/OpenAI-style tool definitions (name, method, path, JSON-Schema inputs) for AI tool-calling. |
| `GET /health` | Liveness + uptime. |
| `GET /info` | Host identity (service, version, pid). |
| `GET /console` | A human dashboard (catalog / tools / health / info), also opened from the tray. |
| `GET /view?path=` | Serve one HTML/`.pbapp` file at our origin (back-compat / ad-hoc). |
| `POST /_open` | Register a file for serving; returns a short `/app/{id}/` URL. Used by `--open`. |
| `GET /app/{id}/…` | Serve a registered app and its folder (relative assets resolve; path-clamped). |

Because clients read `/catalog` and `/tools` live, they never bundle a stale copy of the
API - the gateway describes itself.

---

## What's installed now

Connectors and services currently shipped (from the live `/catalog`):

| Unit | Kind | Routes | What it does |
|------|------|-------:|--------------|
| `con/bloomberg` | connector | 9 | Reference/market data (BDP/BDS/BDH), intraday, field/security search, `bbg://` launch - one shared BLPAPI session. |
| `con/cmp` | connector | 63 | Bloomberg CMP (CMBS/structured analytics): one typed endpoint per request type + a raw JSON-envelope escape hatch; write-gated. |
| `con/excel` | connector | 49 | Live Excel (workbooks, sheets, ranges, named ranges) via COM. |
| `con/outlook` | connector | 17 | Outlook mail/folders/calendar/contacts. |
| `con/pdf` | connector | 12 | PDF generation + reading/table extraction. |
| `con/files` | connector | 2 | Bounded filesystem access. |
| `con/ping` | connector | 2 | Trivial demo connector (used by the mesh demo). |
| `svc/meshcheck` | service | 1 | Self-test: calls the `ping` connector in-process to prove the mesh. |
| `svc/prices` | service | 1 | Composes `bloomberg/bdp` - the realistic service pattern. |

`bloomberg` and `cmp` are **separate connectors that share one BLPAPI session** - see below.

---

## Plugins: how a capability is added

A plugin is a **folder** (entry DLL + its full dependency closure + an optional
`plugin.json` manifest). The host scans the plugins directory at startup, verifies each
one (in managed mode), loads it in its own `AssemblyLoadContext`, and calls
`IPlugin.Register(...)` so it can map routes.

The contract (`Plugboard.Contracts`) is tiny:

```csharp
public interface IPlugin
{
    string Name { get; }
    void Register(IEndpointRegistry registry);
}

// registry.Map("POST", "bloomberg/bdp", handler, routeInfo);
//   route "svc/..." mounts under /svc/, anything else under /con/.
// handler: Func<PluginRequest, Task<object?>>  -> return data; the host wraps it
//   in { ok:true, data } (or { ok:false, error } if it throws).
// RouteInfo/ParamInfo (optional) feed /catalog and /tools.
```

### Shared assemblies (Contracts and BLPAPI)

`PluginLoadContext` forces a few assemblies to resolve to the **host's** copy rather than a
private per-plugin one:

- `Plugboard.Contracts` - so `IPlugin` type identity matches across plugins.
- `Plugboard.Blpapi` + `Bloomberglp.Blpapi` - because BLPAPI's native library is a
  **process singleton**. Two plugins each loading their own managed BLPAPI in separate
  contexts cannot both get a session (the second dies "Session Not Started"). So the
  session manager + JSON↔Element helpers live in one shared `Plugboard.Blpapi` core,
  loaded once by the host; `bloomberg` and `cmp` both bind to that single session, each
  opening its own service (`//blp/refdata`, `//blp/cmp`) on it. `build-plugins.ps1` does
  not copy these into plugin folders.

### Build and deploy

```powershell
.\tools\build-plugins.ps1              # build every src/plugins/* and deploy into the host's plugins dir
.\tools\build-plugins.ps1 -Key my.key  # sign each plugin (managed posture)
```

### Lite vs managed posture

Set in `src/Plugboard.Host/appsettings.json`:

- **Lite** (default): `RequireSignature: false` - any DLL in the plugins dir loads.
- **Managed**: `RequireSignature: true` + `TrustedKeys: [ …public keys… ]` - each plugin
  needs a valid detached `.sig` (RSA-SHA256 over the DLL) or it's refused. Generate keys
  with `tools/gen-keys.ps1`, sign with `tools/sign-plugin.ps1`.

`PluginsDirs` is a list, so a local dev folder and a shared network folder can both be
scanned (first match wins on name collisions).

---

## Tray + discovery

The host runs as a tray app (`WinExe`, no console). The tray icon is a green toggle
switch (matching the `.pbapp`/menu icon) - green when running, slate when stopped - with
Admin Page, Copy Base URL, and Start/Restart/Stop. On startup it writes
`%LOCALAPPDATA%\plugboard\location.json` (baseUrl, exePath, pid, catalog/info URLs) so a
client (e.g. the Tabby2 editor) can find and relaunch the host even when it's down.

---

## Quick start

```powershell
# 1. build + deploy plugins
.\tools\build-plugins.ps1

# 2. build + run the host (tray app on http://localhost:9195)
dotnet build src\Plugboard.Host\Plugboard.Host.csproj -c Release
.\src\Plugboard.Host\bin\Release\net8.0-windows\Plugboard.Host.exe

# 3. one-time: register the .pbapp file association
.\tools\register-open-with-gateway.ps1

# 4. double-click examples\mesh-demo.pbapp  (or open http://localhost:9195/console)
```

Bloomberg/CMP need a running, entitled Bloomberg Terminal; Excel/Outlook need those apps.
The mesh demo's self-test (`/svc/meshcheck`) runs with no external dependency at all.

---

## Repository layout

```
src/
  Plugboard.Host/         the generic host: routing, mesh, security gate, /catalog,
                            /tools, /console, /view, /_open, /app, tray, plugin loader
  Plugboard.Contracts/    IPlugin / IEndpointRegistry / PluginRequest / RouteInfo
  Plugboard.Blpapi/       shared BLPAPI core (one session; Element<->JSON helpers)
  Plugboard.Sign/         signing helper
tools/                      build-plugins, register-open-with-gateway, gen-keys,
                            sign-plugin, generate-icon
examples/                   mesh-demo.pbapp + walkthrough
docs/                       design + reasoning trail (see below)
```

**Plugins:** Core plugins (Bloomberg, CMP, Excel, Outlook, PDF, Files, etc.) are in a separate repository: [PrismShell/core-plugins](https://github.com/PrismShell/core-plugins)

---

## Design docs

The `docs/` folder is the **reasoning trail** - how the design was argued out. This README
describes the system **as built**; where a doc and the code disagree, the code wins.
`docs/03-security-model.md` is kept current with the served/same-origin model.

- `00-thesis` · `01-vision` - the headline idea and the road here
- `02-architecture` - components, trust tiers, request lifecycle
- `03-security-model` - trust boundaries, threat model, mitigations, residual risk (current)
- `04-protocol` · `05-capabilities` - earlier composition framing (superseded, kept for the trail)
- `06-open-questions` - what's still undecided
- `07-prior-art-and-differentiation` - prior art and where this differs
- `08-passthrough-and-bootstrap` - the interim passthrough design (superseded by the served model)
- `09-plugin-host` - the plugin-host mechanics


---

## TODO

- [ ] **Plugin install interface** — in-app UI (`/console` or tray menu) for browsing, downloading, and installing new connectors/services from a registry
- [ ] **Installer** — proper Windows installer (.exe/.msi) that:
  - Installs Plugboard.Host to Program Files
  - Registers `.pbapp` file association
  - Adds Start Menu / tray auto-start option
  - Bundles core plugins (Bloomberg, Excel, Outlook, Files, PDF)
- [ ] **Plugin marketplace** — hosted registry of available plugins with versioning and checksums
- [ ] **Auto-update** — check for and apply Plugboard and plugin updates
- [ ] **RelayCoder integration** — coordinated installer that sets up both Plugboard and RelayCoder together

---

## License

AGPL-3.0 — see [LICENSE](LICENSE) for details.
