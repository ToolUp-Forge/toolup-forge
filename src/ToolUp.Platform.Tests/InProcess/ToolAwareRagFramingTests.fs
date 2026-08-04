module ToolUp.Platform.Tests.InProcess.ToolAwareRagFramingTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.AI.SystemPromptBuilder
open ToolUp.RAG

// ─── Phase 14r — Tool-aware RAG framing ──────────────────────────────
//
// Pure-function coverage for the three shipped surfaces:
//   1. `RAGPromptBuilder.ToolFraming.fromTools` — a live-interface tool
//      (one declaring `IsLiveInterface = true`, or any client-resident
//      tool) flips `HasLiveUiTools`; server-resident analytical tools
//      (incl. the `_platform.ai.*` read family) do not.
//      Phase 538 retargeted this from the `_platform.ui.*` name-prefix
//      trigger to the typed flag — see the by-flag and
//      false-positive-guard cases below.
//   2. `RAGCompose.resolveFramingWithTools` — the live-interface companion
//      is appended only under `Preferred` + `HasLiveUiTools`; `Permissive`
//      / `StrictlyGrounded` and the no-UI-tools case are unchanged (no
//      regression).
//   3. `RAGPromptBuilder.withRetrievalToolAware` empty-result message —
//      redirects to the inspection tool when UI tools are present,
//      byte-identical to the historical wording otherwise.

let private mkTool (name: string) (location: ToolLocation) : AIToolDefinition = {
    Name = name
    Description = "test tool"
    Parameters = []
    SourceModule = "test"
    EmitsActions = None
    Location = location
    Surface = Both
    IsLiveInterface = false
}

/// Stub pipeline whose `Retrieve` always returns `[]` — exercises the
/// empty-result branch of the retrieval builder without a vector store.
let private emptyPipeline =
    { new IRetrievalPipeline with
        member _.Retrieve _ _ = async { return [] }
        member _.Index _ _ _ = async { return () }
        member _.DeleteByScope _ = async { return () }
    }

let private mkContext () : PromptContext = {
    Access = AccessContext.unrestricted (AnonymousSession "anon")
    ActiveModule = None
    ActivePage = None
    ActivePageNarrative = None
    ModuleContexts = Map.empty
    CurrentMessage = Some "what filters do I currently have applied?"
    ConversationHistory = []
    RetrievalFilters = None
    RetrievedSources = ref []
    ShortCircuit = ref None
}

/// Phase 538 — a tool that DECLARES the live-interface capability. Named
/// off the `_platform.ui.*` convention deliberately: the flag, not the
/// name, is what the framing keys off.
let private declaredLiveInterfaceTool = {
    mkTool "acme_host.read_active_view" ServerResident with
        IsLiveInterface = true
}

let private uiInspectTool =
    mkTool "_platform.ui.inspect_active_module" ClientResident

let private serverReadTool =
    mkTool "_platform.ai.list_accessible_modules" ServerResident

/// Phase 538 — the pre-538 FALSE POSITIVE: a server-resident tool that
/// merely happens to carry the `_platform.ui.` name prefix. It reads
/// persisted data like any other server tool and must NOT trip the
/// live-interface framing.
let private serverToolWithUiName = mkTool "_platform.ui.audit_report" ServerResident

