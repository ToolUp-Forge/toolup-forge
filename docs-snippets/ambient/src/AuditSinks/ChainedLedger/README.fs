// Ambient context for `src/AuditSinks/ChainedLedger/README.md`.
//
// The page is a companion README, so every construction block is an
// excerpt from the consuming server's composition root: the deployment's
// `IBlobStorage`, the `ChainedLedgerSettings` the first block builds, and
// the `ILedgerHeadSigner` the reader implements against whatever signing
// substrate they run. Declared here so the blocks compile exactly as a
// reader would copy them, with no composition-root ceremony added to the
// markdown. The blocks that DO build a settings value shadow the ambient
// one, which is the point of the auto-opened module.
open ToolUp.Platform.AuditSinks.ChainedLedger

[<AutoOpen>]
module PageAmbient =

    /// The deployment's blob backend, already composed by the time the
    /// audit sink is constructed.
    let blobStorage: IBlobStorage = failwith "ambient"

    /// The settings value the "How to enable" block builds; later blocks
    /// read it back rather than restating it.
    let settings: ChainedLedgerSettings = failwith "ambient"

    /// The head signer the deployment implements against its own signing
    /// substrate — the README says so rather than shipping one.
    let signer: ILedgerHeadSigner = failwith "ambient"
