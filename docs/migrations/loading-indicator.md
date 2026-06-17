# LoadingIndicator — configurable data-loading surface

**Forge commits:** `e6f3420` (DU + `ClientConfig.LoadingIndicator` field + shell
call site), `4629bdc` (Layout resolver + brand/spinner icons), `c0635f8`
(module-facing React context + Data Manager worked example)

## What changes

The shell's loading surface — previously a hard-coded gray-pulse skeleton — is
now a configurable knob on `ClientConfig`, and is reachable from module views via
a React context. Purely additive: the default reproduces the 0.5.16 skeleton, so
no consumer must act.

- **`ClientConfig.LoadingIndicator: LoadingIndicatorMode`** drives the shell's
  boot-time Prefetching surface:

  ```fsharp
  type LoadingIndicatorMode =
      | SkeletonLoader                       // default — the unchanged 0.5.16 gray-pulse skeleton
      | BrandMarkLoader                      // animated ToolUp mark (Icons.dataLoading, colour-cycling)
      | SpinnerLoader                        // neutral currentColor rotating-arc spinner
      | CustomLoader of (unit -> ReactElement)
  ```

- **`LoadingIndicatorContext`** (Client) — the shell mounts a provider from
  `config.LoadingIndicator`; module components read the resolved in-content
  element via the `useIndicator` hook. Mirrors the `FeatureFlags` /
  `ProcessedDataContext` pattern (the indicator can't live on the Core-tier
  `ClientModuleContext`, which has no Feliz dependency).
- **`Layout.loadingIndicatorInline`** — an in-panel variant: `SkeletonLoader`
  degrades to a compact stacked-pulse, brand/spinner centre at a smaller size.
- **`Icons.dataLoading`** (animated brand mark; SMIL `<animate>` gradient so it
  survives the `vite-plugin-svgr` → SVGO pipeline) + **`Icons.spinner`**.
- **Data Manager (`FileManagerUI`) worked example** — the "Uploaded Files" panel
  now renders the configured indicator while `ListFiles` is in flight, instead
  of flashing "No files uploaded yet." (which was indistinguishable from a
  genuinely-empty scope).

## Diff to apply

**Additive — existing consumers need no change.** The `SkeletonLoader` default
+ the context default preserve existing behaviour exactly (GP 11).

A deployment that wants the brand mark sets one field:

```fsharp
open ToolUp.Platform

let clientConfig =
    { ClientConfig.defaults with
        LoadingIndicator = BrandMarkLoader }      // or SpinnerLoader / CustomLoader (fun () -> myEl)
```

A **module** opts its own loading state into the configured indicator with the
same idiom as feature flags:

```fsharp
open ToolUp.Platform

[<ReactComponent>]
let MyPanelBody (model: Model) =
    if model.IsLoading then LoadingIndicatorContext.useIndicator ()
    else Html.div [ (* loaded content *) ]
```

## Verification steps

1. `dotnet build ToolUp.Forge.sln` — additive; build stays green.
2. `cd samples/MinimalClient && dotnet fable -o output --noCache` — Fable
   client tier compiles unchanged.
3. With `LoadingIndicator = SkeletonLoader` (default), confirm the boot-time
   Prefetching surface is byte-identical to 0.5.16.
4. With `BrandMarkLoader`, confirm the animated mark renders at boot and inside
   the Data Manager "Uploaded Files" panel while files are loading; confirm the
   gradient animates (the SMIL `<animate>` survives the SVGO pipeline).

## Rollback

Additive throughout. Revert the three forge commits; consumers that never set
`ClientConfig.LoadingIndicator` away from `SkeletonLoader` and never call
`LoadingIndicatorContext.useIndicator` are unaffected.
