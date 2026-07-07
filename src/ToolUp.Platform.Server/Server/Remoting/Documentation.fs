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

    let makeDocsSchema
        (recordType: Type)
        (Documentation(docsName, routesDefs))
        (routeBuilder: string -> string -> string)
        =
        let schema = JsonObject()
        let routes = JsonArray()

        for fieldInfo in FSharpType.GetRecordFields recordType do
            let routeDocs =
                List.tryFind (fun routeDocs -> routeDocs.Route = Some fieldInfo.Name) routesDefs

            let route = JsonObject()
            route["remoteFunction"] <- JsonValue.Create(fieldInfo.Name)
            route["httpMethod"] <- JsonValue.Create(routeMethod fieldInfo.PropertyType)
            route["route"] <- JsonValue.Create(routeBuilder recordType.Name fieldInfo.Name)

            let description = routeDocs |> Option.bind _.Description |> Option.defaultValue ""

            let alias = routeDocs |> Option.bind _.Alias |> Option.defaultValue fieldInfo.Name

            route["description"] <- JsonValue.Create(description)
            route["alias"] <- JsonValue.Create(alias)

            let examplesJson = JsonArray()

            match routeDocs with
            | None -> ()
            | Some routeDocs ->
                for (exampleArgs, description) in routeDocs.Examples do
                    let argsJson = JsonArray()

                    for arg in exampleArgs do
                        // Round-trip each arg through STJ to land it as a
                        // JsonNode for embedding (matches the prior JToken.Parse
                        // pattern). Each arg's `serialize` produces the
                        // FableConverters wire shape; JsonNode.Parse reads
                        // that JSON into a JsonNode the tree can absorb.
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