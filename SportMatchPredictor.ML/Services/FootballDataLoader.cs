using CsvHelper;
using CsvHelper.Configuration;
using System.Formats.Asn1;
using System.Globalization;

namespace SportMatchPredictor.ML.Services;

public static class FootballDataLoader
{
    public static IReadOnlyList<TeamRecord> LoadTeams(string teamCsvPath)
    {
        if (!File.Exists(teamCsvPath))
            throw new FileNotFoundException($"Team.csv not found: {teamCsvPath}");

        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ",",
            HasHeaderRecord = true,
            BadDataFound = null,
            MissingFieldFound = null,
            HeaderValidated = null,
            IgnoreBlankLines = true
        };

        using var reader = new StreamReader(teamCsvPath);
        using var csv = new CsvReader(reader, cfg);

        csv.Read();
        csv.ReadHeader();
        var header = csv.HeaderRecord ?? Array.Empty<string>();

        int idxApi = GetIndex(header, "team_api_id");
        int idxLong = GetIndex(header, "team_long_name");
        int idxShort = GetIndex(header, "team_short_name");

        var list = new List<TeamRecord>();

        while (csv.Read())
        {
            var apiStr = csv.GetField(idxApi);
            var longName = csv.GetField(idxLong) ?? "";
            var shortName = csv.GetField(idxShort) ?? "";

            if (!int.TryParse(apiStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var apiId))
                continue;

            if (string.IsNullOrWhiteSpace(longName))
                continue;

            list.Add(new TeamRecord(apiId, longName.Trim(), shortName.Trim()));
        }

        return list
            .GroupBy(t => t.TeamApiId)
            .Select(g => g.First())
            .OrderBy(t => t.TeamLongName)
            .ToList();
    }

    public static IReadOnlyList<RawMatchRecord> LoadMatches(string matchCsvPath)
    {
        if (!File.Exists(matchCsvPath))
            throw new FileNotFoundException($"Match.csv not found: {matchCsvPath}");

        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ",",
            HasHeaderRecord = true,
            BadDataFound = null,
            MissingFieldFound = null,
            HeaderValidated = null,
            IgnoreBlankLines = true,
            DetectColumnCountChanges = false
        };

        using var reader = new StreamReader(matchCsvPath);
        using var csv = new CsvReader(reader, cfg);

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

        var list = new List<RawMatchRecord>(capacity: 250_000);

        while (csv.Read())
        {
            var dateStr = csv.GetField(idxDate);
            var homeStr = csv.GetField(idxHome);
            var awayStr = csv.GetField(idxAway);
            var hgStr = csv.GetField(idxHG);
            var agStr = csv.GetField(idxAG);
            var leagueStr = csv.GetField(idxLeague);
            var seasonStr = csv.GetField(idxSeason);

            if (!int.TryParse(homeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var homeId)) continue;
            if (!int.TryParse(awayStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var awayId)) continue;
            if (!int.TryParse(hgStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hg)) continue;
            if (!int.TryParse(agStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ag)) continue;
            if (!int.TryParse(leagueStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var leagueId)) continue;
            if (string.IsNullOrWhiteSpace(seasonStr)) continue;

            if (!DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
                continue;

            list.Add(new RawMatchRecord(dt, homeId, awayId, hg, ag, leagueId, seasonStr.Trim()));
        }

        list.Sort((a, b) => a.Date.CompareTo(b.Date));
        return list;
    }

    public static IReadOnlyList<string> GetSeasons(IReadOnlyList<RawMatchRecord> matches)
        => matches.Select(m => m.Season).Distinct().OrderBy(s => s).ToList();

    public static DateTime GetSeasonStart(IReadOnlyList<RawMatchRecord> matches, string season)
    {
        var seasonMatches = matches.Where(m => m.Season == season).ToList();
        if (seasonMatches.Count == 0)
            throw new InvalidOperationException($"No matches found for season {season}");

        return seasonMatches.Min(m => m.Date);
    }

    private static int GetIndex(string[] header, string name)
    {
        for (int i = 0; i < header.Length; i++)
            if (string.Equals(header[i], name, StringComparison.OrdinalIgnoreCase))
                return i;

        throw new InvalidOperationException($"Column '{name}' not found in CSV header.");
    }
}
