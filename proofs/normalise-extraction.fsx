// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 850 — the layout normaliser the proof leg runs between extract and byte-diff.
///
/// The F* extractor's F# backend prints a verbose, OCaml-shaped dialect: every block is
/// `begin … end`, every `let` inside an expression is `let … in`, and the structural tokens —
/// match arms, `if`, `in` — are printed at column 0 or column 5 whatever their nesting. F# 8
/// made that layout an error (FS0058 under strict indentation, which is now the only mode: F# 10
/// refuses `#light "off"` outright, FS1205), so the oracle project compiled it only under
/// `--strict-indentation-`, a flag Fable does not read from an fsproj, and Fantomas cannot parse it
/// at all. This script is the fix that is not "format it": a deterministic re-layout of exactly the
/// dialect the backend emits, into indentation-clean F# that both hosts compile with no flag.
///
/// **What it changes and what it does not.** Only layout. `begin`/`end` become parentheses (the
/// two are the same expression in F#), every construct that opens a block starts a new line at
/// its nesting depth, and match arms sit under their `match`. Every parenthesis the extractor
/// wrote is kept, so no precedence can move; no identifier, literal or operator is touched; the
/// text of each atom — a name, a string, a type annotation such as `opt<'a>` — is carried through
/// verbatim. Types, `module` and `open` lines are emitted as they came, with union cases indented.
///
/// **It refuses rather than guesses.** The parser knows the constructs the backend emits —
/// parentheses, brackets, braces, `match … with | … -> …`, `if … then … else …`, `let … in …`,
/// `fun … -> …` — and a token it cannot place fails the run naming the module and the line. A
/// future extraction that reaches a shape this script does not know therefore fails the proof leg
/// loudly rather than compiling into something else.
///
/// Usage (the leg runs it; a developer rarely will):
///
///     dotnet fsi proofs/normalise-extraction.fsx <extracted.fs> [<extracted.fs> …]
///
/// Each file is rewritten in place. The output is a pure function of the input, which is what lets
/// `check.ps1` byte-diff the normalised extraction against the committed oracle.

open System
open System.IO
open System.Text

// ─── Tokens ──────────────────────────────────────────────────────────

type Tok =
    /// `(` `[` `{` — and `begin`, which is `(`.
    | Open of char
    /// `)` `]` `}` — and `end`, which is `)`.
    | Close of char
    /// One of the structural keywords below, including `|` and `->`.
    | Kw of string
    /// Anything else: a name, a literal (strings kept whole), an operator, a type.
    | Word of string

type Positioned =
    { Tok: Tok
      Line: int
      FirstOnLine: bool }

let private keywords =
    set
        [ "match"
          "with"
          "if"
          "then"
          "else"
          "let"
          "rec"
          "in"
          "fun"
          "and"
          "|"
          "->" ]

let private isDelimiter (c: char) =
    c = '(' || c = ')' || c = '[' || c = ']' || c = '{' || c = '}'

let tokenise (moduleName: string) (text: string) : Positioned list =
    let toks = ResizeArray<Positioned>()
    let mutable i = 0
    let mutable line = 1
    let mutable firstOnLine = true
    let n = text.Length

    let push tok =
        toks.Add
            { Tok = tok
              Line = line
              FirstOnLine = firstOnLine }

        firstOnLine <- false

    while i < n do
        let c = text[i]

        if c = '\n' then
            line <- line + 1
            firstOnLine <- true
            i <- i + 1
        elif Char.IsWhiteSpace c then
            i <- i + 1
        elif c = '"' then
            // A string literal, kept whole. The extractor emits refusal
            // messages verbatim, so `(`, `|` and `->` can all sit inside one.
            let start = i
            i <- i + 1

            while i < n && text[i] <> '"' do
                if text[i] = '\\' then
                    i <- i + 1

                if i < n && text[i] = '\n' then
                    line <- line + 1

                i <- i + 1

            if i >= n then
                failwithf "%s: unterminated string literal starting on line %d" moduleName line

            i <- i + 1
            push (Word(text.Substring(start, i - start)))
        elif isDelimiter c then
            (match c with
             | '('
             | '['
             | '{' -> push (Open c)
             | _ -> push (Close c))

            i <- i + 1
        elif c = ';' then
            // A field separator is its own token, glued to nothing, so a record whose field
            // holds a structural value can be laid out one field per line.
            push (Word ";")
            i <- i + 1
        else
            let start = i

            while i < n
                  && not (Char.IsWhiteSpace text[i])
                  && not (isDelimiter text[i])
                  && text[i] <> '"'
                  && text[i] <> ';' do
                i <- i + 1

            let word = text.Substring(start, i - start)

            if word.StartsWith "//" || word.StartsWith "(*" || word.StartsWith "*)" then
                failwithf "%s: line %d carries a comment, which this dialect never has: %s" moduleName line word

            match word with
            | "begin" -> push (Open '(')
            | "end" -> push (Close ')')
            | w when keywords.Contains w -> push (Kw w)
            | w -> push (Word w)

    List.ofSeq toks

