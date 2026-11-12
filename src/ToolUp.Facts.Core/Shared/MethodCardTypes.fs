// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

// ─── Method cards ────────────────────────────────────────────────────
//
// A method card is a versioned, hashed description of one analytical method:
// what it is, what it means, when it applies, how it is called, what it
// answers and how that is read. A deployment registers cards with the method
// registry (`MethodRegistry` in `ToolUp.Facts.Server`); each registered card
// becomes one spec tool, an entry in the method profile the assistant's
// prompt carries, and a page `describe_method` answers.
//
// These records are forge's Fable-safe mirror of the card's eight parts:
// identity, meaning, applicability, signature, outputs, interpretation,
// presentation and examples. The published card specification and its
// generator library will replace the mirror when they ship; until then the
// registry reads this shape, and a card read from that package maps onto it
// part for part.

/// How reproducible a method's result is.
[<RequireQualifiedAccess>]
type MethodDeterminism =
    /// The same inputs give the same result on every host.
    | Pinned
    /// Stochastic, and replayable from the seed the call names.
    | Seeded
    /// Deterministic up to the host's floating-point rounding.
    | HostRounded

/// Which method a card describes, and the definition it binds to.
type MethodIdentity = {
    /// The method's stable id: the operation a fact computed by it names.
    Id: string
    /// The card's version: the version a fact computed by it names.
    Version: string
    /// The hash of the pinned kernel or routine the card binds to. An
    /// evaluator is registered against it, and a card whose hash its
    /// evaluator does not implement is refused.
    DefinitionHash: string
    /// The package that implements the method.
    Package: string
    /// How reproducible its result is.
    Determinism: MethodDeterminism
}

/// What a method does, for a reader who is not a statistician.
type MethodMeaning = {
    /// One paragraph saying what the method does: the tool's description.
    Summary: string
    /// The question it answers.
    Answers: string
    /// What it assumes of the data.
    Assumes: string list
}

/// A named condition of a method: a precondition on its call or its data, or
/// a refusal its evaluator may answer. A call that does not meet it is
/// refused by its name, with its remedy.
type MethodCondition = {
    /// The condition's name: an identifier (an upper-case letter, then
    /// letters and digits), which a refusal carries.
    Name: string
    /// The condition, as the model reads it. One line.
    Statement: string
    /// What to repair when it is not met. One line.
    Remedy: string
}

/// Another method, and when to prefer it.
type MethodAlternative = {
    /// The other method's id or name.
    Method: string
    /// When it is the better choice.
    PreferWhen: string
}

/// Whether choosing a method is mechanical or a judgement.
[<RequireQualifiedAccess>]
type MethodChoice =
    /// The data decide whether the method applies.
    | Mechanical
    /// The choice is a judgement: the assistant proposes and the user
    /// decides.
    | Judgement

/// When a method applies.
type MethodApplicability = {
    /// The conditions a call or its data must meet.
    Preconditions: MethodCondition list
    /// When the method does not apply.
    DoesNotApply: string list
    /// The alternatives, and when to prefer each.
    Alternatives: MethodAlternative list
    /// Whether choosing the method is mechanical or a judgement.
    Choice: MethodChoice
}

/// One argument of a method's call, beside the slice every method reads.
type MethodParameter = {
    /// The argument's name in the tool call.
    Name: string
    /// What it means.
    Meaning: string
    /// Its JSON Schema, as JSON text. Registration decodes it, and refuses a
    /// card whose schema does not decode.
    Schema: string
    /// Whether the call must carry it.
    Required: bool
    /// Its default, as JSON text, when the call may omit it.
    Default: string option
    /// Its admissible range, as the model reads it.
    Range: string option
    /// Its role in the method's data contract (features, weight, group, id,
    /// wave), when it names columns of the data.
    Role: string option
}

/// How a method is called.
type MethodSignature = {
    /// The tool the card becomes: lower-case letters, digits and `_`.
    ToolName: string
    /// The method's own arguments, after the slice arguments every method
    /// takes.
    Parameters: MethodParameter list
    /// The refusals the evaluator may answer beyond the preconditions, each
    /// repairable by name.
    Refusals: MethodCondition list
}

/// One column of a method's result table.
type MethodColumn = {
    /// The column's name.
    Name: string
    /// What it holds.
    Meaning: string
}

/// One result table of a method.
type MethodTable = {
    /// The table's name.
    Name: string
    /// What it holds.
    Meaning: string
    /// Its columns.
    Columns: MethodColumn list
}

/// A diagnostic a method reports, and the range that is healthy.
type MethodDiagnostic = {
    /// The diagnostic's name.
    Name: string
    /// What it measures.
    Meaning: string
    /// The range that reads as healthy, and what outside it means.
    Healthy: string
}

