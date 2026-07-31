# 05 - Capabilities: Primitives vs Compositions

## Two concepts, kept strictly separate
- **Primitive:** a bounded power implemented in the server. Fixed code. The security boundary. Examples: `bloomberg.bdp`, `file.read` (clamped), `pdf.render`, `outlook.send`.
- **Composition (capability):** a named, parameterized arrangement of primitives, declared in the policy page. Data, not code. Example: `offering.quote` = `bloomberg.bdp` with a particular security/field mapping.

## Adding a capability (the common case): no redeploy
1. Edit the policy page (owner-only).
2. Add a capability descriptor that composes existing primitives.
3. Done. The server picks up the new catalog; tool pages can request it.

## Adding a primitive (the rare case): redeploy, reviewed
1. A new kind of power, not expressible by existing primitives.
2. Implement it in the server as a bounded primitive (typed inputs, no arbitrary exec/path).
3. Security review. This is the moment new power enters the system.
4. Rebuild and redeploy the gateway. Deliberately uncommon.

## Boundedness requirements (every primitive must satisfy)
- Typed, schema-validated inputs.
- No raw shell or command interpolation. If a process must run, it comes from a fixed executable allowlist with arguments passed as a vector, not a string.
- File access clamped to a configured root; reject path escapes.
- Network access (if any) restricted to an allowlisted host set.
- Returns the uniform envelope; never leaks raw internal errors as data.

## Suggested starting primitive set (to confirm)
- `bloomberg.bdp` / `bds` / `bdh` (already generic today)
- `file.list` / `file.read` (clamped to share roots)
- `pdf.render`
- `outlook.send` (consider an extra confirmation/guard given it sends mail)
- possibly `http.fetch` (allowlisted hosts only). Powerful; add only if needed.

The presence or absence of anything resembling `exec` is the single most important line-item in this list.
