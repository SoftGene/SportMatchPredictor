using System.Net.Http;
using System.Text.Json;

namespace SportMatchPredictor.ML.Services;

public static class TeamLogoService
{
    private static readonly HttpClient _http = new();

    private static string SanitizeTeamName(string name)
    {
        // замен€ем умл€уты и акценты на базовые буквы
        var normalized = name.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var c in normalized)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            // оставл€ем только буквы, цифры и пробелы
            if (char.IsLetterOrDigit(c) || c == ' ')
                sb.Append(c);
        }
        // убираем двойные пробелы
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString().Trim(), @"\s+", " ");
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

            var sanitized = SanitizeTeamName(teamName);
            if (string.IsNullOrWhiteSpace(sanitized)) return null;

            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://v3.football.api-sports.io/teams?search={Uri.EscapeDataString(sanitized)}");
            request.Headers.Add("x-apisports-key", apiKey);

            var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();

            // временный лог
            await File.AppendAllTextAsync(
                Path.Combine(logosDir, "api_log.txt"),
                $"\n\n=== {teamName} -> [{sanitized}] ===\n{json}");

            using var doc = JsonDocument.Parse(json);
            var results = doc.RootElement.GetProperty("response");
            if (results.GetArrayLength() == 0) return null;

            var logoUrl = results[0]
                .GetProperty("team")
                .GetProperty("logo")
                .GetString();

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
