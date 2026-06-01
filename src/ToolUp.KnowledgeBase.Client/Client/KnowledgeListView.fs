// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module KnowledgeListView

open System
open Feliz
open SharedTypes

// ─── Badges ────────────────────────────────────────────────────────
//
// Extracted so the team Documents page, the team Platform Library page,
// and the Platform Admin module render status / source / file-type
// identically. Previously inlined privately in `KnowledgeBaseView`.

module Badges =

    let statusBadge (status: IngestionStatus) =
        match status with
        | Queued ->
            Html.span [
                prop.className
                    "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-600"
                prop.text "Queued"
            ]
        | ExtractingText ->
            Html.span [
                prop.className
                    "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-blue-100 text-blue-700"
                prop.text "Extracting…"
            ]
        | Embedding(processed, total) ->
            Html.span [
                prop.className
                    "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-yellow-100 text-yellow-700"
                prop.text (sprintf "Embedding %d/%d" processed total)
            ]
        | Complete count ->
            Html.span [
                prop.className
                    "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-green-100 text-green-700"
                prop.text (sprintf "Indexed (%d chunks)" count)
            ]
        | Failed reason ->
            Html.span [
                prop.className
                    "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-red-100 text-red-700"
                prop.title reason
                prop.text "Failed"
            ]

    let sourceBadge (source: KnowledgeSource) =
        match source with
        | UploadedFile ->
            Html.span [
                prop.className
                    "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-gray-100 text-gray-700"
                prop.text "Upload"
            ]
        | FromNarrative src ->
            let tooltip =
                src.SettingsDisplay
                |> List.map (fun (k, v) -> sprintf "%s: %s" k v)
                |> String.concat "\n"

            Html.span [
                prop.className
                    "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-indigo-50 text-indigo-700"
                prop.title (
                    if tooltip = "" then
                        src.ModuleId
                    else
                        sprintf "%s\n%s" src.ModuleId tooltip
                )
                prop.text (sprintf "Narrative · %s" src.ModuleId)
            ]
        | Note src ->
            let edited =
                match src.LastEditedAt with
                | Some t -> sprintf " · edited %s" (t.ToString("yyyy-MM-dd HH:mm"))
                | None -> ""

            Html.span [
                prop.className
                    "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium bg-amber-50 text-amber-700"
                prop.title (
                    sprintf "Authored by %s on %s%s" src.Author (src.CreatedAt.ToString("yyyy-MM-dd HH:mm")) edited
                )
                prop.text "Note"
            ]

    let fileTypeBadge (fileType: string) =
        let color =
            match fileType with
            | "pdf" -> "bg-red-50 text-red-600"
            | "pptx" -> "bg-orange-50 text-orange-600"
            | "docx" -> "bg-blue-50 text-blue-600"
            | "xlsx" -> "bg-green-50 text-green-600"
            | "csv" -> "bg-purple-50 text-purple-600"
            | "note" -> "bg-amber-50 text-amber-600"
            | _ -> "bg-gray-50 text-gray-600"

        Html.span [
            prop.className (
                sprintf "inline-flex items-center px-2 py-0.5 rounded text-xs font-mono font-medium %s" color
            )
            prop.text (fileType.ToUpperInvariant())
        ]

// ─── Filter / group axes ───────────────────────────────────────────

type GroupBy =
    | NoGrouping
    | ByFileType
    | BySource
    | ByStatus
    | ByUploader
    | ByMonth

type DateRange =
    | AllDates
    | Last7Days
    | Last30Days
    | Last90Days
    | OlderThan90Days

/// Source-kind key used for both the filter chip and the group section
/// header. Narratives are bucketed per ModuleId so a team with N modules
/// surfaces N narrative buckets, not one undifferentiated "Narrative".
let private sourceKindKey (source: KnowledgeSource) =
    match source with
    | UploadedFile -> "Uploaded"
    | FromNarrative src -> sprintf "Narrative · %s" src.ModuleId
    | Note _ -> "Note"

