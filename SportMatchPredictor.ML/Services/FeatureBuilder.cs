using SportMatchPredictor.ML.Data;

namespace SportMatchPredictor.ML.Services;

public static class FeatureBuilder
{
    // Должно совпадать с тем, что было в DataPreprocessor
    private const int Window = 5;
    private const int MinHistory = 5;

    private readonly record struct MatchStats(int GoalsFor, int GoalsAgainst, int Points);
    private readonly record struct TeamFeatures(float AvgGoalsFor, float AvgGoalsAgainst, float PointsPerGame, float WinRate);

    public static MatchData BuildFeatures(
        IReadOnlyList<RawMatchRecord> matchesSortedByDate,
        int homeTeamApiId,
        int awayTeamApiId,
        string targetSeason)
    {
        if (homeTeamApiId == awayTeamApiId)
            throw new InvalidOperationException("Home team and Away team must be different.");

        var cutoff = FootballDataLoader.GetSeasonStart(matchesSortedByDate, targetSeason);

        var homeHist = GetLastNTeamMatches(matchesSortedByDate, homeTeamApiId, cutoff, Window);
        var awayHist = GetLastNTeamMatches(matchesSortedByDate, awayTeamApiId, cutoff, Window);

        if (homeHist.Count < MinHistory || awayHist.Count < MinHistory)
            throw new InvalidOperationException($"Not enough history. Need at least {MinHistory} matches for each team before {targetSeason}.");

        var hf = ComputeFeatures(homeHist);
        var af = ComputeFeatures(awayHist);

        // diff
        var avgGoalsForDiff = hf.AvgGoalsFor - af.AvgGoalsFor;
        var avgGoalsAgainstDiff = hf.AvgGoalsAgainst - af.AvgGoalsAgainst;
        var pointsPerGameDiff = hf.PointsPerGame - af.PointsPerGame;
        var winRateDiff = hf.WinRate - af.WinRate;
        var goalDiffDiff = (hf.AvgGoalsFor - hf.AvgGoalsAgainst) - (af.AvgGoalsFor - af.AvgGoalsAgainst);

        var leagueId = InferLeagueId(matchesSortedByDate, homeTeamApiId, cutoff);

        return new MatchData
        {
            LeagueId = leagueId,
            Season = targetSeason,

            HomeAvgGoalsFor = hf.AvgGoalsFor,
            HomeAvgGoalsAgainst = hf.AvgGoalsAgainst,
            HomePointsPerGame = hf.PointsPerGame,
            HomeWinRate = hf.WinRate,

            AwayAvgGoalsFor = af.AvgGoalsFor,
            AwayAvgGoalsAgainst = af.AvgGoalsAgainst,
            AwayPointsPerGame = af.PointsPerGame,
            AwayWinRate = af.WinRate,

            AvgGoalsForDiff = avgGoalsForDiff,
            AvgGoalsAgainstDiff = avgGoalsAgainstDiff,
            PointsPerGameDiff = pointsPerGameDiff,
            WinRateDiff = winRateDiff,
            GoalDiffDiff = goalDiffDiff,

            Result = 0 // для Predict не важно
        };
    }

    private static int InferLeagueId(IReadOnlyList<RawMatchRecord> matches, int teamId, DateTime cutoff)
    {
        // последние 30 матчей команды до cutoff
        var last = matches
            .Where(m => m.Date < cutoff && (m.HomeTeamApiId == teamId || m.AwayTeamApiId == teamId))
            .TakeLast(30)
            .ToList();

        if (last.Count == 0)
            return 0;

        // самая частая лига
        return last
            .GroupBy(m => m.LeagueId)
            .OrderByDescending(g => g.Count())
            .First().Key;
    }

    private static List<MatchStats> GetLastNTeamMatches(IReadOnlyList<RawMatchRecord> matches, int teamId, DateTime cutoff, int n)
    {
        var filtered = matches
            .Where(m => m.Date < cutoff && (m.HomeTeamApiId == teamId || m.AwayTeamApiId == teamId))
            .ToList();

        // берем последние n
        var lastN = filtered.Count <= n ? filtered : filtered.GetRange(filtered.Count - n, n);

        var stats = new List<MatchStats>(lastN.Count);

        foreach (var m in lastN)
        {
            bool isHome = m.HomeTeamApiId == teamId;

            int gf = isHome ? m.HomeGoals : m.AwayGoals;
            int ga = isHome ? m.AwayGoals : m.HomeGoals;

            int points = gf > ga ? 3 : gf == ga ? 1 : 0;
            stats.Add(new MatchStats(gf, ga, points));
        }

        return stats;
    }

    private static TeamFeatures ComputeFeatures(List<MatchStats> hist)
    {
        float games = hist.Count;
        float goalsFor = 0, goalsAgainst = 0, points = 0, wins = 0;

        foreach (var m in hist)
        {
            goalsFor += m.GoalsFor;
            goalsAgainst += m.GoalsAgainst;
            points += m.Points;
            if (m.Points == 3) wins += 1;
        }

        return new TeamFeatures(
            goalsFor / games,
            goalsAgainst / games,
            points / games,
            wins / games
        );
    }
}
