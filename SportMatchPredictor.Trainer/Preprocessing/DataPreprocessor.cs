using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;

namespace SportMatchPredictor.Trainer.Preprocessing;

public static class DataPreprocessor
{
    // Сколько последних матчей учитывать для "формы"
    private const int Window = 5;

    // Минимум матчей в истории команды, чтобы строить признаки
    private const int MinHistory = 5;

    public static void Run()
    {
        var root = Directory.GetCurrentDirectory();

        var rawDir = Path.Combine(root, "data", "raw");
        var processedDir = Path.Combine(root, "data", "processed");
        Directory.CreateDirectory(processedDir);

        var matchPath = Path.Combine(rawDir, "Match.csv");


        if (!File.Exists(matchPath))
            throw new FileNotFoundException($"Не найден файл: {matchPath}");

        var outPath = Path.Combine(processedDir, "dataset.csv");

        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ",",
            HasHeaderRecord = true,
            BadDataFound = null,          // не падаем на странных данных
            MissingFieldFound = null,     // не падаем на пропусках
            HeaderValidated = null,
            IgnoreBlankLines = true,
            DetectColumnCountChanges = false
        };

        // История команд: последние N матчей (очки, голы)
        var history = new Dictionary<int, Queue<MatchStats>>();

        int written = 0;
        int skipped = 0;

        using var reader = new StreamReader(matchPath);
        using var csv = new CsvReader(reader, csvConfig);

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

        using var outWriter = new StreamWriter(outPath);
        // Финальный датасет (только числовые признаки + label)
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

        while (csv.Read())
        {
            // Достаём нужные поля безопасно (string может быть пустым)
            var dateStr = csv.GetField(idxDate);
            var homeStr = csv.GetField(idxHome);
            var awayStr = csv.GetField(idxAway);
            var hgStr = csv.GetField(idxHG);
            var agStr = csv.GetField(idxAG);
            var leagueStr = csv.GetField(idxLeague);
            var seasonStr = csv.GetField(idxSeason);

            // Базовые проверки на пустые значения
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

            // date в твоём датасете обычно: "2008-08-17 00:00:00"
            if (!DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out _))
            {
                // дату нам сейчас не обязательно писать в итоговый датасет,
                // но валидация полезна.
                skipped++;
                continue;
            }

            EnsureTeam(history, homeId);
            EnsureTeam(history, awayId);

            // Признаки считаем ТОЛЬКО из истории ДО текущего матча
            var homeHist = history[homeId];
            var awayHist = history[awayId];

            // Требуем минимальную историю у обеих команд, иначе признаки будут мусорные
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

            int label = GetLabel(homeGoals, awayGoals); // 0/1/2

            // Пишем строку
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

            // После вычисления признаков обновляем историю текущим матчем
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
        throw new InvalidOperationException($"Колонка '{name}' не найдена в CSV заголовке.");
    }

    private static bool TryParseInt(string? s, out int value)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string EscapeSeason(string season)
    {
        // season выглядит как "2008/2009" — оставим как текст (без запятых), в CSV это ок
        // но ML.NET проще если это будет категориальная строка или мы позже уберём сезон.
        return season.Replace(',', '_');
    }

    private readonly record struct MatchStats(int GoalsFor, int GoalsAgainst, int Points);
    private readonly record struct TeamFeatures(float AvgGoalsFor, float AvgGoalsAgainst, float PointsPerGame, float WinRate);
}
