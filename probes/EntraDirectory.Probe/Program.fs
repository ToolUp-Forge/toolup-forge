module EntraDirectory.Probe.Program

// Fast iteration probe for the EntraDirectory companion.
//
// Hits the real Microsoft Graph endpoint via DefaultAzureCredential
// (az login on a dev workstation; managed identity in CI). Catches
// the bug classes that were biting us live in 0.5.7 → 0.5.12:
//
//   • STJ deserialisation defects (`type private` + [<CLIMutable>]
//     producing a non-public ctor STJ rejects — the 0.5.12 fix)
//   • Graph filter / advanced-query shape issues (`$count=true`
//     required when OR-combining startswith — the 0.5.11 fix)
//   • DefaultAzureCredential auth misconfiguration
//   • Per-mailbox Mail.Send permission probes
//
// Does NOT catch Kestrel sync-IO defects (`AllowSynchronousIO=false`
// rejecting `JsonSerializer.Serialize(stream,...)` — the 0.5.10 fix).
// That class only manifests when the response body is written through
// the HTTP pipeline; this probe never crosses an HTTP boundary on the
// outbound side. A separate end-to-end probe (local `dotnet run` of
// toolup-app + curl against the API) is needed for that class.

open System
open ToolUp.Platform

let private printHeader (title: string) =
    let bar = String.replicate (max 4 (title.Length + 4)) "─"
    printfn ""
    printfn "%s" bar
    printfn "  %s" title
    printfn "%s" bar

let private printConfig () =
    let opt (name: string) =
        match Environment.GetEnvironmentVariable name with
        | null
        | "" -> "(unset)"
        | v when name.EndsWith "_OID" || name.EndsWith "_TOKEN" -> sprintf "set (%d chars)" v.Length
        | v -> v

    printHeader "Environment"
    printfn "  TOOLUP_ENTRA_DIRECTORY_ENABLED         = %s" (opt "TOOLUP_ENTRA_DIRECTORY_ENABLED")
    printfn "  TOOLUP_ENTRA_DIRECTORY_GRAPH_ENDPOINT  = %s" (opt "TOOLUP_ENTRA_DIRECTORY_GRAPH_ENDPOINT")
    printfn "  TOOLUP_ENTRA_DIRECTORY_SENDER_OID      = %s" (opt "TOOLUP_ENTRA_DIRECTORY_SENDER_OID")
    printfn "  AZURE_CLIENT_ID                        = %s" (opt "AZURE_CLIENT_ID")
    printfn "  AZURE_TENANT_ID                        = %s" (opt "AZURE_TENANT_ID")

let private resolveDirectory () =
    match ToolUp.AuthProviders.EntraDirectory.fromEnv () with
    | Some d -> d
    | None ->
        eprintfn ""
        eprintfn "EntraDirectory.fromEnv() returned None."
        eprintfn "Set TOOLUP_ENTRA_DIRECTORY_ENABLED=1 before running the probe."
        exit 2

let private renderSummary (i: int) (s: UserSummary) =
    let nameOrBlank = s.DisplayName |> Option.defaultValue "(no display name)"

    let emailOrBlank = s.Email |> Option.defaultValue "(no email)"
    printfn "  [%d] %s  <%s>  id=%s" (i + 1) nameOrBlank emailOrBlank s.UserId

let private search (directory: IUserDirectory) (query: string) (take: int) =
    printHeader (sprintf "SearchUsers query=\"%s\" take=%d" query take)
    let sw = System.Diagnostics.Stopwatch.StartNew()
    let result = directory.SearchUsers(query, take) |> Async.RunSynchronously
    sw.Stop()
    printfn "  elapsed: %d ms" sw.ElapsedMilliseconds

    match result with
    | Ok matches ->
        printfn "  matches: %d" matches.Length
        matches |> List.iteri renderSummary

        if matches.IsEmpty then
            printfn "  (none)"

        0
    | Error msg ->
        printfn "  ERROR:"
        printfn "    %s" msg
        1

let private notify (directory: IUserDirectory) (recipient: string) =
    printHeader (sprintf "NotifyInvitation recipient=%s" recipient)

    let notification: InvitationNotification = {
        Email = recipient
        TeamName = "Probe Test Team"
        InviterName = Some "Probe Test Inviter"
        AppName = "ToolUp Probe"
        RedirectUrl = "https://cd.toolup.pro/probe-test"
        Role = TeamRole.Member
    }

    let sw = System.Diagnostics.Stopwatch.StartNew()
    let result = directory.NotifyInvitation notification |> Async.RunSynchronously
    sw.Stop()
    printfn "  elapsed: %d ms" sw.ElapsedMilliseconds

    match result with
    | Ok() ->
        printfn "  SENT"
        0
    | Error msg ->
        printfn "  ERROR:"
        printfn "    %s" msg
        1

[<EntryPoint>]
let main argv =
    printConfig ()

    match Array.toList argv with
    | [] ->
        // Default exercise: confirm the typeahead path round-trips
        // against Graph with both a name-shaped and an email-shaped
        // prefix. The email-shaped prefix is the case that was
        // silently failing in 0.5.7-0.5.11.
        let d = resolveDirectory ()
        let r1 = search d "and" 5
        let r2 = search d "andrew@" 5
        max r1 r2
    | [ "search"; query ] -> search (resolveDirectory ()) query 10
    | [ "search"; query; take ] ->
        let n = Int32.Parse take
        search (resolveDirectory ()) query n
    | [ "notify"; recipient ] -> notify (resolveDirectory ()) recipient
    | _ ->
        eprintfn ""
        eprintfn "Usage:"
        eprintfn "  dotnet run --project probes/EntraDirectory.Probe"
        eprintfn "  dotnet run --project probes/EntraDirectory.Probe -- search <query> [take]"
        eprintfn "  dotnet run --project probes/EntraDirectory.Probe -- notify <recipient-email>"
        2