// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The one codec table and its posture, pinned per policy.
///
/// `Codecs.resolveJson` / `resolveJsonEncoder` / `resolveMsgPack` are the one
/// seam every host reads a wire type through. What they answer is the whole
/// contract: for a registered type a codec; for a type registered for one API
/// record, that codec on that record's traffic only (then the unscoped one);
/// and for a type with nothing registered, `None` (the reflective path) under
/// `ReflectionFallback` and a refusal NAMING the type under `VerifiedOnly`.
/// The forwarding registries (`RemotingDecoders`, `JsonDecoders`) write the
/// same one table. The server's argument seam and a `VerifiedOnly` mount are
/// pinned through the same answers.
module ToolUp.Platform.Tests.Remoting.CodecsTests

open System
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open ToolUp.Remoting
open ToolUp.Remoting.Json
open ToolUp.Remoting.Server
open ToolUp.Remoting.Giraffe

/// A type registered unscoped on every leg.
type CodecsProbeRegistered = { Label: string }

/// A type registered for one API record only.
type CodecsProbeScoped = { Code: int }

/// A type nothing registers.
type CodecsProbeUnregistered = { Note: string }

/// An API record whose argument and answer are the unregistered type.
type CodecsProbeApi = {
    Echo: CodecsProbeUnregistered -> Async<CodecsProbeUnregistered>
}

let private registeredDecoder: JsonDecoder<CodecsProbeRegistered> =
    JsonDecode.succeed (fun label -> { Label = label })
    |> JsonDecode.apply (JsonDecode.field "Label" JsonDecode.asString)

let private registeredEncoder: JsonEncoder<CodecsProbeRegistered> =
    fun value -> JsonEncode.record [ "Label", JsonEncode.string value.Label ]

let private registeredMsgPack: Decoder<CodecsProbeRegistered> =
    Decode.succeed (fun label -> { Label = label })
    |> Decode.apply (Decode.field "Label" 0 Decode.asString)

let private scopedDecoder: JsonDecoder<CodecsProbeScoped> =
    JsonDecode.succeed (fun code -> { Code = code })
    |> JsonDecode.apply (JsonDecode.field "Code" JsonDecode.asInt32)

let private register () =
    Codecs.register<CodecsProbeRegistered> Scope.Unscoped {
        JsonEncoder = Some registeredEncoder
        JsonDecoder = Some registeredDecoder
        MsgPack = Some registeredMsgPack
    }

    Codecs.register<CodecsProbeScoped> (Scope.ForRecord "IScopedProbeApi") (Codec.jsonDecoder scopedDecoder)

let private policies = [ DecodePolicy.ReflectionFallback; DecodePolicy.VerifiedOnly ]

let private isOk =
    function
    | Some(Ok _) -> true
    | _ -> false

