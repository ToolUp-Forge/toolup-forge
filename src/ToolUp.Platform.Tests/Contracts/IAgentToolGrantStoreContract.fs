// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IAgentToolGrantStoreContract

open Expecto
open ToolUp.AI.McpHost

// ─── Phase 489 — IAgentToolGrantStore portability conformance pack ────
//
// Parametrised contract pack — any `IAgentToolGrantStore` implementation
// binds to it (the blob-backed default, the in-process dev store, or a
// companion over another substrate). `factory` returns a fresh store per
// run.
//
// **What is being held to a bar here is an authorisation floor, not a
// persistence convenience.** This store answers "which tools may this
// agent see and invoke", and every implementation of it must agree on
// three things that a careless one would get wrong in the unsafe
// direction:
//
//   1. **Absence is the EMPTY set, never unrestricted.** A store that
//      answered an unknown account with "everything" — or whose caller
//      read a `None` as "no restriction" — would hand a machine
//      credential the whole tool surface. The seam returns a value, not
//      an option, precisely so there is no such reading available.
//
//   2. **A write REPLACES.** An implementation that merged would make
//      withdrawal impossible through the only write the seam has, and
//      the acceptance clause "revocation takes effect on the next
//      request" would silently not hold.
//
//   3. **Scope isolation is structural (GP 4).** Two accounts with the
//      same id under different scopes are different records. A store
//      that keyed on the account alone would let one tenant's grants
//      answer for another's.
//
// Plus the six portability rules' observable half: identity by value
// (strings in, a record of primitives out), async at every boundary,
// nothing retained between calls (rule 4 — asserted by writing through
// ONE instance and reading back through it after an intervening write,
// which is what a cached implementation fails), and an idempotent
// remove.

let tests (name: string) (factory: unit -> IAgentToolGrantStore) =
    testList (sprintf "IAgentToolGrantStore conformance — %s" name) [

        testCaseAsync
            "an account with no record reaches NOTHING, not everything"
            (async {
                let store = factory ()
                let! read = store.Read("team-a", "never-granted")

                match read with
                | Error _ -> failtest "an absent record is a legitimate answer, not a failure"
                | Ok grants ->
                    Expect.isEmpty grants.GrantedTools "default-deny: an unknown account is granted nothing"

                    Expect.isFalse
                        (AgentToolGrants.grants "anything.at.all" grants)
                        "and the predicate agrees — this is the floor the whole host rests on"
            })

        testCaseAsync
            "a written grant set round-trips with its identity and its author"
            (async {
                let store = factory ()

                let! written =
                    store.Write("team-a", "acct-1", Set.ofList [ "sales.summarise"; "sales.forecast" ], "admin")

                match written with
                | Error _ -> failtest "a well-formed write must succeed"
                | Ok record ->
                    Expect.equal record.AccountId "acct-1" "the record names its account"
                    Expect.equal record.ScopeId "team-a" "and its scope"
                    Expect.equal record.UpdatedBy "admin" "and who wrote it (GP 6)"

                let! read = store.Read("team-a", "acct-1")

                match read with
                | Error _ -> failtest "the record just written must be readable"
                | Ok grants ->
                    Expect.equal
                        grants.GrantedTools
                        (Set.ofList [ "sales.summarise"; "sales.forecast" ])
                        "the grant set round-trips exactly"
            })

        testCaseAsync
            "a second write REPLACES rather than merges — otherwise nothing can be withdrawn"
            (async {
                let store = factory ()
                let! _ = store.Write("team-a", "acct-1", Set.ofList [ "a.one"; "a.two" ], "admin")
                let! _ = store.Write("team-a", "acct-1", Set.ofList [ "a.two" ], "admin")
                let! read = store.Read("team-a", "acct-1")

                match read with
                | Error _ -> failtest "the record must be readable"
                | Ok grants ->
                    Expect.equal
                        grants.GrantedTools
                        (Set.ofList [ "a.two" ])
                        "the narrower set wins whole — a merging store could never revoke a single tool"
            })

        testCaseAsync
            "the same account id under a different scope is a different record (GP 4)"
            (async {
                let store = factory ()
                let! _ = store.Write("team-a", "shared-id", Set.ofList [ "a.secret" ], "admin")
                let! read = store.Read("team-b", "shared-id")

                match read with
                | Error _ -> failtest "reading another scope's account id is an empty answer, not a failure"
                | Ok grants ->
                    Expect.isEmpty
                        grants.GrantedTools
                        "team-b must not see team-a's grants — the scope is part of the identity, not a filter \
                         applied afterwards"
            })

        testCaseAsync
            "a listing is bounded by the scope it was asked for"
            (async {
                let store = factory ()
                let! _ = store.Write("team-a", "acct-1", Set.ofList [ "a.one" ], "admin")
                let! _ = store.Write("team-a", "acct-2", Set.ofList [ "a.two" ], "admin")
                let! _ = store.Write("team-b", "acct-3", Set.ofList [ "b.one" ], "admin")

                let! listed = store.List "team-a"
                let ids = listed |> List.map _.AccountId |> List.sort

                Expect.equal ids [ "acct-1"; "acct-2" ] "one scope's listing enumerates that scope and no other"
            })

        testCaseAsync
            "remove returns the account to default-deny, and is idempotent"
            (async {
                let store = factory ()
                let! _ = store.Write("team-a", "acct-1", Set.ofList [ "a.one" ], "admin")
                let! first = store.Remove("team-a", "acct-1")
                Expect.isTrue (Result.isOk first) "removing an existing record succeeds"

                let! read = store.Read("team-a", "acct-1")

                match read with
                | Error _ -> failtest "a removed record reads as empty, not as a failure"
                | Ok grants -> Expect.isEmpty grants.GrantedTools "and the agent is back at the floor"

                let! second = store.Remove("team-a", "acct-1")

                Expect.isTrue
                    (Result.isOk second)
                    "removing what is not there succeeds — the post-state is the same either way"
            })

        testCaseAsync
            "nothing is retained between calls (GP 12 rule 4)"
            (async {
                // The revocation clause depends on this: an implementation
                // that cached a grant set in memory would keep answering
                // with the withdrawn tools on the node holding the cache,
                // and the host has no way to tell.
                let store = factory ()
                let! _ = store.Write("team-a", "acct-1", Set.ofList [ "a.one"; "a.two" ], "admin")
                let! before = store.Read("team-a", "acct-1")
                let! _ = store.Write("team-a", "acct-1", Set.empty, "admin")
                let! after = store.Read("team-a", "acct-1")

                match before, after with
                | Ok b, Ok a ->
                    Expect.equal b.GrantedTools (Set.ofList [ "a.one"; "a.two" ]) "the first read saw the first write"
                    Expect.isEmpty a.GrantedTools "and the read AFTER the withdrawal sees the withdrawal, immediately"
                | _ -> failtest "both reads must succeed"
            })
    ]