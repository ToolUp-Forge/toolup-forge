module ToolUp.Platform.Tests.InProcess.RagConfigValidatorExtensionTests

// ─── Phase 9m.B — RAG-specific config validators (extension) ────────
//
// The 2026-05-06 ToolUp.RAG gap audit named four config-knob failures
// that a deployment cannot see: an index that silently does not survive
// a restart (Gap 3), a dev-only embedder quietly serving production
// traffic (Gap 4), a RAG deployment that will never index anything
// (Gap 6), and tuning knobs with no upper bound at all (Gap 7).
//
// Every one of them starts cleanly. That is what makes them expensive:
// the symptom arrives later, somewhere else, and points at the wrong
// component. So each validator here is pinned by THREE cases, not one:
//
//   • a RED case proving it fires, asserting on the specific remedy the
//     message names — not merely that some string came back. A message
//     that fires without naming `withDurableIngestionQueue`-style
//     next steps is a validator that has told the operator they have a
//     problem and left them to find the fix;
//   • a GREEN case proving it stays silent on a healthy config, so the
//     red case is not vacuous;
//   • a GATING case proving it stays silent on a deployment the concern
//     does not apply to. This is the one that stops a validator family
//     becoming noise an operator learns to scroll past — and it is the
//     case that would otherwise never be written, because a validator
//     that over-fires still looks like it works.

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.RAG.RagConfigValidator

// ─── Fixtures ───────────────────────────────────────────────────────

/// Minimal `IEmbeddingProvider` whose only interesting property is
/// `ProviderId` — the field every embedder validator dispatches on. The
/// embedding members are never called by a config validator (that is
/// itself part of the contract: preflight must not perform I/O), so
/// they fail loudly rather than returning a plausible empty vector that
/// would let an accidental call pass unnoticed.
let private embedder (providerId: string) =
    { new IEmbeddingProvider with
        member _.ProviderId = providerId
        member _.ModelId = "test-model"
        member _.Dimensions = 8

        member _.GenerateEmbedding(_text) =
            failwith "a config validator must not embed anything"

        member _.GenerateEmbeddings(_texts) =
            failwith "a config validator must not embed anything"
    }

let private localEmbedder = embedder "local"
let private hostedEmbedder = embedder "openai"

let private configWith (surfaces: SurfaceProfile list) = {
    ServerConfig.defaults with
        Surfaces = surfaces
}

let private run (v: IConfigValidator) = v.Validate() |> Async.RunSynchronously

/// Assert `Warning`, and that the message carries every fragment an
/// operator needs to act. `Expect.stringContains` per fragment rather
/// than one big equality so the assertion survives copy-editing of the
/// prose but not the removal of a remedy.
let private expectWarningNaming (fragments: string list) (result: ValidationResult) =
    match result with
    | Warning msg ->
        for f in fragments do
            Expect.stringContains msg f (sprintf "warning names '%s'" f)
    | other -> failtestf "expected Warning, got %A" other

let private expectErrorNaming (fragments: string list) (result: ValidationResult) =
    match result with
    | Error msg ->
        for f in fragments do
            Expect.stringContains msg f (sprintf "error names '%s'" f)
    | other -> failtestf "expected Error, got %A" other

// ─── Gap 3 — RAG durability + its escape hatch ──────────────────────