[<Tests>]
let tests =
    testList "Phase 14r — Tool-aware RAG framing" [

        test "Phase 538 — fromTools flags a tool DECLARING IsLiveInterface" {
            let framing = RAGPromptBuilder.ToolFraming.fromTools [ declaredLiveInterfaceTool ]

            Expect.isTrue
                framing.HasLiveUiTools
                "a tool declaring IsLiveInterface = true is a live-interface tool, whatever it is called"
        }

        test "Phase 538 — a server-resident tool NAMED _platform.ui.* is NOT flagged" {
            let framing = RAGPromptBuilder.ToolFraming.fromTools [ serverToolWithUiName ]

            Expect.isFalse
                framing.HasLiveUiTools
                "the pre-538 name-prefix trigger's false positive: a server tool must not trip framing on its name"
        }

        test "fromTools still flags the client-resident inspect tool (ClientResident implication retained)" {
            let framing = RAGPromptBuilder.ToolFraming.fromTools [ uiInspectTool ]

            Expect.isTrue
                framing.HasLiveUiTools
                "a client-resident tool is live-interface by construction, flag or no flag"
        }

        test "fromTools flags any client-resident tool as live-interface" {
            let framing =
                RAGPromptBuilder.ToolFraming.fromTools [ mkTool "some_module.drive_ui" ClientResident ]

            Expect.isTrue framing.HasLiveUiTools "a client-resident tool drives live UI state"
        }

        test "fromTools does NOT flag server-resident analytical tools" {
            let framing = RAGPromptBuilder.ToolFraming.fromTools [ serverReadTool ]

            Expect.isFalse
                framing.HasLiveUiTools
                "the _platform.ai.* server read family reads persisted data, not live UI state"
        }

        test "fromTools on an empty tool list is not live-interface" {
            Expect.isFalse (RAGPromptBuilder.ToolFraming.fromTools []).HasLiveUiTools "no tools ⇒ no live UI"
        }

        test "Preferred + UI tools appends the live-interface companion" {
            let withUi = {
                RAGPromptBuilder.ToolFraming.none with
                    HasLiveUiTools = true
            }

            let resolved =
                RAGCompose.resolveFramingWithTools RAGCompose.Preferred RAGCompose.defaultRetrievalFraming withUi

            Expect.stringContains resolved RAGCompose.defaultRetrievalFraming "base KB framing is preserved"
            Expect.stringContains resolved RAGCompose.uiToolFramingCompanion "the companion is appended"
        }

        test "Preferred without UI tools is byte-identical to plain resolveFraming (no regression)" {
            let resolved =
                RAGCompose.resolveFramingWithTools
                    RAGCompose.Preferred
                    RAGCompose.defaultRetrievalFraming
                    RAGPromptBuilder.ToolFraming.none

            Expect.equal
                resolved
                (RAGCompose.resolveFraming RAGCompose.Preferred RAGCompose.defaultRetrievalFraming)
                "no UI tools ⇒ unchanged KB-first framing"

            Expect.isFalse
                (resolved.Contains RAGCompose.uiToolFramingCompanion)
                "the companion must not leak into a no-UI-tools deployment"
        }

        test "StrictlyGrounded ignores UI tools (refusal contract stands)" {
            let withUi = {
                RAGPromptBuilder.ToolFraming.none with
                    HasLiveUiTools = true
            }

            let resolved =
                RAGCompose.resolveFramingWithTools RAGCompose.StrictlyGrounded RAGCompose.defaultRetrievalFraming withUi

            Expect.equal
                resolved
                (RAGCompose.resolveFraming RAGCompose.StrictlyGrounded RAGCompose.defaultRetrievalFraming)
                "StrictlyGrounded must not be softened by the live-interface companion"
        }

        test "Permissive stays empty even with UI tools" {
            let withUi = {
                RAGPromptBuilder.ToolFraming.none with
                    HasLiveUiTools = true
            }

            let resolved =
                RAGCompose.resolveFramingWithTools RAGCompose.Permissive RAGCompose.defaultRetrievalFraming withUi

            Expect.equal resolved "" "Permissive emits no framing regardless of tools"
        }

        testAsync "empty retrieval with UI tools redirects to the inspection tool" {
            let withUi = {
                RAGPromptBuilder.ToolFraming.none with
                    HasLiveUiTools = true
            }

            let builder =
                RAGPromptBuilder.withRetrievalToolAware RetrievalDefaults.defaults None None withUi emptyPipeline

            let! block = builder (mkContext ())
            Expect.stringContains block "interface inspection tool" "empty + UI tools nudges the inspection path"
        }

        testAsync "empty retrieval without UI tools keeps the historical wording" {
            let builder =
                RAGPromptBuilder.withRetrievalToolAware
                    RetrievalDefaults.defaults
                    None
                    None
                    RAGPromptBuilder.ToolFraming.none
                    emptyPipeline

            let! block = builder (mkContext ())

            Expect.equal
                block
                "Knowledge-base search ran for this turn and returned no relevant matches."
                "no UI tools ⇒ byte-identical historical empty-retrieval message"
        }

        testAsync "back-compat withRetrieval matches the no-UI-tools tool-aware path" {
            let ctxA = mkContext ()
            let ctxB = mkContext ()

            let! viaBackCompat = RAGPromptBuilder.withRetrieval RetrievalDefaults.defaults None None emptyPipeline ctxA

            let! viaToolAware =
                RAGPromptBuilder.withRetrievalToolAware
                    RetrievalDefaults.defaults
                    None
                    None
                    RAGPromptBuilder.ToolFraming.none
                    emptyPipeline
                    ctxB

            Expect.equal viaBackCompat viaToolAware "the 4-arg wrapper is the ToolFraming.none path"
        }
    ]