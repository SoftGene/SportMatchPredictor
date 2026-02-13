namespace SportMatchPredictor.ML;

public static class MathHelpers
{
    public static float[] Softmax(float[] scores)
    {
        if (scores == null || scores.Length == 0) return Array.Empty<float>();

        var max = scores.Max();
        var exps = scores.Select(s => MathF.Exp(s - max)).ToArray();
        var sum = exps.Sum();
        if (sum <= 0) return scores.Select(_ => 0f).ToArray();

        return exps.Select(e => e / sum).ToArray();
    }
}
