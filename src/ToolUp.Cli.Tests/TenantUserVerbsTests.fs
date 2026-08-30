// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 550 — `toolup tenants` / `toolup users`.
///
/// Two things are pinned here, and the second is the one that matters.
///
/// The first is ordinary: argument parsing, the `Result` envelope read,
/// and a round-trip per verb against a **recording mock transport** — a
/// literal response string in, the route and request body captured out.
/// No socket, no listening port, so the pack stays hermetic and fast.
///
/// The second is the phase's actual guarantee: **the CLI adds no
/// confirmation bypass.** The verbs are a thin client; every gate lives
/// server-side. What a client CAN do wrong is mint its own token, or
/// quietly re-route a refused call to an ungated method, and neither
/// would show up as a failing build. So the tests below assert the exact
/// route each flag combination selects, that a refusal is surfaced
/// verbatim and exits non-zero, and that no verb in the family ever
/// touches the token-minting method.
module ToolUp.Cli.Tests.TenantUserVerbsTests

open Expecto
open System
open System.IO
open System.Text.Json
open ToolUp.Cli.Dispatch
open ToolUp.Cli.Commands

// ── Recording mock transport ────────────────────────────────────────

type private Recorder() =
    let calls = ResizeArray<string * string>()
    member _.Calls = List.ofSeq calls

    member _.Routes = calls |> Seq.map fst |> List.ofSeq

    /// A transport answering every call with `response`, recording the
    /// route and request body it was given.
    member _.Transport(response: string) : Tenants.Transport = {
        Post =
            fun route body ->
                calls.Add(route, body)
                Ok response
    }

/// The arguments of the single recorded call, as a list of JSON values.
let private argsOf (body: string) =
    use doc = JsonDocument.Parse body
    doc.RootElement.EnumerateArray() |> Seq.map _.ToString() |> List.ofSeq

// ── Canned responses (the admin API's documented wire shape) ────────

let private teamsResponse =
    """{"Ok":[{"TeamId":"team-a","Name":"Alpha","CreatedAt":"2026-01-01T00:00:00","Archived":false,"MemberCount":3,"Owners":["u1"],"Admins":[]},{"TeamId":"team-b","Name":"Beta","CreatedAt":"2026-02-01T00:00:00","Archived":true,"MemberCount":0,"Owners":[],"Admins":[]}]}"""

let private previewResponse =
    """{"Ok":{"ScopeId":"team-a","Items":[{"HookName":"EncryptionKeyLifecycle","HasPreview":true,"WouldAffect":1,"Detail":"the scope's encryption key would be destroyed"},{"HookName":"WidgetStore","HasPreview":false,"WouldAffect":0,"Detail":"no preview available"}],"TotalWouldAffect":1}}"""

let private cleanSummary =
    """{"Ok":{"ScopeId":"team-a","Phase":"Deprovisioning","Outcomes":[{"HookName":"EncryptionKeyLifecycle","Result":"Completed","ElapsedMs":5},{"HookName":"WidgetStore","Result":{"Skipped":"substrate not composed"},"ElapsedMs":1}],"TotalElapsedMs":6}}"""

let private failedSummary =
    """{"Ok":{"ScopeId":"team-a","Phase":"Deprovisioning","Outcomes":[{"HookName":"EncryptionKeyLifecycle","Result":"Completed","ElapsedMs":5},{"HookName":"WidgetStore","Result":{"Failed":"store unreachable"},"ElapsedMs":7}],"TotalElapsedMs":12}}"""

let private exportThenResponse =
    """{"Ok":{"Summary":{"ScopeId":"team-a","Phase":"Deprovisioning","Outcomes":[{"HookName":"EncryptionKeyLifecycle","Result":"Completed","ElapsedMs":5}],"TotalElapsedMs":5},"Archive":{"Container":"_platform","BlobPath":"exports/team-a.zip","ContentHash":"deadbeef","SegmentCount":2}}}"""

