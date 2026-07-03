module ToolUp.Platform.Tests.InProcess.TenantCompositionPresetTests

open Expecto
open ToolUp.Platform

// ─── Phase 302 — per-tenant composition presets ──────────────────────
//
// Covers the acceptance shape: two tenants resolve to distinct composition
// variants from one base preset + per-tenant hole bindings; bindings are
// scope-isolated (a tenant never observes another's); a tenant whose preset
// leaves a required hole unbound fails preflight readably (not at first
// request); a fully-bound tenant composes exactly the equivalent app.

let private coreModule () : ServerModule = ServerModule.create "Core"
let private euModule () : ServerModule = ServerModule.create "EU"
let private usModule () : ServerModule = ServerModule.create "US"

// A catalogue registering the base module + both tenants' region modules.
let private catalogue () : RegistrationCatalogue =
    RegistrationCatalogue.empty
    |> RegistrationCatalogue.addModule (coreModule ())
    |> RegistrationCatalogue.addModule (euModule ())
    |> RegistrationCatalogue.addModule (usModule ())

// The shared base preset: Core fixed, a "region" hole every tenant fills.
let private basePreset () : CompositionDescriptor =
    CompositionDescriptor.create [ CompositionDescriptor.select (ComponentId.ofModule "Core") ] ServerConfig.defaults
    |> CompositionDescriptor.withHoles [ "region" ]

// Two tenants filling the "region" hole with different modules.
let private preset () : TenantCompositionPreset =
    TenantCompositionPreset.create (basePreset ())
    |> TenantCompositionPreset.withTenant
        "acme"
        (TenantComposition.empty
         |> TenantComposition.bindHole "region" [ CompositionDescriptor.select (ComponentId.ofModule "EU") ])
    |> TenantCompositionPreset.withTenant
        "globex"
        (TenantComposition.empty
         |> TenantComposition.bindHole "region" [ CompositionDescriptor.select (ComponentId.ofModule "US") ])

let private moduleIds (app: ServerApp) : Set<ComponentId> =
    ServerApp.compositionManifest app |> _.Modules |> List.map _.Id |> Set.ofList

let tests =
    testList "TenantCompositionPreset" [

        // ── two tenants → distinct variants from one base preset ──────
        testCase "two tenants resolve to distinct composition variants from one base preset"
        <| fun _ ->
            let cat = catalogue ()
            let p = preset ()

            let acme =
                match TenantCompositionPreset.resolve cat "acme" p with
                | Ok app -> app
                | Error e -> failtestf "acme failed to resolve: %s" (TenantCompositionPreset.renderError e)

            let globex =
                match TenantCompositionPreset.resolve cat "globex" p with
                | Ok app -> app
                | Error e -> failtestf "globex failed to resolve: %s" (TenantCompositionPreset.renderError e)

            Expect.equal
                (moduleIds acme)
                (Set.ofList [ ComponentId.ofModule "Core"; ComponentId.ofModule "EU" ])
                "acme composes Core + EU"

            Expect.equal
                (moduleIds globex)
                (Set.ofList [ ComponentId.ofModule "Core"; ComponentId.ofModule "US" ])
                "globex composes Core + US"

            Expect.notEqual (moduleIds acme) (moduleIds globex) "the two tenants compose distinct variants"

        // ── bindings are scope-isolated (GP 4) ────────────────────────
        testCase "a tenant's resolution never observes another tenant's bindings"
        <| fun _ ->
            let p = preset ()

            let acmeDescriptor =
                match TenantCompositionPreset.resolveDescriptor "acme" p with
                | Ok d -> d
                | Error e -> failtestf "acme descriptor: %s" (TenantCompositionPreset.renderError e)

            let acmeIds =
                CompositionDescriptor.effectiveComponentIds acmeDescriptor |> Set.ofList

            Expect.contains (Set.toList acmeIds) (ComponentId.ofModule "EU") "acme's own binding is present"

            Expect.isFalse
                (Set.contains (ComponentId.ofModule "US") acmeIds)
                "globex's binding does not leak into acme's resolved composition"

        // ── unbound required hole fails preflight readably ────────────
        testCase "a tenant whose preset leaves a required hole unbound fails preflight, naming it"
        <| fun _ ->
            // A tenant registered with no binding for the "region" hole.
            let p =
                TenantCompositionPreset.create (basePreset ())
                |> TenantCompositionPreset.withTenant "starter" TenantComposition.empty

            match TenantCompositionPreset.preflight (catalogue ()) "starter" p with
            | Ok() -> failtest "expected preflight to fail for an unbound required hole"
            | Error message ->
                Expect.stringContains message "region" "the readable preflight error names the unbound hole"
                Expect.stringContains message "starter" "the error names the offending tenant"

        // ── the same failure surfaces as CompositionInvalid data ──────
        testCase "resolving a tenant with an unbound hole yields CompositionInvalid (UnfilledHoles)"
        <| fun _ ->
            let p =
                TenantCompositionPreset.create (basePreset ())
                |> TenantCompositionPreset.withTenant "starter" TenantComposition.empty

            match TenantCompositionPreset.resolve (catalogue ()) "starter" p with
            | Error(TenantCompositionInvalid("starter", UnfilledHoles [ "region" ])) -> ()
            | other -> failtestf "expected TenantCompositionInvalid (UnfilledHoles [region]), got %A" other

        // ── unknown tenant fails readably ─────────────────────────────
        testCase "resolving an unregistered tenant fails with UnknownTenant"
        <| fun _ ->
            match TenantCompositionPreset.resolve (catalogue ()) "ghost" (preset ()) with
            | Error(TenantNotRegistered "ghost") -> ()
            | other -> failtestf "expected TenantNotRegistered ghost, got %A" other

        // ── a fully-bound tenant composes the equivalent app (GP 11) ──
        testCase "a fully-bound tenant composes exactly the equivalent direct app"
        <| fun _ ->
            let acme =
                match TenantCompositionPreset.resolve (catalogue ()) "acme" (preset ()) with
                | Ok app -> app
                | Error e -> failtestf "%s" (TenantCompositionPreset.renderError e)

            let direct = ServerApp.empty |> ServerApp.addModules [ coreModule (); euModule () ]

            Expect.equal (moduleIds acme) (moduleIds direct) "the preset-composed tenant matches a directly-built app"

        // ── per-tenant config yields a preflight validator (Phase 289) ─
        testCase "tenantConfigValidator produces a Phase 289 validator for a known tenant, None otherwise"
        <| fun _ ->
            let p =
                TenantCompositionPreset.create (basePreset ())
                |> TenantCompositionPreset.withTenant
                    "acme"
                    (TenantComposition.empty
                     |> TenantComposition.bindHole "region" [ CompositionDescriptor.select (ComponentId.ofModule "EU") ]
                     |> TenantComposition.withConfig [
                         ComponentConfig.create (ComponentId.ofModule "Core") [ "tier", "gold" ]
                     ])

            match TenantCompositionPreset.tenantConfigValidator "acme" p with
            | Some validator ->
                Expect.equal validator.Name "component-config-overrides" "the Phase 289 override validator"
            | None -> failtest "expected a config validator for a known tenant"

            Expect.isNone (TenantCompositionPreset.tenantConfigValidator "ghost" p) "no validator for an unknown tenant"
    ]