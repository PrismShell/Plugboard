# Plugboard examples

## `mesh-demo.pbapp` — the composition mesh, interactively

A **view** (a Plugboard app - plain HTML in a `.pbapp` file) that calls a **service**
over HTTP, where the service composes a **connector** in-process. It demonstrates the
three tiers end to end.

### Run it

```powershell
# 1. Build + deploy all plugins into the host's plugins dir (no hand-copying)
.\tools\build-plugins.ps1

# 2. Build + start the host
dotnet build src\Plugboard.Host\Plugboard.Host.csproj -c Release
.\src\Plugboard.Host\bin\Release\net8.0-windows\Plugboard.Host.exe

# 3. (once) register the .pbapp file association
.\tools\register-open-with-gateway.ps1
#    now DOUBLE-CLICK examples\mesh-demo.pbapp
```

There is **no key**. A `.pbapp` opens *through* Plugboard: double-clicking runs
`Plugboard.Host.exe --open <file>`, which serves the file at `/app/{id}/`, so the page
loads at Plugboard's own origin (`http://localhost:9195`). Its capability calls are
therefore **same-origin** and just work; the browser stamps `Sec-Fetch-Site: same-origin`,
which is unforgeable. A random website is cross-origin/cross-site and gets `401` on the
capability routes - it can't call them at all. Built-in reads (`/catalog`, `/info`) are
ungated (shape only, no data), so the status/catalog panels always work.

> Double-clicking the `.pbapp` opens it *through* Plugboard (served) - that's the
> point of the file type. If you instead rename it to `.html` and open that straight off
> disk (`file://`), the page loads but its capability calls (mesh-check, prices) return
> `401` - it isn't served, so it isn't same-origin. Open apps through Plugboard.

### Building a `.pbapp`

A `.pbapp` can be **plain HTML** (like this demo) or a **built bundle**. To build, a project
is a **folder with an `index.html` at its root** (the entry). The build step packs it:

```powershell
Plugboard.Host.exe --build .\myapp             # -> myapp\myapp.pbapp  (the whole folder, zipped)
Plugboard.Host.exe --build .\myapp -o out.pbapp
Plugboard.Host.exe --build .\myapp --flatten   # single-page: inline into one gzipped HTML
```

**Default (zip):** the whole project folder is packed into one opaque compressed file, and
Plugboard serves each file out of it by path (`/app/{id}/page2.html`, `/js/app.js`,
`/data.json`, …). So **multiple linked pages, ES modules, and `fetch()` of local files all
work** - it's your folder served as a static site, just in one portable file. Junk
(`node_modules`, `.git`, `bin`, `obj`, `dist`, dotfiles, prior `*.pbapp`) is excluded.

**`--flatten`:** for a single-page app, inline everything into one minified, gzipped HTML
(smaller, but no sub-files). Plugboard detects the format by header (zip / `PBAPP1` /
plain HTML), so all three kinds of `.pbapp` just work.

Either way it's small and unreadable in an editor at rest - but the browser is served
plaintext, so View Source still shows the code: portability + obfuscation, **not**
confidentiality. No keys, nothing to rotate.

| Section | Call | Shows |
|---|---|---|
| **1 · What's loaded** | `GET /catalog` | every unit the host mounted; `svc/*` = service, rest = connector |
| **2 · Mesh self-test** | `GET /svc/meshcheck` | the `meshcheck` service calls the `ping` connector via `req.Call` - runs anywhere, no Terminal |
| **3 · Prices** | `POST /svc/prices` | the `prices` service composes the `con/bloomberg/bdp` connector (needs the Bloomberg connector deployed + a Terminal) |

In section 2, the `composed` block in the response is exactly what `ping/hello`
returns — that's the service having called the connector in-process. That's the mesh.

### Tiers

- **Connector** — resource/adapter (C# DLL, in-process, trust-gated): `ping`, `bloomberg`.
- **Service** — composes connectors and other services via `req.Call(name, payload)`: `svc/meshcheck`, `svc/prices`.
- **View** — HTML that renders by calling services/connectors over HTTP: this page.
