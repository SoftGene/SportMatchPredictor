namespace SportMatchPredictor.ML.Services;

public sealed record RawMatchRecord(
    DateTime Date,
    int HomeTeamApiId,
    int AwayTeamApiId,
    int HomeGoals,
    int AwayGoals,
    int LeagueId,
    string Season
);
