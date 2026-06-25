module ToolUp.Platform.Tests.InProcess.FlagSourceTests

open System
open System.Collections.Generic
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.FeatureFlagStore
open OpenFeature
open OpenFeature.Providers.Memory
open ToolUp.FeatureFlagProviders.OpenFeature

// ─── Phase 239 — IFlagSource seam + OpenFeature companion ────────────
//
// Proves the read-only external flag-resolution layer: a source is
// consulted only when no in-process scope set the key, and before the
// declared default (additive — empty sources ≡ pre-239); a store
// override still wins; type-aware bool/variant; and the OpenFeature
// adapter resolves through an OpenFeature provider, deferring on unknown.

let private makeStore () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-flagsource-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    let storage = LocalFileStorage.LocalFileStorage(dir) :> IBlobStorage
    BlobFeatureFlagStore(storage) :> IFeatureFlagStore

let private boolFlag key dflt = {
    Key = key
    DefaultValue = FlagValue.Bool dflt
    Description = "test flag"
    Owner = None
}

let private variantFlag key options dflt = {
    Key = key
    DefaultValue = FlagValue.Variant(options, dflt)
    Description = "test variant flag"
    Owner = None
}

let private stubSource (resolve: FeatureFlag -> FlagValue option) =
    { new IFlagSource with
        member _.Resolve(flag, _ctx) = async { return resolve flag }
    }

let private ctx = AccessContext.unrestricted (Subject.AuthenticatedUser "alice")

let tests =
    testList "FlagSource (Phase 239)" [
        testCaseAsync "source consulted when no in-process override"
        <| async {
            let store = makeStore ()
            let decl = [ boolFlag "ext.f" false ] // declared default false

            let src =
                stubSource (fun f -> if f.Key = "ext.f" then Some(FlagValue.Bool true) else None)

            let eval = FlagEvaluator.createWithFlagSources store decl [ src ] None
            let! v = eval.IsEnabled "ext.f" ctx
            Expect.isTrue v "source (true) beats declared default (false)"
        }

        testCaseAsync "store override wins over source"
        <| async {
            let store = makeStore ()

            match! store.SetFlag(FlagScope.Platform, "ext.f", FlagValue.Bool false) with
            | Ok() -> ()
            | Error e -> failtestf "SetFlag failed: %s" e

            let decl = [ boolFlag "ext.f" false ]
            let src = stubSource (fun _ -> Some(FlagValue.Bool true))
            let eval = FlagEvaluator.createWithFlagSources store decl [ src ] None
            let! v = eval.IsEnabled "ext.f" ctx
            Expect.isFalse v "store override (false) beats source (true)"
        }

        testCaseAsync "no sources → declared default (additive equivalence)"
        <| async {
            let store = makeStore ()

            let eval =
                FlagEvaluator.createWithFlagSources store [ boolFlag "ext.f" true ] [] None

            let! v = eval.IsEnabled "ext.f" ctx
            Expect.isTrue v "declared default flows through with no sources"
        }

        testCaseAsync "source that defers (None) falls to declared default"
        <| async {
            let store = makeStore ()
            let src = stubSource (fun _ -> None)

            let eval =
                FlagEvaluator.createWithFlagSources store [ boolFlag "ext.f" true ] [ src ] None

            let! v = eval.IsEnabled "ext.f" ctx
            Expect.isTrue v "defer → declared default true"
        }

        testCaseAsync "source resolves a variant flag"
        <| async {
            let store = makeStore ()
            let decl = [ variantFlag "ext.theme" [ "light"; "dark" ] "light" ]
            let src = stubSource (fun _ -> Some(FlagValue.Variant([ "light"; "dark" ], "dark")))
            let eval = FlagEvaluator.createWithFlagSources store decl [ src ] None
            let! v = eval.ResolveVariant "ext.theme" ctx
            Expect.equal v "dark" "source variant resolved over declared default"
        }

        testCaseAsync "OpenFeature adapter resolves via a provider + defers on unknown"
        <| async {
            let variants = Dictionary<string, bool>(dict [ "on", true; "off", false ])

            let flags: IDictionary<string, Flag> =
                dict [ "ff.on", (Flag<bool>(variants, "on") :> Flag) ]

            do! Api.Instance.SetProviderAsync(InMemoryProvider flags) |> Async.AwaitTask
            let source = OpenFeatureFlagSource() :> IFlagSource
            let! known = source.Resolve(boolFlag "ff.on" false, ctx)
            Expect.equal known (Some(FlagValue.Bool true)) "OpenFeature resolves the known flag"
            let! unknown = source.Resolve(boolFlag "ff.absent" false, ctx)
            Expect.equal unknown None "unknown flag defers to the declared default"
        }

        // ─── Phase 239 follow-on — companion health probe + preflight ───
        // Both key off the process-wide `Api.Instance` provider metadata
        // (the only public readiness signal in OpenFeature .NET 2.3.0).
        // `ShutdownAsync ()` resets `Api.Instance` to the built-in No-op
        // provider — the deterministic "composed but not wired" state.

        testCaseAsync "health probe is Degraded when no external provider is registered"
        <| async {
            do! Api.Instance.ShutdownAsync() |> Async.AwaitTask // → No-op provider
            let probe = Health.create ()
            let! result = probe.Check()

            match result with
            | HealthChecks.Degraded _ -> ()
            | other -> failtestf "expected Degraded for the unwired No-op provider, got %A" other
        }

        testCaseAsync "health probe is Healthy when an external provider is registered"
        <| async {
            let flags: IDictionary<string, Flag> =
                dict [
                    "ff.h", (Flag<bool>(Dictionary<string, bool>(dict [ "on", true ]), "on") :> Flag)
                ]

            do! Api.Instance.SetProviderAsync(InMemoryProvider flags) |> Async.AwaitTask
            let probe = Health.create ()
            let! result = probe.Check()
            Expect.equal result HealthChecks.Healthy "a registered provider is Healthy"
        }

        testCaseAsync "validator Warns (does not abort) when no external provider is registered"
        <| async {
            do! Api.Instance.ShutdownAsync() |> Async.AwaitTask // → No-op provider
            let validator = Validator.create ()
            let! result = validator.Validate()

            match result with
            | ConfigValidation.Warning _ -> ()
            | other -> failtestf "expected Warning (never Error) for the unwired companion, got %A" other
        }

        testCaseAsync "validator is Ok when an external provider is registered"
        <| async {
            let flags: IDictionary<string, Flag> =
                dict [
                    "ff.v", (Flag<bool>(Dictionary<string, bool>(dict [ "on", true ]), "on") :> Flag)
                ]

            do! Api.Instance.SetProviderAsync(InMemoryProvider flags) |> Async.AwaitTask
            let validator = Validator.create ()
            let! result = validator.Validate()
            Expect.equal result ConfigValidation.Ok "a registered provider passes preflight"
        }
    ]