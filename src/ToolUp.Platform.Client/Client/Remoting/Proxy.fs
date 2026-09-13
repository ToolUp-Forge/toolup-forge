// SPDX-License-Identifier: MIT
// Copyright (c) Zaid Ajaj and Fable.Remoting contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Client

open Fable.Core.JsInterop
open Fable.SimpleJson
open ToolUp.Remoting

/// Phase 783 — a minimal, total JSON scanner, used only to lift the
/// `decodeError` detail out of an error envelope.
///
/// It exists because neither obvious tool works on both hosts.
/// `Fable.SimpleJson.parse` is Fable-RUNTIME-only — its parser is built
/// on `Fable.Parsimmon`, whose .NET assembly is binding stubs that throw
/// "You've hit dummy code used for Fable bindings" — so a client
/// classifier written on it cannot be exercised in the .NET test harness
/// at all, and `SimpleJson.parseNative` is `JSON.parse` and does not
/// exist off the browser. `System.Text.Json` is not Fable-compilable.
/// A regex (the shape `ScopeDenial` reached for, and for this same
/// reason) is host-agnostic but cannot read a value containing an escaped
/// quote — and one of the two fields read here is a serialiser's own
/// exception message, which is precisely the field most likely to carry
/// one.
///
/// So: one implementation, string-escape-aware, running identically under
/// Fable and under .NET, which is what makes the client half of this
/// phase testable rather than merely written.
module private DecodeErrorWire =

    /// Decode the JSON string literal whose opening quote is at `start`.
    /// Returns the value and the index just past the closing quote;
    /// `None` if `start` is not a quote or the literal is unterminated.
    let readString (source: string) (start: int) : (string * int) option =
        if start >= source.Length || source[start] <> '"' then
            None
        else
            let sb = System.Text.StringBuilder()
            let mutable i = start + 1
            let mutable closed = false

            while not closed && i < source.Length do
                let c = source[i]

                if c = '\\' && i + 1 < source.Length then
                    match source[i + 1] with
                    | 'n' -> sb.Append '\n' |> ignore
                    | 't' -> sb.Append '\t' |> ignore
                    | 'r' -> sb.Append '\r' |> ignore
                    | 'b' -> sb.Append '\b' |> ignore
                    | 'f' -> sb.Append '\f' |> ignore
                    | 'u' when i + 5 < source.Length ->
                        let mutable code = 0
                        let mutable valid = true

                        for k in 2..5 do
                            let d = source[i + k]

                            let digit =
                                if d >= '0' && d <= '9' then
                                    int d - int '0'
                                elif d >= 'a' && d <= 'f' then
                                    int d - int 'a' + 10
                                elif d >= 'A' && d <= 'F' then
                                    int d - int 'A' + 10
                                else
                                    valid <- false
                                    0

                            code <- code * 16 + digit

                        if valid then
                            sb.Append(char code) |> ignore
                    | other -> sb.Append other |> ignore

                    i <-
                        i
                        + (if source[i + 1] = 'u' && i + 5 < source.Length then
                               6
                           else
                               2)
                elif c = '"' then
                    closed <- true
                    i <- i + 1
                else
                    sb.Append c |> ignore
                    i <- i + 1

            if closed then Some(sb.ToString(), i) else None

    /// Index of the first character of the VALUE bound to `key`. Only
    /// matches an occurrence that is actually in key position (followed
    /// by a colon), so a key name appearing inside some other value does
    /// not divert the scan.
    let valueIndexOf (source: string) (key: string) : int option =
        let needle = "\"" + key + "\""

        let rec search (from: int) =
            let idx = source.IndexOf(needle, from)

            if idx < 0 then
                None
            else
                let mutable k = idx + needle.Length

                while k < source.Length && System.Char.IsWhiteSpace source[k] do
                    k <- k + 1

                if k < source.Length && source[k] = ':' then
                    let mutable v = k + 1

                    while v < source.Length && System.Char.IsWhiteSpace source[v] do
                        v <- v + 1

                    Some v
                else
                    search (idx + needle.Length)

        search 0

    /// The complete JSON object whose opening brace is at `start`, braces
    /// included. Depth-counted and string-aware, so a brace inside a
    /// string value does not close the object early.
    let objectAt (source: string) (start: int) : string option =
        if start >= source.Length || source[start] <> '{' then
            None
        else
            let mutable depth = 0
            let mutable i = start
            let mutable inString = false
            let mutable result = None

            while result.IsNone && i < source.Length do
                let c = source[i]

                if inString then
                    if c = '\\' then
                        i <- i + 1
                    elif c = '"' then
                        inString <- false
                elif c = '"' then
                    inString <- true
                elif c = '{' then
                    depth <- depth + 1
                elif c = '}' then
                    depth <- depth - 1

                    if depth = 0 then
                        result <- Some(source.Substring(start, i - start + 1))

                i <- i + 1

            result

    /// The array of JSON strings whose opening bracket is at `start`.
    /// Stops at the first element that is not a string — the path is
    /// always a string array, and a partial read is preferable to a throw.
    let readStringArray (source: string) (start: int) : string list =
        if start >= source.Length || source[start] <> '[' then
            []
        else
            let items = ResizeArray<string>()
            let mutable i = start + 1
            let mutable go = true

            while go && i < source.Length do
                while i < source.Length && (System.Char.IsWhiteSpace source[i] || source[i] = ',') do
                    i <- i + 1

                match readString source i with
                | Some(value, next) ->
                    items.Add value
                    i <- next
                | None -> go <- false

            List.ofSeq items

