namespace SportMatchPredictor.ML;

public enum MatchResult : int
{
    AwayWin = 0,
    Draw = 1,
    HomeWin = 2
}

public static class MatchResultHelpers
{
    public static string ToShortLabel(int v) => v switch
    {
        (int)MatchResult.AwayWin => "A",
        (int)MatchResult.Draw => "D",
        (int)MatchResult.HomeWin => "H",
        _ => "?"
    };

    public static string ToUiText(int v) => v switch
    {
        (int)MatchResult.AwayWin => "Away win",
        (int)MatchResult.Draw => "Draw",
        (int)MatchResult.HomeWin => "Home win",
        _ => "Unknown"
    };
}
