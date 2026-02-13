using SportMatchPredictor.ML;
using SportMatchPredictor.ML.Data;
using SportMatchPredictor.ML.Services;
using System.IO;
using System.Windows;

namespace SportMatchPredictor.App;

public partial class MainWindow : Window
{
    private IReadOnlyList<TeamRecord> _teams = Array.Empty<TeamRecord>();
    private IReadOnlyList<RawMatchRecord> _matches = Array.Empty<RawMatchRecord>();
    private Dictionary<int, TeamRecord> _teamById = new();

    private SportMatchPredictor.ML.Services.ModelService? _model;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // Пути: WPF запускается из bin/... поэтому идём на уровень выше к корню решения
            // Самый надёжный способ для диплома: копировать data/raw и Models/model.zip в output.
            // Сейчас сделаем так: data/raw читаем из корня решения (2-3 уровнем выше).
            // Если захочешь — позже перенесём raw данные в App как Content.

            var appBase = AppDomain.CurrentDomain.BaseDirectory;

            // model.zip должен быть в App/Models и копироваться в output
            var modelPath = Path.Combine(appBase, "Models", "model.zip");
            _model = new SportMatchPredictor.ML.Services.ModelService(modelPath);

            // raw data читаем из папки рядом с .slnx: N:\dissertation\SportMatchPredictor\data\raw
            // Поднимемся на 3 уровня: bin/Debug/net8.0-windows -> проект -> ...
            var root = FindSolutionRoot(appBase);
            var rawDir = Path.Combine(root, "data", "raw");

            var teamCsv = Path.Combine(rawDir, "Team.csv");
            var matchCsv = Path.Combine(rawDir, "Match.csv");

            _teams = FootballDataLoader.LoadTeams(teamCsv);
            _matches = FootballDataLoader.LoadMatches(matchCsv);
            _teamById = _teams.ToDictionary(t => t.TeamApiId, t => t);

            // UI: заполнить сезоны и команды
            var seasons = FootballDataLoader.GetSeasons(_matches);
            SeasonCombo.ItemsSource = seasons;
            SeasonCombo.SelectedItem = seasons.LastOrDefault();

            HomeTeamCombo.ItemsSource = _teams;
            AwayTeamCombo.ItemsSource = _teams;

            HomeTeamCombo.DisplayMemberPath = nameof(TeamRecord.TeamLongName);
            AwayTeamCombo.DisplayMemberPath = nameof(TeamRecord.TeamLongName);

            StatusText.Text = $"Loaded: Teams={_teams.Count}, Matches={_matches.Count}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Startup error: " + ex.Message;
            MessageBox.Show(ex.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void PredictBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_model is null) throw new InvalidOperationException("Model not loaded.");
            if (_matches.Count == 0) throw new InvalidOperationException("Matches not loaded.");
            if (SeasonCombo.SelectedItem is not string season) throw new InvalidOperationException("Select season.");

            // selected teams
            if (HomeTeamCombo.SelectedItem is not TeamRecord home)
                throw new InvalidOperationException("Select Home team.");
            if (AwayTeamCombo.SelectedItem is not TeamRecord away)
                throw new InvalidOperationException("Select Away team.");

            if (home.TeamApiId == away.TeamApiId)
                throw new InvalidOperationException("Home and Away team must be different.");

            // Build features from historical matches (time-aware)
            MatchData features = FeatureBuilder.BuildFeatures(
                matchesSortedByDate: _matches,
                homeTeamApiId: home.TeamApiId,
                awayTeamApiId: away.TeamApiId,
                targetSeason: season
            );

            // Predict
            var pred = _model.Predict(features);

            // Map label to UI
            int label = (int)pred.PredictedResult;
            PredictedLabelText.Text = MatchResultHelpers.ToUiText(label);
            PredictedDetailsText.Text = $"{home.TeamLongName} vs {away.TeamLongName} • Season {season} • LeagueId={features.LeagueId}";

            // Probabilities
            if (pred.Score is { Length: > 0 })
            {
                var p = MathHelpers.Softmax(pred.Score); // [Away, Draw, Home] — same mapping as your GetLabel

                AwayBar.Value = p[0];
                DrawBar.Value = p[1];
                HomeBar.Value = p[2];

                AwayPct.Text = $"{p[0] * 100:0.0}%";
                DrawPct.Text = $"{p[1] * 100:0.0}%";
                HomePct.Text = $"{p[2] * 100:0.0}%";
            }

            // Debug features
            FeaturesDebug.Text =
                $"LeagueId: {features.LeagueId}\n" +
                $"Home AvgGF: {features.HomeAvgGoalsFor:0.###} | AvgGA: {features.HomeAvgGoalsAgainst:0.###} | PPG: {features.HomePointsPerGame:0.###} | WR: {features.HomeWinRate:0.###}\n" +
                $"Away AvgGF: {features.AwayAvgGoalsFor:0.###} | AvgGA: {features.AwayAvgGoalsAgainst:0.###} | PPG: {features.AwayPointsPerGame:0.###} | WR: {features.AwayWinRate:0.###}\n" +
                $"Diff GF: {features.AvgGoalsForDiff:0.###} | Diff GA: {features.AvgGoalsAgainstDiff:0.###} | Diff PPG: {features.PointsPerGameDiff:0.###} | Diff WR: {features.WinRateDiff:0.###}\n" +
                $"GoalDiffDiff: {features.GoalDiffDiff:0.###}";

            StatusText.Text = "Prediction done.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Predict error: " + ex.Message;
            MessageBox.Show(ex.Message, "Predict error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string FindSolutionRoot(string appBaseDir)
    {
        // Ищем папку, где есть "data/raw/Match.csv"
        var dir = new DirectoryInfo(appBaseDir);

        for (int i = 0; i < 6 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "data", "raw", "Match.csv");
            if (File.Exists(candidate))
                return dir.FullName;

            dir = dir.Parent;
        }

        // fallback: текущая папка
        return Directory.GetCurrentDirectory();
    }
}