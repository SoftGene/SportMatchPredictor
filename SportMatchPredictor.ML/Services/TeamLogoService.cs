using System.Net.Http;
using System.Text.Json;

namespace SportMatchPredictor.ML.Services;

public static class TeamLogoService
{
    private static readonly HttpClient _http = new();

    private static readonly string[] Prefixes =
    [
        "KRC ", "KAA ", "BSC ", "RCD ", "RSC ", "PSV ", "PEC ", "NAC ", "NEC ",
        "SpVgg ", "TSG ", "VfL ", "VfB ", "GKS ", "AZ ", "SV ", "AS ", "US ",
        "KV ", "FC ", "AC ", "SC ", "RC ", "SD ", "UD ", "CD ", "CF "
    ];

    private static readonly string[] Suffixes =
    [
        " FC", " CF", " AC", " SC", " FK", " SK", " BK", " IF"
    ];

    private static List<string> GetVariants(string teamName)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var variants = new List<string>();

        void Add(string s)
        {
            s = s.Trim();
            if (!string.IsNullOrWhiteSpace(s) && seen.Add(s))
                variants.Add(s);
        }

        Add(teamName);

        var stripped = teamName;
        foreach (var p in Prefixes)
        {
            if (teamName.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            {
                stripped = teamName[p.Length..];
                break;
            }
        }
        Add(stripped);

        var strippedSuffix = stripped;
        foreach (var s in Suffixes)
        {
            if (stripped.EndsWith(s, StringComparison.OrdinalIgnoreCase))
            {
                strippedSuffix = stripped[..^s.Length];
                break;
            }
        }
        Add(strippedSuffix);

        var words = strippedSuffix.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && words[0].Length > 4)
            Add(words[0]);
        if (words.Length >= 2)
            Add(string.Join(' ', words[0], words[1]));

        return variants;
    }

    private static async Task<string?> FetchBadgeUrlAsync(string query)
    {
        var url = $"https://www.thesportsdb.com/api/v1/json/3/searchteams.php?t={Uri.EscapeDataString(query)}";
        var json = await _http.GetStringAsync(url);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("teams", out var teams) || teams.ValueKind != JsonValueKind.Array)
            return null;
        if (teams.GetArrayLength() == 0)
            return null;
        return teams[0].GetProperty("strBadge").GetString();
    }

    public static async Task<string?> GetLogoPathAsync(
        int teamApiId, string teamName, string apiKey, string logosDir)
    {
        try
        {
            var localPath = Path.Combine(logosDir, $"{teamApiId}.png");
            if (File.Exists(localPath))
                return localPath;

            Directory.CreateDirectory(logosDir);

            string? logoUrl = null;
            var variants = GetVariants(teamName);

            for (int i = 0; i < variants.Count; i++)
            {
                if (i > 0)
                    await Task.Delay(200);

                logoUrl = await FetchBadgeUrlAsync(variants[i]);
                if (!string.IsNullOrEmpty(logoUrl))
                    break;
            }

            if (string.IsNullOrEmpty(logoUrl)) return null;

            var bytes = await _http.GetByteArrayAsync(logoUrl);
            await File.WriteAllBytesAsync(localPath, bytes);
            return localPath;
        }
        catch
        {
            return null;
        }
    }
}
