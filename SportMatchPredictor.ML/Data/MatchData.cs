using Microsoft.ML.Data;

namespace SportMatchPredictor.ML.Data
{
    public class MatchData
    {
        // Home team features
        public float HomeTeamRating { get; set; }
        public float HomeTeamForm { get; set; }
        public float HomeTeamGoalsAvg { get; set; }

        // Away team features
        public float AwayTeamRating { get; set; }
        public float AwayTeamForm { get; set; }
        public float AwayTeamGoalsAvg { get; set; }

        // Label: 0 = Away win, 1 = Draw, 2 = Home win
        [LoadColumn(6)]
        public float Result { get; set; }
    }
}