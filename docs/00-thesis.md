# 00 - Design Thesis: Capability-First, Internal, On-Prem

This is the headline idea. Everything else in the repo serves it.

## Capability-first vs security-first
Existing brokers in this space (Zero Trust Network Access: Zscaler, Cloudflare Access, Tailscale) are **security-first**. They assume the thing on the far end is a resource to protect, so the whole design optimizes for deny-by-default gatekeeping: block, throttle, inspect, permit a connection. The web client is a supplicant asking to reach something.

Plugboard is **capability-first**. It assumes the web client is *deliberately invoking local execution*, and its job is to make exposing, composing, and safely running local capabilities easy. The design optimizes for "what capabilities do I offer, and how cheaply can a new tool bind to them," not "what do I forbid."

This is a difference in design center, not cosmetics. You would not build Plugboard by configuring a ZTNA product, because their mental model is forbiddance and Plugboard's is provision. Same reason you would not build a database by configuring a firewall.

## The internal, on-prem unlock
Intentional browser-to-local execution is unthinkable on the open internet: it is remote code execution as a service, and no client would accept it. Inside a controlled environment the trust boundary already exists, so the same move goes from unthinkable to useful.

Keeping the policy/capability server **on-prem and internal** (the client's own server, not a vendor cloud) is deliberate:
- No third-party breach target. The org defines capabilities for its own machines.
- No internet dependency in the request path.
- The "we become everyone's single point of compromise" failure mode of a hosted broker does not apply.

Cloud and internet-facing versions of this idea are both crowded and weaker. The strong version is local software plus an internal capability server.

## What this buys
- New tools ship as web pages dropped on a share. No deployment.
- New capabilities ship as compositions in the policy server. No redeploy.
- The browser is the app host; the server stays dumb and stateless.

## What it costs (kept honest)
- Internal softens, but does not delete, the security discipline. The threat model still includes a compromised internal tool page, a supply-chained page on the shared drive, and the insider. Bounded primitives, no exec-class primitive, and policy integrity still apply (see `03-security-model.md`).
- The security of the whole rests heavily on the integrity of the policy server and the policy it serves (see `03` and `07`).
