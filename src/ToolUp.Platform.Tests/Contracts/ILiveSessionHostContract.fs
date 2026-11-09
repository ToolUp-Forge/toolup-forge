// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.ILiveSessionHostContract

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose

// ─── Phase 112 — ILiveSessionHost contract pack ───────────────────────
//
// The conformance bar any `ILiveSessionHost` implementation validates
// against (the in-process default; a future distributed companion).
// The load-bearing invariants: identity-by-value session addressing,
// async at every boundary, STRUCTURAL scope isolation (a session id
// resolved under another scope is `None`, never a cross-scope leak),
// in-order delivery within a single session, and idempotent
// unsubscribe / close.

let private scope (id: string) : StorageScope = {
    ScopeId = id
    Container = $"test-%s{id}"
    Persist = false
}

let private run a = a |> Async.RunSynchronously

/// A subscriber handler that appends each frame to `buffer`.
let private into (buffer: ResizeArray<string>) (frame: string) = async { lock buffer (fun () -> buffer.Add frame) }

let private openOk (host: ILiveSessionHost) (scopeId: string) : LiveSessionDescriptor =
    match host.OpenSession(scope scopeId) |> run with
    | Ok d -> d
    | Error refusal -> failtestf "OpenSession refused unexpectedly: %A" refusal

let tests (name: string) (factory: unit -> ILiveSessionHost) =
    testList $"{name} — ILiveSessionHost contract" [

        testCase "OpenSession returns a value descriptor in the requested scope"
        <| fun _ ->
            let host = factory ()
            let d = openOk host "scope-a"

            Expect.isNotEmpty d.SessionId "session id is a non-empty value"
            Expect.equal d.ScopeId "scope-a" "descriptor carries the opening scope"

        testCase "TryGetChannel resolves the session within its own scope"
        <| fun _ ->
            let host = factory ()
            let d = openOk host "scope-a"

            Expect.isSome (host.TryGetChannel("scope-a", d.SessionId) |> run) "own-scope resolution succeeds"

        testCase "cross-scope resolution is structurally None (GP 4)"
        <| fun _ ->
            let host = factory ()
            let d = openOk host "scope-a"

            Expect.isNone (host.TryGetChannel("scope-b", d.SessionId) |> run) "channel: other scope cannot resolve"

            Expect.isNone
                (host.Subscribe("scope-b", d.SessionId, fun _ -> async.Return()) |> run)
                "subscribe: other scope cannot attach"

        testCase "a pushed frame reaches the session's subscriber"
        <| fun _ ->
            let host = factory ()
            let d = openOk host "scope-a"
            let received = ResizeArray<string>()

            let _sub = (host.Subscribe("scope-a", d.SessionId, into received) |> run).Value

            let channel = (host.TryGetChannel("scope-a", d.SessionId) |> run).Value
            channel.PushFrame "frame-1" |> run

            Expect.equal (List.ofSeq received) [ "frame-1" ] "frame delivered"

        testCase "frames within one session arrive in push order (single-shard ordering)"
        <| fun _ ->
            let host = factory ()
            let d = openOk host "scope-a"
            let received = ResizeArray<string>()

            (host.Subscribe("scope-a", d.SessionId, into received) |> run) |> ignore

            let channel = (host.TryGetChannel("scope-a", d.SessionId) |> run).Value

            for i in 1..5 do
                channel.PushFrame $"frame-%d{i}" |> run

            Expect.equal (List.ofSeq received) [ "frame-1"; "frame-2"; "frame-3"; "frame-4"; "frame-5" ] "in push order"

        testCase "a frame addressed to one scope's session never reaches another scope's subscriber"
        <| fun _ ->
            let host = factory ()
            let dA = openOk host "scope-a"
            let dB = openOk host "scope-b"
            let receivedB = ResizeArray<string>()

            (host.Subscribe("scope-b", dB.SessionId, into receivedB) |> run) |> ignore

            let channelA = (host.TryGetChannel("scope-a", dA.SessionId) |> run).Value
            channelA.PushFrame "secret-for-a" |> run

            Expect.isEmpty receivedB "scope B's subscriber saw nothing"

        testCase "Unsubscribe stops delivery and is idempotent"
        <| fun _ ->
            let host = factory ()
            let d = openOk host "scope-a"
            let received = ResizeArray<string>()
            let sub = (host.Subscribe("scope-a", d.SessionId, into received) |> run).Value
            let channel = (host.TryGetChannel("scope-a", d.SessionId) |> run).Value

            channel.PushFrame "before" |> run
            host.Unsubscribe("scope-a", d.SessionId, sub) |> run
            host.Unsubscribe("scope-a", d.SessionId, sub) |> run
            channel.PushFrame "after" |> run

            Expect.equal (List.ofSeq received) [ "before" ] "nothing delivered after unsubscribe"

        testCase "CloseSession evicts the session and is idempotent"
        <| fun _ ->
            let host = factory ()
            let d = openOk host "scope-a"

            host.CloseSession("scope-a", d.SessionId) |> run
            host.CloseSession("scope-a", d.SessionId) |> run

            Expect.isNone (host.TryGetChannel("scope-a", d.SessionId) |> run) "closed session no longer resolves"

        testCase "ListSessions reflects opens and closes within the scope only"
        <| fun _ ->
            let host = factory ()
            let dA = openOk host "scope-a"
            let _dB = openOk host "scope-b"

            let listed = host.ListSessions "scope-a" |> run

            Expect.equal (listed |> List.map _.SessionId) [ dA.SessionId ] "only scope-a's session listed"

            host.CloseSession("scope-a", dA.SessionId) |> run
            Expect.isEmpty (host.ListSessions "scope-a" |> run) "closed session dropped from the listing"


        testCase "a push awaits every subscriber's handler before it completes (Phase 873)"
        <| fun _ ->
            let host = factory ()
            let d = openOk host "scope-a"
            let written = ResizeArray<string>()

            // The handler completes its write only after a real wait, so a
            // push that did not await it would return with nothing written.
            let slowWrite (frame: string) = async {
                do! Async.Sleep 50
                lock written (fun () -> written.Add frame)
            }

            (host.Subscribe("scope-a", d.SessionId, slowWrite) |> run) |> ignore
            let channel = (host.TryGetChannel("scope-a", d.SessionId) |> run).Value
            channel.PushFrame "awaited" |> run

            Expect.equal (List.ofSeq written) [ "awaited" ] "the subscriber's write is done when the push is"

        testCase "a failing subscriber is contained: the others still receive, and the push does not throw (Phase 873)"
        <| fun _ ->
            let host = factory ()
            let d = openOk host "scope-a"
            let received = ResizeArray<string>()

            let failing (_: string) : Async<unit> = async { return failwith "the subscriber's transport is gone" }

            (host.Subscribe("scope-a", d.SessionId, failing) |> run) |> ignore
            (host.Subscribe("scope-a", d.SessionId, into received) |> run) |> ignore
            let channel = (host.TryGetChannel("scope-a", d.SessionId) |> run).Value

            match Async.Catch(channel.PushFrame "frame") |> run with
            | Choice1Of2() -> ()
            | Choice2Of2 ex -> failtestf "a failing subscriber must not fail the push: %s" ex.Message

            Expect.equal (List.ofSeq received) [ "frame" ] "the healthy subscriber still received the frame"
    ]