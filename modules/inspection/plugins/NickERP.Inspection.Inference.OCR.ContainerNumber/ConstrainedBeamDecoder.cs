namespace NickERP.Inspection.Inference.OCR.ContainerNumber;

/// <summary>
/// Constrained beam search restricted to the ISO 6346 grammar
/// (<c>[A-Z]{4}[0-9]{7}</c>) plus the <c>&lt;unreadable&gt;</c> sentinel. The
/// decoder takes a 2D logits matrix <c>[T, V]</c> where <c>T</c> is at most
/// <see cref="ContainerOcrConfig.MaxTokenBudget"/> and <c>V</c> is the
/// 36-symbol alphabet from <see cref="Iso6346.AllowedAlphabet"/>. It returns
/// the best valid sequence by joint log-probability, falling back to the
/// sentinel if no valid 11-character string can be assembled before the
/// budget is exhausted.
/// </summary>
/// <remarks>
/// This decoder operates against an already-projected logits tensor — the
/// recogniser is responsible for slicing the model's full vocabulary down
/// to the 36 allowed columns before calling here. That keeps this class
/// trainer-agnostic: a Florence-2 export and a Donut export can both
/// produce the same rectangular shape after the call site does the slice.
///
/// The implementation is straightforward log-domain beam search; we keep it
/// synchronous and CPU-side because <c>T ≤ 16</c> and <c>BeamWidth ≤ 8</c>
/// in practice, so the decode loop is bounded at ≤ 128 iterations and is
/// not on the latency critical path (which is the encoder forward pass).
/// </remarks>
internal static class ConstrainedBeamDecoder
{
    /// <summary>
    /// Decode a logits matrix to the best ISO 6346–valid string under the
    /// position-conditional grammar (positions 0–3 letters, 4–10 digits).
    /// </summary>
    /// <param name="logits">Logits of shape <c>[T, V]</c> in row-major; <c>V</c> must equal <see cref="Iso6346.AllowedAlphabet"/>.Length.</param>
    /// <param name="timeSteps">Number of time steps T actually populated (≤ <paramref name="maxBudget"/>).</param>
    /// <param name="vocabSize">Size of the alphabet axis V; must equal 36.</param>
    /// <param name="beamWidth">Beam width.</param>
    /// <param name="maxBudget">Token budget T_max; if no valid sequence emerges within this many steps, returns the sentinel.</param>
    /// <returns>Tuple of (decoded string, mean per-token confidence in [0, 1]).</returns>
    public static (string Decoded, double Confidence) Decode(
        ReadOnlySpan<float> logits,
        int timeSteps,
        int vocabSize,
        int beamWidth,
        int maxBudget)
    {
        if (vocabSize != Iso6346.AllowedAlphabet.Length)
        {
            throw new ArgumentException(
                $"vocabSize must equal {Iso6346.AllowedAlphabet.Length} (the ISO 6346 alphabet); got {vocabSize}.",
                nameof(vocabSize));
        }
        if (timeSteps < 11)
        {
            // Cannot fit an 11-char container number; sentinel.
            return (Iso6346.UnreadableSentinel, 0.0);
        }
        if (logits.Length < timeSteps * vocabSize)
        {
            throw new ArgumentException(
                $"logits length {logits.Length} is smaller than timeSteps*vocabSize = {timeSteps * vocabSize}.",
                nameof(logits));
        }

        var alphabet = Iso6346.AllowedAlphabet;
        var effectiveSteps = Math.Min(timeSteps, maxBudget);

        // Beam state: parallel arrays. Each beam carries the partial string
        // (max 11 chars) and its accumulated log-prob.
        // We size each list at beamWidth + extras and rotate.
        var beams = new List<BeamState> { new(string.Empty, 0.0) };

        for (var t = 0; t < effectiveSteps && beams[0].Token.Length < 11; t++)
        {
            var stepLogits = logits.Slice(t * vocabSize, vocabSize);

            // Position-conditional grammar: first 4 chars must be A-Z, rest 0-9.
            // Build a per-step legal mask once.
            var positionalIsLetter = beams.Count > 0 && beams[0].Token.Length < 4;
            // (All beams advance by one token per step, so the position is
            // shared across the active beam set as long as the beam length
            // stays uniform — which it does here because we always extend by
            // exactly one token per step until length 11.)

            var stepLogProbs = LogSoftmax(stepLogits);

            var candidates = new List<BeamState>(beams.Count * vocabSize);
            for (var b = 0; b < beams.Count; b++)
            {
                var beam = beams[b];
                if (beam.Token.Length >= 11) { candidates.Add(beam); continue; }
                var pos = beam.Token.Length;
                for (var v = 0; v < vocabSize; v++)
                {
                    var ch = alphabet[v];
                    var legal = pos < 4
                        ? (ch >= 'A' && ch <= 'Z')
                        : (ch >= '0' && ch <= '9');
                    if (!legal) continue;
                    candidates.Add(new BeamState(
                        Token: beam.Token + ch,
                        LogProb: beam.LogProb + stepLogProbs[v]));
                }
            }

            if (candidates.Count == 0)
            {
                return (Iso6346.UnreadableSentinel, 0.0);
            }

            // Top-K by joint log-prob.
            candidates.Sort(static (a, b) => b.LogProb.CompareTo(a.LogProb));
            beams = candidates.Take(beamWidth).ToList();
        }

        // Filter to length-11 beams and try in best-first order.
        foreach (var beam in beams.Where(b => b.Token.Length == 11))
        {
            // Mean per-token log-prob → exp → confidence. Log-prob is non-positive,
            // so confidence ∈ (0, 1]. The check-digit gate runs in the recogniser,
            // not here; this decoder returns the best grammar-legal string.
            var meanLogProb = beam.LogProb / beam.Token.Length;
            var confidence = Math.Exp(meanLogProb);
            return (beam.Token, Math.Clamp(confidence, 0.0, 1.0));
        }

        return (Iso6346.UnreadableSentinel, 0.0);
    }

    /// <summary>
    /// Numerically stable log-softmax over a single time-step logit row.
    /// </summary>
    private static double[] LogSoftmax(ReadOnlySpan<float> stepLogits)
    {
        // Subtract max for stability before exp.
        var max = float.NegativeInfinity;
        for (var i = 0; i < stepLogits.Length; i++)
        {
            if (stepLogits[i] > max) max = stepLogits[i];
        }
        if (float.IsNegativeInfinity(max))
        {
            // All -inf → uniform fallback to keep arithmetic finite.
            var n = stepLogits.Length;
            var uniform = -Math.Log(n);
            var arr = new double[n];
            Array.Fill(arr, uniform);
            return arr;
        }

        var sumExp = 0.0;
        for (var i = 0; i < stepLogits.Length; i++)
        {
            sumExp += Math.Exp(stepLogits[i] - max);
        }
        var logSum = Math.Log(sumExp) + max;

        var result = new double[stepLogits.Length];
        for (var i = 0; i < stepLogits.Length; i++)
        {
            result[i] = stepLogits[i] - logSum;
        }
        return result;
    }

    private readonly record struct BeamState(string Token, double LogProb);
}
