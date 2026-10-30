// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System
open System.Globalization
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes

/// What a fact tool is handed for one call: the composed fact store and
/// disclosure gate, the metric registry when one is composed, and the
/// request's resolved scope and caller.
type FactToolContext = {
    Store: IFactStore
    Gate: IFactDisclosureGate
    Registry: Grounding.IMetricRegistry option
    Scope: ResolvedScope
    UserId: string
}

/// The shell every fact tool shares: argument reading, the service and
/// caller resolution, and the serialised answer. A tool is its query; this
/// is everything around it.
[<RequireQualifiedAccess>]
module FactTool =

    /// A tool result as the wire carries it.
    let internal serialize (value: obj) : string =
        JsonSerializer.Serialize(value, FableConverters.shared)

    /// Parse the argument document, which must be a JSON object.
    let internal parseObject (argsJson: string) : Result<JsonElement, string> =
        let parsed =
            try
                Ok((JsonDocument.Parse argsJson).RootElement.Clone())
            with _ ->
                Error "Arguments are not valid JSON."

        match parsed with
        | Error e -> Error e
        | Ok root when root.ValueKind <> JsonValueKind.Object -> Error "Arguments must be a JSON object."
        | Ok root -> Ok root

    /// A present, non-`null` member.
    let internal tryProperty (root: JsonElement) (name: string) : JsonElement option =
        match root.TryGetProperty name with
        | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
        | _ -> None

    /// An optional string argument; `Error` when present with a non-string
    /// shape.
    let internal stringArg (root: JsonElement) (name: string) : Result<string option, string> =
        match tryProperty root name with
        | None -> Ok None
        | Some v when v.ValueKind = JsonValueKind.String -> Ok(Some(v.GetString()))
        | Some _ -> Error(sprintf "Argument '%s' must be a string." name)

    /// A required, non-blank string argument. `hint` tells the model where
    /// the value comes from.
    let internal requiredArg (hint: string) (root: JsonElement) (name: string) : Result<string, string> =
        match stringArg root name with
        | Ok(Some s) when not (String.IsNullOrWhiteSpace s) -> Ok s
        | Ok _ -> Error(sprintf "Required argument '%s' is missing. %s" name hint)
        | Error e -> Error e

    /// An optional boolean argument.
    let internal boolArg (root: JsonElement) (name: string) : Result<bool option, string> =
        match tryProperty root name with
        | None -> Ok None
        | Some v when v.ValueKind = JsonValueKind.True -> Ok(Some true)
        | Some v when v.ValueKind = JsonValueKind.False -> Ok(Some false)
        | Some _ -> Error(sprintf "Argument '%s' must be a boolean." name)

    /// An optional decimal argument.
    let internal numberArg (root: JsonElement) (name: string) : Result<decimal option, string> =
        match tryProperty root name with
        | None -> Ok None
        | Some v when v.ValueKind = JsonValueKind.Number ->
            match v.TryGetDecimal() with
            | true, d -> Ok(Some d)
            | _ -> Error(sprintf "Argument '%s' is not a representable number." name)
        | Some _ -> Error(sprintf "Argument '%s' must be a number." name)

    /// A whole-number argument. A fractional or out-of-range value is
    /// refused rather than truncated: silently reading 2.7 as 2 answers a
    /// question the caller did not ask.
    let internal intArg (root: JsonElement) (name: string) : Result<int option, string> =
        match numberArg root name with
        | Error e -> Error e
        | Ok None -> Ok None
        | Ok(Some d) when Decimal.Truncate d <> d -> Error(sprintf "Argument '%s' must be a whole number." name)
        | Ok(Some d) when d < -1000000m || d > 1000000m ->
            Error(sprintf "Argument '%s' is out of range (expected a whole number within ±1,000,000)." name)
        | Ok(Some d) -> Ok(Some(int d))

    /// An optional ISO-8601 instant, read as UTC; blank is absent.
    let internal instantArg (root: JsonElement) (name: string) : Result<DateTime option, string> =
        match stringArg root name with
        | Error e -> Error e
        | Ok None -> Ok None
        | Ok(Some s) when String.IsNullOrWhiteSpace s -> Ok None
        | Ok(Some s) ->
            match
                DateTime.TryParse(
                    s,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                )
            with
            | true, t -> Ok(Some t)
            | false, _ -> Error(sprintf "Argument '%s' is not a parseable ISO-8601 instant: '%s'." name s)

    /// A closed enum argument — an unrecognised value is refused with the
    /// accepted set enumerated, never silently defaulted.
    let internal enumArg
        (root: JsonElement)
        (name: string)
        (cases: (string * 'a) list)
        (fallback: 'a)
        : Result<'a, string> =
        match stringArg root name with
        | Error e -> Error e
        | Ok None -> Ok fallback
        | Ok(Some s) when String.IsNullOrWhiteSpace s -> Ok fallback
        | Ok(Some s) ->
            match
                cases
                |> List.tryFind (fun (caseId, _) -> String.Equals(caseId, s.Trim(), StringComparison.OrdinalIgnoreCase))
            with
            | Some(_, value) -> Ok value
            | None ->
                Error(
                    sprintf
                        "Argument '%s' must be one of: %s (got '%s')."
                        name
                        (cases |> List.map fst |> String.concat ", ")
                        s
                )

    // The scope is not read from the request items. `ScopeResolution.forRequest`
    // returns the `ResolvedScope` the scope-resolution middleware minted for
    // this request or the explicit anonymous scope, and nothing a caller can
    // fabricate: an unresolved request reads the anonymous shard, which holds
    // only what was asserted anonymously.

    let private userIdOf (ctx: HttpContext) : string =
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> id
        | _ -> "anonymous"

    /// The registered `HttpContext` executor for the fact tool `name`.
    /// Resolves the fact store, the disclosure gate and the optional metric
    /// registry from DI — registered by the same `FactsCompose.withFactStore`
    /// knob that declares the tool, so a composed deployment always finds
    /// them — and hands them to `query` with the request's scope and caller.
    /// Without a store or a gate the answer is the tool's named refusal.
    let define
        (name: string)
        (query: FactToolContext -> string -> Async<string>)
        : HttpContext -> string -> Async<string> =
        fun ctx argsJson ->
            let services = ctx.RequestServices

            match
                ServiceComposition.tryService<IFactStore> services,
                ServiceComposition.tryService<IFactDisclosureGate> services
            with
            | Some store, Some gate ->
                query
                    {
                        Store = store
                        Gate = gate
                        Registry = ServiceComposition.tryService<Grounding.IMetricRegistry> services
                        Scope = StorageScopeResolver.ScopeResolution.forRequest ctx
                        UserId = userIdOf ctx
                    }
                    argsJson
            | _ -> async {
                return
                    serialize {|
                        error = $"The fact store is not composed in this deployment — `{name}` is unavailable."
                    |}
              }