let private statusKey (status: IngestionStatus) =
    match status with
    | Queued -> "Queued"
    | ExtractingText -> "Extracting"
    | Embedding _ -> "Embedding"
    | Complete _ -> "Complete"
    | Failed _ -> "Failed"

let private monthKey (dt: DateTimeOffset) = dt.ToString("yyyy-MM")

let private dateRangeContains (range: DateRange) (uploadedAt: DateTimeOffset) =
    let days = (DateTimeOffset.UtcNow - uploadedAt).TotalDays

    match range with
    | AllDates -> true
    | Last7Days -> days <= 7.0
    | Last30Days -> days <= 30.0
    | Last90Days -> days <= 90.0
    | OlderThan90Days -> days > 90.0

// ─── Chip helpers ──────────────────────────────────────────────────

let private chip (label: string) (active: bool) (onClick: unit -> unit) =
    let className =
        if active then
            "inline-flex items-center px-3 py-1 rounded-full text-xs font-medium bg-blue-600 text-white"
        else
            "inline-flex items-center px-3 py-1 rounded-full text-xs font-medium bg-white text-gray-700 border border-gray-300 hover:bg-gray-50"

    Html.button [
        prop.className className
        prop.text label
        prop.onClick (fun _ -> onClick ())
    ]

let private chipRow (label: string) (children: ReactElement list) =
    if children.IsEmpty then
        Html.none
    else
        Html.div [
            prop.className "flex items-start gap-2 flex-wrap"
            prop.children [
                Html.span [
                    prop.className "text-xs text-gray-500 font-medium pt-1 min-w-[60px]"
                    prop.text label
                ]
                Html.div [ prop.className "flex flex-wrap gap-1.5"; prop.children children ]
            ]
        ]

let private toggleMember (set: Set<string>) (value: string) =
    if set.Contains value then
        set.Remove value
    else
        set.Add value

// ─── Config ────────────────────────────────────────────────────────

type KnowledgeListConfig = {
    /// Shown when the source list is empty (before any filter is applied).
    EmptyStateText: string
    /// Optional per-row action affordance — typically a Delete button.
    /// `None` = read-only list, no action column.
    RowAction: (KnowledgeDocument -> ReactElement) option
    /// Stable identifier for this instance — used as a React key suffix so
    /// two instances on the same page don't collide on grouped section keys.
    InstanceKey: string
}

// ─── Component ─────────────────────────────────────────────────────

