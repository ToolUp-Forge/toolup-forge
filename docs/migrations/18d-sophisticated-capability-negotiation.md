# Migration — Phase 18d: sophisticated capability negotiation

**Status:** additive, opt-in. The existing `GET /peer/v1/capabilities` handshake and the per-call dispatch path are byte-for-byte unchanged; nothing is required of a consumer that does not opt in.

## What changes

`ToolUp.InterPlatform` gains a **per-method capability profile** layer beside the bare `CapabilityList`. A receiver can declare each method's lifecycle (`Active` / `Deprecated` with a sunset window / `Removed`) per contract version; a caller negotiates an individual method at handshake time and learns of deprecation or removal *before* it calls — instead of hitting a runtime `PeerMethodNotFound`.

New public surface (all in `ToolUp.InterPlatform`):

- `DeprecationNotice` / `MethodStatus` (`Active | Deprecated | Removed`) / `MethodProfile` / `ContractVersionProfile` / `ContractProfile` / `PeerProfile` — the profile types.
- `MethodResolution` / `MethodNegotiationError` — the negotiation outcome.
- `PeerCapabilityNegotiation` module — `methodStatusAt`, `negotiate`, `fromCapabilityList`, and the author-facing `profileFor<'TApi>` reflection helper.
- `IPeerProfileProvider` / `DefaultPeerProfileProvider` — aggregates declared profiles over the live capability table.
- `IPeerHandshake.NegotiateMethod` + `IPeerHandshake.LocalProfile` — new interface members (the only implementor, `InMemoryPeerHandshake`, gains a 4-arg constructor; consumers compose it through `PeerServerApp.run`, not directly).
- `PeerServerApp.withContractProfile` — the compose opt-in (+ a new `ContractProfiles` field, defaulting `[]`).
- New route `GET /peer/v1/capabilities/profile`.

## Diff to apply

### Receiver — declare a method lifecycle profile

```diff
+let v1, v2, v3 = { Major = 1; Minor = 0 }, { Major = 2; Minor = 0 }, { Major = 3; Minor = 0 }
+
+let directoryProfile =
+    PeerCapabilityNegotiation.profileFor<DirectoryContract> "directory" [ v1; v2 ] [
+        "GetCapabilities", v2, Deprecated { DeprecatedSince = v2; RemovedIn = Some v3; Note = "use ListContracts" }
+    ]
+
 PeerServerApp.create ()
 |> PeerServerApp.withConfig serverConfig
 |> PeerServerApp.withContract directoryHost
+|> PeerServerApp.withContractProfile directoryProfile
 |> PeerServerApp.run
```

### Caller — negotiate a method

```diff
+match! handshake.NegotiateMethod(target, "directory", "GetCapabilities") with
+| Ok res ->
+    match res.Status with
+    | Active -> () // safe to call at res.Version
+    | Deprecated notice -> logger.Warn $"deprecated (removed in {notice.RemovedIn}): {notice.Note}"
+    | Removed notice -> failwith $"method removed: {notice.Note}"
+| Error (RemoteProfileUnavailable e) -> () // remote unreachable
+| Error e -> () // ContractNotAdvertised / MethodNotAdvertised / NoMutualContractVersion
```

A receiver that declares no profile still advertises versions-only profiles (no per-method lifecycle); a caller against a pre-18d peer degrades via the bare `CapabilityList` (all methods `Active`).

## Verification

- `dotnet build ToolUp.Forge.sln` — clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "IPeerCapabilityNegotiation"` — 16 passed, 0 failed. Covers `profileFor` auto-population + overlay, `methodStatusAt`, per-method resolution of `Active` / `Deprecated` (sunset window) / `Removed`, version-specific resolution (a method `Deprecated` in v2 is `Active` when v1 is the mutual version), the `ContractNotAdvertised` / `MethodNotAdvertised` / `NoMutualContractVersion` error paths, `fromCapabilityList` degradation, and the handshake `NegotiateMethod` happy path + `RemoteProfileUnavailable` wrapping.
- Additivity check: `GET /peer/v1/capabilities` is unchanged; a deployment that never calls `withContractProfile` serves versions-only profiles; `NoPeerSubstrate` deployments mount no peer routes (GP 13).

## Rollback

Remove the `withContractProfile` line on the receiver and the `NegotiateMethod` call on the caller. The `/peer/v1/capabilities/profile` route remains mounted (it answers versions-only profiles, harmless) but is never queried. 18d writes no persisted state — it only declares + reads in-memory profile data — so there is nothing to clean up.
