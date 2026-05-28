module ToolUp.RAG.NoOpDocUnderstanding

open ToolUp.Platform.IOcrProvider
open ToolUp.Platform.ITableExtractor

/// Default `IOcrProvider`. `IsScanned` returns `false` for every input, so
/// extractors that consult it stay on their normal text-extraction path —
/// the byte-equivalent behaviour required by Phase 14i acceptance.
/// `ExtractText` returns the empty list (callers should not reach it,
/// since `IsScanned` is `false`).
type NoOpOcrProvider() =
    interface IOcrProvider with
        member _.Name = "noop"
        member _.IsScanned _ _ = async { return false }
        member _.ExtractText _ _ = async { return [] }

/// Default `ITableExtractor`. Returns the empty list for every input, so
/// extractors that consult it fall through to their default token-split
/// chunking — the byte-equivalent behaviour required by Phase 14i
/// acceptance.
type NoOpTableExtractor() =
    interface ITableExtractor with
        member _.Name = "noop"
        member _.ExtractTables _ _ = async { return [] }

/// Convenience constructors for `composeWithRAG`'s service-config wiring.
let createOcrProvider () : IOcrProvider = NoOpOcrProvider() :> _

let createTableExtractor () : ITableExtractor = NoOpTableExtractor() :> _