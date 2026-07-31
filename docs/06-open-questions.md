# 06 - Open Questions

Decisions still to make. Each one shapes the build.

## 1. The single-primitive ambition
How minimal do we make the server? Options:
- (a) One dispatch plus a small bounded primitive set (recommended). Boundary split between server primitives and policy integrity.
- (b) Near-single generic primitive driven entirely by the policy page. Boundary relocated almost entirely onto policy-page integrity and authenticity. Only acceptable with a hardened, authenticated, owner-only policy store and no exec-class primitive.

## 2. Where does the policy page live, and how is its integrity guaranteed?
- Which locked-down location (owner-only ACL)?
- How does the server authenticate it on load (trusted local path? signature? pinned origin?) so it cannot be swapped or spoofed?

## 3. Shell model: SPA or shell + iframes?
- SPA: smoothest app feel; all tools share one origin and bundle.
- Shell + iframes (currently leaning this way): each tool stays a standalone page on the share, openable independently; looser coupling; the broker enforces capability access across the frame boundary.

## 4. Launcher: raw directory listing, manifest, or both?
- Manifest (`apps.json`) gives titles, icons, routes, and control. Recommended.
- Raw listing as a fallback for "drop a file, it appears."

## 5. CORS and auth specifics
- Exact allowlisted origin(s) for the shell.
- Whether to add a session token, and how the shell obtains it.

## 6. Migration of existing handlers
- Bloomberg is already generic. Excel, Outlook, and PDF may not be. Normalizing all under the envelope and primitive model is a project, not a quick change. Sequence it.

## 7. Relationship to the existing LocalGateway
- Is Plugboard a refactor of LocalGateway in place, or a new server that absorbs its handlers as primitives?
- The LocalGateway self-heal work (session auto-recovery, uniform `/status`) already moves in this direction and can be carried over.

## 8. Plugin packaging: folder now, zip installer later (TODO)
The unit of a plugin is a **folder** (entry DLL + its full dependency closure +
optional `plugin.json`). The host loads folders directly; `tools/build-plugins.ps1`
produces them. Still to build: a single-file **install artifact** for the lite
"install-and-done" flow - zip a plugin folder (+ manifest at root) into Plugboard's
own bundle format, and give the host an "install extension"
action that unzips it into a plugins dir. Loading stays folder-based; the zip is only
the transport. Defer until one-file distribution is actually needed.
