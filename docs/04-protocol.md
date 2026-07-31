# 04 - Protocol

The protocol is deliberately not a new wire format. It is a strict, validated, self-describing convention over plain HTTP and JSON.

## Uniform response envelope
Every response, success or failure, uses one shape:

```json
{ "ok": true,  "data": { ... } }
```
```json
{ "ok": false, "error": { "code": "SESSION_DOWN", "message": "Bloomberg session not started" } }
```

Rationale: the bug that started this whole effort existed because responses were not uniform. Success was a bare keyed object and errors were `{success:false}`, so the client misread an error as "no data" and chased false leads (a wrong market sector, a wrong CUSIP) when the real cause was a dead Bloomberg session. A single envelope makes failures unambiguous at the call site.

## Capability request
A tool page (via the broker) asks for a named capability with parameters:

```json
{ "capability": "offering.quote", "params": { "cusip": "3136GDGK3", "fields": ["PX_BID","PX_ASK"] } }
```

The server never receives "how", only "which capability" and "with what". The "how" is the composition defined in the policy page.

## Discovery (self-describing)
`GET /capabilities` returns the catalog the server is currently honoring: capability names, their parameter schemas, and which primitives they compose. This lets the shell introspect and lets us validate without out-of-band docs. This is what "self-describing protocol" should mean, instead of an invented packet spec.

## Capability descriptor (in the policy page)
Each capability is declared as a composition of primitives plus constraints. Illustrative shape:

```json
{
  "name": "offering.quote",
  "params": {
    "cusip":  { "type": "string", "pattern": "^[0-9A-Z]{9}$" },
    "fields": { "type": "array", "items": { "enum": ["PX_BID","PX_ASK","PX_LAST"] } }
  },
  "use": {
    "primitive": "bloomberg.bdp",
    "with": { "securities": ["{cusip} Corp"], "fields": "{fields}" }
  }
}
```

Notes:
- `use.primitive` must name a primitive the server already ships.
- Parameters are validated against `params` before interpolation.
- Interpolation is structured (typed substitution into primitive inputs), never string concatenation into a shell, path, or query.

## Validation rules
- Reject any capability not in the loaded catalog.
- Reject any `use.primitive` not in the server's fixed primitive registry.
- Validate `params` against the declared schema before use.
- Interpolate parameters into primitive inputs structurally, with type checks, never as raw string substitution.

## The "one primitive for everything" question, answered honestly
Can the server be reduced to a single generic primitive?

- You can reduce the *server* to one dispatch loop plus a small, fixed set of *bounded* primitives, and push all variety into policy-page compositions. That achieves "never redeploy to add a capability."
- A literal single "do anything" primitive does not eliminate risk. It **relocates** the entire security boundary onto the policy page's integrity and delivery (see the security doc). If the policy store is truly owner-only and the page load is authenticated, that is internally consistent, but it becomes a single point of total compromise, and any primitive that can express "exec / arbitrary path" turns a policy bug or a parameter-injection into RCE.
- Recommended target: **one dispatch mechanism plus a few individually-audited, bounded primitives** (Bloomberg, file read/list clamped, pdf render, outlook send, and maybe an `http.fetch` to allowlisted hosts). That gives the "no redeploy for new capabilities" win while keeping each power small enough to reason about.
- Adding a new *primitive type* remains the rare, deliberate, reviewed action that does change the server. That boundary should stay human-reviewed on purpose.
