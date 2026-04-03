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

    private ModelService? _model;

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

            _teams = FootballDataLoader.LoadTeams(teamCsv);
            _matches = FootballDataLoader.LoadMatches(matchCsv);

            var seasons = FootballDataLoader.GetSeasons(_matches);
            SeasonCombo.ItemsSource = seasons;
            SeasonCombo.SelectedItem = seasons.LastOrDefault();

            HomeTeamCombo.ItemsSource = _teams;
            AwayTeamCombo.ItemsSource = _teams;
            HomeTeamCombo.DisplayMemberPath = nameof(TeamRecord.TeamLongName);
            AwayTeamCombo.DisplayMemberPath = nameof(TeamRecord.TeamLongName);
            HomeTeamCombo.SelectionChanged += (_, _) => ValidateSelection();
            AwayTeamCombo.SelectionChanged += (_, _) => ValidateSelection();

            StatusText.Text = $"Loaded: Teams={_teams.Count}, Matches={_matches.Count}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Startup error: " + ex.Message;
            MessageBox.Show(ex.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ValidateSelection()
    {
        if (_matches.Count == 0) return;
        if (SeasonCombo.SelectedItem is not string season) return;
        if (HomeTeamCombo.SelectedItem is not TeamRecord home) return;
        if (AwayTeamCombo.SelectedItem is not TeamRecord away) return;

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

            if (HomeTeamCombo.SelectedItem is not TeamRecord home)
                throw new InvalidOperationException("Select Home team.");
            if (AwayTeamCombo.SelectedItem is not TeamRecord away)
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

            if (pred.Score is { Length: > 0 })
            {
                var p = MathHelpers.Softmax(pred.Score); // [Away, Draw, Home]
                AwayBar.Value = p[0];
                DrawBar.Value = p[1];
                HomeBar.Value = p[2];

                AwayPct.Text = $"{p[0] * 100:0.0}%";
                DrawPct.Text = $"{p[1] * 100:0.0}%";
                HomePct.Text = $"{p[2] * 100:0.0}%";
                ConfidenceText.Text = $"{p[label] * 100:0.0}%";
            }
            else
            {
                AwayBar.Value = DrawBar.Value = HomeBar.Value = 0;
                AwayPct.Text = DrawPct.Text = HomePct.Text = ConfidenceText.Text = string.Empty;
            }

            if (IncludeFeatureSummary.IsChecked == true)
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

    private void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        SeasonCombo.SelectedIndex = -1;
        HomeTeamCombo.SelectedIndex = -1;
        AwayTeamCombo.SelectedIndex = -1;
        StatusText.Text = string.Empty;
        PredictedLabelText.Text = "—";
        PredictedDetailsText.Text = string.Empty;
        ConfidenceText.Text = string.Empty;
        AwayBar.Value = 0;
        AwayPct.Text = string.Empty;
        DrawBar.Value = 0;
        DrawPct.Text = string.Empty;
        HomeBar.Value = 0;
        HomePct.Text = string.Empty;
        FeaturesDebug.Text = string.Empty;
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

    private void IncludeFeatureSummary_Checked(object sender, RoutedEventArgs e)
    {

    }

    private void SeasonCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ValidateSelection();
    }
}
