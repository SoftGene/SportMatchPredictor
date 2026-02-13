using Microsoft.ML;
using SportMatchPredictor.ML.Data;

namespace SportMatchPredictor.ML.Services;

public sealed class ModelService
{
    private readonly MLContext _ml = new(seed: 42);
    private readonly PredictionEngine<MatchData, MatchPrediction> _engine;

    public ModelService(string modelPath)
    {
        var model = _ml.Model.Load(modelPath, out _);
        _engine = _ml.Model.CreatePredictionEngine<MatchData, MatchPrediction>(model);
    }

    public MatchPrediction Predict(MatchData input) => _engine.Predict(input);
}