let private persistenceTests =
    testList "Gap 3 — RagPersistenceValidator: the ephemeral-index escape hatch" [
        test "persistent deployment, no durable backing → Error (the refusal is unchanged)" {
            let cfg = configWith Surfaces.individual

            RagPersistenceValidator(cfg, false, false)
            |> run
            |> expectErrorNaming [
                "DISCARDS bytes"
                // The refusal must now also name the way past it —
                // before this phase there was none, so a deliberately
                // ephemeral deployment simply could not boot.
                "AcceptEphemeralRagIndex"
                "TOOLUP_ACCEPT_EPHEMERAL_RAG_INDEX=1"
            ]
        }

        test "escape hatch degrades the refusal to a Warning — it does NOT silence it" {
            let cfg = {
                configWith Surfaces.individual with
                    AcceptEphemeralRagIndex = true
            }

            // The whole point of the GP 13 escape-hatch shape: an
            // operator opts past the refusal by name, and the choice
            // stays legible in /dev/inspect afterwards. A hatch that
            // returned Ok would make a deployment that loses its corpus
            // on every restart indistinguishable from one that does not.
            RagPersistenceValidator(cfg, false, false)
            |> run
            |> expectWarningNaming [ "EPHEMERAL by explicit operator opt-in"; "re-ingested after every restart" ]
        }

        test "GREEN — blob storage supplied → Ok, hatch or no hatch" {
            let cfg = configWith Surfaces.individual
            Expect.equal (run (RagPersistenceValidator(cfg, true, false))) Ok "IBlobStorage is durable backing"
            Expect.equal (run (RagPersistenceValidator(cfg, false, true))) Ok "an IVectorStore override is too"
        }

        test "GATING — an ephemeral deployment still gets a Warning, never the refusal" {
            // Anonymous is non-persistent BY DESIGN: refusing it would
            // be refusing the shape working as intended.
            configWith Surfaces.anonymous
            |> fun cfg -> RagPersistenceValidator(cfg, false, false)
            |> run
            |> expectWarningNaming [ "Surfaces are ephemeral" ]
        }
    ]

// ─── Gap 4 — the dev-only embedder in a production shape ────────────

let private localEmbedderTests =
    testList "Gap 4 — LocalEmbeddingProviderInProductionModeValidator" [
        test "RED — local embedder in an Individual deployment → Warning naming the remedy" {
            LocalEmbeddingProviderInProductionModeValidator(configWith Surfaces.individual, localEmbedder)
            |> run
            |> expectWarningNaming [
                "Individual"
                "process-stateful"
                "RAGServerApp.create"
                "TOOLUP_ACCEPT_LOCAL_EMBEDDER_AT_SCALE=1"
            ]
        }

        test "RED — AuthenticatedEphemeral (trial) is production-shaped too" {
            LocalEmbeddingProviderInProductionModeValidator(configWith Surfaces.trial, localEmbedder)
            |> run
            |> expectWarningNaming [ "AuthenticatedEphemeral" ]
        }

        test "GREEN — a hosted, stateless embedder is silent in the same deployment" {
            Expect.equal
                (run (LocalEmbeddingProviderInProductionModeValidator(configWith Surfaces.individual, hostedEmbedder)))
                Ok
                "the concern is the local embedder's statefulness, not the deployment shape"
        }

        test "GREEN — the escape hatch silences it" {
            let cfg = {
                configWith Surfaces.individual with
                    AcceptLocalEmbedderAtScale = true
            }

            Expect.equal
                (run (LocalEmbeddingProviderInProductionModeValidator(cfg, localEmbedder)))
                Ok
                "explicit, named opt-in"
        }

        test "GATING — an Anonymous-only deployment is not nagged" {
            Expect.equal
                (run (LocalEmbeddingProviderInProductionModeValidator(configWith Surfaces.anonymous, localEmbedder)))
                Ok
                "a public demo has no per-user corpus whose quality could drift"
        }

        test "GATING — Team mode belongs to TeamModeLocalEmbedderValidator, and ONLY to it" {
            // The load-bearing case for the whole gating story: the two
            // validators name the same remedy, so if both fired on a
            // team deployment the operator would be told once to swap
            // embedder and twice that they have a problem. Asserting
            // both halves together is what stops a later edit to either
            // one silently re-introducing the overlap.
            let cfg = configWith Surfaces.team

            Expect.equal
                (run (LocalEmbeddingProviderInProductionModeValidator(cfg, localEmbedder)))
                Ok
                "this validator stands down on team shapes"

            TeamModeLocalEmbedderValidator(cfg, localEmbedder)
            |> run
            |> expectWarningNaming [ "IDF dictionary is shared across all teams" ]
        }

        test "the two validators share ONE escape hatch" {
            // Two flags for one remedy would be a trap: an operator
            // silences the warning they saw, scales to a team surface,
            // and the family starts talking again.
            let cfg = {
                configWith Surfaces.team with
                    AcceptLocalEmbedderAtScale = true
            }

            Expect.equal (run (TeamModeLocalEmbedderValidator(cfg, localEmbedder))) Ok "same flag, team half"
        }
    ]

