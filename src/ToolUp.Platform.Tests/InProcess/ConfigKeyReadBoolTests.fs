// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `ConfigKey.readBool` — the one boolean parser every reader outside
/// `ServerConfig.fromEnv` resolves through, so the direction and token set
/// the generated reference documents for a key are the ones its code has.
/// Pinned per key the readers outside the binder used to parse by hand, over
/// the token set the reference names, a typo, and unset.
module ToolUp.Platform.Tests.InProcess.ConfigKeyReadBoolTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Config

/// The keys whose readers outside the binder parsed their own tokens before
/// they went through the registry, each with the default its entry declares.
let private rehomedKeys = [
    ConfigKeys.Names.ragRefuseOnIndexCorruption, false
    ConfigKeys.Names.ldapNestedGroups, true
    ConfigKeys.Names.auditAdminRequired, false
    ConfigKeys.Names.entraDirectoryEnabled, false
    ConfigKeys.Names.aiProbeOnStartup, false
    ConfigKeys.Names.remotingAnalyzerAudit, false
]

let private onSpellings = [ "1"; "true"; "TRUE"; "True"; "yes"; "YES"; "on"; "On"; " on " ]
let private offSpellings = [ "0"; "false"; "FALSE"; "no"; "off" ]

let private withEnv (name: string) (value: string option) (body: unit -> 'a) : 'a =
    let prior = Environment.GetEnvironmentVariable name

    try
        Environment.SetEnvironmentVariable(name, Option.toObj value)
        body ()
    finally
        Environment.SetEnvironmentVariable(name, prior)

let tests =
    testList "ConfigKey.readBool — the shared boolean reader" [
        testList "every re-homed key is a Permissive boolean in the registry" [
            for name, expectedDefault in rehomedKeys do
                testCase name
                <| fun _ ->
                    let key = ConfigKeys.key name

                    match key.Kind with
                    | ConfigKeys.ConfigKeyKind.BoolKey(direction, defaultValue) ->
                        Expect.equal direction ConfigKeys.Direction.Permissive "a typo keeps the safe reading"
                        Expect.equal defaultValue (Some expectedDefault) "the default the reader used to hard-code"
                    | other -> failtestf "%s is not a boolean key: %A" name other
        ]

        testList "the reference's on spellings read as on, in any case" [
            for name, _ in rehomedKeys do
                for spelling in onSpellings do
                    testCase (sprintf "%s=%s" name spelling)
                    <| fun _ ->
                        Expect.equal
                            (ConfigKeys.ConfigKey.readBool (ConfigKeys.key name) (Some spelling))
                            (Ok true)
                            "on"
        ]

        testList "the off spellings read as off" [
            for name, _ in rehomedKeys do
                for spelling in offSpellings do
                    testCase (sprintf "%s=%s" name spelling)
                    <| fun _ ->
                        Expect.equal
                            (ConfigKeys.ConfigKey.readBool (ConfigKeys.key name) (Some spelling))
                            (Ok false)
                            "off"
        ]

        testList "a typo reads as off under Permissive, never as on and never as the default" [
            for name, _ in rehomedKeys do
                testCase name
                <| fun _ ->
                    for garbage in [ "ture"; "enabled"; "y"; "1 "; "t" ] do
                        let read = ConfigKeys.ConfigKey.readBool (ConfigKeys.key name) (Some garbage)
                        // `1 ` trims to an on spelling; every other token is off.
                        let expected = if garbage.Trim() = "1" then true else false
                        Expect.equal read (Ok expected) (sprintf "%s=%s" name garbage)
        ]

        testList "unset reads as the typed default" [
            for name, expectedDefault in rehomedKeys do
                testCase name
                <| fun _ ->
                    Expect.equal
                        (ConfigKeys.ConfigKey.readBool (ConfigKeys.key name) None)
                        (Ok expectedDefault)
                        "unset is the registry's default, not a hard-coded one"

                    Expect.equal
                        (ConfigKeys.ConfigKey.readBool (ConfigKeys.key name) (Some ""))
                        (Ok expectedDefault)
                        "an empty value is unset"
        ]

        testCase "a Protective boolean refuses a typo by name, and reads its off spellings"
        <| fun _ ->
            let protective =
                ConfigKeys.keys
                |> List.tryFind (fun k ->
                    match k.Kind with
                    | ConfigKeys.ConfigKeyKind.BoolKey(ConfigKeys.Direction.Protective, _) -> true
                    | _ -> false)

            match protective with
            | None -> Tests.skiptest "the registry declares no Protective boolean key"
            | Some key ->
                Expect.equal (ConfigKeys.ConfigKey.readBool key (Some "off")) (Ok false) "off"
                Expect.equal (ConfigKeys.ConfigKey.readBool key (Some "YES")) (Ok true) "on"

                match ConfigKeys.ConfigKey.readBool key (Some "ture") with
                | Error why ->
                    Expect.stringContains why key.EnvVar "names the key"
                    Expect.stringContains why "ture" "names the value as written"
                | Ok v -> failtestf "a Protective key read a typo as %b" v

        testCase "a key that is not a boolean is a programming error, not a value"
        <| fun _ ->
            let key = ConfigKeys.key ConfigKeys.Names.notificationChannel

            Expect.throwsT<ArgumentException>
                (fun () -> ConfigKeys.ConfigKey.readBool key (Some "redis") |> ignore)
                "an enum key has no boolean reading"

        testList "TOOLUP_NOTIFICATION_CHANNEL has one reader, and both spellings of in-process are in-process" [
            for token, inProcess in
                [
                    None, true
                    Some "", true
                    Some "inprocess", true
                    Some "in-process", true
                    Some "In-Process", true
                    Some " INPROCESS ", true
                    Some "redis", false
                    Some "Redis", false
                    Some "somethingelse", false
                ] do
                testCase (sprintf "%A" token)
                <| fun _ ->
                    withEnv ConfigKeys.Names.notificationChannel token (fun () ->
                        let selected = NotificationChannel.selectedChannel ()

                        Expect.equal
                            (NotificationChannel.isInProcess selected)
                            inProcess
                            (sprintf "selected %s" selected)

                        Expect.equal selected (selected.Trim().ToLowerInvariant()) "normalised")
        ]
    ]