// ─── The expression shapes the backend emits ─────────────────────────

type Item =
    | Atom of string
    /// opener, content, closer — `( … )`, `[ … ]`, `{ … }`, and `begin … end` as `( … )`.
    | Group of char * Expr * char
    /// scrutinee, arms as (pattern, body).
    | Match of Expr * (Expr * Expr) list
    | If of Expr * Expr * Expr option
    /// parameters, body.
    | Fun of Expr * Expr
    /// rec?, pattern, right-hand side, body — `let P = E in BODY` inside an expression.
    | Let of bool * Expr * Expr * Expr

and Expr = Item list

type private Parser(moduleName: string, toks: Positioned[]) =
    let mutable pos = 0

    member _.Peek = if pos < toks.Length then Some toks[pos].Tok else None
    member _.Line = if pos < toks.Length then toks[pos].Line else -1
    member _.Advance() = pos <- pos + 1
    member _.AtEnd = pos >= toks.Length

    member this.Expect(tok: Tok, what: string) =
        match this.Peek with
        | Some t when t = tok -> this.Advance()
        | Some t -> failwithf "%s: line %d: expected %s, found %A" moduleName this.Line what t
        | None -> failwithf "%s: expected %s, found end of input" moduleName what

    /// Parse items until a stop token (not consumed), a closer (not consumed) or the end.
    member this.ParseExpr(stops: Tok -> bool) : Expr =
        let items = ResizeArray<Item>()
        let mutable go = true

        while go do
            match this.Peek with
            | None -> go <- false
            | Some t when stops t -> go <- false
            | Some(Close _) -> go <- false
            | Some(Open o) ->
                this.Advance()

                let closer =
                    (match o with
                     | '(' -> ')'
                     | '[' -> ']'
                     | _ -> '}')

                let inner = this.ParseExpr(fun _ -> false)
                this.Expect(Close closer, sprintf "'%c'" closer)
                items.Add(Group(o, inner, closer))
            | Some(Kw "match") ->
                this.Advance()
                let scrutinee = this.ParseExpr(fun t -> t = Kw "with")
                this.Expect(Kw "with", "'with'")
                let arms = ResizeArray<Expr * Expr>()

                while this.Peek = Some(Kw "|") do
                    this.Advance()
                    let pattern = this.ParseExpr(fun t -> t = Kw "->")
                    this.Expect(Kw "->", "'->' after a match pattern")
                    let body = this.ParseExpr(fun t -> t = Kw "|" || stops t)
                    arms.Add((pattern, body))

                if arms.Count = 0 then
                    failwithf "%s: line %d: a match with no arms" moduleName this.Line

                items.Add(Match(scrutinee, List.ofSeq arms))
            | Some(Kw "if") ->
                this.Advance()
                let cond = this.ParseExpr(fun t -> t = Kw "then")
                this.Expect(Kw "then", "'then'")
                let thenE = this.ParseExpr(fun t -> t = Kw "else" || t = Kw "|" || stops t)

                let elseE =
                    if this.Peek = Some(Kw "else") then
                        this.Advance()
                        Some(this.ParseExpr(fun t -> t = Kw "|" || stops t))
                    else
                        None

                items.Add(If(cond, thenE, elseE))
            | Some(Kw "fun") ->
                this.Advance()
                let parameters = this.ParseExpr(fun t -> t = Kw "->")
                this.Expect(Kw "->", "'->' after fun parameters")
                let body = this.ParseExpr(fun t -> t = Kw "|" || stops t)
                items.Add(Fun(parameters, body))
            | Some(Kw "let") ->
                this.Advance()

                let isRec =
                    if this.Peek = Some(Kw "rec") then
                        this.Advance()
                        true
                    else
                        false

                let pattern = this.ParseExpr(fun t -> t = Word "=")
                this.Expect(Word "=", "'=' after a let pattern")
                let rhs = this.ParseExpr(fun t -> t = Kw "in")
                this.Expect(Kw "in", "'in' after a let right-hand side")
                let body = this.ParseExpr(fun t -> t = Kw "|" || stops t)
                items.Add(Let(isRec, pattern, rhs, body))
            | Some(Kw "->") ->
                // An arrow no `fun` or match arm claimed is a type arrow inside a parameter
                // annotation — `( f : 'a -> 'b )` — and is carried through as text.
                this.Advance()
                items.Add(Atom "->")
            | Some(Kw k) -> failwithf "%s: line %d: unexpected '%s'" moduleName this.Line k
            | Some(Word w) ->
                this.Advance()
                items.Add(Atom w)

        List.ofSeq items