let private principalsResponse =
    """{"Ok":[{"UserId":"u1","Memberships":[["team-a","Owner"],["team-b","Member"]],"LastSeenAt":"2026-08-01T10:00:00","HasUserScopeData":true},{"UserId":"u2","Memberships":[],"LastSeenAt":null,"HasUserScopeData":true},{"UserId":"u3","Memberships":[],"LastSeenAt":"2026-08-20T09:00:00","HasUserScopeData":false}]}"""

let private confirmationRefusal = """{"Error":"offboard confirmation required"}"""

let private adminRefusal = """{"Error":"platform admin role required"}"""

// ── Rendering helpers under test ────────────────────────────────────

let private renderedTeams () =
    use doc = JsonDocument.Parse teamsResponse

    match Tenants.resultPayload doc.RootElement with
    | Ok payload -> fst (Tenants.renderTeams payload)
    | Error e -> failtestf "expected Ok, got %s" e

let private renderedPrincipals teamLessOnly =
    use doc = JsonDocument.Parse principalsResponse

    match Tenants.resultPayload doc.RootElement with
    | Ok payload -> fst (Users.renderPrincipals teamLessOnly payload)
    | Error e -> failtestf "expected Ok, got %s" e

let private joined (lines: string list) = String.concat "\n" lines

