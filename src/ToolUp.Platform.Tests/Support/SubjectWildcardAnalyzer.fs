// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Support.SubjectWildcardAnalyzer

open System
open System.Text.RegularExpressions

// Lightweight text-based analyser for the Phase 66 §4.1 E3 leak shape.
//
// The mixed-mode redesign introduces a `Subject` 4-case DU. A handler
// that pattern-matches on `Subject` with a `_` wildcard fallback can
// silently admit the wrong subject kind into mutating code — e.g.
//
//   match ctx.Subject with
//   | TeamMember (uid, tid) -> doTeamThing uid tid
//   | _ -> doTeamThing "anonymous" "default"   // ← E3 leak
//
// The compiler cannot catch this because the match is exhaustive.
// The leak is detectable by code review, and this analyser mechanises
// the review.
//
// Heuristic: a Subject-match's wildcard arm is flagged when its body
// shares at least one function-call identifier with a sibling
// constructor arm. The live forge idiom is
//
//   | TeamMember (uid, tid) -> [team-role check block]
//   | _ -> return Ok()
//
// — the wildcard body's identifier set, after filtering control-flow
// keywords and result wrappers, is empty, so the intersection with
// any sibling is empty → not flagged. The E3 leak shape's wildcard
// body shares `doTeamThing` with the sibling arm → flagged.
//
// False positives are acceptable per the TIDY-UP spec — reviewers
// dismiss them. Missing the genuine defect is the failure mode this
// exists to prevent.

type Diagnostic = {
    File: string
    /// 1-indexed line of the `match … Subject … with` discriminant.
    MatchLine: int
    /// Raw `match` line text for the reviewer's eye-grep.
    Pattern: string
    /// 1-indexed line of the wildcard arm.
    WildcardLine: int
    /// 1-indexed line of the sibling Subject-constructor arm that
    /// shares an identifier with the wildcard body.
    SiblingLine: int
    /// The lowercase identifier present in both arm bodies — the
    /// concrete evidence the analyser keys off.
    SharedIdent: string
}

let private subjectConstructors =
    Set.ofList [ "AnonymousSession"; "AuthenticatedUser"; "TeamMember"; "ClaimBearer" ]

let private subjectCtorRegex =
    let alt = subjectConstructors |> Set.toSeq |> String.concat "|"
    Regex(@"\b(" + alt + @")\b", RegexOptions.Compiled)

let private subjectMatchRegex =
    // Single-line shape: `<indent>match <expr-containing-Subject> with`
    // at end-of-line. Multi-line discriminants are out of v1 scope.
    Regex(@"^(\s*)match\s+.*?\bSubject\b.*?\s+with\s*$", RegexOptions.Compiled)

let private identRegex =
    // Lowercase-starting identifiers only — F# function names and
    // value bindings. Uppercase identifiers (DU constructors, types,
    // module names) are excluded by construction.
    Regex(@"\b([a-z][a-zA-Z0-9_]*)\b", RegexOptions.Compiled)

/// Identifiers we treat as noise — control-flow keywords, result
/// wrappers, common builder names, and exception primitives. These
/// appear in many bodies and don't carry leak-evidence.
let private ignoredIdents =
    Set.ofList [
        // Control flow
        "let"
        "rec"
        "use"
        "do"
        "return"
        "yield"
        "if"
        "then"
        "else"
        "elif"
        "match"
        "with"
        "when"
        "function"
        "for"
        "in"
        "to"
        "while"
        "try"
        "finally"
        "raise"
        "lazy"
        "fun"
        // Common literals / pseudo-values
        "true"
        "false"
        "null"
        "unit"
        "and"
        "or"
        "not"
        "as"
        "of"
        "is"
        "new"
        "end"
        "begin"
        // F# declarations (defensive — should not appear in arm bodies
        // but harmless to filter)
        "type"
        "module"
        "namespace"
        "open"
        "inline"
        "abstract"
        "override"
        "default"
        "static"
        "private"
        "public"
        "internal"
        "member"
        "interface"
        "mutable"
        "struct"
        "class"
        // Common stdlib values / builders (lowercase forms)
        "ignore"
        "async"
        "task"
        "seq"
        "id"
        // Exception primitives — terminal "this branch is impossible"
        // returns; not leak-evidence.
        "failwith"
        "failwithf"
        "invalidArg"
        "invalidOp"
        "nullArg"
        // Logging primitives — appear in many handlers without
        // carrying mutation semantics.
        "printfn"
        "sprintf"
        "eprintfn"
        "log"
        "logger"
    ]

/// Strip `// …` line comments and string literals before identifier
/// extraction, so words inside comments / strings don't pollute the
/// shared-identifier set.
let private stripCommentsAndStrings (text: string) =
    let sb = System.Text.StringBuilder(text.Length)
    let mutable i = 0
    let mutable inString = false

    while i < text.Length do
        let c = text.[i]

        if inString then
            if c = '"' && (i = 0 || text.[i - 1] <> '\\') then
                inString <- false
                sb.Append(' ') |> ignore
            else
                sb.Append(' ') |> ignore

            i <- i + 1
        elif c = '/' && i + 1 < text.Length && text.[i + 1] = '/' then
            // Line comment — skip to end of line.
            while i < text.Length && text.[i] <> '\n' do
                sb.Append(' ') |> ignore
                i <- i + 1
        elif c = '"' then
            inString <- true
            sb.Append(' ') |> ignore
            i <- i + 1
        else
            sb.Append(c) |> ignore
            i <- i + 1

    sb.ToString()

