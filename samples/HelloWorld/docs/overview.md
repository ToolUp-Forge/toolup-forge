# HelloWorld overview

HelloWorld is the canonical minimal ToolUp sample. It shows the smallest useful
consumer shape: one domain module wired into a composition root.

## What it does

The app exposes a single API — `HelloWorldApi.Echo` — which echoes a request
string back through a pure routine. There is no database, no auth, and no
background work; the point is to show the wiring, not a feature.

## Layout

- `HelloWorld.Module` — the four-file module: `SharedTypes` (the `HelloWorldApi`
  contract), `Server` (the pure `echoRoutine`), and the client-tier
  `ClientModel` / `ClientView` / `Icons` files.
- `HelloWorld.Server` — the composition root that registers the module and runs
  the server.

These docs are themselves the corpus for the static-corpus retrieval example
(see `wiring.md`): the sample's documentation explains the sample, and a
docs-aware assistant answers questions about it from this folder.
