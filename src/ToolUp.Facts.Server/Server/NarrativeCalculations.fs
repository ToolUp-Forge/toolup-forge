// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Globalization
open System.Numerics
open System.Security.Cryptography
open System.Text
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Authorised narrative calculations ───────────────────────────────
//
// A grounded report may state a figure derived from Facts — "revenue rose
// 2.7% on last week" — only as an AUTHORISED CALCULATION: one the deployment
// declared, the server computed, and the grounding gate derives again on its
// own before it admits it. The model never computes. It asks the `calculate`
// tool for a declared calculation over named Fact ids, and cites the
// reference the tool returns, stating the value exactly as the tool rendered
// it. A number the model writes that no Fact and no authorised calculation
// produced is still refused.
//
// **The declaration** (`CalculationDeclaration`) names an operator from a
// CLOSED set (`CalculationOperator`), the metrics its inputs may carry, a
// rounding rule and a display format. Nothing else can be computed: a
// calculation nobody declared is refused by name, at the tool and at the
// gate.
//
// **The reference** carries everything the gate needs to derive the figure
// again, so neither side keeps state between calls:
//
//     calc:<declaration>@<version>/<input fact id>/…/<content address>
//
// The content address is the SHA-256 of the canonical (declaration,
// version, inputs in order, result) tuple. The gate re-reads each input
// (current, disclosable, of a declared metric), re-derives the result
// through the declaration's own evaluation, and recomputes the address: a
// declaration whose version moved, an input that was superseded or
// withheld, and an address that does not match are each refused.
//
// **Two evaluations, held equal.** The tool computes through
// `Fuaran.Compute`'s exact decimal column operations (`CalculationCompute`). The
// gate computes through `CalculationDeclaration.evaluate`, exact rational
// arithmetic over `BigInteger` written for this purpose alone. Neither uses
// floating point; each rounds exactly once, at the declared places, under
// the declared mode. A zero denominator and a result `decimal` cannot hold
// exactly are declared refusals on both paths, never infinity or NaN.

/// The closed set of operators a calculation may be declared with. Inputs
/// are Fact ids in the order each operator names.
[<RequireQualifiedAccess>]
type CalculationOperator =
    /// `(to − from) / |from|`, inputs `[from; to]`.
    | PercentChange
    /// `to − from`, inputs `[from; to]`.
    | Difference
    /// `numerator / denominator`, inputs `[numerator; denominator]`.
    | Ratio
    /// `part / whole`, inputs `[part; whole]`.
    | Share
    /// The sum of two or more inputs.
    | Sum
    /// The arithmetic mean of two or more inputs.
    | Mean

