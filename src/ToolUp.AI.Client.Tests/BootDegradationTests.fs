// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.BootDegradationTests

// ─── Phase 121 — boot-degradation surface ────────────────────────────
//
// Exercises the Platform.Client shell's typed boot-degradation
// accumulator (`Client.update` arms + `BootDegradation` helpers) and
// the `UserSession` auth-bridge failure counter under Node — same
// Phase 110 precedent as `ClientHostBridgeTests` (Platform.Client tier
// reached transitively).
//
// The shell `update` is driven directly with hand-built `Client.Model`
// values: a failing boot load arrives as `BootLoadFailed` (that is the
// exact message `bootLoadCmd`'s `Cmd.OfAsync.either` produces from a
// thrown loader), so the tests pin the Model contract — empty teams +
// one deduped degradation entry + banner-visible state — without
// needing a live server. The bridge tests stub `globalThis.setInterval`
// so `installBridge` never schedules a real timer; refresh cycles are
// driven manually through `UserSession.refreshFromBridge`.

open Fable.Core
open Fable.Core.JsInterop
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

// ─── Substrate stubs ─────────────────────────────────────────────────

[<Emit("""(() => {
    globalThis.window = globalThis.window || {};
    if (!globalThis.window.localStorage) {
        const store = new Map();
        globalThis.window.localStorage = {
            getItem: (k) => (store.has(k) ? store.get(k) : null),
            setItem: (k, v) => { store.set(k, String(v)); },
            removeItem: (k) => { store.delete(k); },
        };
    }
    globalThis.__intervals = { nextId: 1, cleared: [] };
    globalThis.setInterval = (cb, ms) => globalThis.__intervals.nextId++;
    globalThis.clearInterval = (id) => { globalThis.__intervals.cleared.push(id); };
})()""")>]
let private installStubs () : unit = jsNative

[<Emit("globalThis.__intervals.cleared")>]
let private clearedIntervalIds () : int[] = jsNative

// ─── Shell-model fixture ─────────────────────────────────────────────

let private baseModel: Client.Model = {
    ActiveModuleId = "m1"
    ActivePageRoute = None
    ModuleStates = Map.empty
    AccessibleModules = None
    ShowAllModules = false
    ModuleConfigs = Map.empty
    PlatformConfig = Map.empty
    ResolvedFlags = Map.empty
    SidebarPrefs = SidebarPreferences.load ()
    ProcessedData = []
    PrefetchedProcessedData = []
    MyTeams = []
    ActiveTeamId = None
    ActiveTeamLoadCompleted = false
    PlatformRole = None
    ActiveTeamRole = None
    CurrentArea = ModuleArea.Product
    ConfigsPrefetch = Prefetch.none
    FlagsPrefetch = Prefetch.none
    ResetCounters = Map.empty
    InitPhase = Client.Prefetching
    Degradations = []
}

// `update`'s queryBus / modules parameters are untouched by the
// degradation arms under test (the gate-flip path stops before module
// init while both prefetches are pending), so nulls suffice — a test
// that accidentally reaches them fails loudly. The `config` parameter,
// however, IS read on every arm: the post-update `ProcessedData`
// recompute calls `resolveProcessedData config …`, which reads
// `config.DataManager`. A null config therefore throws `Cannot read
// properties of null (reading 'DataManager')` regardless of arm, so we
// pass the real `ClientConfig.defaults` (resolves under Fable — same
// HomeLanding precedent).
let private update msg model =
    Client.update ClientConfig.defaults Unchecked.defaultof<IModuleQueryBus> [] msg model