// ─── Layout ──────────────────────────────────────────────────────────

let rec isFlat (items: Expr) =
    items
    |> List.forall (fun item ->
        match item with
        | Atom _ -> true
        | Group(_, e, _) -> isFlat e
        | Fun(ps, body) -> isFlat ps && isFlat body
        | If(c, t, e) -> isFlat c && isFlat t && (e |> Option.forall isFlat)
        | Match _
        | Let _ -> false)

/// Atoms joined by single spaces, except that a separator (`,` `;`) closes up to what precedes it.
let joinPieces (pieces: string seq) : string =
    let sb = StringBuilder()

    for piece in pieces do
        if sb.Length > 0 && not (piece.StartsWith "," || piece.StartsWith ";") then
            sb.Append ' ' |> ignore

        sb.Append piece |> ignore

    sb.ToString()

let rec flat (items: Expr) : string =
    items
    |> List.map (fun item ->
        match item with
        | Atom s -> s
        | Group(o, e, c) -> string o + flat e + string c
        | Fun(ps, body) -> "fun " + flat ps + " -> " + flat body
        | If(c, t, Some e) -> "if " + flat c + " then " + flat t + " else " + flat e
        | If(c, t, None) -> "if " + flat c + " then " + flat t
        | Match _
        | Let _ -> failwith "flat: a structural item")
    |> joinPieces

let private pad (n: int) = String(' ', n)

/// Split a bracket or brace group's content on its top-level `;` separators.
let private splitFields (items: Expr) : Expr list =
    let fields = ResizeArray<Expr>()
    let current = ResizeArray<Item>()

    for item in items do
        match item with
        | Atom ";" ->
            fields.Add(List.ofSeq current)
            current.Clear()
        | other -> current.Add other

    fields.Add(List.ofSeq current)
    List.ofSeq fields

/// Render at `ind`: every line fully indented, the first at column `ind`. A structural item
/// always begins a line; continuation lines inside a group sit four columns in, so no
/// continuation can land on the offside column of the block it continues (which F# would read
/// as a sequence separator). Match arms, `else`, `then` and a let's body are the permitted
/// undentations and sit at the construct's own column.
let rec render (items: Expr) (ind: int) : string list =
    if isFlat items then
        [ pad ind + flat items ]
    else
        let lines = ResizeArray<string>()
        let pending = ResizeArray<string>()
        let mutable first = true

        let flush () =
            if pending.Count > 0 then
                let col = if first then ind else ind + 4
                lines.Add(pad col + joinPieces pending)
                pending.Clear()
                first <- false

        for item in items do
            match item with
            | Atom s -> pending.Add s
            | _ when isFlat [ item ] -> pending.Add(flat [ item ])
            | structural ->
                flush ()
                let col = if first then ind else ind + 4
                lines.AddRange(renderItem structural col)
                first <- false

        flush ()
        List.ofSeq lines