// ─── Gap 6 — nothing will ever be indexed ───────────────────────────

let private handlerTests =
    testList "Gap 6 — RAGHandlersRegisteredValidator" [
        test "RED — data types registered, no handlers → Warning listing the unhandled ids" {
            RAGHandlersRegisteredValidator([ "SalesData"; "OptimisationData" ], [], false)
            |> run
            |> expectWarningNaming [
                // The ids are the actionable part — "add a handler" is
                // useless without "for these types".
                "SalesData"
                "OptimisationData"
                "2 data type(s)"
                "VectorisationHandler"
            ]
        }

        test "GREEN — one handler present → Ok" {
            Expect.equal
                (run (RAGHandlersRegisteredValidator([ "SalesData" ], [ "SalesData" ], false)))
                Ok
                "the deployment indexes something"
        }

        test "GATING — a retrieval-pipeline override owns its own corpus" {
            // The static-corpus shape: chunk embeddings are precomputed
            // at build time and composeRAG suppresses live ingestion on
            // exactly this condition. Warning here would fire on every
            // static-doc deployment, forever.
            Expect.equal
                (run (RAGHandlersRegisteredValidator([ "SalesData" ], [], true)))
                Ok
                "withRetrievalPipeline is the documented no-handler shape"
        }

        test "GATING — no data types at all → Ok (a document-only / KB-only deployment)" {
            Expect.equal (run (RAGHandlersRegisteredValidator([], [], false))) Ok "nothing to vectorise"
        }
    ]

// ─── Gap 7 — bounds on the tuning knobs ─────────────────────────────

let private inBounds: RagConfigBounds = {
    // The shipped defaults, verbatim — the pin that a deployment which
    // tunes nothing is silent. If a default ever moves outside its own
    // validator's range, this test is the one that says so.
    TopK = 5
    MinScore = None
    MmrLambda = 0.5
    MmrEnabled = false
    SnippetCharLimit = 240
    IngestionConcurrency = 8
    IngestionQueueCapacity = 5000
}

