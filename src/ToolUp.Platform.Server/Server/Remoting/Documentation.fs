namespace ToolUp.Remoting.Server

open Microsoft.FSharp.Quotations
open System
open System.Text.Json
open System.Text.Json.Nodes
open FSharp.Reflection
open ToolUp.Remoting.Json.SystemTextJson

/// Helper class that constructs documented routes
type ApiDocs<'t>() =
    /// Document a route
    member this.route<'u>(expr: Expr<'t -> Async<'u>>) =
        match expr with
        | Patterns.ProxyLambda(name, []) -> {
            Route = Some name
            Alias = None
            Description = None
            Examples = []
          }
        | _ -> {
            Route = None
            Alias = None
            Description = None
            Examples = []
          }

    /// Document a route
    member this.route<'v, 'u>(expr: Expr<'t -> ('v -> Async<'u>)>) =
        match expr with
        | Patterns.ProxyLambda(name, []) -> {
            Route = Some name
            Alias = None
            Description = None
            Examples = []
          }
        | _ -> {
            Route = None
            Alias = None
            Description = None
            Examples = []
          }

    /// Adds a description to the route definition
    member this.description (desc: string) (route: RouteDocs) = { route with Description = Some desc }

    /// Adds example to the route definition form the way you would use the remote function
    member this.example (expr: Expr<'t -> Async<'u>>) (route: RouteDocs) =
        match expr with
        | Patterns.ProxyLambda(name, args) when Some name = route.Route -> {
            route with
                Examples = List.append route.Examples [ (args, "") ]
          }
        | _ -> route

    /// Add human-friendly alias for the remote function name
    member this.alias (name: string) (route: RouteDocs) = { route with Alias = Some name }

module Docs =

    let createFor<'t> () = ApiDocs<'t>()

    /// Pre-configured FableConverters options for argument serialisation.
    /// Reused across calls — STJ caches reflection state on the options
    /// instance, so a fresh instance per call would re-pay the cost.
    let private serializeOptions = FableConverters.create ()

    let serialize result =
        JsonSerializer.Serialize(result, serializeOptions)

    let routeMethod fieldType =
        match TypeInfo.flattenFuncTypes fieldType with
        | [| simpleAsyncValue |] when simpleAsyncValue.FullName.StartsWith("Microsoft.FSharp.Control.FSharpAsync`1") ->
            "GET"
        | [| input; _ |] when input = typeof<unit> -> "GET"
        | _ -> "POST"

    /// Phase 69j.D — the deploy-time schema export, with the per-method
    /// supported wire-schema version vector.
    ///
    /// This is the surface that already enumerates an API record's methods
    /// for an operator or an external consumer (served at
    /// `<docsUrl>/$schema`), so the version vector is published as an
    /// additive `schemaVersions` array on each route rather than through a
    /// second export surface built for one field. A client reading only the
    /// pre-69j keys is unaffected.
    ///
    /// A versioned method is published ONCE, under its logical name — the
    /// name a caller addresses — with every version it serves. Its
    /// `_V<n>` handler fields do not appear as routes of their own, because
    /// they are not separately addressable once negotiation is in front of
    /// them: addressing `GetThing_V2` directly would bypass the very
    /// negotiation the vector is published to describe. `route` and
    /// `httpMethod` are taken from the HIGHEST supported version's handler,
    /// which is what an unpinned caller gets.
    ///
    /// `makeDocsSchema` keeps its arity and delegates here with the
    /// default version, mirroring the `Errors.categorised` /
    /// `categorisedWithSchema` pair: adding a parameter to a shipped public
    /// function is a source break for every caller, and this one has
    /// callers in both adapters.
    let makeDocsSchemaWithSchemaVersions
        (recordType: Type)
        (Documentation(docsName, routesDefs))
        (routeBuilder: string -> string -> string)
        (serverSchemaVersion: int)
        =
        let schema = JsonObject()
        let routes = JsonArray()

        let versionTable = SchemaVersion.classify recordType

        // field name → (logical name, the versions the logical method
        // serves). Present only for fields that participate in versioning.
        let versionedFields =
            versionTable
            |> Map.toList
            |> List.collect (fun (logical, methodSchema) ->
                let versions = SchemaVersion.supportedVersions serverSchemaVersion methodSchema

                methodSchema.Handlers
                |> Map.toList
                |> List.map (fun (version, field) -> field, (logical, versions, version)))
            |> Map.ofList

        // The one handler that represents each logical method in the export:
        // the highest supported version's, since that is what a caller who
        // sends no header is served.
        let representativeFields =
            versionTable
            |> Map.toList
            |> List.choose (fun (_, methodSchema) ->
                if Map.isEmpty methodSchema.Handlers then
                    None
                else
                    methodSchema.Handlers |> Map.toList |> List.maxBy fst |> snd |> Some)
            |> Set.ofList

        // A versioned handler that is not the representative one is folded
        // into its logical method's entry, not published beside it.
        //
        // NonPublic flags, matching every dispatch classifier: an internal
        // or private API record is a record too, and the adapter explicitly
        // supports composing one (see the `isRecordImpl` note in
        // `GiraffeAdapter.buildDispatcherTable`). Without the flags
        // `GetRecordFields` THROWS on such a record rather than returning
        // nothing, so the pre-69j `makeDocsSchema` could not render a docs
        // schema for a composition the dispatcher serves happily. Widened
        // here because 69j.D lands on this surface and a docs export that
        // throws on a supported composition is not a version vector anyone
        // can read.
        let publishable =
            FSharpType.GetRecordFields(recordType, SchemaVersion.reflectionFlags)
            |> Array.filter (fun fieldInfo ->
                match Map.tryFind fieldInfo.Name versionedFields with
                | Some _ -> representativeFields.Contains fieldInfo.Name
                | None -> true)

        for fieldInfo in publishable do
            let logicalName, supported =
                match Map.tryFind fieldInfo.Name versionedFields with
                | Some(logical, versions, _) -> logical, versions
                | None -> fieldInfo.Name, [ serverSchemaVersion ]

            let routeDocs =
                List.tryFind (fun routeDocs -> routeDocs.Route = Some logicalName) routesDefs

            let route = JsonObject()
            route["remoteFunction"] <- JsonValue.Create(logicalName)
            route["httpMethod"] <- JsonValue.Create(routeMethod fieldInfo.PropertyType)
            route["route"] <- JsonValue.Create(routeBuilder recordType.Name logicalName)

            let schemaVersionsJson = JsonArray()

            for version in supported do
                schemaVersionsJson.Add(JsonValue.Create version)

            route["schemaVersions"] <- schemaVersionsJson

            let description = routeDocs |> Option.bind _.Description |> Option.defaultValue ""

            let alias = routeDocs |> Option.bind _.Alias |> Option.defaultValue logicalName

            route["description"] <- JsonValue.Create(description)
            route["alias"] <- JsonValue.Create(alias)

            let examplesJson = JsonArray()

            match routeDocs with
            | None -> ()
            | Some routeDocs ->
                for (exampleArgs, description) in routeDocs.Examples do
                    let argsJson = JsonArray()

                    for arg in exampleArgs do
                        let argText = serialize arg
                        argsJson.Add(JsonNode.Parse argText)

                    let exampleJson = JsonObject()
                    exampleJson["description"] <- JsonValue.Create(description)
                    exampleJson["arguments"] <- argsJson
                    examplesJson.Add(exampleJson)

            route["examples"] <- examplesJson
            routes.Add(route)

        schema["name"] <- JsonValue.Create(docsName)
        schema["routes"] <- routes
        schema

    /// The pre-69j arity, preserved for every existing caller. Delegates
    /// with the default wire-schema version 1, so an API record that never
    /// opted into versioning renders exactly as before plus the additive
    /// `schemaVersions: [1]` key. A host that composed a non-default
    /// `RemotingOptions.SchemaVersion` calls
    /// `makeDocsSchemaWithSchemaVersions` and passes it — which both
    /// adapters do.
    let makeDocsSchema (recordType: Type) (docs: Documentation) (routeBuilder: string -> string -> string) =
        makeDocsSchemaWithSchemaVersions recordType docs routeBuilder 1