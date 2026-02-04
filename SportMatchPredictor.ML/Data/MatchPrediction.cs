using Microsoft.ML.Data;

namespace SportMatchPredictor.ML.Data
{
    public class MatchPrediction
    {
        [ColumnName("PredictedLabel")]
        public float PredictedResult { get; set; }

        public float[]? Score { get; set; }
    }
}