module CalculationOperator =
    /// The most inputs a `Sum` or `Mean` takes.
    [<Literal>]
    let MaxInputs = 50

    /// Every operator, in declaration order.
    let all: CalculationOperator list = [
        CalculationOperator.PercentChange
        CalculationOperator.Difference
        CalculationOperator.Ratio
        CalculationOperator.Share
        CalculationOperator.Sum
        CalculationOperator.Mean
    ]

    /// The operator's wire name.
    let name (operator: CalculationOperator) : string =
        match operator with
        | CalculationOperator.PercentChange -> "percentChange"
        | CalculationOperator.Difference -> "difference"
        | CalculationOperator.Ratio -> "ratio"
        | CalculationOperator.Share -> "share"
        | CalculationOperator.Sum -> "sum"
        | CalculationOperator.Mean -> "mean"

    /// The operator a wire name names; anything else is refused naming the
    /// closed set.
    let tryParse (name': string) : Result<CalculationOperator, string> =
        match all |> List.tryFind (fun o -> name o = name') with
        | Some operator -> Ok operator
        | None ->
            Error(
                sprintf
                    "'%s' is not a calculation operator; the operators are %s"
                    name'
                    (all |> List.map name |> String.concat ", ")
            )

    /// The fewest and most inputs the operator takes.
    let arity (operator: CalculationOperator) : int * int =
        match operator with
        | CalculationOperator.PercentChange
        | CalculationOperator.Difference
        | CalculationOperator.Ratio
        | CalculationOperator.Share -> 2, 2
        | CalculationOperator.Sum
        | CalculationOperator.Mean -> 2, MaxInputs

    /// What the inputs are, in order, as the model is told.
    let inputs (operator: CalculationOperator) : string =
        match operator with
        | CalculationOperator.PercentChange
        | CalculationOperator.Difference -> "from, to"
        | CalculationOperator.Ratio -> "numerator, denominator"
        | CalculationOperator.Share -> "part, whole"
        | CalculationOperator.Sum
        | CalculationOperator.Mean -> sprintf "two to %d values" MaxInputs

/// How a result is brought to its declared places.
[<RequireQualifiedAccess>]
type CalculationRoundingMode =
    /// Ties to the even neighbour.
    | HalfEven
    /// Ties away from zero.
    | HalfAwayFromZero

/// A result's declared precision: decimal places, and how a tie is broken.
type CalculationRounding = {
    /// Decimal places of the displayed result, 0 to 10.
    Places: int
    Mode: CalculationRoundingMode
}

/// A scale word a result is displayed in.
[<RequireQualifiedAccess>]
type CalculationScale =
    | Thousand
    | Million
    | Billion

/// How a result is displayed — and what a narrative states it as.
[<RequireQualifiedAccess>]
type CalculationFormat =
    /// Multiplied by 100 and shown with `%`.
    | Percent
    /// An amount in the ISO 4217 currency `code`; every input must be in it.
    | Currency of code: string
    /// Divided by the scale and shown with its word ("1.2 million").
    | Scaled of CalculationScale
    /// A plain number.
    | Number

/// One calculation a deployment authorises. `Version` is the deployment's:
/// bump it whenever the operator, metrics, rounding or format change, and
/// every reference issued under the old version is refused as superseded.
type CalculationDeclaration = {
    /// Letters, digits, `-` and `_`; unique within a deployment.
    Name: string
    /// 1 or more.
    Version: int
    Operator: CalculationOperator
    /// The metric ids the inputs may carry. At least one.
    Metrics: string list
    Rounding: CalculationRounding
    Format: CalculationFormat
}

/// Why a calculation was refused — by the tool or, re-deriving it, by the
/// gate. Refusal wording names Fact ids and policies, never a withheld value.
type CalculationRefusal =
    | UndeclaredCalculation of name: string * declared: string list
    | WrongInputCount of calculation: string * expected: string * given: int
    | InputUnresolved of factId: string
    | InputSuperseded of factId: string * head: string
    | InputWithheld of factId: string * policyRef: string
    | InputNotScalar of factId: string
    | InputMetricUndeclared of factId: string * metric: string * declared: string list
    | InputMetricUnregistered of factId: string * metric: string
    | InputUnitsDisagree of units: string list
    | InputCurrencyMismatch of factId: string * unit: string * code: string
    | CurrencyUncheckable of calculation: string
    | ZeroDenominator of calculation: string
    | ResultOutOfRange of calculation: string
    | EvaluationFailed of calculation: string * reason: string
    | MalformedReference of reference: string
    | AddressMismatch of reference: string

module CalculationRefusal =
    /// The refusal's wording.
    let describe (refusal: CalculationRefusal) : string =
        match refusal with
        | UndeclaredCalculation(name, []) ->
            sprintf "calculation '%s' is not declared; this deployment declares none" name
        | UndeclaredCalculation(name, declared) ->
            sprintf "calculation '%s' is not declared; the declared ones are %s" name (String.concat ", " declared)
        | WrongInputCount(name, expected, given) ->
            sprintf "calculation '%s' takes %s, and %d were given" name expected given
        | InputUnresolved id -> sprintf "input '%s' does not resolve" id
        | InputSuperseded(id, head) -> sprintf "input '%s' is superseded by '%s'; request it again" id head
        | InputWithheld(id, policyRef) -> sprintf "input '%s' is not disclosable here under policy %s" id policyRef
        | InputNotScalar id -> sprintf "input '%s' is not a single number" id
        | InputMetricUndeclared(id, metric, declared) ->
            sprintf
                "input '%s' is a '%s' fact, and the calculation is declared over %s"
                id
                metric
                (String.concat ", " declared)
        | InputMetricUnregistered(id, metric) ->
            sprintf "input '%s' carries metric '%s', which is not registered" id metric
        | InputUnitsDisagree units -> sprintf "the inputs are in different units (%s)" (String.concat ", " units)
        | InputCurrencyMismatch(id, unit, code) ->
            sprintf "input '%s' is in '%s', and the calculation is declared in %s" id unit code
        | CurrencyUncheckable name ->
            sprintf
                "calculation '%s' is declared in a currency, and no metric registry is composed to check its inputs' currency"
                name
        | ZeroDenominator name -> sprintf "calculation '%s' would divide by zero" name
        | ResultOutOfRange name -> sprintf "calculation '%s' has a result too large to state exactly" name
        | EvaluationFailed(name, reason) -> sprintf "calculation '%s' could not be evaluated: %s" name reason
        | MalformedReference reference -> sprintf "'%s' is not a calculation reference" reference
        | AddressMismatch reference -> sprintf "reference '%s' does not match the calculation it names" reference

/// A computed calculation: what the tool returns and the gate admits.
type CalculationRecord = {
    Declaration: CalculationDeclaration
    /// The input Fact ids, in the operator's order.
    Inputs: string list
    /// The result in display units (a percentage for `Percent`, millions for
    /// `Scaled Million`), rounded to the declared places.
    Display: decimal
    /// The figure a narrative states, in the units the gate reads a stated
    /// value in: `Display` for a percentage, a currency or a number, and
    /// `Display` times the scale for a scaled result.
    Stated: decimal
    /// The canonical rendering ("2.7%", "£1,250.50", "1.2 million").
    Rendering: string
    /// Lowercase-hex SHA-256 of the canonical tuple.
    Address: string
    /// `calc:<declaration>@<version>/<inputs>/<address>`.
    Reference: string
}

/// The gate's reading of a `calc:` reference.
type CalculationCheck =
    /// The calculation holds: re-derived from current inputs, with the same
    /// address.
    | CalculationHolds of CalculationRecord
    /// An input was superseded, or the declaration's version moved; the
    /// successor is the reference the same calculation has now, when it can
    /// be computed.
    | CalculationMoved of successor: string option
    /// An input may not be disclosed at the surface.
    | CalculationWithheld of policyRef: string
    /// Anything else, named.
    | CalculationRefused of CalculationRefusal

/// A deployment's validated calculation declarations. Build with
/// `NarrativeCalculations.declare`; empty, there is no tool and no kind.
type NarrativeCalculations = private {
    ByName: Map<string, CalculationDeclaration>
} with

    /// The declarations, by name.
    member this.Declarations: CalculationDeclaration list =
        this.ByName |> Map.toList |> List.map snd

module CalculationDeclaration =

    // ── The declaration's own evaluation (the gate's path) ─────────

    /// A decimal as an exact fraction (numerator, positive denominator).
    let private rational (d: decimal) : BigInteger * BigInteger =
        let bits = Decimal.GetBits d

        let mantissa =
            BigInteger(uint32 bits[0])
            + (BigInteger(uint32 bits[1]) <<< 32)
            + (BigInteger(uint32 bits[2]) <<< 64)

        let scale = (bits[3] >>> 16) &&& 0xFF
        let signed = if bits[3] < 0 then BigInteger.Negate mantissa else mantissa
        signed, BigInteger.Pow(BigInteger 10, scale)

    let private add (a: BigInteger, b: BigInteger) (c: BigInteger, d: BigInteger) = a * d + c * b, b * d

    let private sub (a: BigInteger, b: BigInteger) (c: BigInteger, d: BigInteger) = a * d - c * b, b * d

    /// `x / y`, or `None` when `y` is zero.
    let private divide (a: BigInteger, b: BigInteger) (c: BigInteger, d: BigInteger) =
        if c.IsZero then
            None
        else
            let n, m = a * d, b * c

            Some(
                if m.Sign < 0 then
                    BigInteger.Negate n, BigInteger.Negate m
                else
                    n, m
            )

    let private scaleFactor (scale: CalculationScale) : BigInteger =
        match scale with
        | CalculationScale.Thousand -> BigInteger 1_000
        | CalculationScale.Million -> BigInteger 1_000_000
        | CalculationScale.Billion -> BigInteger 1_000_000_000

    /// The largest mantissa a `decimal` holds exactly (2^96 − 1).
    let private maxMantissa = BigInteger.Pow(BigInteger 2, 96) - BigInteger.One

    /// The exact fraction `p / q` (q > 0) rounded once to `places` under
    /// `mode`, as a `decimal`, or `None` when no `decimal` holds it exactly.
    let private round (rounding: CalculationRounding) (p: BigInteger, q: BigInteger) : decimal option =
        let n = p * BigInteger.Pow(BigInteger 10, rounding.Places)
        let mutable remainder = BigInteger.Zero
        let quotient = BigInteger.DivRem(n, q, &remainder)
        let twice = BigInteger.Abs remainder * BigInteger 2

        let bump =
            match compare twice q with
            | c when c > 0 -> true
            | 0 when not remainder.IsZero ->
                match rounding.Mode with
                | CalculationRoundingMode.HalfAwayFromZero -> true
                | CalculationRoundingMode.HalfEven -> not quotient.IsEven
            | _ -> false

        let rounded = if bump then quotient + BigInteger(n.Sign) else quotient

        // Strip trailing zeros, so the range test is on the value and not on
        // the places it was rounded to.
        let mutable mantissa = rounded
        let mutable scale = rounding.Places

        while scale > 0 && (mantissa % BigInteger 10).IsZero && not mantissa.IsZero do
            mantissa <- mantissa / BigInteger 10
            scale <- scale - 1

        if BigInteger.Abs mantissa > maxMantissa then
            None
        else
            let magnitude = BigInteger.Abs mantissa
            let mask = BigInteger(UInt32.MaxValue)
            let lo = int (uint32 (magnitude &&& mask))
            let mid = int (uint32 ((magnitude >>> 32) &&& mask))
            let hi = int (uint32 ((magnitude >>> 64) &&& mask))
            Some(Decimal(lo, mid, hi, mantissa.Sign < 0, byte scale))

    /// The declaration's own evaluation over `values` (the inputs' values in
    /// the operator's order): exact rational arithmetic, one rounding, in
    /// display units. The grounding gate re-derives every calculation through
    /// this function, never through the tool's path.
    let evaluate (declaration: CalculationDeclaration) (values: decimal list) : Result<decimal, CalculationRefusal> =
        let xs = values |> List.map rational
        let one = BigInteger.One, BigInteger.One
        let total () = xs |> List.reduce add
        let low, high = CalculationOperator.arity declaration.Operator

        // `Some None` is a zero denominator; `None` an input count the
        // operator does not take.
        let quotient =
            match declaration.Operator, xs with
            | _ when List.length xs < low || List.length xs > high -> None
            | CalculationOperator.PercentChange, [ from; to' ] ->
                let a, b = from
                Some(divide (sub to' from) (BigInteger.Abs a, b))
            | CalculationOperator.Difference, [ from; to' ] -> Some(Some(sub to' from))
            | CalculationOperator.Ratio, [ x; y ]
            | CalculationOperator.Share, [ x; y ] -> Some(divide x y)
            | CalculationOperator.Sum, _ -> Some(divide (total ()) one)
            | CalculationOperator.Mean, _ -> Some(divide (total ()) (BigInteger(List.length xs), BigInteger.One))
            | _ -> None

        match quotient with
        | None ->
            Error(
                WrongInputCount(declaration.Name, CalculationOperator.inputs declaration.Operator, List.length values)
            )
        | Some None -> Error(ZeroDenominator declaration.Name)
        | Some(Some(p, q)) ->
            let p, q =
                match declaration.Format with
                | CalculationFormat.Percent -> p * BigInteger 100, q
                | CalculationFormat.Scaled scale -> p, q * scaleFactor scale
                | CalculationFormat.Currency _
                | CalculationFormat.Number -> p, q

            match round declaration.Rounding (p, q) with
            | Some value -> Ok value
            | None -> Error(ResultOutOfRange declaration.Name)

    // ── What a result is stated as ────────────────────────────────

    /// The result as the content address hashes it: fixed at the declared
    /// places, invariant culture.
    let resultText (declaration: CalculationDeclaration) (display: decimal) : string =
        display.ToString("F" + string declaration.Rounding.Places, CultureInfo.InvariantCulture)

    /// The canonical rendering of a display value.
    let render (declaration: CalculationDeclaration) (display: decimal) : string =
        let number (d: decimal) =
            d.ToString("N" + string declaration.Rounding.Places, CultureInfo.InvariantCulture)

        match declaration.Format with
        | CalculationFormat.Percent -> number display + "%"
        | CalculationFormat.Number -> number display
        | CalculationFormat.Scaled scale ->
            let word =
                match scale with
                | CalculationScale.Thousand -> "thousand"
                | CalculationScale.Million -> "million"
                | CalculationScale.Billion -> "billion"

            number display + " " + word
        | CalculationFormat.Currency code ->
            let symbol =
                CurrencySymbols.prefix code
                |> Option.defaultValue (code.ToUpperInvariant() + " ")

            (if display < 0m then "-" else "") + symbol + number (abs display)

    /// The figure a narrative states for `display`, in the units the gate
    /// compares a stated value in. `None` when it overflows.
    let stated (declaration: CalculationDeclaration) (display: decimal) : decimal option =
        try
            match declaration.Format with
            | CalculationFormat.Scaled CalculationScale.Thousand -> Some(display * 1_000m)
            | CalculationFormat.Scaled CalculationScale.Million -> Some(display * 1_000_000m)
            | CalculationFormat.Scaled CalculationScale.Billion -> Some(display * 1_000_000_000m)
            | _ -> Some display
        with :? OverflowException ->
            None

    /// The content address: lowercase-hex SHA-256 over the canonical
    /// (declaration, version, inputs in order, result) tuple.
    let address (declaration: CalculationDeclaration) (inputs: string list) (display: decimal) : string =
        let canonical =
            String.concat "\n" [
                "calc/1"
                declaration.Name
                string declaration.Version
                String.concat "," inputs
                resultText declaration display
            ]

        SHA256.HashData(Encoding.UTF8.GetBytes canonical)
        |> Convert.ToHexString
        |> _.ToLowerInvariant()

    /// The reference a narrative cites.
    let reference (declaration: CalculationDeclaration) (inputs: string list) (address: string) : string =
        sprintf
            "%s:%s@%d/%s/%s"
            AuthorisedCalculation.ReferenceKind
            declaration.Name
            declaration.Version
            (String.concat "/" inputs)
            address

    /// The record of `display` computed over `inputs`.
    let record
        (declaration: CalculationDeclaration)
        (inputs: string list)
        (display: decimal)
        : Result<CalculationRecord, CalculationRefusal> =
        match stated declaration display with
        | None -> Error(ResultOutOfRange declaration.Name)
        | Some figure ->
            let address = address declaration inputs display

            Ok {
                Declaration = declaration
                Inputs = inputs
                Display = display
                Stated = figure
                Rendering = render declaration display
                Address = address
                Reference = reference declaration inputs address
            }

    /// A reference's parts — declaration name, version, inputs, address —
    /// from the id after `calc:`. `None` when it is not one.
    let parseReference (referenceId: string) : (string * int * string list * string) option =
        match referenceId.Split '/' |> List.ofArray with
        | head :: (_ :: _ :: _ as rest) ->
            let inputs = List.take (List.length rest - 1) rest
            let address = List.last rest

            match head.Split '@' with
            | [| name; version |] ->
                match Int32.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture) with
                | true, v when
                    v > 0
                    && name <> ""
                    && address.Length = 64
                    && address |> Seq.forall (fun c -> Char.IsAsciiDigit c || (c >= 'a' && c <= 'f'))
                    && inputs |> List.forall (fun i -> i <> "")
                    ->
                    Some(name, v, inputs, address)
                | _ -> None
            | _ -> None
        | _ -> None

/// The tool's evaluation, through `Fuaran.Compute`'s exact decimal column
/// operations over a one-row table: one `Quotient` of exact decimals, which
/// rounds once under the declared mode. Kept apart from
/// `CalculationDeclaration.evaluate`, which the gate re-derives through.
module internal CalculationCompute =
    open Fuaran.Core
    open Fuaran.Compute

    let private text (d: decimal) =
        d.ToString(CultureInfo.InvariantCulture)

    let private literal (d: decimal) : ColExpr =
        match Cell.decimal (text d) with
        | Some cell -> Lit cell
        | None -> failwithf "'%s' is not decimal text" (text d)

    let evaluate (declaration: CalculationDeclaration) (values: decimal list) : Result<decimal, CalculationRefusal> =
        let name = declaration.Name
        let columns = values |> List.mapi (fun i _ -> sprintf "x%d" i)
        let x (i: int) = Col columns[i]

        let sum () =
            columns |> List.map Col |> List.reduce (fun a b -> Binary(Add, a, b))

        let shape =
            match declaration.Operator, List.length values with
            | CalculationOperator.PercentChange, 2 -> Some(Binary(Sub, x 1, x 0), ApplyFn(Abs, [ x 0 ]))
            | CalculationOperator.Difference, 2 -> Some(Binary(Sub, x 1, x 0), literal 1m)
            | CalculationOperator.Ratio, 2
            | CalculationOperator.Share, 2 -> Some(x 0, x 1)
            | CalculationOperator.Sum, n when n >= 2 && n <= CalculationOperator.MaxInputs -> Some(sum (), literal 1m)
            | CalculationOperator.Mean, n when n >= 2 && n <= CalculationOperator.MaxInputs ->
                Some(sum (), literal (decimal n))
            | _ -> None

        match shape with
        | None -> Error(WrongInputCount(name, CalculationOperator.inputs declaration.Operator, List.length values))
        | Some(numerator, denominator) ->
            let numerator, denominator =
                match declaration.Format with
                | CalculationFormat.Percent -> Binary(Mul, numerator, literal 100m), denominator
                | CalculationFormat.Scaled CalculationScale.Thousand ->
                    numerator, Binary(Mul, denominator, literal 1_000m)
                | CalculationFormat.Scaled CalculationScale.Million ->
                    numerator, Binary(Mul, denominator, literal 1_000_000m)
                | CalculationFormat.Scaled CalculationScale.Billion ->
                    numerator, Binary(Mul, denominator, literal 1_000_000_000m)
                | CalculationFormat.Currency _
                | CalculationFormat.Number -> numerator, denominator

            let rounding: Rounding = {
                Scale = Slot.Lit declaration.Rounding.Places
                Mode =
                    match declaration.Rounding.Mode with
                    | CalculationRoundingMode.HalfEven -> RoundingMode.HalfEven
                    | CalculationRoundingMode.HalfAwayFromZero -> RoundingMode.HalfUp
            }

            let built =
                List.zip columns values
                |> List.map (fun (column, value) ->
                    match Cell.decimal (text value) with
                    | None -> Error(sprintf "'%s' is not decimal text" (text value))
                    | Some cell ->
                        Column.ofCells column DecimalType [ cell ]
                        |> Result.map (fun c -> Field.create column DecimalType, c)
                        |> Result.mapError (sprintf "%A"))

            match
                built
                |> List.tryPick (function
                    | Error e -> Some e
                    | Ok _ -> None)
            with
            | Some reason -> Error(EvaluationFailed(name, reason))
            | None ->
                let pairs = built |> List.choose Result.toOption

                let table: Table = {
                    Schema = pairs |> List.map fst
                    Columns = pairs |> List.map snd
                }

                let pipeline = [ Derive("result", Quotient(numerator, denominator, rounding)) ]

                match DataFrame.evalPipeline pipeline table with
                | Error e -> Error(EvaluationFailed(name, sprintf "%A" e))
                | Ok result ->
                    match Table.tryColumn "result" result |> Option.map Column.toCells with
                    | Some [ Null ] -> Error(ZeroDenominator name)
                    | Some [ Cell.Decimal digits ] ->
                        match
                            Decimal.TryParse(
                                digits,
                                NumberStyles.AllowLeadingSign ||| NumberStyles.AllowDecimalPoint,
                                CultureInfo.InvariantCulture
                            )
                        with
                        | true, value when DecimalText.tryCanonical (text value) = DecimalText.tryCanonical digits ->
                            Ok value
                        | _ -> Error(ResultOutOfRange name)
                    | other -> Error(EvaluationFailed(name, sprintf "unexpected result %A" other))

module NarrativeCalculations =

    /// No calculations: no tool, no kind.
    let empty: NarrativeCalculations = { ByName = Map.empty }

    /// Is nothing declared?
    let isEmpty (calculations: NarrativeCalculations) : bool = Map.isEmpty calculations.ByName

    /// The declaration named `name`, when one is.
    let tryFind (name: string) (calculations: NarrativeCalculations) : CalculationDeclaration option =
        calculations.ByName.TryFind name

    let private validName (name: string) =
        name <> ""
        && name |> Seq.forall (fun c -> Char.IsAsciiLetterOrDigit c || c = '-' || c = '_')

    let private refusalsOf (declaration: CalculationDeclaration) : string list = [
        let name = declaration.Name

        if not (validName name) then
            yield sprintf "calculation name '%s' must be letters, digits, '-' and '_'" name

        if declaration.Version < 1 then
            yield sprintf "calculation '%s' has version %d; a version is 1 or more" name declaration.Version

        if
            List.isEmpty declaration.Metrics
            || declaration.Metrics |> List.exists String.IsNullOrWhiteSpace
        then
            yield sprintf "calculation '%s' must name at least one metric, and no empty one" name

        if declaration.Rounding.Places < 0 || declaration.Rounding.Places > 10 then
            yield sprintf "calculation '%s' rounds to %d places; places are 0 to 10" name declaration.Rounding.Places

        match declaration.Format with
        | CalculationFormat.Currency code when CurrencySymbols.prefix code |> Option.isNone ->
            yield sprintf "calculation '%s' names currency '%s', which is not an ISO 4217 code" name code
        | _ -> ()

        let formatAllowed =
            match declaration.Operator, declaration.Format with
            | (CalculationOperator.PercentChange | CalculationOperator.Share | CalculationOperator.Ratio),
              (CalculationFormat.Percent | CalculationFormat.Number) -> true
            | (CalculationOperator.Difference | CalculationOperator.Sum | CalculationOperator.Mean),
              (CalculationFormat.Currency _ | CalculationFormat.Scaled _ | CalculationFormat.Number) -> true
            | _ -> false

        if not formatAllowed then
            yield
                sprintf
                    "calculation '%s' declares %s with a format it cannot be shown in: percentChange, share and ratio show as a percent or a number; difference, sum and mean as a currency, a scaled number or a number"
                    name
                    (CalculationOperator.name declaration.Operator)
    ]

    /// Validate and hold `declarations`. Every problem is refused by name: a
    /// malformed name, a version below 1, no metric, places outside 0 to 10,
    /// a currency code that is not ISO 4217, a format the operator cannot be
    /// shown in, and a name declared twice.
    let declare (declarations: CalculationDeclaration list) : Result<NarrativeCalculations, string list> =
        let duplicates =
            declarations
            |> List.countBy _.Name
            |> List.filter (fun (_, n) -> n > 1)
            |> List.map (fun (name, _) -> sprintf "calculation '%s' is declared more than once" name)

        match (declarations |> List.collect refusalsOf) @ duplicates with
        | [] ->
            Ok {
                ByName = declarations |> List.map (fun d -> d.Name, d) |> Map.ofList
            }
        | refusals -> Error refusals

    // ── Reading the inputs ────────────────────────────────────────

    type private InputState =
        | Present of Fact
        | Moved of head: string
        | Hidden of policyRef: string
        | Absent

    /// Each input's state: resolved in `scope`, disclosable at every one of
    /// `surfaces` for `principal`, and the head of its lineage.
    let private readInputs
        (store: IFactStore)
        (disclosure: IFactDisclosureGate)
        (scope: ResolvedScope)
        (principal: string)
        (surfaces: FactEgressSurface list)
        (ids: string list)
        : Async<Map<string, InputState>> =
        async {
            let distinct = List.distinct ids

            let! verdicts =
                surfaces
                |> List.map (fun surface -> disclosure.Check(scope, principal, surface, distinct))
                |> Async.Sequential

            // An id a surface returned no verdict for is denied: the door
            // never fails open.
            let withheld (id: string) =
                verdicts
                |> Array.tryPick (fun byId ->
                    match Map.tryFind id byId with
                    | Some FactDisclosable -> None
                    | Some(FactNotDisclosable policyRef) -> Some policyRef
                    | None -> Some "unknown-fact")

            let! states =
                distinct
                |> List.map (fun id -> async {
                    let! chain = store.QuerySupersessionChain(scope, id) |> FactStoreResult.orRaiseAsync

                    match List.tryFind (fun (f: Fact) -> f.FactId = id) chain with
                    | None -> return id, Absent
                    | Some fact ->
                        match withheld id with
                        | Some policyRef -> return id, Hidden policyRef
                        | None ->
                            let head = List.last chain

                            if head.FactId <> id then
                                return id, Moved head.FactId
                            else
                                return id, Present fact
                })
                |> Async.Sequential

            return Map.ofArray states
        }

    /// The inputs' values in order, once every input is present, a single
    /// number, of a declared metric and, with a registry, in one unit (the
    /// declared currency for a currency calculation).
    let private admitInputs
        (registry: Grounding.IMetricRegistry option)
        (declaration: CalculationDeclaration)
        (ids: string list)
        (states: Map<string, InputState>)
        : Result<decimal list, CalculationRefusal> =
        let each =
            ids
            |> List.map (fun id ->
                match states.TryFind id |> Option.defaultValue Absent with
                | Absent -> Error(InputUnresolved id)
                | Hidden policyRef -> Error(InputWithheld(id, policyRef))
                | Moved head -> Error(InputSuperseded(id, head))
                | Present fact ->
                    match fact.Value with
                    | Scalar value when List.contains fact.Metric.Value declaration.Metrics ->
                        Ok(id, fact.Metric.Value, value)
                    | Scalar _ -> Error(InputMetricUndeclared(id, fact.Metric.Value, declaration.Metrics))
                    | _ -> Error(InputNotScalar id))

        match
            each
            |> List.tryPick (function
                | Error e -> Some e
                | Ok _ -> None)
        with
        | Some refusal -> Error refusal
        | None ->
            let inputs = each |> List.choose Result.toOption

            match registry, declaration.Format with
            | None, CalculationFormat.Currency _ -> Error(CurrencyUncheckable declaration.Name)
            | None, _ -> Ok(inputs |> List.map (fun (_, _, v) -> v))
            | Some registry, format ->
                let units =
                    inputs
                    |> List.map (fun (id, metric, _) ->
                        match registry.TryGetMetric metric with
                        | Some definition -> Ok(id, definition.Unit)
                        | None -> Error(InputMetricUnregistered(id, metric)))

                match
                    units
                    |> List.tryPick (function
                        | Error e -> Some e
                        | Ok _ -> None)
                with
                | Some refusal -> Error refusal
                | None ->
                    let units = units |> List.choose Result.toOption

                    let distinctUnits =
                        units |> List.map (fun (_, u) -> u.ToUpperInvariant()) |> List.distinct

                    let mismatch =
                        match format with
                        | CalculationFormat.Currency code ->
                            units
                            |> List.tryFind (fun (_, u) ->
                                not (String.Equals(u, code, StringComparison.OrdinalIgnoreCase)))
                            |> Option.map (fun (id, u) -> InputCurrencyMismatch(id, u, code.ToUpperInvariant()))
                        | _ when
                            declaration.Operator <> CalculationOperator.Ratio
                            && List.length distinctUnits > 1
                            ->
                            Some(InputUnitsDisagree distinctUnits)
                        | _ -> None

                    match mismatch with
                    | Some refusal -> Error refusal
                    | None -> Ok(inputs |> List.map (fun (_, _, v) -> v))

    let private arityOk (declaration: CalculationDeclaration) (ids: string list) =
        let low, high = CalculationOperator.arity declaration.Operator
        let n = List.length ids

        if n < low || n > high then
            Error(WrongInputCount(declaration.Name, CalculationOperator.inputs declaration.Operator, n))
        else
            Ok()

    let private declared (calculations: NarrativeCalculations) (name: string) =
        match tryFind name calculations with
        | Some declaration -> Ok declaration
        | None -> Error(UndeclaredCalculation(name, calculations.ByName |> Map.toList |> List.map fst))

    /// The tool's path: compute the calculation `name` over `inputs` through
    /// `Fuaran.Compute`, after checking every input at the tool-result and
    /// the narrative-publication surfaces.
    let compute
        (calculations: NarrativeCalculations)
        (store: IFactStore)
        (disclosure: IFactDisclosureGate)
        (registry: Grounding.IMetricRegistry option)
        (scope: ResolvedScope)
        (principal: string)
        (name: string)
        (inputs: string list)
        : Async<Result<CalculationRecord, CalculationRefusal>> =
        async {
            match
                declared calculations name
                |> Result.bind (fun d -> arityOk d inputs |> Result.map (fun () -> d))
            with
            | Error refusal -> return Error refusal
            | Ok declaration ->
                let! states =
                    readInputs store disclosure scope principal [ FactToolResult; FactNarrativePublication ] inputs

                return
                    admitInputs registry declaration inputs states
                    |> Result.bind (CalculationCompute.evaluate declaration)
                    |> Result.bind (CalculationDeclaration.record declaration inputs)
        }

    /// The gate's path: derive the calculation a `calc:` reference names
    /// again, from its inputs as they are now at `surface`, through the
    /// declaration's own evaluation, and check its address.
    let rederive
        (calculations: NarrativeCalculations)
        (store: IFactStore)
        (disclosure: IFactDisclosureGate)
        (registry: Grounding.IMetricRegistry option)
        (scope: ResolvedScope)
        (principal: string)
        (surface: FactEgressSurface)
        (referenceId: string)
        : Async<CalculationCheck> =
        let reference = sprintf "%s:%s" AuthorisedCalculation.ReferenceKind referenceId

        let derive (declaration: CalculationDeclaration) (ids: string list) (states: Map<string, InputState>) =
            admitInputs registry declaration ids states
            |> Result.bind (CalculationDeclaration.evaluate declaration)
            |> Result.bind (CalculationDeclaration.record declaration ids)

        async {
            match CalculationDeclaration.parseReference referenceId with
            | None -> return CalculationRefused(MalformedReference reference)
            | Some(name, version, ids, address) ->
                match
                    declared calculations name
                    |> Result.bind (fun d -> arityOk d ids |> Result.map (fun () -> d))
                with
                | Error refusal -> return CalculationRefused refusal
                | Ok declaration ->
                    let! states = readInputs store disclosure scope principal [ surface ] ids

                    let stateOf id =
                        states.TryFind id |> Option.defaultValue Absent

                    let absent =
                        ids
                        |> List.tryFind (fun id ->
                            match stateOf id with
                            | Absent -> true
                            | _ -> false)

                    let hidden =
                        ids
                        |> List.tryPick (fun id ->
                            match stateOf id with
                            | Hidden policyRef -> Some policyRef
                            | _ -> None)

                    let heads =
                        ids
                        |> List.map (fun id ->
                            match stateOf id with
                            | Moved head -> head
                            | _ -> id)

                    match absent, hidden with
                    | Some id, _ -> return CalculationRefused(InputUnresolved id)
                    | None, Some policyRef -> return CalculationWithheld policyRef
                    | None, None when heads <> ids || version <> declaration.Version ->
                        let! current = readInputs store disclosure scope principal [ surface ] heads

                        return
                            CalculationMoved(
                                derive declaration heads current |> Result.toOption |> Option.map _.Reference
                            )
                    | None, None ->
                        match derive declaration ids states with
                        | Error refusal -> return CalculationRefused refusal
                        | Ok record when record.Address <> address ->
                            return CalculationRefused(AddressMismatch reference)
                        | Ok record -> return CalculationHolds record
        }

    /// The certificate admission sealing `record`, cited at `location`.
    let admission (location: string) (record: CalculationRecord) : CertificateAdmission =
        let declaration = record.Declaration

        let format =
            match declaration.Format with
            | CalculationFormat.Percent -> [ "percent" ]
            | CalculationFormat.Number -> [ "number" ]
            | CalculationFormat.Currency code -> [ "currency"; code.ToUpperInvariant() ]
            | CalculationFormat.Scaled CalculationScale.Thousand -> [ "scaled"; "thousand" ]
            | CalculationFormat.Scaled CalculationScale.Million -> [ "scaled"; "million" ]
            | CalculationFormat.Scaled CalculationScale.Billion -> [ "scaled"; "billion" ]

        let mode =
            match declaration.Rounding.Mode with
            | CalculationRoundingMode.HalfEven -> "half-even"
            | CalculationRoundingMode.HalfAwayFromZero -> "half-away-from-zero"

        CertificateAdmission.ofCalculation location record.Reference [
            CertificateAdmission.DeclarationAttribute, [ declaration.Name ]
            CertificateAdmission.DeclarationVersionAttribute, [ string declaration.Version ]
            CertificateAdmission.OperationAttribute, [ CalculationOperator.name declaration.Operator ]
            CertificateAdmission.InputsAttribute, record.Inputs
            CertificateAdmission.RoundingAttribute, [ string declaration.Rounding.Places; mode ]
            CertificateAdmission.FormatAttribute, format
            CertificateAdmission.ResultAttribute, [ CalculationDeclaration.resultText declaration record.Display ]
            CertificateAdmission.AddressAttribute, [ record.Address ]
        ]

    // ── The `calculate` tool ──────────────────────────────────────

    let private describeDeclaration (declaration: CalculationDeclaration) =
        sprintf
            "%s (%s over %s; inputs in order: %s)"
            declaration.Name
            (CalculationOperator.name declaration.Operator)
            (String.concat ", " declaration.Metrics)
            (CalculationOperator.inputs declaration.Operator)

    /// The `calculate` tool's declaration, listing the declared calculations.
    let definition (calculations: NarrativeCalculations) : AIToolDefinition = {
        Name = AuthorisedCalculation.ToolName
        Description =
            "Compute a derived figure — a change, a difference, a ratio, a share, a sum or a mean — from verified facts. The server computes it by a formula this deployment declared; you never compute a figure yourself. Name one declared calculation and its input fact ids in order. The answer is a reference and a value: cite it as [[label|value|reference]], with the value exactly as rendered. An input that is superseded, withheld or of another metric is refused with the reason. The declared calculations: "
            + (calculations.Declarations |> List.map describeDeclaration |> String.concat "; ")
            + "."
        Parameters = [
            {
                Name = "calculation"
                Type = "string"
                Description = "The name of a declared calculation."
                Required = true
                Default = None
            }
            {
                Name = "inputs"
                Type = "string"
                Description = "The input fact ids, in the calculation's order, separated by commas."
                Required = true
                Default = None
            }
        ]
        SourceModule = FactEvents.SourceModule
        EmitsActions = None
        Location = ServerResident
        Surface = Both
        IsLiveInterface = false
        ResultBudget = DefaultResultBudget
        Effects = ToolEffectDeclaration.readFacts
    }

    /// The executor over explicit dependencies, which `tools` adapts the
    /// request context onto.
    let executeWith
        (calculations: NarrativeCalculations)
        (store: IFactStore)
        (disclosure: IFactDisclosureGate)
        (registry: Grounding.IMetricRegistry option)
        (scope: ResolvedScope)
        (principal: string)
        (argsJson: string)
        : Async<string> =
        async {
            let args =
                FactTool.parseObject argsJson
                |> Result.bind (fun root ->
                    FactTool.requiredArg "a declared calculation name" root "calculation"
                    |> Result.bind (fun name ->
                        FactTool.requiredArg "the input fact ids, separated by commas" root "inputs"
                        |> Result.map (fun inputs ->
                            name,
                            inputs.Split([| ','; ' ' |], StringSplitOptions.RemoveEmptyEntries)
                            |> List.ofArray)))

            match args with
            | Error message -> return FactTool.serialize {| error = message |}
            | Ok(name, inputs) ->
                match! compute calculations store disclosure registry scope principal name inputs with
                | Error refusal ->
                    return
                        FactTool.serialize {|
                            error = CalculationRefusal.describe refusal
                        |}
                | Ok record ->
                    return
                        FactTool.serialize {|
                            reference = record.Reference
                            value = record.Rendering
                            calculation = record.Declaration.Name
                            operation = CalculationOperator.name record.Declaration.Operator
                            inputs = record.Inputs
                            cite = sprintf "[[<label in words>|%s|%s]]" record.Rendering record.Reference
                        |}
        }

    /// The narrative tools the calculations give a grounded run: the
    /// `calculate` tool, or none when nothing is declared. Pass them beside
    /// `NarrativeGrounding.factTools`, and compose the same value with
    /// `FactsCompose.withNarrativeCalculations` so the gate can re-derive
    /// what the tool computes.
    let tools
        (calculations: NarrativeCalculations)
        : (AIToolDefinition * (HttpContext -> string -> Async<string>)) list =
        if isEmpty calculations then
            []
        else
            [
                definition calculations,
                FactTool.define AuthorisedCalculation.ToolName (fun c argsJson ->
                    executeWith calculations c.Store c.Gate c.Registry c.Scope c.UserId argsJson)
            ]