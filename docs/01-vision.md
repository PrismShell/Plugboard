# 01 - Vision and Motivation

## The goal
Never deal with deployment. Ship internal browser tools (pricing sheets, dashboards) without standing up, patching, or redeploying a web server, and without a build/release step for every change.

## The road here
1. Tools were opened directly from a network share as `file://` documents.
2. `file://` documents are treated as unique, opaque origins by the browser. That blocks `fetch()`/XHR of other local files, makes `localStorage` unreliable, and breaks anything resembling a single-page app. Observed in practice: `Unsafe attempt to load URL file://... 'file:' URLs are treated as unique security origins.`
3. Full-page navigation between linked HTML files DOES work under `file://`, so a multi-page app is viable, but it is limited and awkward.
4. Key realization: a local server already runs on every desk machine (the LocalGateway, ASP.NET on `localhost:9194`, used to reach Bloomberg/Excel/Outlook). It already serves files and already accepts cross-origin calls.
5. If that server also serves the tool pages, they load from `http://localhost:9194`, a real and stable origin. Every `file://` limitation disappears: fetch works, storage works, an SPA shell works. And there is still no *separate* server to deploy, because it is the server that is already running.

## The window
A stable origin is the "window" the whole design hangs on. With it, the browser becomes a capable app host: persistent shell, client-side state machine, history, storage, frame messaging. The server does not need to hold any of that.

## The aspiration that defines Plugboard
Push it further: make the server so generic it never changes.
- The set of *capabilities* the system exposes is described in one **policy page** kept in a location only the owner can modify.
- The server loads that policy, serves tool pages from a separate (less privileged) share, and brokers requests from tool pages against the policy.
- Adding a capability becomes: edit the policy page. No redeploy.
- Adding a brand-new *kind of power* (a new primitive) is the rare exception that does touch the server.

This document set specifies that architecture and, importantly, where its security actually lives.
