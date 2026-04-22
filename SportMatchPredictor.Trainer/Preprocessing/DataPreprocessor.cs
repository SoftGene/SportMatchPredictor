using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace SportMatchPredictor.Trainer.Preprocessing;

public static class DataPreprocessor
{
    // Number of recent matches to consider for form
    private const int Window = 5;

    // Minimum number of matches required to compute features
    private const int MinHistory = 5;

    // Raw match record, sorted by date
    private sealed record RawMatch(
        DateTime Date,
        int HomeId,
        int AwayId,
        int HomeGoals,
        int AwayGoals,
        int LeagueId,
        string Season
    );

    public static void Run()
    {
        var root = Directory.GetCurrentDirectory();

        var rawDir = Path.Combine(root, "data", "raw");
        var processedDir = Path.Combine(root, "data", "processed");
        Directory.CreateDirectory(processedDir);

        var matchPath = Path.Combine(rawDir, "Match.csv");

        if (!File.Exists(matchPath))
            throw new FileNotFoundException($"File not found: {matchPath}");

        var outPath = Path.Combine(processedDir, "dataset.csv");

        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ",",
            HasHeaderRecord = true,
            BadDataFound = null,
            MissingFieldFound = null,
            HeaderValidated = null,
            IgnoreBlankLines = true,
            DetectColumnCountChanges = false
        };

        int written = 0;
        int skipped = 0;

        // 1) Load all matches into a list
        List<RawMatch> matches = new(capacity: 250_000);

        using (var reader = new StreamReader(matchPath))
        using (var csv = new CsvReader(reader, csvConfig))
        {
            csv.Read();
            csv.ReadHeader();
            var header = csv.HeaderRecord ?? Array.Empty<string>();

            int idxDate = GetIndex(header, "date");
            int idxHome = GetIndex(header, "home_team_api_id");
            int idxAway = GetIndex(header, "away_team_api_id");
            int idxHG = GetIndex(header, "home_team_goal");
            int idxAG = GetIndex(header, "away_team_goal");
            int idxLeague = GetIndex(header, "league_id");
            int idxSeason = GetIndex(header, "season");

            while (csv.Read())
            {
                var dateStr = csv.GetField(idxDate);
                var homeStr = csv.GetField(idxHome);
                var awayStr = csv.GetField(idxAway);
                var hgStr = csv.GetField(idxHG);
                var agStr = csv.GetField(idxAG);
                var leagueStr = csv.GetField(idxLeague);
                var seasonStr = csv.GetField(idxSeason);

                // Validate required fields
                if (!TryParseInt(homeStr, out int homeId) ||
                    !TryParseInt(awayStr, out int awayId) ||
                    !TryParseInt(hgStr, out int homeGoals) ||
                    !TryParseInt(agStr, out int awayGoals) ||
                    !TryParseInt(leagueStr, out int leagueId) ||
                    string.IsNullOrWhiteSpace(seasonStr))
                {
                    skipped++;
                    continue;
                }

                if (!DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                {
                    skipped++;
                    continue;
                }

                matches.Add(new RawMatch(
                    Date: dt,
                    HomeId: homeId,
                    AwayId: awayId,
                    HomeGoals: homeGoals,
                    AwayGoals: awayGoals,
                    LeagueId: leagueId,
                    Season: seasonStr
                ));
            }
        }

        // 2) Sort by date — critical for correct history tracking
        matches.Sort((a, b) => a.Date.CompareTo(b.Date));

        // 3) Team history: last N matches (points, goals)
        var history = new Dictionary<int, Queue<MatchStats>>();

        // 4) Write the output dataset
        using var outWriter = new StreamWriter(outPath);

        // Header: numeric features + label
        outWriter.WriteLine(string.Join(',',
            "LeagueId",
            "Season",
            "HomeAvgGoalsFor",
            "HomeAvgGoalsAgainst",
            "HomePointsPerGame",
            "HomeWinRate",
            "AwayAvgGoalsFor",
            "AwayAvgGoalsAgainst",
            "AwayPointsPerGame",
            "AwayWinRate",
            "AvgGoalsForDiff",
            "AvgGoalsAgainstDiff",
            "PointsPerGameDiff",
            "WinRateDiff",
            "GoalDiffDiff",
            "Result" // 0=AwayWin, 1=Draw, 2=HomeWin
        ));

        foreach (var m in matches)
        {
            int homeId = m.HomeId;
            int awayId = m.AwayId;
            int homeGoals = m.HomeGoals;
            int awayGoals = m.AwayGoals;
            int leagueId = m.LeagueId;
            string seasonStr = m.Season;

            EnsureTeam(history, homeId);
            EnsureTeam(history, awayId);

            // Features are computed using history BEFORE the current match
            var homeHist = history[homeId];
            var awayHist = history[awayId];

            // Both teams must have enough history; otherwise features would be noise
            if (homeHist.Count < MinHistory || awayHist.Count < MinHistory)
            {
                // После этого матча историю всё равно обновим
                UpdateHistory(history, homeId, awayId, homeGoals, awayGoals);
                skipped++;
                continue;
            }

            var hf = ComputeFeatures(homeHist);
            var af = ComputeFeatures(awayHist);

            var avgGoalsForDiff = hf.AvgGoalsFor - af.AvgGoalsFor;
            var avgGoalsAgainstDiff = hf.AvgGoalsAgainst - af.AvgGoalsAgainst;
            var pointsPerGameDiff = hf.PointsPerGame - af.PointsPerGame;
            var winRateDiff = hf.WinRate - af.WinRate;
            var goalDiffDiff = (hf.AvgGoalsFor - hf.AvgGoalsAgainst) - (af.AvgGoalsFor - af.AvgGoalsAgainst);

            int label = GetLabel(homeGoals, awayGoals); // 0=AwayWin, 1=Draw, 2=HomeWin

            outWriter.WriteLine(string.Join(',',
                leagueId.ToString(CultureInfo.InvariantCulture),
                EscapeSeason(seasonStr),
                hf.AvgGoalsFor.ToString(CultureInfo.InvariantCulture),
                hf.AvgGoalsAgainst.ToString(CultureInfo.InvariantCulture),
                hf.PointsPerGame.ToString(CultureInfo.InvariantCulture),
                hf.WinRate.ToString(CultureInfo.InvariantCulture),
                af.AvgGoalsFor.ToString(CultureInfo.InvariantCulture),
                af.AvgGoalsAgainst.ToString(CultureInfo.InvariantCulture),
                af.PointsPerGame.ToString(CultureInfo.InvariantCulture),
                af.WinRate.ToString(CultureInfo.InvariantCulture),
                avgGoalsForDiff.ToString(CultureInfo.InvariantCulture),
                avgGoalsAgainstDiff.ToString(CultureInfo.InvariantCulture),
                pointsPerGameDiff.ToString(CultureInfo.InvariantCulture),
                winRateDiff.ToString(CultureInfo.InvariantCulture),
                goalDiffDiff.ToString(CultureInfo.InvariantCulture),
                label.ToString(CultureInfo.InvariantCulture)
            ));

            written++;

            // Update history with the current match result
            UpdateHistory(history, homeId, awayId, homeGoals, awayGoals);
        }

        outWriter.Flush();

        Console.WriteLine($"Processed dataset created: {outPath}");
        Console.WriteLine($"Written rows: {written}");
        Console.WriteLine($"Skipped rows: {skipped}");
        Console.WriteLine($"Window={Window}, MinHistory={MinHistory}");
    }