and renderItem (item: Item) (ind: int) : string list =
    match item with
    | Atom s -> [ pad ind + s ]
    | Group(o, e, c) ->
        let inner =
            if o = '(' then
                render e (ind + 1)
            else
                // A record or list whose content is structural: one field per line, aligned
                // at the content column, each closed by its `;` — so a field's structural
                // value, which extends as far as it can, is ended by the next field's
                // undentation and never swallows it.
                let fields = splitFields e

                fields
                |> List.mapi (fun k field ->
                    let fieldLines = render field (ind + 1)

                    if k = fields.Length - 1 then
                        fieldLines
                    else
                        let last = List.last fieldLines
                        (List.take (fieldLines.Length - 1) fieldLines) @ [ last + ";" ])
                |> List.concat

        let head = pad ind + string o + inner.Head.TrimStart()
        let rest = inner.Tail
        let all = head :: rest
        let last = List.last all
        (List.take (all.Length - 1) all) @ [ last + string c ]
    | Match(scrutinee, arms) ->
        let header =
            if isFlat scrutinee then
                [ pad ind + "match " + flat scrutinee + " with" ]
            else
                [ pad ind + "match" ] @ render scrutinee (ind + 4) @ [ pad ind + "with" ]

        let armLines =
            arms
            |> List.collect (fun (pattern, body) ->
                if not (isFlat pattern) then
                    failwith "a match pattern is never structural"

                let lead = pad ind + "| " + flat pattern + " ->"

                if isFlat body then
                    [ lead + " " + flat body ]
                else
                    lead :: render body (ind + 4))

        header @ armLines
    | If(cond, thenE, elseE) ->
        let condLines =
            if isFlat cond then
                [ pad ind + "if " + flat cond + " then" ]
            else
                [ pad ind + "if" ] @ render cond (ind + 4) @ [ pad ind + "then" ]

        let elseLines =
            match elseE with
            | Some e -> [ pad ind + "else" ] @ render e (ind + 4)
            | None -> []

        condLines @ render thenE (ind + 4) @ elseLines
    | Fun(ps, body) ->
        if not (isFlat ps) then
            failwith "fun parameters are never structural"

        (pad ind + "fun " + flat ps + " ->") :: render body (ind + 4)
    | Let(isRec, pattern, rhs, body) ->
        let lead = pad ind + (if isRec then "let rec " else "let ") + flat pattern + " ="

        let binding =
            if isFlat rhs then
                [ lead + " " + flat rhs + " in" ]
            else
                let rhsLines = render rhs (ind + 4)
                let last = List.last rhsLines
                lead :: (List.take (rhsLines.Length - 1) rhsLines) @ [ last + " in" ]

        binding @ render body ind

// ─── Top level ───────────────────────────────────────────────────────

/// The header of a top-level binding — everything between `let`/`and` and the first `=` at
/// depth 0 — carried through token by token (it may hold a type annotation with arrows).
let private joinHeader (toks: Tok list) =
    let sb = StringBuilder()

    for t in toks do
        let piece =
            match t with
            | Open c -> string c
            | Close c -> string c
            | Kw k -> k
            | Word w -> w

        if sb.Length > 0 && not (piece = ")" || piece = "]" || piece = "}") then
            let prev = sb[sb.Length - 1]

            if not (prev = '(' || prev = '[' || prev = '{') then
                sb.Append ' ' |> ignore

        sb.Append piece |> ignore

    sb.ToString()

type private Decl =
    | Verbatim of string list
    | Binding of keyword: string * header: string * rhs: Expr