/// The refusal a `VerifiedOnly` miss answers with, or a failure naming what
/// was answered instead.
let private refusalOf (answer: Result<'a, DecodeError> option) : DecodeError =
    match answer with
    | Some(Error refusal) -> refusal
    | Some(Ok _) -> failtest "a registered codec was answered; a refusal was expected"
    | None -> failtest "None (the reflective path) was answered; a refusal was expected"

[<Tests>]
let tests =
    testList "Codecs — one table, one seam, one posture" [

        testCase "a registered type resolves on every leg under every policy"
        <| fun () ->
            register ()

            for policy in policies do
                for scope in [ Scope.Unscoped; Scope.ForRecord "AnyApi" ] do
                    let t = typeof<CodecsProbeRegistered>

                    Expect.isTrue
                        (isOk (Codecs.resolveJson policy scope t))
                        (sprintf "JSON decoder, %A %A" policy scope)

                    Expect.isTrue
                        (isOk (Codecs.resolveJsonEncoder policy scope t))
                        (sprintf "JSON encoder, %A %A" policy scope)

                    Expect.isTrue
                        (isOk (Codecs.resolveMsgPack policy scope t))
                        (sprintf "MessagePack, %A %A" policy scope)

        testCase "a record-scoped type resolves for its record only; elsewhere it is a miss under the policy"
        <| fun () ->
            register ()
            let t = typeof<CodecsProbeScoped>

            for policy in policies do
                Expect.isTrue
                    (isOk (Codecs.resolveJson policy (Scope.ForRecord "IScopedProbeApi") t))
                    (sprintf "its own record, %A" policy)

            Expect.isNone
                (Codecs.resolveJson DecodePolicy.ReflectionFallback (Scope.ForRecord "IOtherApi") t)
                "another record's traffic takes the reflective path"

            Expect.isNone
                (Codecs.resolveJson DecodePolicy.ReflectionFallback Scope.Unscoped t)
                "so does a caller with no record to name"

            let refusal =
                refusalOf (Codecs.resolveJson DecodePolicy.VerifiedOnly (Scope.ForRecord "IOtherApi") t)

            Expect.stringContains refusal.Expected (Codecs.keyFor t) "VerifiedOnly names the type"

            // The rule is per leg: a scoped registration carrying only a
            // decoder leaves the encoder lookup to the unscoped table.
            Expect.isNone
                (Codecs.resolveJsonEncoder DecodePolicy.ReflectionFallback (Scope.ForRecord "IScopedProbeApi") t)
                "no encoder is registered for the scoped type at any scope"

        testCase "an unregistered type is None under ReflectionFallback and a named refusal under VerifiedOnly"
        <| fun () ->
            let t = typeof<CodecsProbeUnregistered>

            for scope in [ Scope.Unscoped; Scope.ForRecord "AnyApi" ] do
                Expect.isNone (Codecs.resolveJson DecodePolicy.ReflectionFallback scope t) "JSON decoder: reflective"

                Expect.isNone
                    (Codecs.resolveJsonEncoder DecodePolicy.ReflectionFallback scope t)
                    "JSON encoder: reflective"

                Expect.isNone (Codecs.resolveMsgPack DecodePolicy.ReflectionFallback scope t) "MessagePack: reflective"

                let erase (answer: Result<'a, DecodeError> option) =
                    answer |> Option.map (Result.map ignore)

                let json = erase (Codecs.resolveJson DecodePolicy.VerifiedOnly scope t)
                let encoder = erase (Codecs.resolveJsonEncoder DecodePolicy.VerifiedOnly scope t)
                let msgPack = erase (Codecs.resolveMsgPack DecodePolicy.VerifiedOnly scope t)

                let answers = [
                    (json, "JSON decoder")
                    (encoder, "JSON encoder")
                    (msgPack, "MessagePack decoder")
                ]

                for answer, leg in answers do
                    let refusal = refusalOf answer

                    Expect.stringContains
                        refusal.Expected
                        (Codecs.keyFor t)
                        (sprintf "%s: the refusal names the type" leg)

                    Expect.stringContains refusal.Expected leg (sprintf "%s: and the leg" leg)
                    Expect.stringContains refusal.Found "VerifiedOnly" (sprintf "%s: and the posture" leg)

        testCase "the forwarding registries write and read the one table"
        <| fun () ->
            Codecs.resetForTests ()
            RemotingDecoders.register<CodecsProbeRegistered> registeredMsgPack
            JsonDecoders.registerFor<CodecsProbeScoped> "IScopedProbeApi" scopedDecoder

            Expect.isTrue
                (isOk (Codecs.resolveMsgPack DecodePolicy.VerifiedOnly Scope.Unscoped typeof<CodecsProbeRegistered>))
                "RemotingDecoders.register lands in Codecs"

            Expect.isTrue
                (isOk (
                    Codecs.resolveJson
                        DecodePolicy.VerifiedOnly
                        (Scope.ForRecord "IScopedProbeApi")
                        typeof<CodecsProbeScoped>
                ))
                "JsonDecoders.registerFor lands in Codecs, scoped"

            Expect.equal
                (RemotingDecoders.registered ())
                [ Codecs.keyFor typeof<CodecsProbeRegistered> ]
                "the MessagePack census reads the MessagePack leg only"

            Expect.equal
                (JsonDecoders.registered ())
                [ Some "IScopedProbeApi", Codecs.keyFor typeof<CodecsProbeScoped> ]
                "the JSON census reads the JSON decoder leg only"

            // A leg registered later joins, never replaces, the others.
            Codecs.register<CodecsProbeRegistered> Scope.Unscoped (Codec.jsonEncoder registeredEncoder)

            Expect.isTrue
                (isOk (Codecs.resolveMsgPack DecodePolicy.VerifiedOnly Scope.Unscoped typeof<CodecsProbeRegistered>))
                "the MessagePack leg survives an encoder registration"

            // A forwarding reset drops its own leg only.
            JsonDecoders.resetForTests ()

            Expect.isEmpty (JsonDecoders.registered ()) "the JSON decoder leg is gone"

            Expect.isTrue
                (isOk (Codecs.resolveJsonEncoder DecodePolicy.VerifiedOnly Scope.Unscoped typeof<CodecsProbeRegistered>))
                "the encoder leg is not"

            Codecs.resetForTests ()
            ToolUp.Remoting.PlatformDecoders.registerAll ()
            PlatformJsonDecoders.registerAll ()
            PlatformJsonEncoders.registerAll ()

        testCase "the server's argument seam: STJ under ReflectionFallback, refused by name under VerifiedOnly"
        <| fun () ->
            use document = JsonDocument.Parse """{"Note":"hello"}"""
            let element = document.RootElement.Clone()
            let options = SystemTextJson.FableConverters.create ()

            match
                SystemTextJson.FableConverters.tryDeserialiseWith<CodecsProbeUnregistered>
                    DecodePolicy.ReflectionFallback
                    (Some "CodecsProbeApi")
                    element
                    options
            with
            | Ok value -> Expect.equal value { Note = "hello" } "the converter set read it"
            | Error e -> failtestf "the reflective path must read it: %A" e

            match
                SystemTextJson.FableConverters.tryDeserialiseWith<CodecsProbeUnregistered>
                    DecodePolicy.VerifiedOnly
                    (Some "CodecsProbeApi")
                    element
                    options
            with
            | Ok _ -> failtest "VerifiedOnly must refuse a type with no registered decoder"
            | Error refusal ->
                Expect.stringContains refusal.Expected (Codecs.keyFor typeof<CodecsProbeUnregistered>) "named"

        testCase "a VerifiedOnly mount refuses to start, naming each method, leg and type with no codec"
        <| fun () ->
            let impl = { Echo = fun x -> async { return x } }

            let message =
                try
                    Remoting.createApi ()
                    |> Remoting.withDecodePolicy DecodePolicy.VerifiedOnly
                    |> Remoting.fromValue impl
                    |> Remoting.buildHttpHandler
                    |> ignore

                    None
                with :? InvalidOperationException as ex ->
                    Some ex.Message

            match message with
            | None -> failtest "a VerifiedOnly mount over unregistered types must refuse"
            | Some text ->
                Expect.stringContains text "CodecsProbeApi" "names the record"
                Expect.stringContains text "Echo (DecodeJson)" "names the argument's leg"
                Expect.stringContains text "Echo (EncodeJson)" "names the answer's leg"
                Expect.stringContains text (Codecs.keyFor typeof<CodecsProbeUnregistered>) "names the type"

            // The same walk, as data.
            let misses = Proxy.codecMisses (Contract.read typeof<CodecsProbeApi>)
            Expect.equal (List.length misses) 2 "one argument, one answer"

            // The default posture mounts the same record without a word.
            Remoting.createApi ()
            |> Remoting.fromValue impl
            |> Remoting.buildHttpHandler
            |> ignore
    ]