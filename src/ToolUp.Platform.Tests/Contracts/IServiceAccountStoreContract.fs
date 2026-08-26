module ToolUp.Platform.Tests.Contracts.IServiceAccountStoreContract

open System
open Expecto
open ToolUp.Platform

// ─── IServiceAccountStore contract pack (Phase 527) ──────────────────
//
// Parametrised tests for any `IServiceAccountStore` implementation. The
// factory hands back a fresh `(store, scopeA, scopeB)` triple so the
// cross-scope isolation cases have two genuinely distinct scopes to work
// with, matching `IShareTokenStoreContract`'s shape.
//
// The load-bearing cases, in the order the phase's Acceptance states
// them:
//
//   * a minted token authenticates as its account with EXACTLY the
//     declared permission set — not a superset, and not the empty map
//     that would read as unrestricted;
//   * a revoked token, an expired token, and every token of a DISABLED
//     account are refused, each with its own typed reason;
//   * the mint response is the only exposure of the secret (the record
//     the store hands back carries a hash, never the secret; the
//     on-disk assertion lives in the binding, which can see storage);
//   * a token scoped to team A can never reach team B (GP 4);
//   * an empty declared permission set is refused at create, at update,
//     and at validation.
//
// Plus the two authority-meet directions, which are the part of this
// design most likely to be broken by a well-meaning later change:
// narrowing the ACCOUNT must narrow every outstanding token, and
// widening the account must NOT widen a token minted before it.

let private okOrFail label result =
    match result with
    | Ok v -> v
    | Error err -> failtestf "%s: expected Ok, got %A" label err

let private errOrFail label result =
    match result with
    | Ok v -> failtestf "%s: expected Error, got Ok %A" label v
    | Error err -> err

let private readOnly = Map.ofList [ "reports", [ ModulePermission.Read ] ]

let private readWrite =
    Map.ofList [ "reports", [ ModulePermission.Read ]; "exports", [ ModulePermission.Write ] ]

let private createAccount (store: IServiceAccountStore) scopeId name permissions = async {
    let! created =
        store.Create {
            DisplayName = name
            ScopeId = scopeId
            Permissions = permissions
            CreatedBy = "alice"
        }

    return okOrFail $"create {name}" created
}

let private mint (store: IServiceAccountStore) (account: ServiceAccount) label expiresAt = async {
    let! minted =
        store.MintToken {
            AccountId = account.AccountId
            ScopeId = account.ScopeId
            DisplayName = label
            IssuedBy = "alice"
            ExpiresAt = expiresAt
        }

    return okOrFail $"mint {label}" minted
}