/// What a method answers.
type MethodOutputs = {
    /// The result tables.
    Tables: MethodTable list
    /// The diagnostics, with their healthy ranges.
    Diagnostics: MethodDiagnostic list
    /// The warnings the method may raise.
    Warnings: string list
    /// The registered derived metrics the method asserts facts of. Empty: the
    /// result is a block only, and the tool declares no write.
    Facts: string list
}

/// How a method's result is read.
type MethodInterpretation = {
    /// The rules for reading the outputs.
    Rules: string list
    /// What may be said of a result.
    Say: string list
    /// What must not be claimed.
    NeverSay: string list
}

/// How a method's result is shown by default.
type MethodPresentation = {
    /// The default blocks (chart kinds, tables).
    Blocks: string list
    /// The base card the result carries, when the method declares one.
    BaseCard: string option
}

/// A worked call and how its result is read.
type MethodExample = {
    /// What the example shows.
    Title: string
    /// The call's arguments, as JSON text.
    Call: string
    /// The result, as the reader sees it.
    Result: string
    /// How the result is read.
    Explanation: string
}

/// A method card: the eight parts.
type MethodCard = {
    /// Which method, and the definition it binds to.
    Identity: MethodIdentity
    /// What it does.
    Meaning: MethodMeaning
    /// When it applies.
    Applicability: MethodApplicability
    /// How it is called.
    Signature: MethodSignature
    /// What it answers.
    Outputs: MethodOutputs
    /// How the result is read.
    Interpretation: MethodInterpretation
    /// How the result is shown by default.
    Presentation: MethodPresentation
    /// Worked calls: on demand through `describe_method`, never in the
    /// prompt.
    Examples: MethodExample list
}

/// What a method tool answers when a call is refused, read the same way as
/// every spec tool's refusal.
[<RequireQualifiedAccess>]
type MethodRefusal =
    /// The spec-tool door refused the call: its arguments, the slice read, the
    /// cap or the assertion.
    | Door of SpecDoorRefusal
    /// The call omits an argument the method requires.
    | MissingArgument of methodId: string * argument: string
    /// An argument is not of the type the method's signature declares.
    | ArgumentMistyped of methodId: string * argument: string * expected: string
    /// An argument names a value outside the closed set the signature admits.
    | ArgumentNotAdmitted of methodId: string * argument: string * admissible: string list
    /// A condition the card declares is not met: a precondition, or a refusal
    /// of the method's evaluator. Named by the condition.
    | Unmet of methodId: string * condition: MethodCondition
    /// The evaluator refused with a name the card does not declare.
    | Undeclared of methodId: string * name: string

    interface ISpecRefusal with
        member this.Name =
            match this with
            | MethodRefusal.Door door -> (door :> ISpecRefusal).Name
            | MethodRefusal.MissingArgument _ -> "MissingArgument"
            | MethodRefusal.ArgumentMistyped _ -> "ArgumentMistyped"
            | MethodRefusal.ArgumentNotAdmitted _ -> "ArgumentNotAdmitted"
            | MethodRefusal.Unmet(_, condition) -> condition.Name
            | MethodRefusal.Undeclared _ -> "UndeclaredRefusal"

        member this.Remedy =
            match this with
            | MethodRefusal.Door door -> (door :> ISpecRefusal).Remedy
            | MethodRefusal.MissingArgument(methodId, argument) ->
                sprintf "method '%s' requires the argument '%s'; add it" methodId argument
            | MethodRefusal.ArgumentMistyped(methodId, argument, expected) ->
                sprintf "method '%s' reads the argument '%s' as %s; write it so" methodId argument expected
            | MethodRefusal.ArgumentNotAdmitted(methodId, argument, admissible) ->
                sprintf
                    "method '%s' admits the argument '%s' as one of %s; write one of them"
                    methodId
                    argument
                    (admissible |> List.map (sprintf "'%s'") |> String.concat ", ")
            | MethodRefusal.Unmet(methodId, condition) ->
                sprintf "method '%s' requires that %s; %s" methodId condition.Statement condition.Remedy
            | MethodRefusal.Undeclared(methodId, name) ->
                sprintf
                    "method '%s' refused the call as '%s', which its card does not declare; read the method with describe_method and call it within its preconditions"
                    methodId
                    name

        member this.Admissible =
            match this with
            | MethodRefusal.Door door -> (door :> ISpecRefusal).Admissible
            | MethodRefusal.ArgumentNotAdmitted(_, _, admissible) -> admissible
            | _ -> []