let tests =
    testList "TenantUserVerbs" [

        // ── Command parsing (550.A / 550.B) ─────────────────────────

        testList "parsing" [
            test "a bare scope id is the positional argument" {
                match Tenants.parse Tenants.defaultOptions [ "team-a" ] with
                | Ok opts -> Expect.equal opts.ScopeId (Some "team-a") "positional scope id"
                | Error e -> failtest e
            }

            test "offboard flags parse independently of order" {
                let args = [ "--token"; "tok-1"; "team-a"; "--export-first"; "--reason"; "leaving" ]

                match Tenants.parse Tenants.defaultOptions args with
                | Ok opts ->
                    Expect.equal opts.ScopeId (Some "team-a") "scope id"
                    Expect.equal opts.Token (Some "tok-1") "token"
                    Expect.isTrue opts.ExportFirst "--export-first"
                    Expect.equal opts.Reason "leaving" "--reason"
                | Error e -> failtest e
            }

            test "--endpoint, --token-file and --team-less parse" {
                let args = [ "--endpoint"; "https://x"; "--token-file"; "/tmp/t"; "--team-less" ]

                match Tenants.parse Tenants.defaultOptions args with
                | Ok opts ->
                    Expect.equal opts.Endpoint (Some "https://x") "endpoint"
                    Expect.equal opts.TokenFile (Some "/tmp/t") "token file"
                    Expect.isTrue opts.TeamLess "--team-less"
                | Error e -> failtest e
            }

            test "--token-file and --token are different options" {
                // One is a path to a credential, the other a single-use
                // confirmation token. A parser that conflated them would make
                // `--token-file` look like a way to supply a confirmation.
                let args = [ "--token-file"; "/tmp/t"; "--token"; "tok-1" ]

                match Tenants.parse Tenants.defaultOptions args with
                | Ok opts ->
                    Expect.equal opts.TokenFile (Some "/tmp/t") "the path"
                    Expect.equal opts.Token (Some "tok-1") "the confirmation token"
                | Error e -> failtest e
            }

            test "the default reason is non-empty so the audit row is never blank" {
                Expect.isNotEmpty Tenants.defaultOptions.Reason "a default reason exists"
            }

            test "a value-less option is a parse error, not a silent default" {
                for flag in [ "--endpoint"; "--token-file"; "--reason"; "--token" ] do
                    Expect.isError (Tenants.parse Tenants.defaultOptions [ flag ]) flag
            }

            test "a second positional argument is refused" {
                Expect.isError (Tenants.parse Tenants.defaultOptions [ "a"; "b" ]) "two scope ids"
            }

            test "an unknown flag is refused rather than ignored" {
                Expect.isError (Tenants.parse Tenants.defaultOptions [ "--force" ]) "unknown flag"
            }
        ]

        // ── The Result envelope ─────────────────────────────────────

        testList "result envelope" [
            test "reads the single-property Ok form" {
                use doc = JsonDocument.Parse """{"Ok":[1,2]}"""

                match Tenants.resultPayload doc.RootElement with
                | Ok payload -> Expect.equal payload.ValueKind JsonValueKind.Array "payload is the Ok field"
                | Error e -> failtest e
            }

            test "reads the string-prefixed array form" {
                use doc = JsonDocument.Parse """["Error","nope"]"""
                Expect.equal (Tenants.resultPayload doc.RootElement) (Error "nope") "array-form Error"
            }

            test "surfaces the server's banner verbatim" {
                // The whole point of the refusal path: an operator
                // forwarding this to a colleague must be forwarding what
                // the server said, not a CLI paraphrase of it.
                use doc = JsonDocument.Parse confirmationRefusal

                Expect.equal
                    (Tenants.resultPayload doc.RootElement)
                    (Error "offboard confirmation required")
                    "banner passes through unedited"
            }

            test "an unrecognised shape is an error, never a silent empty render" {
                use doc = JsonDocument.Parse "42"
                Expect.isError (Tenants.resultPayload doc.RootElement) "a bare number is not a Result"
            }
        ]

        // ── Round-trips: tenants (550.A) ────────────────────────────

        testList "tenants round-trips" [
            test "list posts the team admin read and renders a row per team" {
                let rec' = Recorder()
                Expect.equal (Tenants.runList (rec'.Transport teamsResponse)) ExitOk "clean read exits 0"
                Expect.equal rec'.Routes [ "/api/TeamApi/ListAllTeams" ] "the deployment-wide team read"

                let body = joined (renderedTeams ())
                Expect.stringContains body "team-a" "first team"
                Expect.stringContains body "team-b" "second team"
                Expect.stringContains body "2 team(s)" "count footer"
            }

            test "preview posts the read-only projection with the scope id" {
                let rec' = Recorder()

                Expect.equal (Tenants.runPreview (rec'.Transport previewResponse) "team-a") ExitOk "preview exits 0"

                match rec'.Calls with
                | [ (route, body) ] ->
                    Expect.equal route "/api/_platform/tenants/PreviewDeprovision" "the preview method"
                    Expect.equal (argsOf body) [ "team-a" ] "one argument: the scope"
                | other -> failtestf "expected one call, got %A" other
            }

            test "preview names the hooks that offered no projection" {
                use doc = JsonDocument.Parse previewResponse

                match Tenants.resultPayload doc.RootElement with
                | Ok payload ->
                    let body = joined (fst (Tenants.renderPreview payload))
                    Expect.stringContains body "EncryptionKeyLifecycle" "the hook that previewed"
                    Expect.stringContains body "WidgetStore" "the hook that did not"
                    // A hook that opted out contributes 0 to the total, so
                    // the total under-reports; the render has to say so or
                    // it reads as a complete blast radius.
                    Expect.stringContains body "NOT in that total" "the gap is named"
                | Error e -> failtest e
            }

            test "a token-less offboard posts the plain deprovision method" {
                let rec' = Recorder()

                Expect.equal
                    (Tenants.runOffboard (rec'.Transport cleanSummary) "team-a" "leaving" false None)
                    ExitOk
                    "a clean sweep exits 0"

                match rec'.Calls with
                | [ (route, body) ] ->
                    Expect.equal route "/api/_platform/tenants/DeprovisionTenant" "the token-less method"
                    Expect.equal (argsOf body) [ "team-a"; Tenants.WireActor; "leaving" ] "scope, actor, reason"
                | other -> failtestf "expected one call, got %A" other
            }

            test "--token routes to the confirmed method and replays the token verbatim" {
                let rec' = Recorder()

                Expect.equal
                    (Tenants.runOffboard (rec'.Transport cleanSummary) "team-a" "leaving" false (Some "tok-9"))
                    ExitOk
                    "confirmed sweep exits 0"

                match rec'.Calls with
                | [ (route, body) ] ->
                    Expect.equal route "/api/_platform/tenants/DeprovisionTenantConfirmed" "the confirmed method"

                    Expect.equal
                        (argsOf body)
                        [ "team-a"; Tenants.WireActor; "leaving"; "tok-9" ]
                        "the operator's token is replayed unchanged"
                | other -> failtestf "expected one call, got %A" other
            }

            test "--export-first routes to export-then-erase and reports the archive" {
                let rec' = Recorder()

                Expect.equal
                    (Tenants.runOffboard (rec'.Transport exportThenResponse) "team-a" "leaving" true None)
                    ExitOk
                    "export-then-erase exits 0"

                Expect.equal rec'.Routes [ "/api/_platform/tenants/ExportThenDeprovision" ] "the export method"

                use doc = JsonDocument.Parse exportThenResponse

                match Tenants.resultPayload doc.RootElement with
                | Ok payload ->
                    let body = joined (fst (Tenants.renderExportThenSummary payload))
                    Expect.stringContains body "exports/team-a.zip" "the archive path the operator hands over"
                    Expect.stringContains body "deadbeef" "the content hash"
                | Error e -> failtest e
            }

            test "--export-first with --token is refused before anything is sent" {
                // The admin API has no confirmed export-then-erase method.
                // Refusing locally is not a gate of our own — it is
                // declining to send a request whose refusal would read as
                // a token problem.
                let rec' = Recorder()

                Expect.equal
                    (Tenants.runOffboard (rec'.Transport cleanSummary) "team-a" "leaving" true (Some "tok-9"))
                    ExitUsage
                    "an unsatisfiable combination is a usage error"

                Expect.isEmpty rec'.Calls "nothing was sent"
            }

            test "a failed hook exits non-zero so a scripted sweep stops" {
                let rec' = Recorder()

                Expect.equal
                    (Tenants.runOffboard (rec'.Transport failedSummary) "team-a" "leaving" false None)
                    ExitRuntimeError
                    "a partial offboard is not a clean run"
            }

            test "a skipped hook is not a failure" {
                let rec' = Recorder()

                Expect.equal
                    (Tenants.runOffboard (rec'.Transport cleanSummary) "team-a" "leaving" false None)
                    ExitOk
                    "Skipped is a disposition, not an error"
            }

            test "a server refusal exits non-zero on every verb" {
                for response in [ confirmationRefusal; adminRefusal ] do
                    let rec' = Recorder()

                    Expect.equal
                        (Tenants.runOffboard (rec'.Transport response) "team-a" "leaving" false None)
                        ExitRuntimeError
                        "a refused offboard is a failure"

                    Expect.equal (Tenants.runList (rec'.Transport response)) ExitRuntimeError "a refused list too"
            }

            test "a transport failure exits non-zero rather than reading as empty" {
                let failing: Tenants.Transport = {
                    Post = fun _ _ -> Error "connection refused"
                }

                Expect.equal (Tenants.runList failing) ExitRuntimeError "unreachable deployment"
                Expect.equal (Tenants.runPreview failing "team-a") ExitRuntimeError "unreachable deployment"
            }

            test "a non-JSON response is an error, not a crash" {
                let garbage: Tenants.Transport = {
                    Post = fun _ _ -> Ok "<html>502</html>"
                }

                Expect.equal (Tenants.runList garbage) ExitRuntimeError "a proxy error page is not a payload"
            }
        ]

        // ── Round-trips: users (550.B) ──────────────────────────────

        testList "users round-trips" [
            test "list posts the derived principal enumeration" {
                let rec' = Recorder()

                Expect.equal (Users.runList (rec'.Transport principalsResponse) false) ExitOk "list exits 0"

                match rec'.Calls with
                | [ (route, body) ] ->
                    Expect.equal route "/api/_platform/tenants/ListPrincipals" "the principal registry read"
                    Expect.equal body "[]" "a unit-argument call posts an empty argument array"
                | other -> failtestf "expected one call, got %A" other
            }

            test "every principal is rendered without the filter" {
                let body = joined (renderedPrincipals false)
                Expect.stringContains body "u1" "membered principal"
                Expect.stringContains body "u2" "team-less principal"
                Expect.stringContains body "3 principal(s)" "count footer"
            }

            test "--team-less keeps exactly the principals holding no membership row" {
                let body = joined (renderedPrincipals true)
                Expect.isFalse (body.Contains "u1") "u1 holds memberships"
                Expect.stringContains body "u2" "u2 is team-less"
                Expect.stringContains body "u3" "u3 is team-less"
                Expect.stringContains body "2 team-less principal(s) of 3" "the filter reports both counts"
            }

            test "a null LastSeenAt renders as a placeholder, not as 'null'" {
                let body = joined (renderedPrincipals true)
                Expect.isFalse (body.Contains "null") "an absent sign-in is not the word null"
            }

            test "membership team ids are read out of the (teamId, role) tuples" {
                let body = joined (renderedPrincipals false)
                Expect.stringContains body "team-a,team-b" "both team ids, role dropped"
            }

            test "offboard targets the user's personal scope" {
                let rec' = Recorder()

                Expect.equal
                    (Users.runOffboardUser (rec'.Transport cleanSummary) "u2" "stray account" false None)
                    ExitOk
                    "user offboard exits 0"

                match rec'.Calls with
                | [ (route, body) ] ->
                    Expect.equal route "/api/_platform/tenants/DeprovisionTenant" "the same method as a scope offboard"

                    Expect.equal
                        (argsOf body)
                        [ "user-u2"; Tenants.WireActor; "stray account" ]
                        "the user id becomes the user-<id> scope"
                | other -> failtestf "expected one call, got %A" other
            }

            test "the user scope name matches the substrate's convention" {
                Expect.equal (Users.userScope "u2") "user-u2" "user-<id>"
            }

            test "a user offboard inherits the confirmation gate unchanged" {
                let rec' = Recorder()

                Expect.equal
                    (Users.runOffboardUser (rec'.Transport confirmationRefusal) "u2" "stray" false None)
                    ExitRuntimeError
                    "the sugar does not soften the gate"

                Expect.equal
                    (Users.runOffboardUser (rec'.Transport cleanSummary) "u2" "stray" false (Some "tok-9"))
                    ExitOk
                    "and the confirmed path works through it"
            }
        ]

        // ── The bypass guarantee ────────────────────────────────────

        testList "no confirmation bypass" [
            test "no verb ever calls the token-minting method" {
                // Exhaustive over the flag combinations the CLI can
                // produce. Minting is a second-operator act; a client that
                // could mint its own token would dissolve the two-person
                // rule without a single failing test elsewhere.
                let rec' = Recorder()
                let t = rec'.Transport cleanSummary

                Tenants.runList t |> ignore
                Tenants.runPreview t "team-a" |> ignore
                Tenants.runOffboard t "team-a" "r" false None |> ignore
                Tenants.runOffboard t "team-a" "r" true None |> ignore
                Tenants.runOffboard t "team-a" "r" false (Some "tok") |> ignore
                Users.runList t false |> ignore
                Users.runList t true |> ignore
                Users.runOffboardUser t "u2" "r" false None |> ignore
                Users.runOffboardUser t "u2" "r" false (Some "tok") |> ignore

                Expect.isNonEmpty rec'.Routes "the sweep actually issued calls"

                Expect.isFalse
                    (rec'.Routes |> List.exists (fun r -> r.Contains "RequestDeprovisionToken"))
                    "the CLI never mints a confirmation token"

                Expect.isFalse
                    (rec'.Routes |> List.exists (fun r -> r.Contains "Schedule"))
                    "and never reaches for the scheduled path to dodge a refusal"
            }

            test "the confirmed method is reachable ONLY with an operator-supplied token" {
                let confirmedRoute = "/api/_platform/tenants/DeprovisionTenantConfirmed"

                let routesFor token =
                    let rec' = Recorder()

                    Tenants.runOffboard (rec'.Transport cleanSummary) "team-a" "r" false token
                    |> ignore

                    rec'.Routes

                Expect.isFalse (routesFor None |> List.contains confirmedRoute) "no token, no confirmed call"
                Expect.isTrue (routesFor (Some "t") |> List.contains confirmedRoute) "a token routes there"
            }
        ]

        // ── Endpoint resolution (550.C) ─────────────────────────────

        testList "endpoint resolution" [
            test "each half of the configuration is required, and named when absent" {
                match Tenants.resolveEndpoint None None with
                | Error message -> Expect.stringContains message "--endpoint" "names the missing flag"
                | Ok _ -> failtest "an unconfigured CLI must not resolve an endpoint"

                match Tenants.resolveEndpoint (Some "https://x") None with
                | Error message -> Expect.stringContains message "--token-file" "names the missing flag"
                | Ok _ -> failtest "an endpoint without a credential must not resolve"
            }

            test "the token is read from the file, trimmed, with the base url normalised" {
                let path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
                File.WriteAllText(path, "  bearer-value\r\n")

                try
                    match Tenants.resolveEndpoint (Some " https://app.example.com/ ") (Some path) with
                    | Ok endpoint ->
                        Expect.equal endpoint.Token "bearer-value" "trailing newline trimmed"

                        Expect.equal
                            endpoint.BaseUrl
                            "https://app.example.com"
                            "trailing slash stripped so route concatenation cannot double it"
                    | Error e -> failtest e
                finally
                    File.Delete path
            }

            test "an empty or unreadable token file is refused, not sent as an empty credential" {
                let empty = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
                File.WriteAllText(empty, "   \n")

                try
                    Expect.isError (Tenants.resolveEndpoint (Some "https://x") (Some empty)) "empty token file"
                finally
                    File.Delete empty

                let missing = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName())
                Expect.isError (Tenants.resolveEndpoint (Some "https://x") (Some missing)) "absent token file"
            }

            test "the credential is never taken from an argument" {
                // A bearer token in argv lands in shell history and in every
                // process listing, so the parser must have no option that
                // accepts one — `--token-file` takes a path. `--token` is the
                // *confirmation* token: single-use, scope-bound, not a
                // credential.
                let credentialFlags = [ "--admin-token"; "--bearer"; "--credential"; "--password" ]

                for flag in credentialFlags do
                    Expect.isError (Tenants.parse Tenants.defaultOptions [ flag; "secret" ]) flag
            }

            test "the credential is never taken from the environment either" {
                // TOOLUP_* is the DEPLOYMENT's config namespace, and
                // TOOLUP_ADMIN_TOKEN in it is a different secret entirely (the
                // shared crypto-shred token, replayed as X-Admin-Token). A CLI
                // that picked it up on a box running the server would send the
                // wrong secret under the wrong scheme. Setting them must have
                // no effect at all.
                let priorToken = Environment.GetEnvironmentVariable "TOOLUP_ADMIN_TOKEN"
                let priorEndpoint = Environment.GetEnvironmentVariable "TOOLUP_ADMIN_ENDPOINT"

                try
                    Environment.SetEnvironmentVariable("TOOLUP_ADMIN_TOKEN", "server-shared-secret")
                    Environment.SetEnvironmentVariable("TOOLUP_ADMIN_ENDPOINT", "https://wrong.example.com")

                    Expect.isError (Tenants.resolveEndpoint None None) "the environment configures nothing"
                finally
                    Environment.SetEnvironmentVariable("TOOLUP_ADMIN_TOKEN", priorToken)
                    Environment.SetEnvironmentVariable("TOOLUP_ADMIN_ENDPOINT", priorEndpoint)
            }

            test "routes carry the reserved admin prefixes" {
                Expect.equal (Tenants.tenantRoute "X") "/api/_platform/tenants/X" "tenant admin prefix"
                Expect.equal (Tenants.teamRoute "X") "/api/TeamApi/X" "team api prefix"
            }
        ]
    ]