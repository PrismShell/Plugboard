# 08 - The Passthrough: State, Security, and Bootstrap

This document records where the design landed. It simplifies the earlier framing. Read it as the current shape of the system.

## Design update (supersedes the composition layer in 04 and 05)
Earlier docs introduced a layer of *capability compositions* defined in the policy page, built from server *primitives*. We are dropping that layer. Reason: the local gateway's endpoints (`bdp`, `bds`, `bdh`, and the Excel/Outlook/PDF/file ones) are already well-defined, bounded operations. They ARE the capabilities. There is no need for a separate primitive abstraction or a composition layer on top.

So:
- **Capabilities = the gateway's existing endpoints.** Keep the local gateway essentially as it is.
- **The passthrough does NOT define capabilities.** It handles **state** and **security** only.

## What the passthrough is
A trusted, signed, locked-down **page** (browser-side, served from `localhost` by the gateway). Because it is a page, the state it manages lives in the browser, and the gateway stays stateless. Its two jobs:
1. **State:** session and app state, the shell/window the tools live in.
2. **Security:** it is the broker. Untrusted tool pages reach the gateway only through it. It decides which endpoints may be called, with what, by whom, and carries the trusted origin's credential to the gateway.

It is explicitly NOT a capability-definition layer and NOT a place where execution logic lives. Execution logic is 100% in the gateway (compiled code). The passthrough never holds code the gateway runs; that would be the RCE case rejected in `03` and `04`.

## Integrity: how "no one can modify the passthrough" is actually enforced
File ACLs alone are not enough. The mechanism is signing plus a pinned key:
- The passthrough page is **signed** by a private key only the owner/superadmin holds.
- The gateway ships with the corresponding **public key pinned** in its (signed) binary.
- The gateway verifies the passthrough signature before serving or using it. A page that was modified, or re-signed with the wrong key, fails verification and is **refused**.

This is what makes the "no one modifies the passthrough" requirement real: even someone with write access to where the passthrough is stored cannot make a change the gateway will accept, because they do not hold the private key.

Root of trust, stated honestly: this holds as long as the gateway binary itself is intact. An attacker who can replace the gateway binary could swap the pinned key. That is the same "local machine compromised equals everything compromised" boundary drawn in `03`. The signature protects the passthrough against tampering given an intact gateway; it does not protect a compromised machine.

## Bootstrap and caching (the startup flow)
On startup the gateway:
1. Reads its configured passthrough source (the remote secure location).
2. Fetches the passthrough page and its signature.
3. Verifies the signature against the pinned public key.
4. On success: writes it to a local cache (atomically) and serves it at `localhost`.
5. On fetch failure (remote unreachable): loads the last-known-good cached copy, re-verifying its signature before use.
6. On signature failure (either source): refuses to serve it. It does NOT fall back to unverified content. A bad signature is a hard stop, not a degrade.

Refresh: re-fetch on startup, and optionally on a schedule or on demand, always verifying before replacing the cache.

The browser then opens `http://localhost:9194/`, receives the verified passthrough (the shell/broker), and from there loads tool pages and brokers their access to the gateway endpoints.

## Why serving it from the gateway is the right call
- **Stable origin:** the browser loads the passthrough from `localhost`, so it is a real origin (the "window") with storage, history, and postMessage.
- **Offline resilience:** the last-known-good cache means a brief remote outage does not stop the desk.
- **Shared fate:** the passthrough lives close to the remote tool files, so "passthrough unreachable" usually coincides with "tools unreachable anyway," and the cache covers the rest.
- **One verification point:** the gateway verifies the signature once, at fetch, before anything trusts it.

## IP posture
This is treated as a **trade secret**, not a patent play (see `07` for the honesty on novelty). Practical implication: protection depends on keeping it confidential. Limit access, do not publish, mark internal materials confidential. This is engineering and operational framing, not legal advice.
