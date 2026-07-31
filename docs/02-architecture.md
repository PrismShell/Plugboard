# 02 - Architecture

## Trust tiers
Three tiers, from most to least trusted:

1. **Gateway server** (most trusted, fixed code). A small, stateless local process on `localhost`. Ships a closed set of *primitives* (bounded powers). Knows how to: load and verify the policy page, serve static tool pages, validate and dispatch capability requests, return a uniform response. It contains no business logic and no app state.

2. **Policy page, the "gateway page"** (trusted, owner-only writable). Lives in a locked-down location only the owner can modify. Defines the *capability catalog*: named capabilities, each expressed as a *composition* of server primitives plus parameter constraints. This is the Trusted Computing Base for policy. Changing what the system can do means changing this, and only the owner can.

3. **Tool pages** (untrusted, shared). The actual sheets and dashboards, kept on a general team share that many people can write to. Loaded into the app shell, typically in iframes. They may only *request* named capabilities. They cannot define capabilities and cannot reach primitives directly.

## Overview

```
        owner-only origin                      localhost                      team share
      +------------------+            +-----------------------+         +------------------+
      |   Policy page    |  load &    |    Gateway server     | serves  |   Tool pages     |
      | (capability      |==verify==> | (dumb, stateless,     |========>| (untrusted,      |
      |  catalog)        |            |  fixed primitives)    |         |  many writers)   |
      +------------------+            +-----------+-----------+         +--------+---------+
                                                  |                              |
                                                  | brokers capability           | requests
                                                  | requests against policy      | capabilities
                                                  v                              |
                                        +-----------------------+                |
                                        |  Browser app shell    |<---------------+
                                        |  (trusted origin):    |  postMessage
                                        |  holds state + broker |
                                        |  +-- iframe: tool ----+|
                                        +-----------------------+
```

## The broker / iframe model
- The **app shell** is served from the trusted gateway origin and holds the capability catalog and all app state. It is the only thing allowed to call the gateway server (CORS is locked to this origin; see the security doc).
- **Tool pages** load in cross-origin iframes. They cannot read the shell's DOM, token, or storage (browser origin isolation enforces this).
- A tool page asks for work by `postMessage` to the shell: "invoke capability X with params Y."
- The shell checks the request against the policy catalog. If allowed, it calls the gateway server (carrying the trusted origin's credentials), then returns the result to the iframe by `postMessage`. If the capability is not in the catalog, the shell refuses and the iframe gets nothing.
- Net effect: an untrusted tool page's only path to any power runs through the broker, which enforces policy. A tool page that asks for an undefined capability simply cannot do anything.

## Request lifecycle (happy path)
1. Browser opens `http://localhost:9194/`. The gateway serves the app shell (trusted origin).
2. The shell loads the policy page (capability catalog) and renders a launcher (optionally from a share manifest).
3. The user opens a tool; the shell loads it in an iframe from the team share (via the gateway's file serving).
4. The tool page postMessages a capability request to the shell.
5. The shell validates against the catalog and parameter constraints.
6. The shell calls the gateway server with a uniform request envelope.
7. The server validates again (defense in depth), dispatches to the named primitive composition, executes bounded primitives, and returns a uniform `{ok, data, error}` envelope.
8. The shell relays the result back to the iframe.

## What is stateless
The server keeps no app state and (per current decision) no persistent key/value store. State that must survive navigation lives in the shell (memory, `localStorage`, IndexedDB) or in the URL. State never needs to outlive the browser.
