module ToolUp.AI.Client.Tests.PlatformAIKeysAdminUITests

open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI
open ToolUp.AI.Client.PlatformAIKeysAdminUI

// ─── Phase 70 E.4 — happy-path MVU update test ──────────────────────
//
// Drives `PlatformAIKeysAdminUI.update` through the standard E.4 flow
// from Phase 70's deferred-task body: list providers → set platform
// key → verify hasKey → set team key → verify team-scope reflection.
//
// Why Fable-side and not .NET Expecto? `PlatformAIKeysAdminUI.fs`
// declares a module-level `let private api = Api.makeProxy<…>`. F#
// initialises module-level bindings eagerly via the static
// constructor on first member access. ToolUp.Remoting's reflection-
// based proxy builder is shaped for Fable's runtime and fails under
// .NET reflection (`buildProxy` cannot reconstruct the record of
// FSharpFunc values, raising `System.ArgumentException` at
// `init ()` time). The Fable transpile + `node:test` path runs the
// code in its actual deployment runtime, where the proxy
// construction works.
//
// View rendering (Feliz, React `useState`, JSDOM) is not exercised
// here. Adding JSDOM as a devDependency and writing the first view
// test is a follow-on once a concrete view-level case lands.

let private descriptor (providerId: string) (displayName: string) : AIProviderDescriptor = {
    Id = providerId
    DisplayName = displayName
    SupportedModels = [ "model-x" ]
    DefaultModel = "model-x"
    Capabilities = AIProviderCapabilities.unknown
}

let private bannerSuccessMessage (banner: Banner option) : string =
    match banner with
    | Some(Success msg) -> msg
    | Some(Failure err) -> sprintf "unexpected Failure banner: %s" err
    | None -> "no banner"

let tests =
    testList "PlatformAIKeysAdminUI (Phase 70 E.4)" [
        testCase "happy path: list providers → set platform key → verify hasKey → set team key → team-scope reflected"
        <| fun _ ->
            // init — empty model, Loading = true while load Cmds dispatch.
            let m0, _ = init ()
            Expect.isEmpty m0.Descriptors "init: no descriptors before load"
            Expect.isEmpty m0.PlatformStatuses "init: no platform statuses"
            Expect.isEmpty m0.Teams "init: no teams"
            Expect.equal m0.SelectedTeamId None "init: no team selected"
            Expect.equal m0.Banner None "init: no banner"
            Expect.isTrue m0.Loading "init: Loading = true while load Cmds dispatched"

            // LoadDescriptors (Finished _) populates Descriptors; Loading clears.
            let descriptors = [ descriptor "anthropic" "Claude"; descriptor "openai" "GPT-4" ]
            let m1, _ = update (LoadDescriptors(Finished descriptors)) m0
            Expect.equal m1.Descriptors descriptors "LoadDescriptors Finished: descriptors set"
            Expect.isFalse m1.Loading "LoadDescriptors Finished: Loading cleared"

            // LoadPlatformStatuses (Finished _) — pre-SetPlatformKey, no keys.
            let statusesBefore = [
                {
                    ProviderId = "anthropic"
                    HasKey = false
                }
                {
                    ProviderId = "openai"
                    HasKey = false
                }
            ]

            let m2, _ = update (LoadPlatformStatuses(Finished statusesBefore)) m1

            let anthropicBefore =
                m2.PlatformStatuses |> List.find (fun s -> s.ProviderId = "anthropic")

            Expect.isFalse anthropicBefore.HasKey "pre-SetPlatformKey: anthropic HasKey = false"

            // ActionFinished (Ok ()) — success banner carries SetPlatformKey result message.
            let m3, _ = update (ActionFinished(Ok(), "Saved platform key for anthropic.")) m2

            Expect.equal
                (bannerSuccessMessage m3.Banner)
                "Saved platform key for anthropic."
                "success banner carries SetPlatformKey message"

            // LoadPlatformStatuses refresh — anthropic now HasKey = true (server-side
            // SetPlatformKey persisted; the refresh Cmd fetched the new state).
            let statusesAfter = [
                {
                    ProviderId = "anthropic"
                    HasKey = true
                }
                {
                    ProviderId = "openai"
                    HasKey = false
                }
            ]

            let m4, _ = update (LoadPlatformStatuses(Finished statusesAfter)) m3

            let anthropicAfter =
                m4.PlatformStatuses |> List.find (fun s -> s.ProviderId = "anthropic")

            Expect.isTrue anthropicAfter.HasKey "post-refresh: anthropic HasKey = true"

            // LoadTeams (Finished _) populates the dropdown candidates.
            let teams = [
                {
                    TeamId = "team-a"
                    DisplayName = "Team A"
                }
                {
                    TeamId = "team-b"
                    DisplayName = "Team B"
                }
            ]

            let m5, _ = update (LoadTeams(Finished teams)) m4
            Expect.equal m5.Teams teams "LoadTeams Finished: teams set"

            // SelectTeam (Some _) stores the id and clears stale TeamStatuses.
            let m6, _ = update (SelectTeam(Some "team-a")) m5
            Expect.equal m6.SelectedTeamId (Some "team-a") "SelectTeam Some: id stored"
            Expect.isEmpty m6.TeamStatuses "SelectTeam: TeamStatuses cleared awaiting refresh"

            // LoadTeamStatuses (Finished _) — team-scope override visible for anthropic.
            let teamStatuses = [
                {
                    TeamId = "team-a"
                    ProviderId = "anthropic"
                    HasKey = true
                }
                {
                    TeamId = "team-a"
                    ProviderId = "openai"
                    HasKey = false
                }
            ]

            let m7, _ = update (LoadTeamStatuses(Finished teamStatuses)) m6

            let teamAnthropic =
                m7.TeamStatuses |> List.find (fun s -> s.ProviderId = "anthropic")

            Expect.isTrue teamAnthropic.HasKey "team-scope reflection: team-a anthropic override visible"
            Expect.equal m7.SelectedTeamId (Some "team-a") "team selection survives LoadTeamStatuses Finished"
    ]