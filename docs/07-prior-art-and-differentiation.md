# 07 - Prior Art and the Path to Differentiation

An honest map of what already exists, and where genuine differentiation could live. This is a working document, not a claim of novelty. Any real novelty or patentability assessment requires a formal prior-art search and a patent attorney. Treat everything here as engineering judgment, and note that the IP question does not gate implementation.

## Prior art (the lineage to be clear-eyed about)
- **Object-capability security and reference monitors:** KeyKOS, EROS, the E language, Capsicum. A trusted mediator grants narrow, unforgeable authorities. OS and language level.
- **Capability attenuation and tokens:** macaroons (caveats that can only narrow authority); OAuth scopes.
- **Zero Trust Network Access:** Zscaler, Cloudflare Access, Tailscale. A control plane defines policy, a local client enforces. Security-first.
- **Browser native messaging hosts:** Chrome/Edge let a web extension talk to a locally installed native executable, governed by a manifest of allowed extension IDs, enterprise-managed by group policy. The closest existing mechanism to "policy controls which web code may invoke local native execution."
- **Single-purpose localhost agents:** Plaid, Zoom, Dropbox, Ledger/Trezor bridges. The browser intentionally invokes local execution, but per-vendor and single-purpose.
- **Policy engines:** Open Policy Agent and control planes. Policy decision, not capability provision or execution.

The building blocks all exist. What is not sitting on a shelf is the *assembled* product described in this repo.

## Where differentiation actually lives (the less-trodden combination)
None of these is individually unprecedented. The combination, aimed at this design center, is where the distinctiveness sits.

1. **Extension-free, composable capabilities served to plain web pages.** Native messaging needs a browser extension and is point-to-point per host. Plugboard serves a *composable* capability catalog to ordinary web pages via a localhost origin plus a postMessage broker, no extension. Tools bind at runtime.
2. **Self-describing, runtime-bound catalog.** `GET /capabilities` lets a tool page discover and bind capabilities at load time, so a new tool needs zero server change.
3. **Strict define/reference split across a trust boundary.** The trusted policy server *defines* capabilities; untrusted tool pages can only *reference* them by name with constrained params. The gateway never executes anything not pre-composed by trusted policy. Object-capability confinement applied at the browser/local-gateway layer.
4. **Two-tier extensibility with a human-reviewed primitive boundary.** Compositions are data (no redeploy). Primitives are code (rare, reviewed). New power entering the system is always a deliberate, reviewed act.

## The candidate "truly novel" core (a direction to pursue, not a claim)
If there is a defensible, genuinely distinctive mechanism here, the most promising direction is the combination of:

- **Capability attenuation at the browser/local-gateway boundary.** A tool page can only request a *narrower* form of a capability it was granted, never broaden it, enforced by the broker. Borrows from ocap and macaroons, applied in a context (browser to local resources, internal) where it is not the norm.
- **Tamper-evident policy that mechanically closes the policy-integrity gap.** Doc `03` names the central residual risk: the whole security collapses onto the integrity of the policy. A signed, verifiable capability manifest (the gateway verifies a signature from the on-prem policy authority before honoring any capability) turns "trust the policy store's ACL" into "verify the policy cryptographically." Signed manifests are prior art in general; using them to make a browser-facing local capability broker tamper-evident, so the broker can safely consume policy it did not author, is the less-trodden move.
- **All of the above with the extension-free, self-describing, runtime-bound serving of items 1-3.**

Honest framing: each ingredient has lineage. The *combination*, in the *capability-first, internal, browser/local* design center, is what is distinctive, and possibly novel. Whether it rises to patentable non-obviousness is a question for a prior-art search and a patent attorney, and it does not change the decision to implement.

## Why these two mechanisms matter even if novelty does not pan out
Attenuation and signed policy are not just differentiation. They are exactly the hardening that closes the worst residual risks in `03`:
- Attenuation contains a compromised or careless tool page: it cannot widen its authority.
- Signed policy removes the "policy store ACL is the entire security boundary" single point of failure.

So they are worth building on their own merits. Any novelty is a bonus on top of better security.

## What to do with this
Implementation proceeds regardless of the IP question. Build the minimal version (`03` mitigations + `04` protocol + one bounded primitive + iframe broker), then decide whether to invest in the attenuation and signed-policy mechanisms above, which are where both the security hardening and any distinctiveness come from.
