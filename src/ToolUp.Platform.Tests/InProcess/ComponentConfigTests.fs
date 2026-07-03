module ToolUp.Platform.Tests.InProcess.ComponentConfigTests

open Expecto
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation
open ToolUp.Platform.ComponentConfigResolver

// ─── Phase 289 — component-scoped configuration binding ───────────────
//
// Covers the acceptance shape: an id-scoped env override
// (TOOLUP_COMPONENT__<id>__<key>) reaches exactly its component; an
// override targeting an unknown component / undeclared key fails
// preflight with an id-named message; a no-override deployment is
// byte-for-byte unchanged (GP 11). The env accessors are injected (a
// Map / list) so the tests are pure over an environment view and never
// mutate process-global state.

let private ordersId = ComponentId.ofModule "orders-service"
let private inventoryId = ComponentId.ofModule "inventory"

/// A declared section: orders with two keys, inventory with one.
let private ordersSection =
    ComponentConfig.create ordersId [ "maxItems", "100"; "currency", "GBP" ]

let private inventorySection =
    ComponentConfig.create inventoryId [ "reorderPoint", "5" ]

/// An `EnvReader` over an in-memory map.
let private reader (pairs: (string * string) list) : EnvReader =
    let map = Map.ofList pairs
    fun name -> map |> Map.tryFind name

/// An `EnvEnumerator` over an in-memory list.
let private enumerator (pairs: (string * string) list) : EnvEnumerator = fun () -> pairs

let tests =
    testList "ComponentConfig" [

        // ── env-var-name derivation is deterministic + shell-legal ────
        testCase "envVarName folds a slot-prefixed id into a legal token"
        <| fun _ ->
            Expect.equal
                (ComponentConfig.envVarName ordersId "maxItems")
                "TOOLUP_COMPONENT__MODULE_ORDERS_SERVICE__MAXITEMS"
                "id + key canonicalised, joined by the __ delimiter"

            Expect.equal
                (ComponentConfig.envVarName (ComponentId.forCompanionImpl "IAuditSink" "SplunkHec") "endpoint")
                "TOOLUP_COMPONENT__COMPANION_IAUDITSINK_SPLUNKHEC__ENDPOINT"
                "companion sub-id id folds : and / to single underscores"

        testCase "canonicalSegment collapses runs so the delimiter stays unambiguous"
        <| fun _ ->
            Expect.equal (ComponentConfig.canonicalSegment "a--b__c") "A_B_C" "runs of non-alnum collapse to one _"
            Expect.equal (ComponentConfig.canonicalSegment "  trim  ") "TRIM" "leading / trailing folded away"

        // ── an id-scoped override reaches exactly its component ───────
        testCase "an id-scoped override overrides exactly the matching declared key"
        <| fun _ ->
            let env = reader [ ComponentConfig.envVarName ordersId "maxItems", "250" ]

            let resolved = resolve env ordersSection

            Expect.equal
                (ComponentConfig.tryValue "maxItems" resolved)
                (Some "250")
                "overridden key takes the env value"

            Expect.equal
                (ComponentConfig.tryValue "currency" resolved)
                (Some "GBP")
                "un-overridden key keeps its default"

        testCase "an override for another component does not leak across ids"
        <| fun _ ->
            // An override addressed to inventory must not touch orders,
            // even for a same-named key.
            let env =
                reader [
                    ComponentConfig.envVarName inventoryId "maxItems", "999"
                    ComponentConfig.envVarName ordersId "maxItems", "250"
                ]

            let resolvedInventory = resolve env inventorySection

            // inventory declares no `maxItems`, so its section is unaffected.
            Expect.equal
                (ComponentConfig.tryValue "maxItems" resolvedInventory)
                None
                "an override for an undeclared key does not appear on the section"

            Expect.equal
                (ComponentConfig.tryValue "reorderPoint" resolvedInventory)
                (Some "5")
                "inventory's own declared key is unchanged"

        // ── GP 11: no overrides → byte-for-byte unchanged ─────────────
        testCase "a section with no overrides resolves to itself"
        <| fun _ ->
            let env = reader []
            Expect.equal (resolve env ordersSection) ordersSection "no override → structurally identical section"

        // ── preflight: an invalid override fails with an id-named msg ──
        testCase "an override targeting no known component fails preflight, naming the variable"
        <| fun _ ->
            // A typo'd id (`orders` rather than `orders-service`).
            let strayName =
                ComponentConfig.envVarName (ComponentId.ofModule "orders") "maxItems"

            let validator =
                ComponentConfigOverrideValidator(enumerator [ strayName, "1" ], [ ordersSection; inventorySection ])
                :> IConfigValidator

            let result = validator.Validate() |> Async.RunSynchronously

            Expect.equal (ValidationResult.status result) "Error" "a stray override is a preflight Error"
            Expect.stringContains (ValidationResult.message result) strayName "the message names the offending variable"

        testCase "an override for an undeclared key on a known component still fails preflight"
        <| fun _ ->
            let strayName = ComponentConfig.envVarName ordersId "nonexistentKey"

            let validator =
                ComponentConfigOverrideValidator(enumerator [ strayName, "1" ], [ ordersSection ]) :> IConfigValidator

            let result = validator.Validate() |> Async.RunSynchronously
            Expect.equal (ValidationResult.status result) "Error" "an undeclared key is a stray override"

        // ── preflight: valid / absent overrides pass silently (GP 11) ─
        testCase "a valid override passes preflight"
        <| fun _ ->
            let goodName = ComponentConfig.envVarName ordersId "maxItems"

            let validator =
                ComponentConfigOverrideValidator(enumerator [ goodName, "250" ], [ ordersSection ]) :> IConfigValidator

            let result = validator.Validate() |> Async.RunSynchronously
            Expect.equal (ValidationResult.status result) "Ok" "a well-formed override validates Ok"

        testCase "no overrides at all passes preflight"
        <| fun _ ->
            let validator =
                ComponentConfigOverrideValidator(enumerator [ "PATH", "/usr/bin" ], [ ordersSection ])
                :> IConfigValidator

            let result = validator.Validate() |> Async.RunSynchronously
            Expect.equal (ValidationResult.status result) "Ok" "non-component env vars are ignored (GP 11)"

        testCase "unrecognisedOverrides lists only stray component overrides"
        <| fun _ ->
            let stray = ComponentConfig.envVarName (ComponentId.ofModule "typo") "k"
            let good = ComponentConfig.envVarName ordersId "maxItems"

            let strays =
                unrecognisedOverrides (enumerator [ stray, "1"; good, "2"; "HOME", "/root" ]) [ ordersSection ]

            Expect.equal strays [ stray ] "only the stray component override is reported"
    ]