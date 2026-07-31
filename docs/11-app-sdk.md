# 11 - App SDK (build a .pbapp / browser tool)

A Plugboard **app** is a plain HTML/CSS/JS page that calls the gateway's capabilities. The
trick: the app is **served by the gateway**, so it runs at the gateway's own origin
(`http://localhost:9195`). That makes every `/con/*` and `/svc/*` call **same-origin**, so
there is no CORS wall and **no secret/token** to manage - the host authorizes by origin.

## Open an app

```
POST http://localhost:9195/_open?path=<absolute path to index.html or .pbapp>
  -> { "url": "/app/<id>/" }
```

Then load `http://localhost:9195/app/<id>/`. In practice you either:

- **Double-click a `.pbapp`** (after `tools\register-open-with-gateway.ps1` associates the
  extension with the host), or
- Preview from the editor (RelayCoder's preview extension calls `/_open` for you), or
- `POST /_open` yourself for a folder's `index.html` during development.

## Call capabilities from the page

Just `fetch` the routes - relative URLs, because you're same-origin:

```html
<script>
async function quote() {
  const res = await fetch('/svc/prices', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ securities: ['AAPL US Equity', 'MSFT US Equity'] })
  });
  const env = await res.json();          // { ok: true, data: {...} }  or  { ok: false, error }
  if (!env.ok) throw new Error(env.error);
  render(env.data);
}
</script>
```

Every response is the envelope `{ ok, data }` on success or `{ ok, error }` on failure -
unwrap `.data` before use.

## Discover what's available

The gateway is self-describing - read the live API instead of hardcoding it:

| Endpoint | Purpose |
|---|---|
| `GET /catalog` | Every mounted route + its `RouteInfo` (summary, sample, params) |
| `GET /tools`   | The same routes projected to JSON Schema (for AI tool-calling) |
| `GET /health`  | Liveness |
| `GET /info`    | Host/version/build info |
| `GET /console` | Built-in interactive console to poke routes |

Example: `fetch('/catalog').then(r => r.json())` gives you the connectors
(`con/bloomberg`, `con/excel`, `con/outlook`, `con/pdf`, `con/files`, ...) and services
(`svc/prices`, ...) with their input shapes.

## Package as a .pbapp

A `.pbapp` is a **frozen snapshot** of an app folder (source is a directory with an
`index.html`). Build one from the host:

```powershell
.\src\Plugboard.Host\bin\Release\net8.0-windows\Plugboard.Host.exe --build <appFolder> -o <name>.pbapp
```

During development, preview the live folder (via `/_open` on the folder) so edits show on
save; ship the `.pbapp` when it's stable. The host serves either transparently.

## Security model (why there's no login)

- **Loopback only:** the host binds `http://localhost:9195` and its host-header allow-list
  is `localhost;127.0.0.1`. Nothing off-box can reach it.
- **Same-origin gate:** `/con/*` and `/svc/*` are served only when `Sec-Fetch-Site` is
  `same-origin` (or absent) - i.e. calls coming from a page the gateway itself served.
  A random website in your browser cannot drive it.
- **No auth header:** do not add `Authorization`/tokens; there is nothing to authenticate.

## Minimal app

```
myapp/
  index.html
```

```html
<!doctype html>
<meta charset="utf-8">
<title>Quotes</title>
<button onclick="go()">Quote AAPL</button>
<pre id="out"></pre>
<script>
async function go() {
  const r = await fetch('/svc/prices', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ securities: ['AAPL US Equity'] })
  });
  const env = await r.json();
  document.getElementById('out').textContent =
    env.ok ? JSON.stringify(env.data, null, 2) : 'Error: ' + env.error;
}
</script>
```

`POST /_open?path=C:\...\myapp\index.html`, open the returned `/app/<id>/`, click the
button. That's the whole loop.
