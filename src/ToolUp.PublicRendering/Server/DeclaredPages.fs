// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.PublicRendering

open System
open Giraffe.ViewEngine
open ToolUp.Platform
open ToolUp.Platform.Config
open ToolUp.Platform.Infra
open ToolUp.Platform.Narrative
open ToolUp.Platform.Compose
open ToolUp.Platform.Jobs
open ToolUp.Platform.Scope

// ─── Declared pages ─────────────────────────────────────
//
// A page an application declares as data: its route, audience, layout and
// body. The declarations sit in front of the content API, so forge's page
// handler gates and renders them as it does any page, a slug none of them
// matches falls through to the API behind them, and the gated enumeration
// stays forge's own predicate over the whole set. The audience rides on the
// page: an `IContentSource` would synthesise it `Public` and enumerate it.

/// The grounded narrative a declared page serves: the latest one its run
/// published, with the page's own body over it.
type ServedNarrative = {
    /// The grounded-narrative run key whose latest narrative the page reads.
    Run: string
    /// The page's body over the route's captures and that narrative.
    Body: Map<string, string> -> NarrativeDocument -> ContentBody
    /// For a `Publication` page: the user id of a declared principal of the
    /// publishing team, the identity the disclosure re-check resolves its
    /// scope through. `None` withholds a narrative that cites a fact.
    PublishingPrincipal: string option
}

/// One page an application declares.
type DeclaredPage = {
    /// A route shape (`RouteShape`): `insights/latest`, `insights/{date}`.
    Route: string
    Title: string
    /// The registered layout that frames the page. Compose refuses a name
    /// nobody registered.
    Layout: LayoutName
    Audience: PageAudience
    /// The predicate each named capture's value must satisfy. A slug whose
    /// capture does not hold matches no declared page (`DeclaredPages.Capture`
    /// holds the common ones). A capture left out here takes any segment.
    Captures: (string * (string -> bool)) list
    /// The page's body over the route's captures.
    Body: Map<string, string> -> ContentBody
    Narrative: ServedNarrative option
}

/// How a declared page reads its narrative for one reader: the page's served
/// narrative, audience and slug, and the reader.
type DeclaredNarrativeRead =
    ServedNarrative -> PageAudience -> string -> AccessContext -> Async<NarrativeDocument option>