let private boundsTests =
    testList "Gap 7 — RAGConfigBoundsValidator" [
        test "GREEN — the shipped defaults are silent" {
            Expect.equal (run (RAGConfigBoundsValidator inBounds)) Ok "an untuned deployment must never be nagged"
        }

        test "RED — withTopK 200 refuses boot with a bounds message (the acceptance criterion)" {
            RAGConfigBoundsValidator { inBounds with TopK = 200 }
            |> run
            |> expectErrorNaming [ "TopK = 200"; "outside [1, 100]"; "RAGServerApp.withTopK" ]
        }

        test "WARN — TopK above 50 is legal but flagged, not refused" {
            // The severity split is the interesting part: 60 is a real
            // (if unusual) choice, so refusing it would be the SDK
            // overruling an operator. 200 is not.
            RAGConfigBoundsValidator { inBounds with TopK = 60 }
            |> run
            |> expectWarningNaming [ "TopK = 60"; "legal but unusually high" ]
        }

        test "RED — SnippetCharLimit below 32 (the setter clamps only to 16)" {
            // Precisely the hole this validator exists to close: the
            // setter's `max 16` accepts 20, so nothing today rejects it.
            RAGConfigBoundsValidator { inBounds with SnippetCharLimit = 20 }
            |> run
            |> expectErrorNaming [ "SnippetCharLimit = 20"; "outside [32, 8192]" ]
        }

        test "RED — IngestionConcurrency above 64" {
            RAGConfigBoundsValidator {
                inBounds with
                    IngestionConcurrency = 500
            }
            |> run
            |> expectErrorNaming [ "IngestionConcurrency = 500"; "outside [1, 64]" ]
        }

        test "RED — IngestionQueueCapacity below 100" {
            RAGConfigBoundsValidator {
                inBounds with
                    IngestionQueueCapacity = 10
            }
            |> run
            |> expectErrorNaming [ "IngestionQueueCapacity = 10"; "outside [100, 1000000]" ]
        }

        test "RED — MinScore outside the cosine range" {
            RAGConfigBoundsValidator { inBounds with MinScore = Some 1.5 }
            |> run
            |> expectErrorNaming [ "MinScore = 1.5"; "outside [0.0, 1.0]" ]
        }

        test "GREEN — MinScore = None is not a bound violation" {
            Expect.equal
                (run (RAGConfigBoundsValidator { inBounds with MinScore = None }))
                Ok
                "None disables the gate; it is not an out-of-range value"
        }

        test "GATING — an out-of-range MmrLambda is inert while MMR is off" {
            // MmrLambda only participates when MMR is enabled. Erroring
            // on a value nothing reads would refuse boot over a field
            // that has no effect.
            Expect.equal
                (run (
                    RAGConfigBoundsValidator {
                        inBounds with
                            MmrLambda = 7.0
                            MmrEnabled = false
                    }
                ))
                Ok
                "λ is not read unless MMR is on"

            RAGConfigBoundsValidator {
                inBounds with
                    MmrLambda = 7.0
                    MmrEnabled = true
            }
            |> run
            |> expectErrorNaming [ "MmrLambda = 7"; "outside [0.0, 1.0]" ]
        }

        test "several violations are reported together, not one per boot" {
            // An operator fixing a config should learn everything wrong
            // with it in one cycle. Reporting the first violation only
            // turns a three-knob typo into three restarts.
            RAGConfigBoundsValidator {
                inBounds with
                    TopK = 0
                    SnippetCharLimit = 99999
                    IngestionConcurrency = 0
            }
            |> run
            |> expectErrorNaming [ "TopK = 0"; "SnippetCharLimit = 99999"; "IngestionConcurrency = 0" ]
        }
    ]

// ─── /dev/inspect contributors ──────────────────────────────────────

let private contributorTests =
    testList "Phase 9m.B — /dev/inspect panels" [
        testAsync "RAG durability panel names the posture and the opt-in" {
            let! name, payload =
                (RagDurabilityContributor(false, false, true) :> IDevDiagnosticsContributor).Contribute()

            Expect.equal name "RAG durability" "panel name"
            let rendered = sprintf "%A" payload
            Expect.stringContains rendered "ephemeral" "states the posture"
            Expect.stringContains rendered "AcceptEphemeralRagIndex" "and how it got there"
        }

        testAsync "durable deployment reports durable" {
            let! _, payload = (RagDurabilityContributor(true, false, false) :> IDevDiagnosticsContributor).Contribute()

            Expect.stringContains (sprintf "%A" payload) "durable" "blob storage is durable backing"
        }

        testAsync "Vectorisation-handler panel surfaces PARTIAL coverage, which no validator warns about" {
            // The reason the panel is registered unconditionally: the
            // validator only speaks when the handler list is empty, so
            // "two types registered, one indexed" — the far more common
            // production question — is answered by silence otherwise.
            let! name, payload =
                (VectorisationHandlerContributor([ "SalesData"; "OptimisationData" ], [ "SalesData"; "Orphaned" ])
                :> IDevDiagnosticsContributor)
                    .Contribute()

            Expect.equal name "Vectorisation handlers" "panel name"
            let rendered = sprintf "%A" payload
            Expect.stringContains rendered "OptimisationData" "the registered type nothing will index"
            Expect.stringContains rendered "Orphaned" "and the handler that will never fire"
        }
    ]

let tests =
    testList "Phase 9m.B — RAG-specific config validators (extension)" [
        persistenceTests
        localEmbedderTests
        handlerTests
        boundsTests
        contributorTests
    ]