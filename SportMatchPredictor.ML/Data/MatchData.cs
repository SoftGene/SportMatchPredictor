using Microsoft.ML.Data;

namespace SportMatchPredictor.ML.Data;

public class MatchData
{
    [LoadColumn(0)]
    public float LeagueId { get; set; }

    [LoadColumn(1)]
    public string Season { get; set; } = string.Empty;

    [LoadColumn(2)]
    public float HomeAvgGoalsFor { get; set; }

    [LoadColumn(3)]
    public float HomeAvgGoalsAgainst { get; set; }

    [LoadColumn(4)]
    public float HomePointsPerGame { get; set; }

    [LoadColumn(5)]
    public float HomeWinRate { get; set; }

    [LoadColumn(6)]
    public float AwayAvgGoalsFor { get; set; }

    [LoadColumn(7)]
    public float AwayAvgGoalsAgainst { get; set; }

    [LoadColumn(8)]
    public float AwayPointsPerGame { get; set; }

    [LoadColumn(9)]
    public float AwayWinRate { get; set; }

    [LoadColumn(10)]
    public float AvgGoalsForDiff { get; set; }

    [LoadColumn(11)]
    public float AvgGoalsAgainstDiff { get; set; }

    [LoadColumn(12)]
    public float PointsPerGameDiff { get; set; }

    [LoadColumn(13)]
    public float WinRateDiff { get; set; }

    [LoadColumn(14)]
    public float GoalDiffDiff { get; set; }

    [LoadColumn(15)]
    public float Result { get; set; } // 0/1/2
}
