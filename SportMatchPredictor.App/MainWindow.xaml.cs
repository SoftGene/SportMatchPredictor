using SportMatchPredictor.ML;
using SportMatchPredictor.ML.Data;
using SportMatchPredictor.ML.Services;
using System.IO;
using System.Windows;
using System.Windows.Media.Animation;

namespace SportMatchPredictor.App;

public partial class MainWindow : Window
{
    private List<TeamViewModel> _teams = new();
    private IReadOnlyList<RawMatchRecord> _matches = Array.Empty<RawMatchRecord>();

    private ModelService? _model;

    private static readonly string ApiKey = LoadApiKey();

    private static string LoadApiKey()
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keys.json");
            if (!File.Exists(path)) return string.Empty;
            var json = File.ReadAllText(path);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("ApiKey").GetString() ?? string.Empty;
        }
        catch { return string.Empty; }
    }
    private readonly Dictionary<int, string?> _logoCache = new();

    private string? GetLogoPath(int teamApiId)
        => _logoCache.TryGetValue(teamApiId, out var path) ? path : null;

    private readonly List<string> _history = new();

    private void AddToHistory(string home, string away, string season, int label, float confPct)
    {
        var outcome = label switch { 0 => "Away Win", 1 => "Draw", 2 => "Home Win", _ => "?" };
        var entry = $"{home} vs {away}  ·  {season}  ·  {outcome}  ·  {confPct:0.0}%";

        if (_history.Contains(entry))
            return;

        _history.Insert(0, entry);
        if (_history.Count > 5) _history.RemoveAt(5);
        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = _history;
    }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var appBase = AppDomain.CurrentDomain.BaseDirectory;
            var modelPath = Path.Combine(appBase, "Models", "model.zip");
            _model = new ModelService(modelPath);
            if (!File.Exists(modelPath))
            {
                StatusText.Text = "⚠ model.zip not found. Run Trainer first.";
                PredictBtn.IsEnabled = false;
                return;
            }

            var root = FindSolutionRoot(appBase);
            var rawDir = Path.Combine(root, "data", "raw");

            var teamCsv = Path.Combine(rawDir, "Team.csv");
            var matchCsv = Path.Combine(rawDir, "Match.csv");

            var rawTeams = FootballDataLoader.LoadTeams(teamCsv);
            _teams = rawTeams.Select(t => new TeamViewModel(t)).ToList();
            _matches = FootballDataLoader.LoadMatches(matchCsv);

            var seasons = FootballDataLoader.GetSeasons(_matches);
            SeasonCombo.ItemsSource = seasons;
            SeasonCombo.SelectedItem = seasons.LastOrDefault();

            HomeTeamCombo.ItemsSource = _teams;
            AwayTeamCombo.ItemsSource = _teams;
            HomeTeamCombo.SelectionChanged += (_, _) => ValidateSelection();
            AwayTeamCombo.SelectionChanged += (_, _) => ValidateSelection();

            StatusText.Text = $"Loaded: Teams={_teams.Count}, Matches={_matches.Count}.";

            var logosDir = Path.Combine(root, "data", "logos");
            _ = LoadLogosAsync(_teams.ToList(), logosDir);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Startup error: " + ex.Message;
            MessageBox.Show(ex.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadLogosAsync(List<TeamViewModel> teams, string logosDir)
    {
        foreach (var vm in teams)
        {
            var path = await SportMatchPredictor.ML.Services.TeamLogoService.GetLogoPathAsync(
                vm.TeamApiId, vm.TeamLongName, ApiKey, logosDir);
            _logoCache[vm.TeamApiId] = path;
            if (path is not null)
                Application.Current.Dispatcher.BeginInvoke(() => vm.LogoPath = path);
        }
    }

    private void ValidateSelection()
    {
        if (_matches.Count == 0) return;
        if (SeasonCombo.SelectedItem is not string season) return;
        if (HomeTeamCombo.SelectedItem is not TeamViewModel home) return;
        if (AwayTeamCombo.SelectedItem is not TeamViewModel away) return;

        if (home.TeamApiId == away.TeamApiId)
        {
            PredictBtn.IsEnabled = false;
            StatusText.Text = "⚠ Home and Away team must be different.";
            return;
        }

        try
        {
            FeatureBuilder.BuildFeatures(_matches, home.TeamApiId, away.TeamApiId, season);
            PredictBtn.IsEnabled = true;
            StatusText.Text = "Ready to predict.";
        }
        catch (Exception ex)
        {
            PredictBtn.IsEnabled = false;
            StatusText.Text = $"⚠ {ex.Message}";
        }
    }

    private void PredictBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_model is null) throw new InvalidOperationException("Model not loaded.");
            if (_matches.Count == 0) throw new InvalidOperationException("Matches not loaded.");
            if (SeasonCombo.SelectedItem is not string season) throw new InvalidOperationException("Select season.");

            if (HomeTeamCombo.SelectedItem is not TeamViewModel home)
                throw new InvalidOperationException("Select Home team.");
            if (AwayTeamCombo.SelectedItem is not TeamViewModel away)
                throw new InvalidOperationException("Select Away team.");

            if (home.TeamApiId == away.TeamApiId)
                throw new InvalidOperationException("Home and Away team must be different.");

            var features = FeatureBuilder.BuildFeatures(
                matchesSortedByDate: _matches,
                homeTeamApiId: home.TeamApiId,
                awayTeamApiId: away.TeamApiId,
                targetSeason: season
            );

            var pred = _model.Predict(features);

            int label = (int)pred.PredictedResult;
            PredictedLabelText.Text = MatchResultHelpers.ToUiText(label);
            PredictedDetailsText.Text = $"{home.TeamLongName} vs {away.TeamLongName} • Season {season} • LeagueId={features.LeagueId}";

            float confPct = 0f;
            if (pred.Score is { Length: > 0 })
            {
                var p = MathHelpers.Softmax(pred.Score); // [Away, Draw, Home]
                AwayBar.Value = 0;
                DrawBar.Value = 0;
                HomeBar.Value = 0;
                AnimateBar(AwayBar, p[0], 0.0);
                AnimateBar(DrawBar, p[1], 0.15);
                AnimateBar(HomeBar, p[2], 0.3);

                AwayPct.Text = $"{p[0] * 100:0.0}%";
                DrawPct.Text = $"{p[1] * 100:0.0}%";
                HomePct.Text = $"{p[2] * 100:0.0}%";
                confPct = p[label] * 100f;
                ConfidenceText.Text = $"{confPct:0.0}%";

                HighlightWinningBar(label);
            }
            else
            {
                AwayBar.Value = DrawBar.Value = HomeBar.Value = 0;
                AwayPct.Text = DrawPct.Text = HomePct.Text = ConfidenceText.Text = string.Empty;
            }

            AddToHistory(home.TeamLongName, away.TeamLongName, season, label, confPct);

            if (true)
            {
                FeaturesDebug.Text =
                    $"{"Metric",-22} {"Home",10} {"Away",10} {"Diff",10}\n" +
                    $"{new string('─', 54)}\n" +
                    $"{"Avg Goals For",-22} {features.HomeAvgGoalsFor,10:0.###} {features.AwayAvgGoalsFor,10:0.###} {features.AvgGoalsForDiff,10:0.###}\n" +
                    $"{"Avg Goals Against",-22} {features.HomeAvgGoalsAgainst,10:0.###} {features.AwayAvgGoalsAgainst,10:0.###} {features.AvgGoalsAgainstDiff,10:0.###}\n" +
                    $"{"Points Per Game",-22} {features.HomePointsPerGame,10:0.###} {features.AwayPointsPerGame,10:0.###} {features.PointsPerGameDiff,10:0.###}\n" +
                    $"{"Win Rate",-22} {features.HomeWinRate,10:0.###} {features.AwayWinRate,10:0.###} {features.WinRateDiff,10:0.###}\n" +
                    $"{new string('─', 54)}\n" +
                    $"{"LeagueId",-22} {features.LeagueId,10}\n" +
                    $"{"GoalDiffDiff",-22} {features.GoalDiffDiff,10:0.###}";
            }
            else
            {
                FeaturesDebug.Text = string.Empty;
            }

            StatusText.Text = "Prediction done.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Predict error: " + ex.Message;
            MessageBox.Show(ex.Message, "Predict error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

    }

    private void HighlightWinningBar(int label)
    {
        // сбрасываем все три в обычное состояние
        AwayBorder.BorderThickness = new Thickness(0);
        DrawBorder.BorderThickness = new Thickness(0);
        HomeBorder.BorderThickness = new Thickness(0);

        AwayBorder.Opacity = 0.6;
        DrawBorder.Opacity = 0.6;
        HomeBorder.Opacity = 0.6;

        // выделяем победителя
        var winner = label switch
        {
            0 => AwayBorder,
            1 => DrawBorder,
            2 => HomeBorder,
            _ => null
        };

        if (winner is not null)
        {
            winner.BorderThickness = new Thickness(2);
            winner.BorderBrush = label switch
            {
                0 => new System.Windows.Media.SolidColorBrush(
                         System.Windows.Media.Color.FromRgb(0xEF, 0x53, 0x50)),
                1 => new System.Windows.Media.SolidColorBrush(
                         System.Windows.Media.Color.FromRgb(0xFF, 0xA7, 0x26)),
                2 => new System.Windows.Media.SolidColorBrush(
                         System.Windows.Media.Color.FromRgb(0x66, 0xBB, 0x6A)),
                _ => System.Windows.Media.Brushes.Transparent
            };
            winner.Opacity = 1.0;
        }
    }

    private void AnimateBar(System.Windows.Controls.ProgressBar bar, double targetValue, double delaySeconds)
    {
        var animation = new DoubleAnimation
        {
            From = 0,
            To = targetValue,
            Duration = TimeSpan.FromMilliseconds(600),
            BeginTime = TimeSpan.FromSeconds(delaySeconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        bar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, animation);
    }

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        SeasonCombo.SelectedIndex = -1;
        HomeTeamCombo.SelectedIndex = -1;
        AwayTeamCombo.SelectedIndex = -1;
        StatusText.Text = string.Empty;
        PredictedLabelText.Text = "—";
        PredictedDetailsText.Text = string.Empty;
        ConfidenceText.Text = string.Empty;
        AwayBar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
        DrawBar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
        HomeBar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, null);
        AwayBar.Value = 0;
        AwayPct.Text = string.Empty;
        DrawBar.Value = 0;
        DrawPct.Text = string.Empty;
        HomeBar.Value = 0;
        HomePct.Text = string.Empty;
        FeaturesDebug.Text = string.Empty;
        PredictBtn.IsEnabled = false;
        AwayBorder.BorderThickness = new Thickness(0);
        DrawBorder.BorderThickness = new Thickness(0);
        HomeBorder.BorderThickness = new Thickness(0);
        AwayBorder.Opacity = 1.0;
        DrawBorder.Opacity = 1.0;
        HomeBorder.Opacity = 1.0;
    }

    private static string FindSolutionRoot(string appBaseDir)
    {
        var dir = new DirectoryInfo(appBaseDir);
        for (int i = 0; i < 6 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "data", "raw", "Match.csv");
            if (File.Exists(candidate))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }

    private void SeasonCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ValidateSelection();
    }
}
