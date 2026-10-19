module MyDataPlatform.Tests.Scripted

open System
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.AI

// ─── A scripted model and a constant embedder ─────────────────────
//
// The end-to-end test runs the real composition with no network and no API
// key. The model is the one part replaced: it reads one fact through the
// fact tools, then writes the report citing it, as a model is asked to.

/// The report the model writes once it has read a fact: the headline cites
/// the fact by id; the other sections carry no figure at all.
let private answer (factId: string) (rendering: string) =
    JsonSerializer.Serialize {|
        sections = [
            {|
                id = "headline"
                paragraphs = [ $"The uk-web site took [[Revenue|{rendering}|{factId}]] on the day." ]
            |}
            {|
                id = "drivers"
                paragraphs = [ "Search led the channels on revenue." ]
            |}
            {|
                id = "questions"
                paragraphs = [ "Is the social channel's order value holding up?" ]
            |}
        ]
    |}

/// First turn: ask the fact tools for the uk-web site's revenue. Second
/// turn: write the report citing the first fact returned.
type ScriptedModel() =
    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "scripted"
            Model = "scripted-model"
        }

        member _.SendMessage(messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            match messages |> List.collect _.ToolResults |> List.tryLast with
            | None ->
                return
                    Ok {
                        Content = ""
                        ToolCalls = [
                            {
                                Id = Guid.NewGuid().ToString()
                                Name = "query_facts"
                                Arguments =
                                    """{"subject_hierarchy":"site","subject_path":"uk-web","metric":"revenue"}"""
                            }
                        ]
                        StopReason = "tool_use"
                        Usage = None
                    }
            | Some result ->
                let fact =
                    match (JsonDocument.Parse result.Content).RootElement.TryGetProperty "facts" with
                    | true, facts when facts.GetArrayLength() > 0 -> facts.EnumerateArray() |> Seq.head
                    | _ -> failwithf "query_facts returned no fact: %s" result.Content

                return
                    Ok {
                        Content =
                            answer (fact.GetProperty("factId").GetString()) (fact.GetProperty("rendering").GetString())
                        ToolCalls = []
                        StopReason = "end_turn"
                        Usage = None
                    }
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

/// Every caller resolves the scripted model.
type ScriptedFactory(provider: IAIProvider) =
    interface IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Ok provider }
        member _.TryResolveByLabel(_, _) = async { return Error NoProviderConfigured }
        member _.BuildPlatform(_, _, _) = None

let private unit: float32 array =
    Array.init 8 (fun i -> if i = 0 then 1.0f else 0.0f)

/// An embedder that maps every text to one vector: retrieval is not under
/// test here, and a real embedding model would download weights.
let constantEmbedder =
    { new IEmbeddingProvider with
        member _.GenerateEmbedding _ = async { return unit }
        member _.GenerateEmbeddings texts = async { return texts |> Seq.map (fun _ -> unit) |> Seq.toArray }
        member _.ProviderId = "constant"
        member _.ModelId = "constant"
        member _.Dimensions = 8
    }