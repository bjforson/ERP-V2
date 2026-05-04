// Copyright (c) Nick TC-Scan Ltd. All rights reserved.

namespace NickERP.Tools.OcrEvaluation;

/// <summary>
/// Streams <see cref="CorpusRow"/> instances from some backing store. The
/// orchestrator iterates lazily so a 50k-row Postgres run never holds all
/// image bytes in memory at once.
/// </summary>
internal interface ICorpusSource : IDisposable
{
    /// <summary>Approximate row count (used for progress logging).
    /// May return -1 when unknown.</summary>
    int ApproximateCount { get; }

    /// <summary>Streaming iteration. Caller may abandon enumeration at
    /// any time; the source must release its backing resources cleanly
    /// when disposed.</summary>
    IEnumerable<CorpusRow> Stream(int hardLimit, CancellationToken ct);
}
