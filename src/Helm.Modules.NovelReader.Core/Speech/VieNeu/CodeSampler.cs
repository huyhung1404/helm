namespace Helm.Modules.NovelReader.Speech.VieNeu;

/// <summary>
/// Picks the next audio code of one codebook from the model's scores, as VieNeu samples: codes heard in the last
/// <see cref="Window"/> frames are made less likely (it breaks loops), then temperature, the 25 best, and the smallest
/// set of them that holds 95 % of the chance. Each codebook keeps its own window of recent codes.
/// </summary>
public sealed class CodeSampler(int codebooks, Random random, bool greedy = false)
{
    public const float Temperature = 0.8f;
    public const int TopK = 25;
    public const float TopP = 0.95f;
    public const float RepetitionPenalty = 1.2f;

    /// <summary>About 5 s of speech at 12.5 frames a second: long enough to catch a loop, short enough not to punish a long vowel.</summary>
    public const int Window = 64;

    private readonly Queue<int>[] _recent = Enumerable.Range(0, codebooks).Select(_ => new Queue<int>()).ToArray();
    private readonly Dictionary<int, int>[] _seen = Enumerable.Range(0, codebooks).Select(_ => new Dictionary<int, int>()).ToArray();
    private readonly int[] _candidates = new int[TopK];
    private readonly float[] _scores = new float[TopK];

    /// <summary>Samples a code for <paramref name="codebook"/> from <paramref name="logits"/> (changed in place) and remembers it.</summary>
    public int Sample(int codebook, Span<float> logits)
    {
        foreach (var code in _seen[codebook].Keys)
        {
            var score = logits[code];
            logits[code] = score < 0 ? score * RepetitionPenalty : score / RepetitionPenalty;
        }
        if (greedy)
        {
            // Tests only: the best code, so a run can be compared code for code with VieNeu's own engine.
            var best = 0;
            for (var i = 1; i < logits.Length; i++)
                if (logits[i] > logits[best]) best = i;
            Remember(codebook, best);
            return best;
        }

        var k = Math.Min(TopK, logits.Length);
        TopCandidates(logits, k);
        // Softmax over the candidates (best first), at the temperature.
        var max = _scores[0] / Temperature;
        var total = 0.0;
        for (var i = 0; i < k; i++)
        {
            _scores[i] = MathF.Exp(_scores[i] / Temperature - max);
            total += _scores[i];
        }
        // Nucleus: keep a candidate while the chance before it is under TopP.
        var kept = 0;
        var before = 0.0;
        for (; kept < k; kept++)
        {
            var p = _scores[kept] / total;
            if (before >= TopP) break;
            before += p;
        }
        var keptTotal = 0.0;
        for (var i = 0; i < kept; i++) keptTotal += _scores[i];
        var pick = random.NextDouble() * keptTotal;
        var chosen = _candidates[kept - 1];
        for (var i = 0; i < kept; i++)
        {
            pick -= _scores[i];
            if (pick < 0)
            {
                chosen = _candidates[i];
                break;
            }
        }
        Remember(codebook, chosen);
        return chosen;
    }

    /// <summary>The <paramref name="k"/> highest scores, best first, into <see cref="_candidates"/> and <see cref="_scores"/>.</summary>
    private void TopCandidates(ReadOnlySpan<float> logits, int k)
    {
        var count = 0;
        for (var i = 0; i < logits.Length; i++)
        {
            var score = logits[i];
            if (count == k && score <= _scores[k - 1]) continue;
            var at = count < k ? count++ : k - 1;
            while (at > 0 && _scores[at - 1] < score)
            {
                _scores[at] = _scores[at - 1];
                _candidates[at] = _candidates[at - 1];
                at--;
            }
            _scores[at] = score;
            _candidates[at] = i;
        }
    }

    private void Remember(int codebook, int code)
    {
        var seen = _seen[codebook];
        seen[code] = seen.GetValueOrDefault(code) + 1;
        var recent = _recent[codebook];
        recent.Enqueue(code);
        if (recent.Count <= Window) return;
        var old = recent.Dequeue();
        if (seen[old] <= 1) seen.Remove(old);
        else seen[old]--;
    }
}
