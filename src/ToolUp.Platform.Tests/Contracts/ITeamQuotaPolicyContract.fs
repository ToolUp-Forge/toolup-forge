module ToolUp.Platform.Tests.Contracts.ITeamQuotaPolicyContract

open System
open Expecto
open ToolUp.Platform.Usage

// ─── ITeamQuotaPolicy contract pack ───────────────────────────────
//
// Parametrised tests for any `ITeamQuotaPolicy` implementation. The
// factory hands the test a pre-configured policy bound to scopeA
// with the supplied caps; scopeB is independent and always has no
// caps configured. Tests verify:
//   * Concurrency gate fires correctly at threshold
//   * Token-budget gate fires when exceeded
//   * Missing config = unrestricted
//   * Breach error shape (Kind / Limit / Requested / ScopeId)
//   * Per-team independence (Team A's breach doesn't block Team B)

type QuotaFactoryConfig = {
    /// Caps applied to scopeA. None for any field = unrestricted.
    MaxConcurrentJobs: int option
    MaxAITokensPerDay: decimal option
    MaxAITokensPerMonth: decimal option
}

module QuotaFactoryConfig =
    let unrestricted = {
        MaxConcurrentJobs = None
        MaxAITokensPerDay = None
        MaxAITokensPerMonth = None
    }

let tests (name: string) (factory: QuotaFactoryConfig -> ITeamQuotaPolicy * string * string) =

    let okOrFail label result =
        match result with
        | Ok v -> v
        | Error err -> failtestf "%s: expected Ok, got %A" label err

    let breachOrFail label result =
        match result with
        | Error b -> b
        | Ok _ -> failtestf "%s: expected QuotaBreached, got Ok" label

    testList $"{name} — ITeamQuotaPolicy contract" [

        // ─── Concurrency gate ────────────────────────────────

        testCaseAsync "Concurrency gate allows below limit"
        <| async {
            let cfg = {
                QuotaFactoryConfig.unrestricted with
                    MaxConcurrentJobs = Some 3
            }

            let policy, scopeA, _ = factory cfg
            let! result = policy.CheckJobConcurrency(scopeA, 2)
            okOrFail "expected Ok at currentInFlight=2 of 3" result
        }

        testCaseAsync "Concurrency gate breaches at limit"
        <| async {
            let cfg = {
                QuotaFactoryConfig.unrestricted with
                    MaxConcurrentJobs = Some 3
            }

            let policy, scopeA, _ = factory cfg
            let! result = policy.CheckJobConcurrency(scopeA, 3)
            let breach = breachOrFail "expected breach at currentInFlight=3" result
            Expect.equal breach.Kind "concurrency" "Kind = concurrency"
            Expect.equal breach.ScopeId scopeA "echoes the scope"
            Expect.equal breach.Limit 3M "Limit = 3"
        }

        // ─── Token-budget gate ───────────────────────────────

        testCaseAsync "Token-budget gate allows below limit"
        <| async {
            let cfg = {
                QuotaFactoryConfig.unrestricted with
                    MaxAITokensPerDay = Some 1000M
            }

            let policy, scopeA, _ = factory cfg
            let! result = policy.CheckTokenBudget(scopeA, ResourceKinds.aiTokensInput, 50M)
            okOrFail "expected Ok at requested=50 of 1000" result
        }

        testCaseAsync "Token-budget gate breaches at limit"
        <| async {
            let cfg = {
                QuotaFactoryConfig.unrestricted with
                    MaxAITokensPerDay = Some 100M
            }

            let policy, scopeA, _ = factory cfg
            // Request 200 against a 100-token budget — instant breach.
            let! result = policy.CheckTokenBudget(scopeA, ResourceKinds.aiTokensInput, 200M)
            let breach = breachOrFail "expected breach at requested=200" result
            Expect.equal breach.Kind ResourceKinds.aiTokensInput "echoes the kind"
            Expect.equal breach.ScopeId scopeA "echoes the scope"
            Expect.equal breach.Limit 100M "Limit = 100"
        }

        // ─── Missing config = unrestricted ───────────────────

        testCaseAsync "Missing config: concurrency check passes"
        <| async {
            let policy, scopeA, _ = factory QuotaFactoryConfig.unrestricted
            let! result = policy.CheckJobConcurrency(scopeA, 1000)
            okOrFail "expected Ok with no concurrency cap" result
        }

        testCaseAsync "Missing config: token-budget check passes"
        <| async {
            let policy, scopeA, _ = factory QuotaFactoryConfig.unrestricted

            let! result = policy.CheckTokenBudget(scopeA, ResourceKinds.aiTokensInput, 1_000_000M)

            okOrFail "expected Ok with no token-budget cap" result
        }

        // ─── Per-team independence ───────────────────────────

        testCaseAsync "Per-team independence: scopeB unaffected by scopeA breach"
        <| async {
            // ScopeA has tight cap; scopeB inherits the same factory
            // but factories scope per-call so scopeB has no config —
            // the contract is "scopeB's check is unaffected by
            // scopeA's state".
            let cfg = {
                QuotaFactoryConfig.unrestricted with
                    MaxConcurrentJobs = Some 1
            }

            let policy, scopeA, scopeB = factory cfg
            let! aBreach = policy.CheckJobConcurrency(scopeA, 1)
            let! bOk = policy.CheckJobConcurrency(scopeB, 1000)
            breachOrFail "scopeA over cap" aBreach |> ignore
            okOrFail "scopeB has no cap configured" bOk
        }
    ]