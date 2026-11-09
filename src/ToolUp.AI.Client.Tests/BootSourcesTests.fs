// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.BootSourcesTests

// The shell's boot sources are one table (`BootSources.all`); its loader set,
// its resets, its retry and the banner's labels are filters over it. These pin
// the table's own invariants and that the shell reads it rather than a second
// spelling of it.

open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

/// Every key the shell can put on a degradation: each `BootSources.Key`.
let private keysTheShellEmits =
    List.sort [
        BootSources.Key.Permissions
        BootSources.Key.Configs
        BootSources.Key.Flags
        BootSources.Key.PlatformRole
        BootSources.Key.PlatformInfo
        BootSources.Key.Teams
        BootSources.Key.ActiveTeam
        BootSources.Key.TeamRole
        BootSources.Key.TeamAutoSelect
        BootSources.Key.DataSnapshot
        BootSources.Key.AuthBridge
        BootSources.Key.SidebarPrefs
    ]

let private table = Client.bootSources
/// Sorted, as a list: `Expect.equal` compares Fable's `Set` by its tree, not its members.
let private keysOf (sources: BootSources.BootSource<_, _> list) = sources |> List.map _.Key |> List.sort


let tests =
    testList "The boot-source table (Phase 1071)" [
        testCase "no two sources share a key" (fun () ->
            Expect.equal (keysOf table |> List.distinct).Length table.Length "every key is unique")

        testCase "the keys the shell emits are exactly the keys the table declares" (fun () ->
            Expect.equal (keysOf table) keysTheShellEmits "one set, no source outside the table")

        testCase "every source has a catalog label" (fun () ->
            let labels =
                table
                |> List.map (fun source -> source.Label MessageCatalog.english.BootDegradation.Sources)

            for source, label in List.zip table labels do
                Expect.isFalse (System.String.IsNullOrWhiteSpace label) $"`{source.Key}` has an empty label"

            Expect.equal (List.distinct labels).Length labels.Length "no two sources share a label")

        testCase "the banner's label for a source is its row's label" (fun () ->
            for source in table do
                Expect.equal
                    (BootDegradation.localisedLabel
                        MessageCatalog.english.BootDegradation.Sources
                        source.Key
                        "fallback")
                    (source.Label MessageCatalog.english.BootDegradation.Sources)
                    $"`{source.Key}`")

        testCase "a retryable source has a loader, and a reported one has none" (fun () ->
            for source in table do
                match source.Scope with
                | BootSources.BootScope.Reported ->
                    Expect.isFalse source.Retryable $"`{source.Key}` is reported, not loaded"
                | _ -> Expect.isTrue source.Retryable $"`{source.Key}` loads, so Retry can re-issue it")

        testCase "every source states which resets clear it, and only loaded sources are cleared" (fun () ->
            for source in table do
                if source.Scope = BootSources.BootScope.Reported then
                    Expect.isTrue source.ClearedBy.IsEmpty $"`{source.Key}` has no load to re-issue")

        testCase "a token arriving re-issues exactly the boot set" (fun () ->
            for teamScope in [ true; false ] do
                Expect.equal
                    (keysOf (BootSources.reissuedBy BootSources.ResetTrigger.TokenAcquired teamScope table))
                    (keysOf (BootSources.issuedAtBoot teamScope table))
                    $"team scope {teamScope}")

        testCase "the team sources are issued only where there is a team scope" (fun () ->
            let teamOnly =
                keysOf (BootSources.issuedAtBoot true table)
                |> List.filter (fun key -> not (List.contains key (keysOf (BootSources.issuedAtBoot false table))))

            Expect.equal
                teamOnly
                (List.sort [ BootSources.Key.Teams; BootSources.Key.ActiveTeam ])
                "teams, active team")

        testCase "a locale switch re-issues the configs, the flags and the access response" (fun () ->
            Expect.equal
                (keysOf (BootSources.reissuedBy BootSources.ResetTrigger.LocaleSwitch true table))
                (List.sort [ BootSources.Key.Permissions; BootSources.Key.Configs; BootSources.Key.Flags ])
                "the language-dependent loads")

        testCase "a team switch re-issues the team's loads and the role on it" (fun () ->
            Expect.equal
                (keysOf (BootSources.reissuedBy BootSources.ResetTrigger.TeamSwitch true table))
                (List.sort [
                    BootSources.Key.Permissions
                    BootSources.Key.Configs
                    BootSources.Key.Flags
                    BootSources.Key.Teams
                    BootSources.Key.TeamRole
                ])
                "the team-scoped loads")

        testCase "the first init waits for the configs and the flags" (fun () ->
            Expect.equal
                (Set.toList Client.firstInitGates)
                (List.sort [ BootSources.Key.Configs; BootSources.Key.Flags ])
                "the two sources a module's context is built from")

        testCase "a failed first-init source still releases the first init" (fun () ->
            for gate in Client.firstInitGates do
                let model: Client.Model = {
                    ActiveModuleId = "m1"
                    ActivePageRoute = None
                    Hosts = Map.empty
                    AccessibleModules = None
                    ShowAllModules = false
                    ModuleConfigs = Map.empty
                    PlatformConfig = Map.empty
                    ResolvedFlags = Map.empty
                    SidebarPrefs = SidebarPreferences.UserSidebarPreferences.empty
                    ProcessedData = []
                    PrefetchedProcessedData = []
                    MyTeams = []
                    ActiveTeamId = None
                    ActiveTeamLoadCompleted = false
                    PlatformRole = None
                    ActiveTeamRole = None
                    CurrentArea = ModuleArea.Product
                    AwaitingFirstInit = Client.firstInitGates
                    InitPhase = Client.Prefetching
                    Degradations = []
                    AppAccessRefused = false
                    CommandPalette = CommandPaletteNav.closed
                    LocaleOverride = None
                }

                let failed, _ =
                    Client.update
                        ClientConfig.defaults
                        Unchecked.defaultof<IModuleQueryBus>
                        []
                        (Client.BootLoadFailed(gate, "boom"))
                        model

                Expect.isFalse (failed.AwaitingFirstInit.Contains gate) $"`{gate}`'s failure is an answer")
    ]