module Proxy =
    /// Phase 783 — recover the server's decode refusal from an error
    /// response body, so `ProxyRequestException.DecodeError` is populated
    /// without any caller parsing message text.
    ///
    /// The shape is the Phase 69e categorised envelope carrying the Phase
    /// 783 detail: `{ error: { methodName, decodeError: { expected,
    /// found, path }, message }, category: "validation", … }`. Anything
    /// else — a body that is not JSON, a `validation` envelope from the
    /// 69e attribute validators (which carries `violations`, not
    /// `decodeError`), a server that predates this phase — yields `None`,
    /// which is the correct answer rather than a degraded one: absence of
    /// the detail means the failure was not a decode refusal.
    ///
    /// Deliberately total. A client that threw while classifying a server
    /// error would replace a legible failure with an illegible one.
    let tryReadDecodeError (responseBody: string) : DecodeError option =
        if System.String.IsNullOrWhiteSpace responseBody then
            None
        else
            try
                DecodeErrorWire.valueIndexOf responseBody "decodeError"
                |> Option.bind (DecodeErrorWire.objectAt responseBody)
                |> Option.bind (fun detail ->
                    let field key =
                        DecodeErrorWire.valueIndexOf detail key
                        |> Option.bind (DecodeErrorWire.readString detail)
                        |> Option.map fst

                    match field "expected", field "found" with
                    | Some expected, Some found ->
                        let path =
                            DecodeErrorWire.valueIndexOf detail "path"
                            |> Option.map (DecodeErrorWire.readStringArray detail)
                            |> Option.defaultValue []

                        Some {
                            Path = path
                            Expected = expected
                            Found = found
                        }
                    | _ -> None)
            with _ ->
                None

    let combineRouteWithBaseUrl route (baseUrl: string option) =
        match baseUrl with
        | None -> route
        | Some url -> sprintf "%s%s" (url.TrimEnd('/')) route

    let isByteArray =
        function
        | TypeInfo.Array getElemType ->
            match getElemType () with
            | TypeInfo.Byte -> true
            | otherwise -> false
        | otherwise -> false

    let isAsyncOfByteArray =
        function
        | TypeInfo.Async getAsyncType ->
            match getAsyncType () with
            | TypeInfo.Array getElemType ->
                match getElemType () with
                | TypeInfo.Byte -> true
                | otherwise -> false
            | otherwise -> false
        | otherwise -> false

    let rec getReturnType typ =
        if Reflection.FSharpType.IsFunction typ then
            let _, res = Reflection.FSharpType.GetFunctionElements typ
            getReturnType res
        elif typ.IsGenericType then
            typ.GetGenericArguments() |> Array.head
        else
            typ

    let proxyFetch options typeName (func: RecordField) fieldType =
        let funcArgs: (TypeInfo[]) =
            match func.FieldType with
            | TypeInfo.Async inner -> [| func.FieldType |]
            | TypeInfo.Promise inner -> [| func.FieldType |]
            | TypeInfo.Func getArgs -> getArgs ()
            | _ -> failwithf "Field %s does not have a valid definiton" func.FieldName

        let argumentCount = (Array.length funcArgs) - 1
        let returnTypeAsync = Array.last funcArgs

        let isMultipart =
            match func.FieldType with
            | TypeInfo.Func getArgs -> options.IsMultipartEnabled && getArgs () |> Array.exists isByteArray
            | otherwise -> false

        let route = options.RouteBuilder typeName func.FieldName
        let url = combineRouteWithBaseUrl route options.BaseUrl

        let funcNeedParameters =
            match funcArgs with
            | [| TypeInfo.Async _ |] -> false
            | [| TypeInfo.Promise _ |] -> false
            | [| TypeInfo.Unit; TypeInfo.Async _ |] -> false
            | otherwise -> true

        let inputArgumentTypes = Array.take argumentCount funcArgs

        let headers = [
            // xhr will set content-type and boundary for multipart
            if not isMultipart then
                yield "Content-Type", "application/json; charset=utf-8"

            yield "x-remoting-proxy", "true"
            yield! options.CustomHeaders
            match options.Authorization with
            | Some authToken -> yield "Authorization", authToken
            | None -> ()
        ]

        let executeRequest =
            if options.CustomResponseSerialization.IsSome || isAsyncOfByteArray returnTypeAsync then
                let onOk =
                    match options.CustomResponseSerialization with
                    | Some serializer ->
                        let returnType = getReturnType fieldType
                        fun response -> serializer response returnType
                    | _ -> box

                fun requestBody -> async {
                    // read as arraybuffer and deserialize
                    let! (response, statusCode) =
                        if funcNeedParameters then
                            Http.post url
                            |> Http.withBody requestBody
                            |> Http.withHeaders headers
                            |> Http.withCredentials options.WithCredentials
                            |> Http.sendAndReadBinary
                        else
                            Http.get url
                            |> Http.withHeaders headers
                            |> Http.withCredentials options.WithCredentials
                            |> Http.sendAndReadBinary

                    match statusCode with
                    | 200 ->
                        // Phase 783 — this is the binary-response path, so
                        // `onOk` runs the MsgPack reader. A malformed
                        // reply now refuses with a `DecodeError` instead
                        // of a `failwithf`, and it is reported through the
                        // SAME record the server's refusals arrive in: the
                        // caller asks "did this fail to decode?" once,
                        // regardless of which side failed to decode.
                        try
                            return onOk response
                        with DecodeException error ->
                            let failed = { StatusCode = 200; ResponseBody = "" }

                            return!
                                raise (
                                    ProxyRequestException(
                                        failed,
                                        sprintf
                                            "The server's response to %s did not decode: %s"
                                            url
                                            (DecodeError.render error),
                                        "",
                                        Some error
                                    )
                                )
                    | n ->
                        let responseAsBlob =
                            InternalUtilities.createBlobWithMimeType !^response "text/plain"

                        let! responseText = InternalUtilities.readBlobAsText responseAsBlob

                        let response = {
                            StatusCode = statusCode
                            ResponseBody = responseText
                        }

                        let errorMsg =
                            if n = 500 then
                                sprintf "Internal server error (500) while making request to %s" url
                            else
                                sprintf "Http error (%d) while making request to %s" n url

                        return!
                            raise (
                                ProxyRequestException(
                                    response,
                                    errorMsg,
                                    response.ResponseBody,
                                    tryReadDecodeError response.ResponseBody
                                )
                            )
                }
            else
                let returnType =
                    match returnTypeAsync with
                    | TypeInfo.Async getAsyncTypeArgument -> getAsyncTypeArgument ()
                    | TypeInfo.Promise getPromiseTypeArgument -> getPromiseTypeArgument ()
                    | TypeInfo.Any getReturnType ->
                        let t = getReturnType ()

                        if t.FullName.StartsWith "System.Threading.Tasks.Task`1" then
                            t.GetGenericArguments().[0] |> createTypeInfo
                        else
                            failwithf "Expected field %s to have a return type of Async<'t> or Task<'t>" func.FieldName
                    | _ -> failwithf "Expected field %s to have a return type of Async<'t> or Task<'t>" func.FieldName

                fun requestBody -> async {
                    // make plain RPC request and let it go through the deserialization pipeline
                    let! response =
                        if funcNeedParameters then
                            Http.post url
                            |> Http.withBody requestBody
                            |> Http.withHeaders headers
                            |> Http.withCredentials options.WithCredentials
                            |> Http.send
                        else
                            Http.get url
                            |> Http.withHeaders headers
                            |> Http.withCredentials options.WithCredentials
                            |> Http.send

                    match response.StatusCode with
                    | 200 ->
                        let parsedJson = SimpleJson.parseNative response.ResponseBody
                        return Convert.fromJsonAs parsedJson returnType
                    | 500 ->
                        return!
                            raise (
                                ProxyRequestException(
                                    response,
                                    sprintf "Internal server error (500) while making request to %s" url,
                                    response.ResponseBody,
                                    // Phase 783 — a 500 should no longer
                                    // be able to carry one (a decode
                                    // refusal is a 400 now), but the
                                    // recovery is cheap and total, and a
                                    // consumer pinned to an older server
                                    // still reads whatever arrives.
                                    tryReadDecodeError response.ResponseBody
                                )
                            )
                    | n ->
                        return!
                            raise (
                                ProxyRequestException(
                                    response,
                                    sprintf "Http error (%d) from server occured while making request to %s" n url,
                                    response.ResponseBody,
                                    // Phase 783 — the decode-refusal path:
                                    // the server answers 400 + a
                                    // `validation` envelope carrying the
                                    // structured refusal.
                                    tryReadDecodeError response.ResponseBody
                                )
                            )
                }

        fun arg0 arg1 arg2 arg3 arg4 arg5 arg6 arg7 ->
            let inputArguments =
                if funcNeedParameters then
                    Array.take argumentCount [|
                        box arg0
                        box arg1
                        box arg2
                        box arg3
                        box arg4
                        box arg5
                        box arg6
                        box arg7
                    |]
                else
                    [||]

            let requestBody =
                if isMultipart then
                    inputArguments
                    |> Array.mapi (fun i x ->
                        let typ = inputArgumentTypes.[i]

                        // in theory the input byte array could be untyped, so it's better to check the expected type
                        // than `instanceof Uint8Array` on the actual value
                        if isByteArray typ then
                            InternalUtilities.createBlobWithMimeType (x :?> _) "application/octet-stream"
                        else
                            let json = Convert.serialize x typ
                            InternalUtilities.createBlobWithMimeType !^json "application/json")
                    |> RequestBody.Multipart
                else
                    match inputArgumentTypes.Length with
                    | 1 when not (Convert.arrayLike inputArgumentTypes.[0]) ->
                        let typeInfo = TypeInfo.Tuple(fun _ -> inputArgumentTypes)

                        let requestBodyJson =
                            inputArguments
                            |> Array.tryHead
                            |> Option.map (fun arg -> Convert.serialize arg typeInfo)
                            |> Option.defaultValue "{}"

                        RequestBody.Json requestBodyJson
                    | 1 ->
                        // for array-like types, use an explicit array surranding the input array argument
                        let requestBodyJson =
                            Convert.serialize [| inputArguments.[0] |] (TypeInfo.Array(fun _ -> inputArgumentTypes.[0]))

                        RequestBody.Json requestBodyJson
                    | n ->
                        let typeInfo = TypeInfo.Tuple(fun _ -> inputArgumentTypes)
                        let requestBodyJson = Convert.serialize inputArguments typeInfo
                        RequestBody.Json requestBodyJson

            executeRequest requestBody