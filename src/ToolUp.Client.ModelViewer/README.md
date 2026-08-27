# ToolUp.Client.ModelViewer

A typed Feliz binding to Google's [`<model-viewer>`](https://modelviewer.dev/) web
component for inline 3D display in ToolUp.Platform module views: glTF / GLB
rendering with camera controls (orbit / zoom / auto-rotate), poster + loading
states, exposure / environment-image control, AR attribute passthrough, and typed
`load` / `error` / `progress` events.

Deliberately a **display** binding: section planes, measurement overlays and
custom interaction belong to a fuller three.js binding that is out of scope until
a concrete consumer demands interaction.

## Quick start

```fsharp skip=fragment
open ToolUp.Client.ModelViewer

ModelViewer.viewer "/assets/turbine.glb" "Wind turbine assembly, rotatable" [
    modelViewer.cameraControls true
    modelViewer.autoRotate true
    modelViewer.poster "/assets/turbine-poster.webp"
    modelViewer.cameraOrbit "45deg 55deg 2.5m"
    modelViewer.exposure 1.0
    modelViewer.ar true
    modelViewer.arModes [ ArMode.WebXr; ArMode.SceneViewer; ArMode.QuickLook ]
    modelViewer.style [ style.width (length.px 480); style.height (length.px 360) ]
    modelViewer.onLoad (fun _ -> dispatch ModelReady)
    modelViewer.onError (fun e -> dispatch (ModelFailed e.ErrorType))
]
```

Add the npm dependency to the consuming client app's `package.json` (it is
declared only in this companion — `ToolUp.Platform.Client` never references it):

```json
"dependencies": { "@google/model-viewer": "^4.1.0" }
```

A worked example lives at `samples/ModelViewerDemo/`.

## Component registration

Importing `@google/model-viewer` registers the custom element with the browser.
The binding does this itself — `ModelViewer.fs` carries the side-effect import,
which Fable hoists to the top of the compiled module — so registration happens
once per app, automatically, and **only when the companion is composed**: the
import enters the bundle graph only when consumer code references the binding.
Apps that don't reference it ship zero additional script weight (verified by the
strip check: `samples/MinimalClient`'s Fable output contains no model-viewer
import; `samples/ModelViewerDemo`'s does).

## Accessibility

`alt` is **required by construction** — it is the second argument of
`ModelViewer.viewer`, not an optional prop. Write it like img alt text for a
visual object ("Wind turbine assembly, rotatable"), and prefer mentioning that
the model can be manipulated when `cameraControls` is on.

## Poster → reveal lifecycle (skeleton states)

The component shows `poster` until the model is ready, then reveals per the
`reveal` attribute (`Auto` — as soon as ready; `Interaction` — on user input;
`Manual` — when dismissed programmatically). For skeleton states consistent with
platform conventions: drive a module-level loading flag from
`modelViewer.onProgress` (download fraction 0.0–1.0) and flip it on
`modelViewer.onLoad`; `onError` carries the typed failure kind
(`"loadfailure"` / `"webglcontextlost"`).

## Serving the assets

glTF / GLB files are ordinary static assets — serve them from `IAssetStore` or
any static route. Two operational notes:

- **MIME type**: serve `.glb` as `model/gltf-binary` (and `.gltf` as
  `model/gltf+json`). Misconfigured static hosts that fall back to
  `application/octet-stream` usually still load, but correct types keep
  download heuristics and CDN compression behaving.
- The component fetches the model with `fetch` from the page origin —
  cross-origin asset hosts need CORS plus a `connect-src` entry (below).

## CSP implications

`<model-viewer>` creates **Web Workers from `blob:` URLs** and uses `blob:` /
`data:` image URLs internally. Against the SDK's CSP hardening (the
`ICspContributor` aggregation in `ToolUp.Platform.Server`):

- `img-src` already carries `data: blob:` in the SDK baseline — no change needed.
- **`worker-src blob:` is required** (the SDK baseline emits no `worker-src`
  directive, so workers would otherwise fall back to `default-src 'self'` and the
  blob worker is blocked). Register the first-party `BlobWorkerCspContributor`
  on the server composition to widen the policy to `worker-src 'self' blob:`:

  ```fsharp skip=fragment
  app |> ServerApp.withCspContributor (BlobWorkerCspContributor())
  ```

  Older Safari (no `worker-src` support) additionally needs `child-src blob:`,
  which has no SDK lever — add it via a policy override only when targeting those
  browsers.
- Models served from another origin additionally need that origin in
  `connect-src` (the `ICspContributor` `ConnectSrc` case covers this).

## Forward compatibility

New component attributes don't need a binding release — pass them through the
escape hatch:

```fsharp
modelViewer.custom ("shadow-intensity", "1")
```

## Licence note (GP 2)

`@google/model-viewer` is **Apache-2.0** — free, no paid tier involved.
Verified at adoption (Phase 125); the dependency is isolated to this companion
per GP 1.
