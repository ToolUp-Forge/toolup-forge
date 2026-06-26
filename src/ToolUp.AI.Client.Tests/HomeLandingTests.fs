module ToolUp.AI.Client.Tests.HomeLandingTests

open ToolUp.AI.Client.Tests.NodeTest
open Feliz
open ToolUp.Elmish
open ToolUp.Platform

// ─── Phase 171 — Home / Overview landing-selection tests ──────────────
//
// The landing module under test lives in `ToolUp.Platform.Client`
// (`Client/Home.fs` + the head-injection in `Client/SDK.Client.fs`
// `prepareModules`); the tests live in this harness because it is the
// established Fable-tier client test rig (the ProjectReference drives
// Platform.Client through the Fable compiler transitively, and only here
// does `ClientConfig.defaults` resolve — in-process it throws on the
// AG Grid Fable `import`; see the note in
// `ToolUp.Platform.Tests/InProcess/HomeOverviewTests.fs`).
//
// Pins the `prepareModules` head-injection contract behind
// `ClientConfig.HomeModule`:
//   - `EnabledHomeModule` + `ActiveModule = None` ⇒ Home is `modules[0]`,
//     so the shell's documented init rule ("land on `modules[0]` when
//     `ActiveModule = None`") makes `_sdk.home` the default surface.
//   - an explicit `ActiveModule` still wins (Home injection is
//     orthogonal — it stays head-injected for the sidebar).
//   - `NoHomeModule` (the default) ⇒ Home is absent entirely (GP 13).

/// Minimal app module with a chosen id — enough for `prepareModules`
/// ordering assertions (the view is never rendered here).
let private appModule (id: string) (name: string) : ErasedModule =
    ClientModule.create {
        Init = fun () -> 0, Cmd.none
        Update = fun (_: int) (m: int) -> m, Cmd.none
        Name = name
        Icon = Html.none
    }
    |> ClientModule.withView (fun _ _ -> Html.none, Html.none)
    |> ClientModule.withId id
    |> ClientModule.register

/// Mirror of `SDK.Client.init`'s documented landing rule (read-only
/// production, SDK.Client.fs ~lines 1001-1003): an explicit
/// `ActiveModule` wins; otherwise the shell lands on `modules[0]`.
let private landingId (config: ClientConfig) (prepared: ErasedModule list) : string =
    match config.ActiveModule with
    | Some id -> id
    | None -> prepared[0].Definition.Id

let private moduleIds (modules: ErasedModule list) =
    modules |> List.map (fun m -> m.Definition.Id)

let private apps = [ appModule "app.alpha" "Alpha"; appModule "app.beta" "Beta" ]

let tests =
    testList "Home landing selection (Phase 171)" [

        testCase "EnabledHomeModule + ActiveModule = None ⇒ Home is head and the landing surface"
        <| fun () ->
            let config = {
                ClientConfig.defaults with
                    HomeModule = EnabledHomeModule
                    ActiveModule = None
            }

            let prepared = Client.prepareModules config apps
            let ids = moduleIds prepared

            Expect.equal (List.head ids) "_sdk.home" "Home head-injected at modules[0]"
            Expect.equal (landingId config prepared) "_sdk.home" "shell lands on Home when ActiveModule unset"

        testCase "explicit ActiveModule wins — Home stays injected but is not the landing"
        <| fun () ->
            let config = {
                ClientConfig.defaults with
                    HomeModule = EnabledHomeModule
                    ActiveModule = Some "app.alpha"
            }

            let prepared = Client.prepareModules config apps
            let ids = moduleIds prepared

            // Home injection is orthogonal to ActiveModule: it is still
            // head-injected (so it renders as the leading sidebar entry),
            // but the explicit selection is the landing surface.
            Expect.equal (List.head ids) "_sdk.home" "Home still head-injected"
            Expect.isTrue (List.contains "app.alpha" ids) "the explicitly-selected module is present"
            Expect.equal (landingId config prepared) "app.alpha" "explicit ActiveModule wins over modules[0]"

        testCase "NoHomeModule (default) ⇒ Home absent entirely"
        <| fun () ->
            let config = {
                ClientConfig.defaults with
                    HomeModule = NoHomeModule
            }

            let prepared = Client.prepareModules config apps
            let ids = moduleIds prepared

            Expect.isFalse (List.contains "_sdk.home" ids) "no Home module injected"
            Expect.isFalse (List.head ids = "_sdk.home") "head is the first non-Home module"

            // The default is off, so an existing deployment is byte-for-byte
            // unchanged (GP 13). HomeModuleMode carries non-equatable cases
            // (ReactElement / ErasedModule), so assert via a pattern match.
            let defaultIsOff =
                match ClientConfig.defaults.HomeModule with
                | NoHomeModule -> true
                | _ -> false

            Expect.isTrue defaultIsOff "default HomeModule is NoHomeModule (off)"

        testCase "ConfiguredHomeModule + ExternalHomeModule also head-inject"
        <| fun () ->
            let configured = {
                ClientConfig.defaults with
                    HomeModule = ConfiguredHomeModule { Name = "Dashboard"; Icon = Html.none }
            }

            let configuredIds = moduleIds (Client.prepareModules configured apps)
            Expect.equal (List.head configuredIds) "_sdk.home" "ConfiguredHomeModule head-injects the Home module"

            let custom = appModule "custom.home" "Custom Home"

            let external = {
                ClientConfig.defaults with
                    HomeModule = ExternalHomeModule custom
            }

            let externalIds = moduleIds (Client.prepareModules external apps)
            Expect.equal (List.head externalIds) "custom.home" "ExternalHomeModule head-injects the supplied module"
    ]