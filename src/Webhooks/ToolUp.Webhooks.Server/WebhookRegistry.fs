namespace ToolUp.Webhooks.Server

open ToolUp.Webhooks

/// Per-`Kind` registry of inbound-webhook handlers. Rejects duplicate
/// kinds at compose time (the same fail-fast shape as the notification-
/// sink registry) so two handlers can't silently shadow each other.
type WebhookRegistry private (handlers: Map<string, IInboundWebhookHandler>) =

    /// Look up the handler registered for a kind.
    member _.TryFind(kind: string) : IInboundWebhookHandler option = handlers.TryFind kind

    /// The registered kinds (diagnostic / route enumeration).
    member _.Kinds: string list = handlers |> Map.toList |> List.map fst

    /// Build a registry, rejecting any duplicate `Kind`. `Error` names
    /// the first clash; composition roots that want fail-fast call
    /// `ofList` instead.
    static member tryOfList(items: IInboundWebhookHandler list) : Result<WebhookRegistry, string> =
        let rec loop acc remaining =
            match remaining with
            | [] -> Ok(WebhookRegistry acc)
            | (h: IInboundWebhookHandler) :: rest ->
                if Map.containsKey h.Kind acc then
                    Error(sprintf "Duplicate inbound-webhook handler for kind '%s'" h.Kind)
                else
                    loop (Map.add h.Kind h acc) rest

        loop Map.empty items

    /// Build a registry, raising on a duplicate `Kind` (compose-time
    /// fail-fast).
    static member ofList(items: IInboundWebhookHandler list) : WebhookRegistry =
        match WebhookRegistry.tryOfList items with
        | Ok reg -> reg
        | Error msg -> failwith msg