let normalise (moduleName: string) (text: string) : string =
    let sourceLines = text.Replace("\r\n", "\n").Split('\n')
    let toks = tokenise moduleName text |> Array.ofList

    // Declaration boundaries: a `let` / `and` / `type` / `module` / `open`
    // that is the first token of its line at paren depth 0.
    let starts = ResizeArray<int>()
    let mutable depth = 0

    toks
    |> Array.iteri (fun i p ->
        match p.Tok with
        | Open _ -> depth <- depth + 1
        | Close _ -> depth <- depth - 1
        | Kw("let" | "and")
        | Word("type" | "module" | "open") when depth = 0 && p.FirstOnLine -> starts.Add i
        | _ -> ())

    if depth <> 0 then
        failwithf "%s: unbalanced delimiters (depth %d at end of input)" moduleName depth

    if starts.Count = 0 || starts[0] <> 0 then
        failwithf "%s: the file does not begin with a declaration" moduleName

    let decls =
        [ for k in 0 .. starts.Count - 1 do
              let from = starts[k]
              let until = if k + 1 < starts.Count then starts[k + 1] else toks.Length
              let slice = toks[from .. until - 1]

              match slice[0].Tok with
              | Word("type" | "module" | "open") ->
                  // Verbatim, by source line: from this declaration's first line up to the
                  // line before the next declaration's first line.
                  let firstLine = slice[0].Line

                  let lastLine =
                      if until < toks.Length then
                          toks[until].Line - 1
                      else
                          sourceLines.Length

                  let lines =
                      [ for ln in firstLine..lastLine do
                            let raw = sourceLines[ln - 1].TrimEnd()

                            if raw <> "" then
                                if raw.StartsWith "|" then "    " + raw else raw ]

                  Verbatim lines
              | Kw keyword ->
                  let mutable d = 0
                  let mutable eq = -1
                  let mutable j = 1

                  while eq < 0 && j < slice.Length do
                      (match slice[j].Tok with
                       | Open _ -> d <- d + 1
                       | Close _ -> d <- d - 1
                       | Word "=" when d = 0 -> eq <- j
                       | _ -> ())

                      j <- j + 1

                  if eq < 0 then
                      failwithf "%s: line %d: a binding with no '='" moduleName slice[0].Line

                  let header =
                      slice[1 .. eq - 1] |> Array.map (fun p -> p.Tok) |> List.ofArray |> joinHeader

                  let parser = Parser(moduleName, slice[eq + 1 ..])
                  let rhs = parser.ParseExpr(fun _ -> false)

                  if not parser.AtEnd then
                      failwithf "%s: line %d: unconsumed input after a binding" moduleName parser.Line

                  Binding(keyword, header, rhs)
              | t -> failwithf "%s: line %d: unexpected declaration start %A" moduleName slice[0].Line t ]

    let out = ResizeArray<string>()

    out.Add(
        sprintf "// GENERATED by proofs/check.ps1 — the F* extractor's output for ../%s.fst, re-laid-out by" moduleName
    )

    out.Add "// proofs/normalise-extraction.fsx (Phase 850). Do not edit: the proof leg byte-diffs this file."
    out.Add ""

    decls
    |> List.iteri (fun k decl ->
        if k > 0 then
            out.Add ""

        match decl with
        | Verbatim lines -> out.AddRange lines
        | Binding(keyword, header, rhs) ->
            let lead = keyword + " " + header + " ="

            if isFlat rhs then
                out.Add(lead + " " + flat rhs)
            else
                out.Add lead
                out.AddRange(render rhs 4))

    String.Join("\n", out) + "\n"

// ─── Entry ───────────────────────────────────────────────────────────

let private args =
    fsi.CommandLineArgs
    |> Array.skip 1
    |> Array.filter (fun a -> not (a.StartsWith "--"))

if args.Length = 0 then
    eprintfn "usage: dotnet fsi normalise-extraction.fsx <extracted.fs> [...]"
    exit 2

for path in args do
    let moduleName = Path.GetFileNameWithoutExtension path
    let text = File.ReadAllText path
    let normalised = normalise moduleName text
    File.WriteAllText(path, normalised, UTF8Encoding(false))

    printfn
        "    normalised %s (%d lines -> %d lines)"
        (Path.GetFileName path)
        (text.Split('\n').Length)
        (normalised.Split('\n').Length)