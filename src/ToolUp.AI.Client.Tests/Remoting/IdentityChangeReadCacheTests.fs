// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.IdentityChangeReadCacheTests

// ─── Phase 908 — every identity change clears the read cache ───────────
//
// A declared read's cached result (Phase 854) belongs to the identity that
// fetched it, and the cache key does not carry the identity. So each route
// that changes who the server sees must drop the cache, or the next
// identity is served the previous one's reads. One case per route, each
// run on the TRANSPILED client through the real proxy against the scripted
// XMLHttpRequest `ReadPolicyTests` installs (same global, same shape):
//
//   prime a `Cacheable` read  ->  change the identity by that route  ->
//   the same read is NOT answered synchronously from the cache.
//
// Every route that has a "no change" twin carries it as a control, and the
// control expects the cached value to BE served: without it a probe that
// never served anything would pass every case above it.
//
// The browser substrate is a fresh jsdom per case (`RenderScope.installDom`
// — real `localStorage`, `document.cookie` and `StorageEvent`), so the
// cross-tab cases dispatch a real `storage` event rather than calling a
// handler by hand.

open Fable.Core
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.Remoting.Client
open ToolUp.Platform.Tests.Remoting.ReadPolicyFixture
open ToolUp.AI.Client.Tests.NodeTest

// ─── XMLHttpRequest stub (shared with ReadPolicyTests) ────────────────

[<Emit("""(() => {
    if (globalThis.__xhrStub) return;
    const stub = { calls: [], status: 200, body: '' };
    globalThis.__xhrStub = stub;
    globalThis.XMLHttpRequest = class {
        constructor() { this.readyState = 0; this.status = 0; this.responseText = ''; this.headers = {}; }
        open(method, url) { this.method = method; this.url = url; }
        setRequestHeader(key, value) { this.headers[key] = value; }
        abort() { }
        send(body) {
            stub.calls.push({ method: this.method, url: this.url, body });
            this.status = stub.status;
            this.responseText = stub.body;
            this.readyState = 4;
            queueMicrotask(() => { if (this.onreadystatechange) this.onreadystatechange(); });
        }
    };
})()""")>]
let private installXhrStubJs () : unit = jsNative

[<Emit("(globalThis.__xhrStub.status = $0, globalThis.__xhrStub.body = $1, undefined)")>]
let private scriptResponse (status: int) (body: string) : unit = jsNative

/// A JWT-shaped token whose payload names `sub` (the signature is never
/// checked client-side). `nonce` makes two tokens for one subject differ,
/// the shape of a refresh.
[<Emit("'h.' + btoa(JSON.stringify({ sub: $0, nonce: $1 })).replace(/\\+/g, '-').replace(/\\//g, '_').replace(/=+$/, '') + '.s'")>]
let private jwtFor (subject: string) (nonce: int) : string = jsNative

/// Fire a real `storage` event on the current window — what the browser
/// does in THIS tab when ANOTHER tab of the origin writes `localStorage`.
[<Emit("window.dispatchEvent(new window.StorageEvent('storage', { key: $0, oldValue: $1, newValue: $2 }))")>]
let private otherTabWrote (key: string) (oldValue: string) (newValue: string) : unit = jsNative

// Storage keys mirror the private constants in `UserSession.fs` (the same
// manual-sync rule `NotificationClientTests` follows).
[<Literal>]
let private TokenKey = "toolup-auth-token"

[<Literal>]
let private TokenUserIdKey = "toolup-token-user-id"

// ─── Fixture ─────────────────────────────────────────────────────────

let private api: ReadCatalogApi =
    Remoting.createApi () |> Remoting.buildProxy<ReadCatalogApi>

let private setUp () =
    RenderScope.installDom "identity-908" |> ignore
    Http.useTransport Http.Transport.Xhr
    installXhrStubJs ()
    ReadPolicies.registerFor<ReadCatalogApi> declarations
    // The token path that needs no server round-trip, on every case.
    UserSession.configureAuthTokenStorage ClientCookieAndLocalStorage
    ReadPolicies.clear ()
    scriptResponse 200 "1"

type private Outcome =
    | Pending
    | Served of int
    | Raised of exn

/// What a call has produced by the time this returns, without awaiting: a
/// cached read is `Served` at once, anything that went to the server is
/// still `Pending`.
let private callNow (call: Async<int>) : Outcome =
    let mutable outcome = Pending

    Async.StartImmediate(
        async {
            try
                let! value = call
                outcome <- Served value
            with ex ->
                outcome <- Raised ex
        }
    )

    outcome

let private same (actual: 'T) (expected: 'T) (message: string) : unit =
    Expect.isTrue (actual = expected) (sprintf "%s\n  actual:   %A\n  expected: %A" message actual expected)

/// Prime `key` under the identity `before` leaves in place, change the
/// identity with `change`, and read `key` again at once.
let private acrossChange
    (key: string)
    (before: unit -> unit)
    (change: unit -> unit)
    (expected: Outcome)
    (what: string)
    =
    testCaseDeferred what 50 (fun () ->
        setUp ()
        before ()
        let mutable afterChange = Raised(exn "the case never ran")

        Async.StartImmediate(
            async {
                let! _ = api.GetCount key
                change ()
                afterChange <- callNow (api.GetCount key)
            }
        )

        fun () -> same afterChange expected what)

let private notServed = Pending
let private stillServed = Served 1

// ─── The shell's team switch ─────────────────────────────────────────