let tests (name: string) (factory: unit -> IServiceAccountStore * string * string) =

    testList $"{name} — IServiceAccountStore contract" [

        // ─── Create / read / list ────────────────────────────────────

        testCaseAsync "Create returns an Active account readable by Get"
        <| async {
            let store, scopeA, _ = factory ()
            let! account = createAccount store scopeA "nightly-export" readOnly

            Expect.equal account.Status ServiceAccountStatus.Active "a new account starts Active"
            Expect.equal account.ScopeId scopeA "the account is owned by the requested scope"
            Expect.equal account.Permissions readOnly "the declared permission set round-trips"

            let! fetched = store.Get(scopeA, account.AccountId)
            Expect.equal (okOrFail "get" fetched) account "Get returns the created account"
        }

        testCaseAsync "Create refuses an empty permission set"
        <| async {
            let store, scopeA, _ = factory ()

            let! result =
                store.Create {
                    DisplayName = "wide-open"
                    ScopeId = scopeA
                    Permissions = Map.empty
                    CreatedBy = "alice"
                }

            Expect.equal
                (errOrFail "create empty" result)
                ServiceAccountError.NoPermissionsDeclared
                "an empty map reads as UNRESTRICTED downstream, so it must be refused at the door"
        }

        testCaseAsync "Create refuses a module key mapped to an empty grant list"
        <| async {
            let store, scopeA, _ = factory ()

            let! result =
                store.Create {
                    DisplayName = "hollow"
                    ScopeId = scopeA
                    Permissions = Map.ofList [ "reports", [] ]
                    CreatedBy = "alice"
                }

            Expect.equal
                (errOrFail "create hollow" result)
                ServiceAccountError.NoPermissionsDeclared
                "the same hole one level down — a key with no grants is not a grant"
        }

        testCaseAsync "List returns only the requested scope's accounts"
        <| async {
            let store, scopeA, scopeB = factory ()
            let! a = createAccount store scopeA "a-account" readOnly
            let! b = createAccount store scopeB "b-account" readOnly

            let! listedA = store.List scopeA
            let! listedB = store.List scopeB

            Expect.contains (listedA |> List.map _.AccountId) a.AccountId "scope A lists its own account"

            Expect.isFalse
                (listedA |> List.exists (fun x -> x.AccountId = b.AccountId))
                "scope A never lists scope B's account (GP 4)"

            Expect.contains (listedB |> List.map _.AccountId) b.AccountId "scope B lists its own account"
        }

        testCaseAsync "Get refuses another scope's account rather than returning it"
        <| async {
            let store, scopeA, scopeB = factory ()
            let! b = createAccount store scopeB "b-account" readOnly

            let! result = store.Get(scopeA, b.AccountId)

            Expect.isError (Result.map ignore result) "a cross-scope Get never succeeds"
        }

        // ─── Token mint + validation ─────────────────────────────────

        testCaseAsync "a minted token validates to its account with exactly the declared permissions"
        <| async {
            let store, scopeA, _ = factory ()
            let! account = createAccount store scopeA "ci" readWrite
            let! minted = mint store account "deploy-key" None

            let! validated = store.ValidateToken minted.Secret
            let principal = okOrFail "validate" validated

            Expect.equal principal.AccountId account.AccountId "the principal is the minting account"
            Expect.equal principal.ScopeId scopeA "the principal carries the account's scope"
            Expect.equal principal.Permissions readWrite "exactly the declared set — no more, no less"
        }

        testCaseAsync "the persisted token record never carries the secret"
        <| async {
            let store, scopeA, _ = factory ()
            let! account = createAccount store scopeA "ci" readOnly
            let! minted = mint store account "deploy-key" None

            Expect.isFalse
                (minted.Record.SecretHash.Contains minted.Secret)
                "the persisted hash is not the secret, nor does it contain it"

            let! listed = store.ListTokens(scopeA, account.AccountId)

            for token in listed do
                Expect.isFalse (token.SecretHash.Contains minted.Secret) "no listed record leaks the secret"
                Expect.isFalse (token.Salt.Contains minted.Secret) "nor does the salt"
        }

        testCaseAsync "a token string that is not ours is refused as Malformed"
        <| async {
            let store, _, _ = factory ()

            let! result = store.ValidateToken "Bearer-ish-nonsense"

            Expect.equal
                (errOrFail "malformed" result)
                ServiceAccountError.Malformed
                "no prefix means it was never one of our tokens"
        }

        testCaseAsync "a token with a valid id and a wrong secret is refused"
        <| async {
            let store, scopeA, _ = factory ()
            let! account = createAccount store scopeA "ci" readOnly
            let! minted = mint store account "deploy-key" None

            // Same token id, a secret one character different.
            let tokenId, secret =
                match ServiceAccountTypes.tryParseToken minted.Secret with
                | Ok pair -> pair
                | Error e -> failtestf "the store minted an unparseable token: %A" e

            let tampered = ServiceAccountTypes.formatToken tokenId (secret + "x")

            let! result = store.ValidateToken tampered

            Expect.equal
                (errOrFail "wrong secret" result)
                ServiceAccountError.InvalidSecret
                "holding the id is not holding the credential"
        }

        testCaseAsync "an expired token is refused"
        <| async {
            let store, scopeA, _ = factory ()
            let! account = createAccount store scopeA "ci" readOnly
            let! minted = mint store account "already-stale" (Some(DateTimeOffset.UtcNow.AddMinutes -1.0))

            let! result = store.ValidateToken minted.Secret

            Expect.equal (errOrFail "expired" result) ServiceAccountError.Expired "expiry is enforced at validation"
        }

        testCaseAsync "a revoked token is refused, and revocation is idempotent"
        <| async {
            let store, scopeA, _ = factory ()
            let! account = createAccount store scopeA "ci" readOnly
            let! minted = mint store account "deploy-key" None

            let! first = store.RevokeToken(scopeA, minted.Record.TokenId, "alice")
            okOrFail "revoke" first |> ignore

            let! result = store.ValidateToken minted.Secret
            Expect.equal (errOrFail "revoked" result) ServiceAccountError.RevokedToken "a revoked token stops working"

            let! second = store.RevokeToken(scopeA, minted.Record.TokenId, "alice")
            Expect.isOk (Result.map ignore second) "revoking twice is a success, not an error"
        }

        testCaseAsync "disabling the account refuses every one of its tokens wholesale"
        <| async {
            let store, scopeA, _ = factory ()
            let! account = createAccount store scopeA "ci" readOnly
            let! first = mint store account "key-one" None
            let! second = mint store account "key-two" None

            let! disabled = store.SetStatus(scopeA, account.AccountId, ServiceAccountStatus.Disabled, "alice")
            okOrFail "disable" disabled |> ignore

            let! firstResult = store.ValidateToken first.Secret
            let! secondResult = store.ValidateToken second.Secret

            Expect.equal
                (errOrFail "first" firstResult)
                ServiceAccountError.AccountDisabled
                "a disabled account's tokens are refused"

            Expect.equal
                (errOrFail "second" secondResult)
                ServiceAccountError.AccountDisabled
                "…all of them, not just the first"
        }

        testCaseAsync "re-enabling the account restores its tokens without a re-mint"
        <| async {
            let store, scopeA, _ = factory ()
            let! account = createAccount store scopeA "ci" readOnly
            let! minted = mint store account "deploy-key" None

            let! _ = store.SetStatus(scopeA, account.AccountId, ServiceAccountStatus.Disabled, "alice")
            let! _ = store.SetStatus(scopeA, account.AccountId, ServiceAccountStatus.Active, "alice")

            let! result = store.ValidateToken minted.Secret

            Expect.equal
                (okOrFail "revalidate" result).AccountId
                account.AccountId
                "disable is a reversible kill switch, not a mass revocation"
        }

        testCaseAsync "a disabled account cannot mint fresh credentials"
        <| async {
            let store, scopeA, _ = factory ()
            let! account = createAccount store scopeA "ci" readOnly
            let! _ = store.SetStatus(scopeA, account.AccountId, ServiceAccountStatus.Disabled, "alice")

            let! result =
                store.MintToken {
                    AccountId = account.AccountId
                    ScopeId = scopeA
                    DisplayName = "sneaky"
                    IssuedBy = "alice"
                    ExpiresAt = None
                }

            Expect.equal
                (errOrFail "mint while disabled" result)
                ServiceAccountError.AccountDisabled
                "otherwise 'disable' only pauses the tokens that already exist"
        }

        // ─── Cross-scope isolation (GP 4) ────────────────────────────

        testCaseAsync "a token scoped to A resolves to A's scope and never B's"
        <| async {
            let store, scopeA, scopeB = factory ()
            let! account = createAccount store scopeA "a-ci" readOnly
            let! minted = mint store account "deploy-key" None

            let! validated = store.ValidateToken minted.Secret
            let principal = okOrFail "validate" validated

            Expect.equal principal.ScopeId scopeA "the resolved scope is the account's own"
            Expect.notEqual principal.ScopeId scopeB "and is never the other scope"
        }

        testCaseAsync "revoking a token from the wrong scope does not revoke it"
        <| async {
            let store, scopeA, scopeB = factory ()
            let! account = createAccount store scopeA "a-ci" readOnly
            let! minted = mint store account "deploy-key" None

            let! wrongScope = store.RevokeToken(scopeB, minted.Record.TokenId, "mallory")
            Expect.isError (Result.map ignore wrongScope) "scope B cannot name scope A's token"

            let! stillValid = store.ValidateToken minted.Secret
            Expect.isOk (Result.map ignore stillValid) "and the token is untouched by the attempt"
        }

        testCaseAsync "ListTokens never returns another scope's tokens"
        <| async {
            let store, scopeA, scopeB = factory ()
            let! a = createAccount store scopeA "a-ci" readOnly
            let! aToken = mint store a "a-key" None

            let! fromB = store.ListTokens(scopeB, a.AccountId)

            Expect.isFalse
                (fromB |> List.exists (fun t -> t.TokenId = aToken.Record.TokenId))
                "the scope prefix is the isolation, and it holds"
        }

        // ─── The authority meet, both directions ─────────────────────

        testCaseAsync "narrowing the account narrows an already-minted token"
        <| async {
            let store, scopeA, _ = factory ()
            let! account = createAccount store scopeA "ci" readWrite
            let! minted = mint store account "deploy-key" None

            let! narrowed = store.SetPermissions(scopeA, account.AccountId, readOnly, "alice")
            okOrFail "narrow" narrowed |> ignore

            let! validated = store.ValidateToken minted.Secret
            let principal = okOrFail "validate" validated

            Expect.equal
                principal.Permissions
                readOnly
                "the live account is the ceiling — narrowing it bites on the next request"
        }

        testCaseAsync "widening the account does NOT widen a token minted before the widening"
        <| async {
            let store, scopeA, _ = factory ()
            let! account = createAccount store scopeA "ci" readOnly
            let! minted = mint store account "deploy-key" None

            let! widened = store.SetPermissions(scopeA, account.AccountId, readWrite, "alice")
            okOrFail "widen" widened |> ignore

            let! validated = store.ValidateToken minted.Secret
            let principal = okOrFail "validate" validated

            Expect.equal
                principal.Permissions
                readOnly
                "the mint-time snapshot is the other half of the meet — a credential cannot silently gain authority"

            // …but a token minted AFTER the widening does carry it.
            let! reloaded = store.Get(scopeA, account.AccountId)
            let! fresh = mint store (okOrFail "reload" reloaded) "second-key" None
            let! freshValidated = store.ValidateToken fresh.Secret

            Expect.equal
                (okOrFail "validate fresh" freshValidated).Permissions
                readWrite
                "a token minted after the widening carries the wider set"
        }

        testCaseAsync "SetPermissions refuses an empty set"
        <| async {
            let store, scopeA, _ = factory ()
            let! account = createAccount store scopeA "ci" readOnly

            let! result = store.SetPermissions(scopeA, account.AccountId, Map.empty, "alice")

            Expect.equal
                (errOrFail "empty update" result)
                ServiceAccountError.NoPermissionsDeclared
                "the create-time rule holds on the update path too"
        }

        testCaseAsync "SetPermissions refuses another scope's account"
        <| async {
            let store, scopeA, scopeB = factory ()
            let! account = createAccount store scopeA "a-ci" readOnly

            let! result = store.SetPermissions(scopeB, account.AccountId, readWrite, "mallory")

            Expect.isError (Result.map ignore result) "authority cannot be granted across a scope boundary"
        }
    ]

