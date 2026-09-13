// SPDX-License-Identifier: MIT
// Copyright (c) Zaid Ajaj and Fable.Remoting contributors
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Remoting.Client

open System

type HttpMethod =
    | GET
    | POST

type RequestBody =
    | Empty
    | Json of string
    | Multipart of Browser.Types.Blob[]

type CustomResponseSerializer = byte[] -> Type -> obj

type HttpRequest = {
    HttpMethod: HttpMethod
    Url: string
    Headers: (string * string) list
    RequestBody: RequestBody
    WithCredentials: bool
}

type HttpResponse = {
    StatusCode: int
    ResponseBody: string
}

type RemoteBuilderOptions = {
    CustomHeaders: (string * string) list
    BaseUrl: string option
    Authorization: string option
    WithCredentials: bool
    RouteBuilder: (string -> string -> string)
    CustomResponseSerialization: CustomResponseSerializer option
    IsMultipartEnabled: bool
}

type ProxyRequestException
    (response: HttpResponse, errorMsg, reponseText: string, decodeError: ToolUp.Remoting.DecodeError option) =
    inherit System.Exception(errorMsg)

    /// Phase 783 — the pre-783 three-argument shape, preserved so every
    /// existing construction site compiles unchanged (GP 11) and the
    /// public-API baseline records an ADDITION rather than a retype. An
    /// optional parameter (`?decodeError`) would have folded the two into
    /// one widened constructor and read as a REMOVAL of the three-argument
    /// token — see the approval-gate rules in CLAUDE.md.
    new(response, errorMsg, reponseText) = ProxyRequestException(response, errorMsg, reponseText, None)

    member this.Response = response
    member this.StatusCode = response.StatusCode
    member this.ResponseText = reponseText

    /// Phase 783 — `Some` when this failure is a DECODE refusal rather
    /// than a handler fault: either the server refused to decode the
    /// request arguments (a `validation`-category envelope carrying a
    /// `decodeError` detail), or this client could not decode the
    /// server's own reply. An Elmish `update` branches on it directly
    /// instead of matching on message text — which is the whole point:
    /// message text is not a contract, and the pre-783 shape gave a
    /// caller nothing else to go on.
    ///
    /// `None` for every other failure, including a `validation` envelope
    /// raised by the Phase 69e attribute validators — those are a
    /// *decoded* value failing a rule, which is a different thing from a
    /// value that never decoded, and the two must stay distinguishable.
    member this.DecodeError = decodeError