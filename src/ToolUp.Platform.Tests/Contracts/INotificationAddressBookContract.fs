module ToolUp.Platform.Tests.Contracts.INotificationAddressBookContract

open System
open Expecto
open ToolUp.Platform

/// Cross-implementation contract for `INotificationAddressBook`.
/// Phase 6f — bound by every concrete `INotificationAddressBook`
/// implementation (the SDK-default `BlobBackedNotificationAddressBook`,
/// future LDAP / Okta / directory companions).
///
/// `addressBook` is a fresh instance constructed by the binding test.
/// `populate` lets the binding test seed contact records in whatever
/// storage the implementation uses (blob upload for the default; an
/// LDAP fixture for a directory companion). Bindings that can't
/// populate (e.g. read-only auth-provider-driven impls) skip the
/// "populated lookup" cases by passing a populator that no-ops.
let tests (name: string) (factory: unit -> INotificationAddressBook) (populate: string -> UserContact -> Async<unit>) =
    let uniqueScope () =
        let suffix = Guid.NewGuid().ToString("N").Substring(0, 8)
        "scope-test-" + suffix

    testList $"{name} — INotificationAddressBook contract" [
        testCaseAsync "ResolveEmail returns None for an unknown user"
        <| async {
            let book = factory ()
            let! result = book.ResolveEmail("user-does-not-exist", uniqueScope ())
            Expect.isNone result "no contact = no email"
        }

        testCaseAsync "ResolvePhone returns None for an unknown user"
        <| async {
            let book = factory ()
            let! result = book.ResolvePhone("user-does-not-exist", uniqueScope ())
            Expect.isNone result "no contact = no phone"
        }

        testCaseAsync "ResolvePushTokens returns [] for an unknown user"
        <| async {
            let book = factory ()
            let! result = book.ResolvePushTokens("user-does-not-exist", uniqueScope ())
            Expect.isEmpty result "no contact = no tokens"
        }

        testCaseAsync "Populated email round-trips through ResolveEmail"
        <| async {
            let book = factory ()
            let scope = uniqueScope ()

            let contact = {
                UserId = "user-A"
                Email =
                    Some {
                        Address = "alice@example.com"
                        DisplayName = Some "Alice"
                    }
                Phone = None
                PushTokens = []
            }

            do! populate scope contact

            let! result = book.ResolveEmail("user-A", scope)

            match result with
            | Some addr ->
                Expect.equal addr.Address "alice@example.com" "address round-trips"
                Expect.equal addr.DisplayName (Some "Alice") "display name round-trips"
            | None -> failtest "expected populated email to resolve"
        }

        testCaseAsync "Populated push tokens round-trip"
        <| async {
            let book = factory ()
            let scope = uniqueScope ()

            let contact = {
                UserId = "user-A"
                Email = None
                Phone = None
                PushTokens = [
                    {
                        Token = "https://push.example/abc"
                        Platform = "WebPush"
                    }
                    {
                        Token = "https://push.example/xyz"
                        Platform = "WebPush"
                    }
                ]
            }

            do! populate scope contact

            let! tokens = book.ResolvePushTokens("user-A", scope)
            Expect.equal tokens.Length 2 "both tokens round-trip"
        }

        testCaseAsync "Scope isolation: contact in scope A is not visible from scope B"
        <| async {
            let book = factory ()
            let scopeA = uniqueScope ()
            let scopeB = uniqueScope ()

            let contact = {
                UserId = "user-A"
                Email =
                    Some {
                        Address = "alice@a.example.com"
                        DisplayName = None
                    }
                Phone = None
                PushTokens = []
            }

            do! populate scopeA contact

            let! crossScope = book.ResolveEmail("user-A", scopeB)
            Expect.isNone crossScope "scope-isolated lookup must not leak across scopes"

            let! sameScope = book.ResolveEmail("user-A", scopeA)
            Expect.isSome sameScope "lookup in the populated scope still works"
        }
    ]