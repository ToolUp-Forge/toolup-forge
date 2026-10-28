// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts.Presentation

open ToolUp.Platform.AI
open ToolUp.Facts

/// What a series read answered for one metric: the gated series, or the
/// read's refusal verbatim (over the point cap it names the count and the cap).
[<RequireQualifiedAccess>]
type SeriesAnswer =
    /// The series the read served, already judged by the disclosure gate.
    | Answered of FactSeries
    /// The read refused this metric; the reason is the read's own text.
    | Refused of metric: string * reason: string

/// The `BlockProvenance` (Phase 1128) a built block carries: the request a
/// model wrote and the read that answered it. A builder fills it; nothing
/// here decides what is drawn.
[<RequireQualifiedAccess>]
module Provenance =

    /// The provenance of a block drawn for `request` from `answers`.
    ///
    /// `FactIds` lists every point's fact id in the order the reads returned
    /// them — a withheld point's id included, since the read served it (it is
    /// existence-level, never a value). `Withheld` sums the reads' withheld
    /// counts by disclosure policy, in the order the policies first appear.
    /// A refused read contributes nothing.
    let ofAnswers (request: PresentationRequest) (answers: SeriesAnswer list) : BlockProvenance =
        let served =
            answers
            |> List.choose (function
                | SeriesAnswer.Answered s -> Some s
                | SeriesAnswer.Refused _ -> None)

        let withheld =
            served
            |> List.collect _.Withheld
            |> List.fold
                (fun (acc: (string * int) list) w ->
                    if acc |> List.exists (fun (p, _) -> p = w.PolicyRef) then
                        acc |> List.map (fun (p, c) -> if p = w.PolicyRef then p, c + w.Count else p, c)
                    else
                        acc @ [ w.PolicyRef, w.Count ])
                []
            |> List.filter (fun (_, c) -> c > 0)
            |> List.map (fun (p, c) -> ({ PolicyRef = p; Count = c }: BlockWithheld))

        {
            Kind = PresentationRequest.kindName request.Kind
            Request = PresentationRequest.toJson request
            FactIds = served |> List.collect (fun s -> s.Points |> List.map _.FactId)
            Withheld = withheld
            TableId = request.TableId
            Metric = String.concat "," request.Metrics
            Period = PresentationRequest.periodText request
        }