[<ReactComponent>]
let KnowledgeListView (config: KnowledgeListConfig) (documents: KnowledgeDocument list) =
    // Hooks unconditional, in fixed order — Rules of Hooks.
    let search, setSearch = React.useState ""
    let fileTypeFilter, setFileTypeFilter = React.useState (Set.empty: Set<string>)
    let sourceFilter, setSourceFilter = React.useState (Set.empty: Set<string>)
    let statusFilter, setStatusFilter = React.useState (Set.empty: Set<string>)
    let uploaderFilter, setUploaderFilter = React.useState (Set.empty: Set<string>)
    let dateRange, setDateRange = React.useState AllDates
    let groupBy, setGroupBy = React.useState NoGrouping

    if documents.IsEmpty then
        Html.div [
            prop.className "bg-gray-50 border border-dashed border-gray-300 rounded-lg p-8 text-center"
            prop.children [
                Html.p [ prop.className "text-sm text-gray-600"; prop.text config.EmptyStateText ]
            ]
        ]
    else
        let availableFileTypes =
            documents |> List.map _.FileType |> List.distinct |> List.sort

        let availableSourceKinds =
            documents
            |> List.map (fun d -> sourceKindKey d.Source)
            |> List.distinct
            |> List.sort

        let availableStatuses =
            documents
            |> List.map (fun d -> statusKey d.Status)
            |> List.distinct
            |> List.sort

        let availableUploaders =
            documents |> List.map _.UploadedBy |> List.distinct |> List.sort

        let matchesSearch (doc: KnowledgeDocument) =
            if String.IsNullOrWhiteSpace search then
                true
            else
                doc.FileName.ToLowerInvariant().Contains(search.Trim().ToLowerInvariant())

        let matchesFileType (doc: KnowledgeDocument) =
            fileTypeFilter.IsEmpty || fileTypeFilter.Contains doc.FileType

        let matchesSource (doc: KnowledgeDocument) =
            sourceFilter.IsEmpty || sourceFilter.Contains(sourceKindKey doc.Source)

        let matchesStatus (doc: KnowledgeDocument) =
            statusFilter.IsEmpty || statusFilter.Contains(statusKey doc.Status)

        let matchesUploader (doc: KnowledgeDocument) =
            uploaderFilter.IsEmpty || uploaderFilter.Contains doc.UploadedBy

        let filtered =
            documents
            |> List.filter (fun d ->
                matchesSearch d
                && matchesFileType d
                && matchesSource d
                && matchesStatus d
                && matchesUploader d
                && dateRangeContains dateRange d.UploadedAt)
            |> List.sortByDescending _.UploadedAt

        let groupOf (doc: KnowledgeDocument) =
            match groupBy with
            | NoGrouping -> ""
            | ByFileType -> doc.FileType.ToUpperInvariant()
            | BySource -> sourceKindKey doc.Source
            | ByStatus -> statusKey doc.Status
            | ByUploader -> doc.UploadedBy
            | ByMonth -> monthKey doc.UploadedAt

        let documentRow (doc: KnowledgeDocument) =
            let actionCell =
                match config.RowAction with
                | Some renderAction ->
                    Html.td [ prop.className "px-4 py-3 text-right"; prop.children [ renderAction doc ] ]
                | None -> Html.none

            Html.tr [
                prop.key doc.Id
                prop.className "hover:bg-gray-50"
                prop.children [
                    Html.td [
                        prop.className "px-4 py-3 text-sm font-medium text-gray-900"
                        prop.text doc.FileName
                    ]
                    Html.td [
                        prop.className "px-4 py-3"
                        prop.children [ Badges.fileTypeBadge doc.FileType ]
                    ]
                    Html.td [ prop.className "px-4 py-3"; prop.children [ Badges.sourceBadge doc.Source ] ]
                    Html.td [ prop.className "px-4 py-3 text-xs text-gray-500"; prop.text doc.UploadedBy ]
                    Html.td [
                        prop.className "px-4 py-3 text-xs text-gray-500"
                        prop.text (doc.UploadedAt.ToString("yyyy-MM-dd HH:mm"))
                    ]
                    Html.td [ prop.className "px-4 py-3"; prop.children [ Badges.statusBadge doc.Status ] ]
                    actionCell
                ]
            ]

        let tableHead =
            Html.thead [
                prop.className "bg-gray-50 border-b border-gray-200"
                prop.children [
                    Html.tr [
                        prop.children [
                            Html.th [
                                prop.className "px-4 py-2 text-left text-xs font-medium text-gray-500"
                                prop.text "File"
                            ]
                            Html.th [
                                prop.className "px-4 py-2 text-left text-xs font-medium text-gray-500"
                                prop.text "Type"
                            ]
                            Html.th [
                                prop.className "px-4 py-2 text-left text-xs font-medium text-gray-500"
                                prop.text "Source"
                            ]
                            Html.th [
                                prop.className "px-4 py-2 text-left text-xs font-medium text-gray-500"
                                prop.text "Uploader"
                            ]
                            Html.th [
                                prop.className "px-4 py-2 text-left text-xs font-medium text-gray-500"
                                prop.text "Added"
                            ]
                            Html.th [
                                prop.className "px-4 py-2 text-left text-xs font-medium text-gray-500"
                                prop.text "Status"
                            ]
                            if config.RowAction.IsSome then
                                Html.th [ prop.className "px-4 py-2" ]
                        ]
                    ]
                ]
            ]

        let renderTable (rows: KnowledgeDocument list) =
            Html.table [
                prop.className "w-full text-sm"
                prop.children [ tableHead; Html.tbody [ prop.children (rows |> List.map documentRow) ] ]
            ]

        let groupHeader (key: string) (count: int) =
            Html.div [
                prop.className
                    "px-3 py-1.5 text-xs font-medium text-gray-600 bg-gray-100 border-b border-gray-200 flex items-center justify-between"
                prop.children [
                    Html.span [ prop.text key ]
                    Html.span [ prop.className "text-gray-400"; prop.text (sprintf "%d" count) ]
                ]
            ]

        let body =
            if filtered.IsEmpty then
                Html.div [
                    prop.className "p-8 text-center text-sm text-gray-500 bg-white border border-gray-200 rounded-lg"
                    prop.text "No documents match the current filters."
                ]
            else
                match groupBy with
                | NoGrouping ->
                    Html.div [
                        prop.className "bg-white border border-gray-200 rounded-lg overflow-hidden"
                        prop.children [ renderTable filtered ]
                    ]
                | _ ->
                    let groups = filtered |> List.groupBy groupOf |> List.sortBy fst

                    Html.div [
                        prop.className "space-y-4"
                        prop.children (
                            groups
                            |> List.map (fun (key, rows) ->
                                Html.div [
                                    prop.key (config.InstanceKey + ":" + key)
                                    prop.className "bg-white border border-gray-200 rounded-lg overflow-hidden"
                                    prop.children [ groupHeader key rows.Length; renderTable rows ]
                                ])
                        )
                    ]

        let dateRangeLabel range =
            match range with
            | AllDates -> "All dates"
            | Last7Days -> "Last 7 days"
            | Last30Days -> "Last 30 days"
            | Last90Days -> "Last 90 days"
            | OlderThan90Days -> "Older than 90 days"

        let groupByLabel gb =
            match gb with
            | NoGrouping -> "No grouping"
            | ByFileType -> "Group by file type"
            | BySource -> "Group by source"
            | ByStatus -> "Group by status"
            | ByUploader -> "Group by uploader"
            | ByMonth -> "Group by month"

        let groupByValue =
            match groupBy with
            | NoGrouping -> "none"
            | ByFileType -> "type"
            | BySource -> "source"
            | ByStatus -> "status"
            | ByUploader -> "uploader"
            | ByMonth -> "month"

        let parseGroupBy =
            function
            | "type" -> ByFileType
            | "source" -> BySource
            | "status" -> ByStatus
            | "uploader" -> ByUploader
            | "month" -> ByMonth
            | _ -> NoGrouping

        let groupBySelect =
            Html.select [
                prop.className "text-xs border border-gray-300 rounded px-2 py-1 bg-white"
                prop.value groupByValue
                prop.onChange (fun (s: string) -> setGroupBy (parseGroupBy s))
                prop.children [
                    Html.option [ prop.value "none"; prop.text (groupByLabel NoGrouping) ]
                    Html.option [ prop.value "type"; prop.text (groupByLabel ByFileType) ]
                    Html.option [ prop.value "source"; prop.text (groupByLabel BySource) ]
                    Html.option [ prop.value "status"; prop.text (groupByLabel ByStatus) ]
                    Html.option [ prop.value "uploader"; prop.text (groupByLabel ByUploader) ]
                    Html.option [ prop.value "month"; prop.text (groupByLabel ByMonth) ]
                ]
            ]

        let dateRangeValue =
            match dateRange with
            | AllDates -> "all"
            | Last7Days -> "7"
            | Last30Days -> "30"
            | Last90Days -> "90"
            | OlderThan90Days -> "old"

        let parseDateRange =
            function
            | "7" -> Last7Days
            | "30" -> Last30Days
            | "90" -> Last90Days
            | "old" -> OlderThan90Days
            | _ -> AllDates

        let dateRangeSelect =
            Html.select [
                prop.className "text-xs border border-gray-300 rounded px-2 py-1 bg-white"
                prop.value dateRangeValue
                prop.onChange (fun (s: string) -> setDateRange (parseDateRange s))
                prop.children [
                    Html.option [ prop.value "all"; prop.text (dateRangeLabel AllDates) ]
                    Html.option [ prop.value "7"; prop.text (dateRangeLabel Last7Days) ]
                    Html.option [ prop.value "30"; prop.text (dateRangeLabel Last30Days) ]
                    Html.option [ prop.value "90"; prop.text (dateRangeLabel Last90Days) ]
                    Html.option [ prop.value "old"; prop.text (dateRangeLabel OlderThan90Days) ]
                ]
            ]

        let activeFilterCount =
            (fileTypeFilter.Count
             + sourceFilter.Count
             + statusFilter.Count
             + uploaderFilter.Count)
            + (if dateRange = AllDates then 0 else 1)
            + (if String.IsNullOrWhiteSpace search then 0 else 1)

        let clearAll () =
            setSearch ""
            setFileTypeFilter Set.empty
            setSourceFilter Set.empty
            setStatusFilter Set.empty
            setUploaderFilter Set.empty
            setDateRange AllDates

        let header =
            Html.div [
                prop.className "flex items-center justify-between gap-3 flex-wrap"
                prop.children [
                    Html.input [
                        prop.type' "search"
                        prop.className
                            "px-3 py-1.5 text-sm border border-gray-300 rounded-md focus:outline-none focus:ring-2 focus:ring-blue-400 w-64"
                        prop.placeholder "Search by file name…"
                        prop.value search
                        prop.onChange setSearch
                    ]
                    Html.div [
                        prop.className "flex items-center gap-3 text-xs text-gray-500"
                        prop.children [
                            Html.span [ prop.text (sprintf "%d of %d" filtered.Length documents.Length) ]
                            groupBySelect
                            if activeFilterCount > 0 then
                                Html.button [
                                    prop.className "text-blue-600 hover:text-blue-800 font-medium"
                                    prop.text "Clear filters"
                                    prop.onClick (fun _ -> clearAll ())
                                ]
                        ]
                    ]
                ]
            ]

        let filterBar =
            Html.div [
                prop.className "space-y-2"
                prop.children [
                    chipRow
                        "Type"
                        (availableFileTypes
                         |> List.map (fun ft ->
                             chip (ft.ToUpperInvariant()) (fileTypeFilter.Contains ft) (fun () ->
                                 setFileTypeFilter (toggleMember fileTypeFilter ft))))
                    chipRow
                        "Source"
                        (availableSourceKinds
                         |> List.map (fun sk ->
                             chip sk (sourceFilter.Contains sk) (fun () ->
                                 setSourceFilter (toggleMember sourceFilter sk))))
                    chipRow
                        "Status"
                        (availableStatuses
                         |> List.map (fun st ->
                             chip st (statusFilter.Contains st) (fun () ->
                                 setStatusFilter (toggleMember statusFilter st))))
                    chipRow
                        "Uploader"
                        (availableUploaders
                         |> List.map (fun u ->
                             chip u (uploaderFilter.Contains u) (fun () ->
                                 setUploaderFilter (toggleMember uploaderFilter u))))
                    Html.div [
                        prop.className "flex items-center gap-2"
                        prop.children [
                            Html.span [
                                prop.className "text-xs text-gray-500 font-medium min-w-[60px]"
                                prop.text "Added"
                            ]
                            dateRangeSelect
                        ]
                    ]
                ]
            ]

        Html.div [
            prop.className "space-y-4"
            prop.children [
                header
                Html.div [
                    prop.className "bg-gray-50 border border-gray-200 rounded-lg p-3"
                    prop.children [ filterBar ]
                ]
                body
            ]
        ]