let tests =
    installStubs ()

    testList "Boot-degradation surface (Phase 121)" [
        testList "accumulator" [
            testCase "add dedups by source — latest error wins" (fun () ->
                let e1: BootDegradation.BootDegradation = {
                    Source = "teams"
                    Label = "Team memberships"
                    Error = "first"
                    Retryable = true
                    OccurredAt = System.DateTime.UtcNow
                }

                let e2 = { e1 with Error = "second" }

                let acc = [] |> BootDegradation.add e1 |> BootDegradation.add e2

                Expect.equal (List.length acc) 1 "one entry per source"
                Expect.equal acc.Head.Error "second" "latest replaces prior")

            testCase "remove clears only the named source" (fun () ->
                let entry source : BootDegradation.BootDegradation = {
                    Source = source
                    Label = source
                    Error = "x"
                    Retryable = true
                    OccurredAt = System.DateTime.UtcNow
                }

                let acc = [ entry "teams"; entry "flags" ] |> BootDegradation.remove "teams"

                Expect.equal (acc |> List.map _.Source) [ "flags" ] "only the other source remains")
        ]

        testList "shell update arms" [
            testCase "failing teams load: empty teams + one degradation (banner-visible), deduped on repeat" (fun () ->
                let m1, _ = update (Client.BootLoadFailed("teams", "ECONNREFUSED")) baseModel

                Expect.equal
                    m1.MyTeams
                    []
                    "model keeps the benign default — failed ≠ teamless is carried by Degradations, not MyTeams"

                Expect.equal (List.length m1.Degradations) 1 "one banner entry"
                Expect.equal m1.Degradations.Head.Source "teams" "keyed by source"
                Expect.isTrue m1.Degradations.Head.Retryable "teams load is retryable"

                let m2, _ = update (Client.BootLoadFailed("teams", "still down")) m1
                Expect.equal (List.length m2.Degradations) 1 "repeat failure dedups"
                Expect.equal m2.Degradations.Head.Error "still down" "latest error wins")

            testCase "load success clears its degradation — retry path restores teams without a reload" (fun () ->
                let failed, _ = update (Client.BootLoadFailed("teams", "boom")) baseModel
                let retried, _ = update (Client.RetryBootLoad "teams") failed

                Expect.isEmpty retried.Degradations "retry optimistically clears the entry"

                let recovered, _ = update (Client.MyTeamsLoaded []) retried
                Expect.isEmpty recovered.Degradations "a clean MyTeamsLoaded leaves no degradation")

            testCase "genuinely empty data ≠ failed: clean loads produce no degradations" (fun () ->
                let m1, _ = update (Client.MyTeamsLoaded []) baseModel
                let m2, _ = update (Client.ActiveTeamLoaded None) m1

                Expect.isEmpty m2.Degradations "teamless user sees no banner"
                Expect.isTrue m2.ActiveTeamLoadCompleted "the fetch genuinely completed")

            testCase
                "failed active-team fetch does NOT mark the load completed (no auto-select off failed data)"
                (fun () ->
                    let m, _ = update (Client.BootLoadFailed("active-team", "boom")) baseModel

                    Expect.isFalse m.ActiveTeamLoadCompleted "failure is not completion"
                    Expect.equal (m.Degradations |> List.map _.Source) [ "active-team" ] "but it is visible")

            testCase "failed configs load still flips the prefetch gate (no skeleton dead-lock)" (fun () ->
                let m, _ = update (Client.BootLoadFailed("configs", "boom")) baseModel

                Expect.isTrue (Prefetch.isComplete m.ConfigsPrefetch) "gate flipped despite the failure"
                Expect.equal (m.Degradations |> List.map _.Source) [ "configs" ] "and the failure is recorded")

            testCase "dismiss clears every entry; auth-bridge entry is not retryable" (fun () ->
                let m1, _ =
                    update (Client.BootLoadFailed("auth-bridge", "GetJwt failing")) baseModel

                Expect.isFalse m1.Degradations.Head.Retryable "bridge retries itself on its interval"

                let m2, _ = update (Client.BootLoadFailed("flags", "boom")) m1
                let m3, _ = update Client.DismissBootDegradations m2
                Expect.isEmpty m3.Degradations "dismiss clears all"

                let m4, _ = update (Client.BootLoadRecovered "auth-bridge") m1
                Expect.isEmpty m4.Degradations "bridge recovery clears its entry")
        ]

        testList "auth-bridge failure counter (UserSession)" [
            testCase "threshold emission, recovery notification, and re-install interval sentinel" (fun () ->
                let mutable shouldFail = true

                let bridge =
                    { new IAuthBridge with
                        member _.GetJwt() = async {
                            if shouldFail then
                                return failwith "IdP iframe blocked"
                            else
                                return None
                        }

                        member _.GetUserId() = None
                        member _.ProviderName = "test-double"
                    }

                let healthEvents = ResizeArray<string option>()
                let stopObserving = UserSession.onBridgeHealthChange healthEvents.Add

                let diagnosticLines = ResizeArray<string>()
                AuthDiagnostics.install (AuthDiagnostics.writingTracer diagnosticLines.Add)

                let refreshOnce () =
                    Async.StartImmediate(UserSession.refreshFromBridge ())

                // install fires one immediate (failing) refresh = streak 1
                UserSession.installBridge bridge
                refreshOnce () // streak 2
                Expect.isEmpty healthEvents "below threshold — silent"

                refreshOnce () // streak 3 = threshold

                Expect.equal
                    (List.ofSeq healthEvents)
                    [ Some "Session refresh is failing — you may be signed out shortly" ]
                    "one degraded notification at threshold"

                refreshOnce () // streak 4 — already notified
                Expect.equal healthEvents.Count 1 "no re-notification while still failing"

                Expect.isTrue
                    (diagnosticLines |> Seq.exists (fun l -> l.Contains "bridge-refresh-failing"))
                    "AuthDiagnostics carries the failure edge"

                shouldFail <- false
                refreshOnce () // clean round-trip

                Expect.equal
                    (List.ofSeq healthEvents)
                    [ Some "Session refresh is failing — you may be signed out shortly"; None ]
                    "recovery notifies None once"

                // Re-install clears the prior refresh interval (the
                // pre-121 leak): our setInterval stub hands out ids and
                // records clearInterval calls.
                UserSession.installBridge bridge
                Expect.isTrue (clearedIntervalIds().Length >= 1) "re-install cleared the prior interval"

                UserSession.uninstallBridge ()
                stopObserving ()
                AuthDiagnostics.install AuthDiagnostics.nullTracer)
        ]
    ]