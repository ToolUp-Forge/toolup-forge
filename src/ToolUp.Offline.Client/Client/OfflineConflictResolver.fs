// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Offline.Client.OfflineConflictResolver

open System
open System.Text
open Feliz
open ToolUp.Platform.DataProp
open ToolUp.Offline
open ToolUp.Offline.Client.OfflineQueue

// ─── Phase 24 — conflict resolution UI ───────────────────────────────
//
// v1 is last-writer-wins with an EXPLICIT user choice — there is no
// automatic merge, and this component is where that choice is made.
// The phase's out-of-scope list records CRDT-based merge as a
// follow-up; nothing here should be read as a placeholder for one.
//
// **Both documents are shown as text.** The queue holds `byte[]`, and
// the companion has no schema knowledge of the entity — only the
// module that owns the record does. Rendering the decoded JSON is
// therefore the most the SDK can honestly offer; a deployment that
// wants a field-level diff passes its own `renderDocument` and gets
// its record shape back to render however it likes.

/// Decode queued bytes for display. Total — bytes that are not UTF-8
/// text render as a placeholder rather than throwing inside a React
/// render, which would blank the whole shell.
let decodeDocument (bytes: byte[]) : string =
    if isNull (box bytes) || Array.isEmpty bytes then
        "(empty)"
    else
        try
            Encoding.UTF8.GetString bytes
        with _ ->
            sprintf "(%d bytes — not displayable as text)" bytes.Length

/// How one conflicted entry is presented and resolved.
type ConflictResolverProps = {
    /// Conflicted entries, from `IOfflineQueue.List`.
    Conflicts: QueueEntry list
    /// Invoked with the user's choice. The host applies it via
    /// `IOfflineQueue.MarkConflict` and re-reads the list — the
    /// component holds no queue state of its own, so a stale render
    /// cannot resolve a conflict twice.
    OnResolve: QueueEntry -> ConflictResolution -> unit
    /// Optional deployment renderer for one side of the comparison,
    /// given the raw bytes. `None` renders the decoded text.
    RenderDocument: (byte[] -> ReactElement) option
}

module ConflictResolverProps =
    let create
        (conflicts: QueueEntry list)
        (onResolve: QueueEntry -> ConflictResolution -> unit)
        : ConflictResolverProps =
        {
            Conflicts = conflicts
            OnResolve = onResolve
            RenderDocument = None
        }

let private documentPane (props: ConflictResolverProps) (title: string) (subtitle: string) (bytes: byte[]) =
    Html.div [
        prop.style [
            style.flexGrow 1
            style.flexBasis (length.percent 0)
            style.minWidth (length.px 0)
            style.border (1, borderStyle.solid, "#e5e7eb")
            style.borderRadius (length.px 6)
            style.padding (length.px 12)
            style.backgroundColor "#ffffff"
        ]
        prop.children [
            Html.div [
                prop.style [ style.fontWeight 600; style.fontSize (length.em 0.9) ]
                prop.text title
            ]
            Html.div [
                prop.style [
                    style.fontSize (length.em 0.75)
                    style.color "#6b7280"
                    style.marginBottom (length.px 8)
                ]
                prop.text subtitle
            ]
            match props.RenderDocument with
            | Some render -> render bytes
            | None ->
                Html.pre [
                    prop.style [
                        style.fontSize (length.em 0.75)
                        style.whitespace.prewrap
                        style.overflowWrap.breakWord
                        style.maxHeight (length.px 240)
                        style.overflowY.auto
                        style.margin 0
                    ]
                    prop.text (decodeDocument bytes)
                ]
        ]
    ]

let private actionButton (label: string) (isPrimary: bool) (onClick: unit -> unit) =
    Html.button [
        prop.type' "button"
        prop.style [
            style.padding (length.px 6, length.px 14)
            style.borderRadius (length.px 4)
            style.fontSize (length.em 0.85)
            style.cursor.pointer
            style.border (1, borderStyle.solid, (if isPrimary then "#1e40af" else "#d1d5db"))
            style.backgroundColor (if isPrimary then "#1e40af" else "#ffffff")
            style.color (if isPrimary then "#ffffff" else "#374151")
        ]
        prop.onClick (fun _ -> onClick ())
        prop.text label
    ]

let private conflictCard (props: ConflictResolverProps) (entry: QueueEntry) =
    let serverBytes = defaultArg entry.ServerEntity Array.empty

    Html.div [
        prop.key entry.Mutation.Id
        dataProp.custom "data-toolup-offline-conflict" entry.Mutation.EntityType
        prop.style [
            style.border (1, borderStyle.solid, "#fbbf24")
            style.borderRadius (length.px 8)
            style.padding (length.px 16)
            style.marginBottom (length.px 16)
            style.backgroundColor "#fffbeb"
        ]
        prop.children [
            Html.div [
                prop.style [ style.marginBottom (length.px 10) ]
                prop.children [
                    Html.div [
                        prop.style [ style.fontWeight 600 ]
                        prop.text (sprintf "%s — %s" entry.Mutation.EntityType entry.Mutation.EntityId)
                    ]
                    Html.div [
                        prop.style [ style.fontSize (length.em 0.8); style.color "#92400e" ]
                        prop.text (
                            sprintf
                                "Edited offline at %s. Someone else changed this record since — pick which version to keep."
                                (entry.Mutation.EnqueuedAt.ToLocalTime().ToString "g")
                        )
                    ]
                ]
            ]
            Html.div [
                prop.style [
                    style.display.flex
                    style.gap (length.px 12)
                    style.marginBottom (length.px 12)
                    style.flexWrap.wrap
                ]
                prop.children [
                    documentPane
                        props
                        "Your offline edit"
                        "Made on this device while disconnected."
                        entry.Mutation.Payload
                    documentPane props "On the server" "The current saved version." serverBytes
                ]
            ]
            Html.div [
                prop.style [ style.display.flex; style.gap (length.px 8) ]
                prop.children [
                    actionButton "Keep mine" true (fun () -> props.OnResolve entry KeepLocal)
                    actionButton "Keep theirs" false (fun () -> props.OnResolve entry KeepServer)
                    actionButton "Decide later" false (fun () -> props.OnResolve entry Defer)
                ]
            ]
        ]
    ]

/// Renders every pending conflict, newest edit first. Renders NOTHING
/// (not an empty box, not a heading) when there are none — the
/// component is safe to mount unconditionally in a shell, which is what
/// makes it usable without the host tracking conflict state itself.
[<ReactComponent>]
let OfflineConflictResolver (props: ConflictResolverProps) =
    let conflicts =
        props.Conflicts
        |> List.filter (fun e -> e.State = Conflicted)
        |> List.sortByDescending _.Mutation.EnqueuedAt

    if List.isEmpty conflicts then
        Html.none
    else
        Html.div [
            dataProp.custom "data-toolup-offline" "conflict-resolver"
            prop.style [ style.marginBottom (length.px 16) ]
            prop.children [
                Html.h3 [
                    prop.style [
                        style.fontSize (length.em 1.0)
                        style.fontWeight 600
                        style.marginBottom (length.px 8)
                    ]
                    prop.text (sprintf "%d offline edit(s) need your attention" (List.length conflicts))
                ]
                yield! (conflicts |> List.map (conflictCard props))
            ]
        ]