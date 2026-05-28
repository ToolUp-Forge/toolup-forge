module ToolUp.Platform.Tests.InProcess.UserClaimsTests

open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts

// ─── Phase 62 — IUserClaims in-process contract binding ───────────
//
// Two factories drive the portable `IUserClaimsContract` pack:
//   * `NoOpUserClaims` — the SDK's default. Records the operator's
//     intent on grant / revoke but does not persist; bound with
//     `persists = false` so the round-trip cases are skipped (the
//     contract pack documents this as the explicit exemption — see
//     IUserClaimsContract.fs header).
//   * `InMemoryUserClaims` — a small test-only impl that persists in
//     a `ConcurrentDictionary`. Mirrors the surface a production
//     provider-backed impl exposes (Clerk, OIDC), so the persisting
//     round-trip assertions get coverage.

type private InMemoryUserClaims() =
    let store = ConcurrentDictionary<string, PremiumStatus>()

    interface IUserClaims with
        member _.GetPremiumStatus(userId: string) = async {
            match store.TryGetValue userId with
            | true, status -> return status
            | false, _ -> return NotPremium
        }

        member _.ListPremiumUsers() = async {
            let entries =
                store
                |> Seq.choose (fun kvp ->
                    match kvp.Value with
                    | Premium _ -> Some(kvp.Key, kvp.Value)
                    | NotPremium -> None)
                |> Seq.toList

            return Ok entries
        }

        member _.GrantPremium(userId, grantor, reason) = async {
            let status = Premium(System.DateTimeOffset.UtcNow, grantor, reason)
            store[userId] <- status
            return Ok status
        }

        member _.RevokePremium(userId, _grantor, _reason) = async {
            store.TryRemove userId |> ignore
            return Ok()
        }

// ─── Portable contract bindings ───────────────────────────────────

let noOpTests =
    let factory () = NoOpUserClaims() :> IUserClaims
    IUserClaimsContract.tests "NoOpUserClaims" false factory

let inMemoryTests =
    let factory () = InMemoryUserClaims() :> IUserClaims
    IUserClaimsContract.tests "InMemoryUserClaims" true factory

let tests = testList "IUserClaims contract bindings" [ noOpTests; inMemoryTests ]