let private extractIdents (text: string) : Set<string> =
    let cleaned = stripCommentsAndStrings text

    identRegex.Matches(cleaned)
    |> Seq.cast<Match>
    |> Seq.map (fun m -> m.Groups.[1].Value)
    |> Seq.filter (fun id -> not (ignoredIdents.Contains id))
    |> Set.ofSeq

let private leadingWhitespaceLen (line: string) =
    let mutable i = 0

    while i < line.Length && (line.[i] = ' ' || line.[i] = '\t') do
        i <- i + 1

    i

let private isBlank (line: string) =
    leadingWhitespaceLen line >= line.Length

let private startsWithBar (line: string) =
    let idx = leadingWhitespaceLen line

    idx < line.Length
    && line.[idx] = '|'
    && (idx + 1 >= line.Length || line.[idx + 1] <> '|')

type private ArmBlock = {
    PatternText: string
    BodyText: string
    /// 1-indexed.
    StartLine: int
}

/// Parse arms starting at `firstArmIdx` (the index of the first line
/// whose leading non-whitespace is `|`). Multi-pattern arms whose
/// first `|` line lacks `->` are split into separate empty-body
/// records — their empty body cannot contribute to the
/// shared-identifier check, so the split is semantically harmless.
let private parseArms (lines: string[]) (firstArmIdx: int) (armCol: int) : ArmBlock list =
    let mutable arms: ArmBlock list = []
    let mutable curPattern: string option = None
    let curBody = System.Text.StringBuilder()
    let mutable curStart = 0
    let mutable idx = firstArmIdx
    let mutable stop = false

    let flush () =
        match curPattern with
        | Some pat ->
            arms <-
                {
                    PatternText = pat
                    BodyText = curBody.ToString()
                    StartLine = curStart + 1
                }
                :: arms
        | None -> ()

        curPattern <- None
        curBody.Clear() |> ignore

    while not stop && idx < lines.Length do
        let line = lines.[idx]

        if isBlank line then
            curBody.Append('\n') |> ignore
            idx <- idx + 1
        else
            let ws = leadingWhitespaceLen line

            if startsWithBar line && ws = armCol then
                flush ()
                let afterBar = line.Substring(ws + 1)
                let arrowIdx = afterBar.IndexOf("->")

                if arrowIdx >= 0 then
                    curPattern <- Some(afterBar.Substring(0, arrowIdx).Trim())
                    curBody.Append(afterBar.Substring(arrowIdx + 2)) |> ignore
                    curBody.Append('\n') |> ignore
                else
                    curPattern <- Some(afterBar.Trim())
                    curBody.Append('\n') |> ignore

                curStart <- idx
                idx <- idx + 1
            elif ws < armCol then
                stop <- true
            else
                curBody.Append(line) |> ignore
                curBody.Append('\n') |> ignore
                idx <- idx + 1

    flush ()
    List.rev arms

let private patternMentionsSubject (pat: string) =
    subjectCtorRegex.IsMatch(stripCommentsAndStrings pat)

let private isWildcardPattern (pat: string) =
    let t = pat.Trim()
    // Bare wildcard arm. `_ when guard` still admits any subject and
    // counts as a wildcard for our purposes.
    t = "_" || t.StartsWith("_ ") || t.StartsWith("_\t") || t.StartsWith("_\n")

/// Scan `source` and return one `Diagnostic` per (Subject-match,
/// wildcard-arm, sibling-arm) triple whose wildcard body shares a
/// non-ignored lowercase identifier with the sibling arm body.
let lintSource (filename: string) (source: string) : Diagnostic list =
    let lines = source.Replace("\r\n", "\n").Split('\n')
    let mutable diagnostics: Diagnostic list = []

    for i in 0 .. lines.Length - 1 do
        let line = lines.[i]

        if subjectMatchRegex.IsMatch(line) then
            // Skip blank lines to find the first arm.
            let mutable j = i + 1

            while j < lines.Length && isBlank lines.[j] do
                j <- j + 1

            if j < lines.Length && startsWithBar lines.[j] then
                let armCol = leadingWhitespaceLen lines.[j]
                let arms = parseArms lines j armCol

                let subjectArms =
                    arms |> List.filter (fun a -> patternMentionsSubject a.PatternText)

                let wildcardArms = arms |> List.filter (fun a -> isWildcardPattern a.PatternText)

                if not subjectArms.IsEmpty && not wildcardArms.IsEmpty then
                    let siblings = arms |> List.filter (fun a -> not (isWildcardPattern a.PatternText))

                    for w in wildcardArms do
                        let wIdents = extractIdents w.BodyText

                        if not (Set.isEmpty wIdents) then
                            for sib in siblings do
                                let sibIdents = extractIdents sib.BodyText
                                let shared = Set.intersect wIdents sibIdents

                                if not (Set.isEmpty shared) then
                                    let one = shared |> Set.toSeq |> Seq.head

                                    diagnostics <-
                                        {
                                            File = filename
                                            MatchLine = i + 1
                                            Pattern = line.Trim()
                                            WildcardLine = w.StartLine
                                            SiblingLine = sib.StartLine
                                            SharedIdent = one
                                        }
                                        :: diagnostics

    List.rev diagnostics

/// Format a diagnostic for Expecto error output.
let formatDiagnostic (d: Diagnostic) =
    sprintf
        "%s:%d — `%s` arm at line %d shares identifier `%s` with sibling arm at line %d (potential cross-shape leak per Phase 66 §4.1 E3)"
        d.File
        d.MatchLine
        d.Pattern
        d.WildcardLine
        d.SharedIdent
        d.SiblingLine