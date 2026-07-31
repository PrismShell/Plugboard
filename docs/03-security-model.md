# 03 - Security Model

This is the most important document in the repo. The architecture is only as sound as this section.

## The one rule: data may travel, code may not
- Tool pages and the policy page may send **data**: which named capability, and which parameters.
- They may never cause the server to run **arbitrary code, processes, or paths**.
- The page composes vetted building blocks. It never gets a raw "execute" power.

The instant any primitive can run an arbitrary command, read an arbitrary path, or evaluate arbitrary code, the page that can reach it owns the machine. On a trading desktop with Bloomberg, Outlook (sends mail), Excel, and filesystem access, that is catastrophic.

## Where the security boundary actually lives
There are two viable places to put the boundary. This design uses both, layered.

1. **The primitive set in the server.** Security reduces to: what are the primitives, and is each one individually safe (bounded, parameterized, no arbitrary exec)? This is fixed code, audited once, changed rarely.

2. **The integrity and authenticity of the policy page.** Because the policy page defines capability compositions, the system's behavior is only as trustworthy as (a) the locked-down origin's access control and (b) the authenticity of the channel the server uses to load the policy page.

Critical point for the "make the server a single generic primitive" idea (see the protocol doc): collapsing the server toward one generic primitive does NOT remove the boundary. It **relocates** it almost entirely onto item 2, the policy page's integrity and its delivery. That can be a legitimate design, but only if the locked-down origin and the policy fetch are genuinely hardened. If either is spoofable or writable by others, it is total remote code execution.

## Threat model (localhost is not "safe")
- **Other local processes.** Anything running on the machine can call `localhost:9194`. Malware or a rogue script will happily speak whatever protocol we define.
- **Any website the user visits.** With permissive CORS, a random page in the user's browser can drive the gateway (pull Bloomberg data, trigger an Outlook send, read files). This hole exists in the current LocalGateway today: `AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()`.
- **The team share's ACL.** "Add capabilities by editing files" means write access to a location equals power. The team share holds untrusted tool pages on purpose, so the policy page must NOT live there. The policy page's store ACL is part of the security boundary.
- **Parameter injection.** Even with safe primitives, an untrusted tool page supplies parameters. If a primitive interpolates a parameter into a path, command, or query without validation, that is the injection door.

## Why NOT a homegrown packet format or custom crypto
The instinct to "design our own protocol, even packets, then we can really secure it" is understandable and wrong, for two reasons:
1. **It secures the wrong layer.** A custom binary or encrypted channel protects against eavesdropping and tampering *in transit*. On localhost the traffic never leaves the machine, so the in-transit threat is minimal. It is armor on an interior hallway.
2. **Attackers speak the protocol fine.** The real attackers (local processes, cross-origin pages) can implement any format we publish. Obscurity is not security. And hand-rolled crypto is the canonical way capable engineers ship broken security.

If a secure channel is ever genuinely needed, the answer is TLS plus a token, never an invented format.

## Mitigations that actually matter (in priority order)
1. **Lock CORS to the trusted shell origin only.** Replace `AllowAnyOrigin` with an explicit allowlist. Highest value, smallest change. This alone stops arbitrary websites from driving the gateway.
2. **Closed capability/primitive allowlist.** The server dispatches only to primitives it ships. Unknown capability or primitive is rejected. This *is* the protocol's security model.
3. **Bounded primitives.** Each primitive is typed and constrained: file ops clamped to a root, network calls to allowlisted hosts, process execution (if it must exist) only from a fixed executable allowlist with no shell interpolation.
4. **Path clamping.** Any file primitive rejects paths that escape its configured root (no `..`).
5. **Policy page integrity.** Owner-only write ACL on the policy store; authenticate or verify the policy page on load (signature, or a pinned trusted local path) so it cannot be swapped or MITM'd.
6. **Parameter validation against schema** at both the broker (shell) and the server (defense in depth).
7. **Served origin + same-origin gate** (realized - see "Local browser-origin hardening" below). Apps are opened *through* the gateway, so they run at the gateway's own origin and their capability calls are `Sec-Fetch-Site: same-origin` - a browser-set, unforgeable signal. No secret lives in any file.

