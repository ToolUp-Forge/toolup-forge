module ToolUp.RAG.Benchmarks.BeirTypes

/// One BEIR corpus document. The `_id` field in the JSONL file is mapped to
/// `Id`. Title and Text are concatenated by the loader's `toFixture` adapter
/// — title-then-newline-newline-text — to produce the chunk content the
/// retrieval pipeline indexes against.
type BeirCorpusDoc = {
    Id: string
    Title: string
    Text: string
}

/// One BEIR query. `_id` → `Id`. The text is what gets embedded at retrieval
/// time. BEIR queries are typically short (8–20 tokens for SciFact, longer
/// for FiQA).
type BeirQuery = { Id: string; Text: string }

/// One BEIR qrel — the labelled (query, doc, relevance) tuple. `Score` is
/// the integer relevance grade (0 = irrelevant, 1+ = increasing relevance).
/// We binarise at score >= 1 for Recall / MRR per BEIR convention. nDCG also
/// binarises in v1; graded relevance is on the deferred list.
type BeirQrel = {
    QueryId: string
    CorpusId: string
    Score: int
}

/// A loaded BEIR dataset — corpus, queries, qrels, and the dataset name for
/// CSV reporting. Held in memory; for the smallest BEIR datasets (SciFact,
/// NFCorpus) this is a few MB. For FiQA (~57k docs, ~80 MB JSONL) we still
/// hold the full corpus because every query touches it; the JSONL parsers
/// stream-read so peak memory is bounded by the result list, not the file.
type BeirDataset = {
    Name: string
    Corpus: BeirCorpusDoc list
    Queries: BeirQuery list
    Qrels: BeirQrel list
}