    private static void UpdateHistory(Dictionary<int, Queue<MatchStats>> history, int homeId, int awayId, int hg, int ag)
    {
        int homePoints = hg > ag ? 3 : hg == ag ? 1 : 0;
        int awayPoints = ag > hg ? 3 : ag == hg ? 1 : 0;

        Enqueue(history[homeId], new MatchStats(hg, ag, homePoints));
        Enqueue(history[awayId], new MatchStats(ag, hg, awayPoints));
    }

    private static void EnsureTeam(Dictionary<int, Queue<MatchStats>> history, int teamId)
    {
        if (!history.ContainsKey(teamId))
            history[teamId] = new Queue<MatchStats>(Window);
    }

    private static void Enqueue(Queue<MatchStats> q, MatchStats s)
    {
        if (q.Count >= Window) q.Dequeue();
        q.Enqueue(s);
    }

    private static TeamFeatures ComputeFeatures(Queue<MatchStats> hist)
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

    private static int GetLabel(int homeGoals, int awayGoals)
        => homeGoals > awayGoals ? 2 : homeGoals == awayGoals ? 1 : 0;

    private static int GetIndex(string[] header, string name)
    {
        for (int i = 0; i < header.Length; i++)
        {
            if (string.Equals(header[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        throw new InvalidOperationException($"Column '{name}' not found in CSV header.");
    }

    private static bool TryParseInt(string? s, out int value)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string EscapeSeason(string season)
    {
        // Season looks like "2008/2009" — no commas, safe as a CSV text field
        return season.Replace(',', '_');
    }

    private readonly record struct MatchStats(int GoalsFor, int GoalsAgainst, int Points);
    private readonly record struct TeamFeatures(float AvgGoalsFor, float AvgGoalsAgainst, float PointsPerGame, float WinRate);
}