## Residual risks to keep visible
- Compromise or mis-ACL of the policy store equals full control. Treat it like a production secret.
- A single over-broad primitive silently reintroduces RCE. Adding a primitive is a security event, not a feature.
- Parameter injection inside an otherwise-safe primitive.
- The trust placed in the browser's origin isolation between shell and iframes (correct today, but it is a dependency).

## Local browser-origin hardening (posture)

The host runs on the desk, so the real threat is not a remote host - it is a web
page the user visits driving the local browser at `localhost:9195`. Layered defense:

1. **Loopback-only bind.** Kestrel listens on `127.0.0.1` / `::1` only (proven:
   `netstat -ano | findstr 9195` shows loopback, not `0.0.0.0`). No off-box route.
2. **Host filtering.** `AllowedHosts` is `localhost;127.0.0.1`, so any request with a
   non-loopback `Host` header is rejected `400` - kills DNS-rebinding.
3. **Served origin, not a key.** Apps are opened *through* the gateway (right-click an
   `.html` -> **Serve via Gateway**, or the gateway's `--open <file>` helper). The
   gateway serves the file from `GET /view?path=<file>`, so the page runs at
   `http://localhost:9195` - the gateway's own origin. Its capability calls are therefore
   **same-origin**, which the browser stamps as `Sec-Fetch-Site: same-origin`. That
   header is set by the browser and **cannot be forged by page script** (it is a
   Forbidden header name). The capability gate is exactly this:

   - `/con/*` and `/svc/*` require `Sec-Fetch-Site: same-origin` -> a served app passes,
     a random website (`cross-site`) gets `401` before any data returns.
   - `/view` refuses `Sec-Fetch-Site: cross-site` (`403`), so a hostile page cannot
     navigate the browser to `/view?path=...` and get its own file served at our origin.

   No secret lives in any file. Nothing to rotate, nothing to leak. **Rotation, keyring,
   and the `X-Plugboard-Token` header are gone** - the served model makes them
   unnecessary.
4. **CORS locked to the gateway origin.** The default policy allows only
   `http://localhost:9195` / `http://127.0.0.1:9195`. Because apps are served, the
   legitimate caller *is* same-origin; a cross-origin page gets no
   `Access-Control-Allow-Origin` and cannot read responses (proven: `Origin: evil.com`
   -> no ACAO; `Origin: localhost` -> allowed).
5. **Built-in endpoints** (`/`, `/health`, `/info`, `/catalog`, `/tools`, `/console`)
   are NOT gated - they expose only the API shape, no desk data - so the admin page and
   monitors work without any auth. Data and actions live behind the same-origin gate.

### Why served-origin beats the key (and the `file://` model)

We spent real effort on the `file://` distribution model (double-click the HTML off a
share, no serving) plus a shared key. Two problems killed it:

- A `file://` page has a `null` origin that a hostile sandboxed iframe can also present,
  so an origin check can't tell our app from an attacker's. That forced a **secret** into
  every file - which then has to be distributed, rotated, and re-stamped fleet-wide.
- The secret-in-the-file is itself readable by anything that can read the file.

Serving the app fixes the root cause: a served page has a **real, unspoofable origin**,
so `Sec-Fetch-Site: same-origin` is a sound boundary with no secret at all. The tradeoff
is that the app must be *launched through the gateway* rather than double-clicked - which
the right-click verb makes a one-click gesture.

### Residual risks (kept visible)

- **Social-engineering a malicious HTML.** A user tricked into right-click-serving a
  hostile `.html` runs it same-origin, so it can call capabilities. This is a higher bar
  than a drive-by (the user must save the file and deliberately serve it), and it is
  still bounded by the primitive set (no arbitrary exec) and by Bloomberg/Outlook
  entitlements. It is not a silent remote hole.
- **Non-browser local callers can no longer authenticate.** Removing the key means a
  local Python/VBA/curl caller has no `Sec-Fetch-Site: same-origin` and no key, so it
  gets `401`. The gateway is now a **served-browser-app** surface. If machine callers are
  needed, the cleanest fix is to *also* allow requests with **no** `Sec-Fetch-*` headers
  (which a browser can never omit -> only a non-browser local process on loopback can
  produce), rather than reintroducing a key.
- **A local malicious process** could forge `Sec-Fetch-Site` on a raw socket. But such a
  process already owns the box - out of scope, same as before.
