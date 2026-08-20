# Migration — chained audit ledger sink

**What changes:** a new companion package, `ToolUp.AuditSinks.ChainedLedger`, adds a tamper-evident `IAuditSink`. **Nothing else changes.** No existing type is retyped, no existing default moves, no composition root gains a step. A deployment that does not reference the package is byte-for-byte what it was (GP 11 / GP 13).

Adopt it when you need an audit trail that can answer *is this still what the application recorded?* — not merely *what did the application record?*

## Before

Audit events reach whatever sinks are composed; each sink is an independent transport. Nothing chains, nothing is signable, and a record edited or deleted at the destination after the fact leaves no trace.

## After

Add one project reference and construct one sink. The unsigned form needs no key material:

```fsharp
open ToolUp.Platform.AuditSinks.ChainedLedger

let settings = {
    ChainedLedgerSettings.defaults with
        Container = "audit-ledger-prod"
        PathPrefix = Some "ledger"
}

let ledgerSink = create "ledger-prod" settings blobStorage
```

Register it exactly like any other audit sink. It coexists with existing sinks — the ledger is an additional destination, not a replacement for one.

To sign the head, implement `ILedgerHeadSigner` against your own signing substrate and swap the constructor:

```fsharp
let ledgerSink = createSigned "ledger-prod" settings blobStorage signer
```

`ILedgerHeadSigner` asks for three things — a key id to record, an algorithm name to record, and bytes-to-signature. It is deliberately generic so the ledger carries no key-management dependency of its own.

## Per-file diff

**`src/Server/<your composition root>.fs`** — add the open and the sink construction:

```diff
+open ToolUp.Platform.AuditSinks.ChainedLedger
+
+let ledgerSink =
+    create "ledger-prod" { ChainedLedgerSettings.defaults with Container = "audit-ledger-prod" } blobStorage
```

**`src/Server/<your server>.fsproj`** — add the reference:

```diff
+    <ProjectReference Include="..\..\..\toolup-forge\src\AuditSinks\ChainedLedger\ToolUp.AuditSinks.ChainedLedger.fsproj" />
```

Or, from a packaged consumer, the `PackageReference`:

```diff
+    <PackageReference Include="ToolUp.AuditSinks.ChainedLedger" />
```

## Verification steps

1. **Build.** `dotnet build` — the package adds no vendor dependency (BCL SHA-256 and `System.Text.Json` only), so no restore surprise.
2. **Deliver and read back.** Exercise an audited operation, then verify the ledger:

   ```fsharp
   match! verify settings blobStorage None with
   | Ok(LedgerVerified(count, headDigest, HeadUnsigned)) -> // count matches what you emitted
   | other -> // investigate
   ```

3. **Prove the check can fail.** A verification pass that has never failed proves nothing. Edit one stored record's `ScopeId` by hand and re-run `verify`: it must return `LedgerBroken` with `Kind = TamperedRecord` and `Position` equal to that record's index. Restore the record and confirm it verifies again.
4. **If you composed a signer**, confirm the cold path: build an `ILedgerHeadVerifier` from **public key material only**, in a process that has never run the sink, and check that `verify` returns `LedgerVerified` with `HeadSignatureValid`. A signed head with no verifier supplied returns `LedgerHeadUntrusted` / `HeadSignatureUnverifiable` — that is correct behaviour, not a failure to configure.

## Things to know before adopting

- **One writer per ledger.** The chain is serial by nature. The sink serialises appends in-process and detects a head that moved beneath it, refusing rather than forking — but `IBlobStorage` has no compare-and-set, so this is detection, not exclusion. Run one ledger per writing process and verify each chain independently.
- **A signing failure fails the delivery.** The dispatcher retries the batch. This is deliberate: reporting success on a head that was not signed would make the signature meaningless exactly when it matters.
- **Retries do not duplicate.** Segments are content-addressed, so a re-delivered batch recomputes the same bytes and overwrites its own segment rather than landing beside it.
- **Retention is configured at the destination.** Write-once behaviour comes from the object-storage retention policy or filesystem ACL on the container, not from the sink.
- **The chain alone is tamper-evident, not tamper-proof.** It catches an editor who cannot rewrite the tail. An attacker with write access to the whole ledger can recompute every record from an edit forward; only a head signature they cannot forge closes that. Adopt the unsigned form first if signing infrastructure is not ready — an unsigned chain is a large improvement on no chain — and add the signer later by changing one call.

## Rollback

Remove the project or package reference and delete the sink construction. Nothing else was touched, so no other code path needs unwinding. Ledger blobs already written remain readable and verifiable by any process that still references the package.
