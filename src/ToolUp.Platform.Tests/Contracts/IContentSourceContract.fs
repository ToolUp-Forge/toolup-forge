module ToolUp.Platform.Tests.Contracts.IContentSourceContract

open Expecto
open ToolUp.Platform
open ToolUp.Platform.Narrative
open ToolUp.PublicRendering

// ─── IContentSource contract pack ───────────────────────────────────
//
// Parametrised tests for any `IContentSource` implementation — the
// SDK-provided `ContentSource.ofRoute` / `ContentSource.create`
// constructors and any consumer-supplied resolver alike. The factory
// yields a source conforming to the canonical fixture below.
//
// **Canonical fixture** — every binding must arrange a source that:
//
//   slug                  | result
//   ──────────────────────┼─────────────────────────────────────────
//   claimed/widget        | Some (Narrative <doc titled "Widget">)
//   <anything else>        | None  (falls through to the next source)
//
// The source must accept ANY `AccessContext` (anonymous, authenticated,
// team, claim-bearer) without throwing — a resolver scopes its query to
// the principal, but the substrate contract is that every principal kind
// is a legal input.
//
// The fixture deliberately does not vary its output by principal; the
// "scope by AccessContext" behaviour is impl-specific and exercised in
// the impl-specific tests, not the conformance bar.

let private anon = AccessContext.unrestricted (AnonymousSession "contract-anon")
let private user = AccessContext.unrestricted (AuthenticatedUser "contract-user")

let tests (name: string) (factory: unit -> IContentSource) =

    testList $"{name} — IContentSource contract" [

        // ─── Rule 2 (async) + claim path ──────────────────────────

        testCaseAsync "Resolve returns Some for a claimed slug, as a Narrative body"
        <| async {
            let source = factory ()
            let! bodyOpt = source.Resolve (Slug "claimed/widget") anon

            match bodyOpt with
            | Some(Narrative doc) -> Expect.equal doc.Title "Widget" "claimed slug resolves the fixture document"
            | Some other -> failtestf "Expected a Narrative body; got %A" other
            | None -> failtest "Expected Some for the claimed slug; got None"
        }

        // ─── Fall-through path ─────────────────────────────────────

        testCaseAsync "Resolve returns None for an unclaimed slug (fall-through to next source)"
        <| async {
            let source = factory ()
            let! bodyOpt = source.Resolve (Slug "definitely/not/claimed") anon
            Expect.isNone bodyOpt "an unclaimed slug must return None so the chain falls through"
        }

        // ─── Rule 4 (stateless between invocations) ────────────────

        testCaseAsync "Resolve is deterministic across repeated calls (rule 4 — no state held between calls)"
        <| async {
            let source = factory ()
            let! first = source.Resolve (Slug "claimed/widget") anon
            let! second = source.Resolve (Slug "claimed/widget") anon

            let title b =
                match b with
                | Some(Narrative d) -> Some d.Title
                | _ -> None

            Expect.equal (title second) (title first) "two calls with the same inputs must yield the same result"
        }

        // ─── Rule 1 (identity by value) — every AccessContext kind is legal ──

        testCaseAsync "Resolve accepts any AccessContext kind without throwing"
        <| async {
            let source = factory ()
            // Both an anonymous and an authenticated principal are legal
            // inputs; the substrate must not assume one shape.
            let! _ = source.Resolve (Slug "claimed/widget") anon
            let! _ = source.Resolve (Slug "claimed/widget") user
            Expect.isTrue true "both principal kinds resolved without an exception"
        }
    ]