// ─── Pure-function tests (no implementation needed) ──────────────────
//
// `effectivePermissions` and `tryParseToken` are total functions over
// data, so they are tested directly rather than through a store. They
// are where the design's subtlety actually lives — the meet has to
// honour the `ModulePermission.implies` hierarchy rather than
// intersecting lists, and getting that wrong produces a credential that
// looks scoped and is not.

let pureTests =
    testList "ServiceAccountTypes" [

        test "the meet honours the implies hierarchy rather than intersecting lists" {
            // Snapshot granted Admin; the account has since narrowed to
            // Write. A naive list intersection is EMPTY here, which would
            // drop the module entirely — the correct answer is everything
            // Write confers.
            let snapshot = Map.ofList [ "reports", [ ModulePermission.Admin ] ]
            let live = Map.ofList [ "reports", [ ModulePermission.Write ] ]

            let effective = ServiceAccountTypes.effectivePermissions live snapshot
            let granted = effective |> Map.find "reports"

            Expect.equal
                granted
                [ ModulePermission.Write ]
                "Write is admitted (Admin covers it) and is the MAXIMAL admitted grant, so it is the whole answer"

            Expect.isFalse
                (List.contains ModulePermission.Admin granted)
                "but not Admin — the account no longer holds it"

            // The reduction to maxima is not cosmetic: it is what keeps
            // the returned map comparable to the map an operator
            // declared. Assert the closure is NOT what comes back.
            Expect.isFalse
                (List.contains ModulePermission.SchemaOnly granted)
                "the downward closure is reduced away — Write already implies SchemaOnly"
        }

        test "a plain Read grant comes back as Read, not as its closure" {
            let both = Map.ofList [ "reports", [ ModulePermission.Read ] ]

            Expect.equal
                (ServiceAccountTypes.effectivePermissions both both)
                both
                "the meet of a set with itself is that set — anything else is not comparable to what was declared"
        }

        test "the meet is symmetric in which side is narrower" {
            let wide = Map.ofList [ "reports", [ ModulePermission.Admin ] ]
            let narrow = Map.ofList [ "reports", [ ModulePermission.Read ] ]

            Expect.equal
                (ServiceAccountTypes.effectivePermissions wide narrow)
                (ServiceAccountTypes.effectivePermissions narrow wide)
                "it is a meet, so the order of the arguments cannot change the answer"
        }

        test "a module absent from either side is dropped" {
            let live =
                Map.ofList [ "reports", [ ModulePermission.Read ]; "exports", [ ModulePermission.Read ] ]

            let snapshot = Map.ofList [ "reports", [ ModulePermission.Read ] ]

            let effective = ServiceAccountTypes.effectivePermissions live snapshot

            Expect.equal (effective |> Map.toList |> List.map fst) [ "reports" ] "only the module both sides grant"
        }

        test "SchemaOnly does not silently confer Read" {
            let live = Map.ofList [ "reports", [ ModulePermission.Read ] ]
            let snapshot = Map.ofList [ "reports", [ ModulePermission.SchemaOnly ] ]

            let granted =
                ServiceAccountTypes.effectivePermissions live snapshot
                |> Map.find "reports"
                |> Set.ofList

            Expect.equal
                granted
                (Set.ofList [ ModulePermission.SchemaOnly ])
                "the Phase 30d carve-out survives the meet — a schema-only token stays schema-only"
        }

        test "validatePermissions refuses empty and hollow maps" {
            Expect.equal
                (ServiceAccountTypes.validatePermissions Map.empty)
                (Error ServiceAccountError.NoPermissionsDeclared)
                "empty is refused"

            Expect.equal
                (ServiceAccountTypes.validatePermissions (Map.ofList [ "reports", [] ]))
                (Error ServiceAccountError.NoPermissionsDeclared)
                "a key with no grants is refused"

            Expect.equal
                (ServiceAccountTypes.validatePermissions (Map.ofList [ "reports", [ ModulePermission.Read ] ]))
                (Ok())
                "one real grant is enough"
        }

        test "formatToken and tryParseToken round-trip" {
            let formatted = ServiceAccountTypes.formatToken "abc123" "s3cr3t"

            Expect.stringStarts formatted ServiceAccountTypes.TokenPrefix "the prefix is what makes it greppable"

            Expect.equal
                (ServiceAccountTypes.tryParseToken formatted)
                (Ok("abc123", "s3cr3t"))
                "the round-trip is exact"
        }

        test "tryParseToken refuses every malformed shape" {
            let cases = [
                "", "empty"
                "   ", "whitespace"
                "abc123.s3cr3t", "no prefix"
                ServiceAccountTypes.TokenPrefix, "prefix only"
                ServiceAccountTypes.TokenPrefix + "abc123", "no separator"
                ServiceAccountTypes.TokenPrefix + ".s3cr3t", "empty id"
                ServiceAccountTypes.TokenPrefix + "abc123.", "empty secret"
            ]

            for candidate, label in cases do
                Expect.equal
                    (ServiceAccountTypes.tryParseToken candidate)
                    (Error ServiceAccountError.Malformed)
                    $"{label} is malformed"
        }

        test "classifyToken reports revocation ahead of expiry" {
            let baseToken = {
                TokenId = "t"
                AccountId = "a"
                ScopeId = "s"
                Salt = "salt"
                SecretHash = "hash"
                DisplayName = "d"
                IssuedBy = "alice"
                IssuedAt = DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
                ExpiresAt = DateTimeOffset(2020, 2, 1, 0, 0, 0, TimeSpan.Zero)
                Revoked = false
                ScopeSnapshot = Map.empty
            }

            let now = DateTimeOffset(2020, 1, 15, 0, 0, 0, TimeSpan.Zero)
            let after = DateTimeOffset(2020, 3, 1, 0, 0, 0, TimeSpan.Zero)

            Expect.equal (ServiceAccountTypes.classifyToken now baseToken) (Ok()) "live inside its window"

            Expect.equal
                (ServiceAccountTypes.classifyToken after baseToken)
                (Error ServiceAccountError.Expired)
                "expired past it"

            Expect.equal
                (ServiceAccountTypes.classifyToken after { baseToken with Revoked = true })
                (Error ServiceAccountError.RevokedToken)
                "a token that is BOTH reports revoked — the deliberate act outranks the lapse, and it is what an operator needs to see"
        }
    ]