/// Built per case, never at import: `SidebarPreferences.load` reads the
/// window this case installed.
let private shellModel () : Client.Model = {
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
    ActiveTeamId = Some "team-a"
    ActiveTeamLoadCompleted = true
    PlatformRole = None
    ActiveTeamRole = None
    CurrentArea = ModuleArea.Product
    ConfigsPrefetch = Prefetch.none
    FlagsPrefetch = Prefetch.none
    ResetCounters = Map.empty
    InitPhase = Client.Ready
    Degradations = []
    CommandPalette = CommandPaletteNav.closed
    LocaleOverride = None
}

/// Run the shell's `TeamSwitched` arm and every command it returns (the
/// reloads go to the scripted stub; their messages are dropped).
let private switchTeam (teamId: string option) () =
    let _, cmd =
        Client.update
            ClientConfig.defaults
            Unchecked.defaultof<IModuleQueryBus>
            []
            (Client.TeamSwitched teamId)
            (shellModel ())

    for effect in cmd do
        effect ignore

// ─── The cross-tab watch ─────────────────────────────────────────────

/// Install the watch on THIS case's window: drop whatever an earlier boot
/// installed (on an earlier window), then install afresh.
let private watchThisWindow () =
    (UserSession.watchIdentityAcrossTabs ()) ()
    UserSession.watchIdentityAcrossTabs () |> ignore

let tests =
    testList "Phase 908 — every identity change clears the read cache" [

        testList "tokens (setAuthToken / clearAuthToken)" [
            acrossChange
                "tok-other"
                (fun () -> UserSession.setAuthToken (jwtFor "alice" 1))
                (fun () -> UserSession.setAuthToken (jwtFor "bob" 1))
                notServed
                "a token for a different subject: the previous subject's read is not served"

            acrossChange
                "tok-opaque"
                (fun () -> UserSession.setAuthToken (jwtFor "alice" 1))
                (fun () -> UserSession.setAuthToken "an-opaque-token")
                notServed
                "a token whose subject cannot be read: not served"

            acrossChange
                "tok-out"
                (fun () -> UserSession.setAuthToken (jwtFor "alice" 1))
                UserSession.clearAuthToken
                notServed
                "sign-out: not served"

            acrossChange
                "tok-refresh"
                (fun () -> UserSession.setAuthToken (jwtFor "alice" 1))
                (fun () -> UserSession.setAuthToken (jwtFor "alice" 2))
                stillServed
                "control: a refresh for the SAME subject keeps serving the cached read"
        ]

        testList "boot configuration (configure / configureDevDefault / configureAuthTokenStorage)" [
            acrossChange
                "kind-moved"
                (fun () -> UserSession.configure UserKind)
                (fun () -> UserSession.configure AnonymousKind)
                notServed
                "a different subject kind: not served"

            acrossChange
                "kind-same"
                (fun () -> UserSession.configure UserKind)
                (fun () -> UserSession.configure UserKind)
                stillServed
                "control: re-configuring the same kind keeps serving"

            acrossChange
                "dev-moved"
                (fun () -> UserSession.configureDevDefault None)
                (fun () -> UserSession.configureDevDefault (Some "dev-908"))
                notServed
                "a different dev-default identity: not served"

            acrossChange
                "dev-same"
                (fun () -> UserSession.configureDevDefault (Some "dev-908"))
                (fun () -> UserSession.configureDevDefault (Some "dev-908"))
                stillServed
                "control: the same dev-default identity keeps serving"

            acrossChange
                "storage-moved"
                ignore
                (fun () ->
                    UserSession.configureAuthTokenStorage ServerSetHttpOnlyCookie
                    // Restored at once (after the change was observed by the
                    // cache) so no later case reflects a token to a server.
                    UserSession.configureAuthTokenStorage ClientCookieAndLocalStorage)
                notServed
                "a different token-storage strategy: not served"
        ]

        testList "the shell's team switch (TeamSwitched)" [
            acrossChange
                "team-moved"
                ignore
                (switchTeam (Some "team-b"))
                notServed
                "switching the active team: the previous team's read is not served"

            acrossChange "team-revoked" ignore (switchTeam None) notServed "losing the active team: not served"
        ]

        testList "another tab (watchIdentityAcrossTabs)" [
            acrossChange
                "tab-other"
                watchThisWindow
                (fun () -> otherTabWrote TokenUserIdKey "alice" "bob")
                notServed
                "another tab signed in as a different subject: not served"

            acrossChange
                "tab-token-other"
                watchThisWindow
                (fun () -> otherTabWrote TokenKey (jwtFor "alice" 1) (jwtFor "bob" 1))
                notServed
                "another tab stored a token for a different subject: not served"

            acrossChange
                "tab-out"
                watchThisWindow
                (fun () -> otherTabWrote TokenKey (jwtFor "alice" 1) null)
                notServed
                "another tab signed out: not served"

            acrossChange
                "tab-cleared"
                watchThisWindow
                (fun () -> otherTabWrote null null null)
                notServed
                "another tab cleared storage: not served"

            acrossChange
                "tab-refresh"
                watchThisWindow
                (fun () -> otherTabWrote TokenKey (jwtFor "alice" 1) (jwtFor "alice" 2))
                stillServed
                "control: another tab's same-subject refresh keeps serving"

            acrossChange
                "tab-unrelated"
                watchThisWindow
                (fun () -> otherTabWrote "toolup-sidebar-prefs" "a" "b")
                stillServed
                "control: another tab's unrelated write keeps serving"
        ]
    ]