[<RequireQualifiedAccess>]
module DeclaredPages =

    /// The capture predicates a route commonly declares.
    module Capture =
        let private isDigit (c: char) = c >= '0' && c <= '9'

        /// An ISO 8601 calendar date, YYYY-MM-DD, that exists.
        let date (value: string) : bool =
            value.Length = 10
            && value
               |> Seq.mapi (fun i c -> if i = 4 || i = 7 then c = '-' else isDigit c)
               |> Seq.forall id
            && (let year = int (value.Substring(0, 4))
                let month = int (value.Substring(5, 2))
                let day = int (value.Substring(8, 2))

                year >= 1
                && month >= 1
                && month <= 12
                && day >= 1
                && day <= DateTime.DaysInMonth(year, month))

        /// Runs of lowercase ASCII letters and digits joined by single hyphens.
        let slug (value: string) : bool =
            value <> ""
            && value |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || isDigit c || c = '-')
            && not (value.StartsWith("-", StringComparison.Ordinal))
            && not (value.EndsWith("-", StringComparison.Ordinal))
            && not (value.Contains("--", StringComparison.Ordinal))

        /// An optional `-` then ASCII digits, within the signed 32-bit range.
        let integer (value: string) : bool =
            let digits =
                if value.StartsWith("-", StringComparison.Ordinal) then
                    value.Substring 1
                else
                    value

            digits <> ""
            && Seq.forall isDigit digits
            && fst (
                Int32.TryParse(
                    value,
                    Globalization.NumberStyles.AllowLeadingSign,
                    Globalization.CultureInfo.InvariantCulture
                )
            )

    let private isCaptured (page: DeclaredPage) =
        page.Route.Contains("{", StringComparison.Ordinal)

    /// What is wrong with the declarations themselves, apart from their
    /// layouts: a route declared twice, and a predicate on a capture its route
    /// does not have, which would never run and leave the segment unchecked.
    let shapeRefusals (pages: DeclaredPage list) : string list =
        let repeated =
            pages
            |> List.countBy _.Route
            |> List.filter (fun (_, count) -> count > 1)
            |> List.map (fun (route, _) -> sprintf "route '%s' is declared more than once" route)

        let unknownCaptures =
            pages
            |> List.collect (fun page ->
                page.Captures
                |> List.choose (fun (name, _) ->
                    if page.Route.Contains("{" + name + "}", StringComparison.Ordinal) then
                        None
                    else
                        Some(sprintf "route '%s' has no capture '%s' to check" page.Route name)))

        repeated @ unknownCaptures

    /// Refuse declarations no request could serve: a layout nobody registered
    /// (`LayoutRefusedException`, the refusal the handler would otherwise answer
    /// 500 with on every request) or a `shapeRefusals` finding.
    let refuseAtCompose (layouts: Map<LayoutName, PublicPage -> XmlNode>) (pages: DeclaredPage list) : unit =
        match shapeRefusals pages with
        | [] -> ()
        | refusals -> invalidOp ("PublicRendering: declared pages: " + String.concat "; " refusals)

        let refused =
            pages
            |> List.choose (fun page ->
                match PublicLayouts.resolve layouts page.Layout with
                | Ok _ -> None
                | Error refusal -> Some(sprintf "declared page '%s'" page.Route, refusal))

        if not refused.IsEmpty then
            raise (LayoutRefusedException refused)

    let private pageOf (slug: string) (page: DeclaredPage) (captures: Map<string, string>) : PublicPage = {
        Slug = Slug slug
        Title = page.Title
        Description = ""
        Body = page.Body captures
        Layout = page.Layout
        Frontmatter = Map.empty
        PublishedAt = None
        Collection = None
        Status = Published
        Audience = page.Audience
    }

    let private capturesOf (page: DeclaredPage) (slug: string) : Map<string, string> option =
        RouteShape.tryMatch page.Route slug
        |> Option.filter (fun found ->
            page.Captures
            |> List.forall (fun (name, holds) -> found |> Map.tryFind name |> Option.forall holds))

    /// The declared pages in front of `inner`, which answers every slug none of
    /// them matches. Literal routes match before captured ones, whatever the
    /// declaration order, so a capture never shadows a literal. A captured route
    /// names a family of pages nobody listed, so it is never enumerated. No
    /// pages leaves `inner` as it is.
    let over
        (readNarrative: DeclaredNarrativeRead)
        (pages: DeclaredPage list)
        (inner: IPublicContentApi)
        : IPublicContentApi =
        let literalFirst = pages |> List.sortBy isCaptured

        let matching (slug: string) =
            literalFirst
            |> List.tryPick (fun page -> capturesOf page slug |> Option.map (fun found -> page, found))

        let listed = pages |> List.filter (isCaptured >> not)

        if pages.IsEmpty then
            inner
        else
            { new IPublicContentApi with
                member _.GetPage slug = async {
                    match matching slug with
                    | Some(page, found) -> return Some(pageOf slug page found)
                    | None -> return! inner.GetPage slug
                }

                member _.GetPageInContext(slug, ctx) = async {
                    match matching slug with
                    | Some(page, found) ->
                        let plain = pageOf slug page found

                        match page.Narrative with
                        | None -> return Some plain
                        | Some served ->
                            match! readNarrative served page.Audience slug ctx with
                            | Some document ->
                                return
                                    Some {
                                        plain with
                                            Body = served.Body found document
                                    }
                            | None -> return Some plain
                    | None -> return! inner.GetPageInContext(slug, ctx)
                }

                member _.ListPages prefix = async {
                    let ours =
                        listed
                        |> List.filter (fun page -> page.Route.StartsWith(prefix, StringComparison.Ordinal))
                        |> List.map (fun page -> pageOf page.Route page Map.empty)

                    let declared = ours |> List.map _.Slug |> Set.ofList
                    let! theirs = inner.ListPages prefix

                    // A declared route answers its slug, so the page it shadows is not listed.
                    return
                        (theirs |> List.filter (fun page -> not (declared.Contains page.Slug))) @ ours
                        |> List.sortBy (fun page -> Slug.value page.Slug)
                }

                member this.ListPagesPublic(now, prefix) =
                    PublicContentApi.defaultListPagesPublic this now prefix

                member _.GetCollection collectionId = inner.GetCollection collectionId
            }

    /// The tag a grounded-narrative run's narratives carry.
    let private runTag (run: string) = "grounded-run:" + run

    /// The scope a publication's disclosure re-check runs in: its declared
    /// principal, through the platform's resolver. A principal that does not
    /// resolve, or resolves to another scope, is logged and answers `None`, and
    /// the read then withholds a narrative citing a fact.
    let private publishingScope
        (logger: ILogger)
        (principals: DeclaredPrincipalResolver option)
        (served: ServedNarrative)
        (audience: PublicationAudience)
        : Async<ResolvedScope option> =
        async {
            match served.PublishingPrincipal, principals with
            | Some userId, Some resolver ->
                match! DeclaredPrincipal.resolveInScope resolver audience.PublishingScope userId with
                | Ok scope -> return Some scope
                | Error refusal ->
                    logger.Warn(
                        sprintf
                            "PublicRendering: publication '%s': %s; a narrative citing a fact is withheld"
                            audience.PublishingScope
                            refusal
                    )

                    return None
            | _ -> return None
        }

    /// The narrative read over composed services. A `Publication` page reads the
    /// publishing scope through `PublicationRead`: the audience decides, disclosure
    /// is re-checked and the read is audited. Any other page reads the reader's own
    /// scope once its audience admits the reader. A page answers its own body when
    /// there is nothing to serve.
    ///
    /// The composition root resolves `principals` and `narratives` in its
    /// registration factory and hands them over as values.
    let narrativeReadIn
        (config: ServerConfig)
        (logger: ILogger)
        (principals: DeclaredPrincipalResolver option)
        (narratives: INarrativeStore option)
        (services: IServiceProvider)
        : DeclaredNarrativeRead =
        fun served audience slug reader -> async {
            match audience with
            | PageAudience.Publication publication ->
                let! publishing = publishingScope logger principals served publication

                match!
                    PublicationRead.latestNarrativeIn services publishing publication slug (runTag served.Run) reader
                with
                | PublicationNarrative.Served(_, document) -> return Some document
                | _ -> return None
            | _ ->
                match
                    AudienceGate.evaluate reader audience,
                    narratives,
                    Middleware.StorageScopeDerivation.tryFromSubject (Some logger) config reader.Subject
                with
                | AudienceDecision.Allow, Some store, Some scope ->
                    match! store.ListByTag(scope.ScopeId, 1, Some(runTag served.Run)) with
                    | info :: _ ->
                        let! entry = store.Get(scope.ScopeId, info.Id)
                        return entry |> Option.map _.Document
                    | [] -> return None
                | _ -> return None
        }

    /// A read that serves nothing: where no request is in hand, as in a static
    /// export, a page answers its own body.
    let noNarrative: DeclaredNarrativeRead = fun _ _ _ _ -> async.Return None