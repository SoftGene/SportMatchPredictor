using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MaterialDesignThemes.Wpf;
using SportMatchPredictor.ML;
using SportMatchPredictor.ML.Data;
using SportMatchPredictor.ML.Services;

namespace SportMatchPredictor.App;

public partial class MainWindow : Window
{
    private List<TeamViewModel> _teams = new();
    private IReadOnlyList<RawMatchRecord> _matches = Array.Empty<RawMatchRecord>();
    private IReadOnlyList<string> _seasons = Array.Empty<string>();
    private bool _isDarkTheme = false;
    private ModelService? _model;
    private static readonly string ApiKey = LoadApiKey();
    private readonly Dictionary<int, string?> _logoCache = new();
    private readonly List<HistoryEntry> _history = new();
    private System.Windows.Data.ListCollectionView? _homeView;
    private System.Windows.Data.ListCollectionView? _awayView;

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
            _seasons = seasons;
            SeasonCombo.ItemsSource = seasons;
            SeasonCombo.SelectedItem = seasons.LastOrDefault();

            // Independent views so filtering Home's list doesn't affect Away's list
            _homeView = new System.Windows.Data.ListCollectionView(_teams);
            _awayView = new System.Windows.Data.ListCollectionView(_teams);
            HomeTeamCombo.ItemsSource = _homeView;
            AwayTeamCombo.ItemsSource = _awayView;
            HomeTeamCombo.SelectionChanged += (_, _) => ValidateSelection();
            AwayTeamCombo.SelectionChanged += (_, _) => ValidateSelection();

            QuickStatsText.Text = $"Trained on {_matches.Count:N0} matches  ·  {_teams.Count} European teams  ·  14 predictive features";

            var missingLogosDir = Path.Combine(FindSolutionRoot(AppDomain.CurrentDomain.BaseDirectory), "data", "logos");
            var notFound = _teams
                .Where(t => !File.Exists(Path.Combine(missingLogosDir, $"{t.TeamApiId}.png")))
                .Select(t => $"{t.TeamApiId} - {t.TeamLongName}")
                .ToList();
            File.WriteAllLines(Path.Combine(missingLogosDir, "not_found.txt"), notFound);

            var logosDir = Path.Combine(root, "data", "logos");
            _ = LoadLogosAsync(_teams.ToList(), logosDir);
        }
        catch (Exception ex)
        {
            StatusText.Text = "Startup error: " + ex.Message;
            MessageBox.Show(ex.ToString(), "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        ShowResult(false);
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

    private void SeasonCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ValidateSelection();
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

                confPct = p[label] * 100f;
                AwayPct.Text = "0.0%";
                DrawPct.Text = "0.0%";
                HomePct.Text = "0.0%";
                ConfidenceText.Text = "0.0%";
                AnimatePct(AwayPct, p[0] * 100, 0.0);
                AnimatePct(DrawPct, p[1] * 100, 0.15);
                AnimatePct(HomePct, p[2] * 100, 0.3);
                AnimatePct(ConfidenceText, confPct, 0.0);

                HighlightWinningBar(label);
                ShowResult(true);
            }
            else
            {
                AwayBar.Value = DrawBar.Value = HomeBar.Value = 0;
                AwayPct.Text = DrawPct.Text = HomePct.Text = ConfidenceText.Text = string.Empty;
            }

            AddToHistory(home, away, season, label, confPct);

            // HomeWin (2): HomeLogo left, others hidden
            // AwayWin (0): AwayLogo left, others hidden
            // Draw (1): HomeLogo left, AwayLogoRight right
            HomeLogo.Source = (label == 2 || label == 1) && home.LogoPath is not null
                ? new BitmapImage(new Uri(home.LogoPath))
                : null;
            AwayLogo.Source = label == 0 && away.LogoPath is not null
                ? new BitmapImage(new Uri(away.LogoPath))
                : null;
            AwayLogoRight.Source = label == 1 && away.LogoPath is not null
                ? new BitmapImage(new Uri(away.LogoPath))
                : null;

            HomeLogo.Visibility = HomeLogo.Source is not null ? Visibility.Visible : Visibility.Collapsed;
            AwayLogo.Visibility = AwayLogo.Source is not null ? Visibility.Visible : Visibility.Collapsed;
            AwayLogoRight.Visibility = AwayLogoRight.Source is not null ? Visibility.Visible : Visibility.Collapsed;

            var rows = BuildFeatureRows(features);
            var view = new System.Windows.Data.ListCollectionView(rows);
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(FeatureRow.Category)));
            FeaturesGrid.ItemsSource = view;
            FeaturesDebug.Text = string.Empty;

            StatusText.Text = "Prediction done.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Predict error: " + ex.Message;
            MessageBox.Show(ex.Message, "Predict error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        if (HeroBlock.Visibility == Visibility.Visible)
        {
            var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            HeroBlock.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            ProbabilityBlock.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            TabBlock.BeginAnimation(UIElement.OpacityProperty, fadeOut);

            await Task.Delay(200);
        }

        SeasonCombo.SelectedIndex = -1;
        HomeTeamCombo.SelectedIndex = -1;
        AwayTeamCombo.SelectedIndex = -1;
        StatusText.Text = string.Empty;
        PredictedLabelText.Text = "—";
        PredictedDetailsText.Text = string.Empty;
        ConfidenceText.Text = string.Empty;
        AwayBar.BeginAnimation(ProgressBar.ValueProperty, null);
        DrawBar.BeginAnimation(ProgressBar.ValueProperty, null);
        HomeBar.BeginAnimation(ProgressBar.ValueProperty, null);
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
        HomeLogo.Source = null;
        AwayLogo.Source = null;
        AwayLogoRight.Source = null;
        HomeLogo.Visibility = Visibility.Collapsed;
        AwayLogo.Visibility = Visibility.Collapsed;
        AwayLogoRight.Visibility = Visibility.Collapsed;
        // Reset team cards
        HomeEmptyState.Visibility = Visibility.Visible;
        HomeSelectedState.Visibility = Visibility.Collapsed;
        AwayEmptyState.Visibility = Visibility.Visible;
        AwaySelectedState.Visibility = Visibility.Collapsed;
        HomeLogoCard.Source = null;
        AwayLogoCard.Source = null;
        ShowResult(false);
    }

    private void SwapBtn_Click(object sender, RoutedEventArgs e)
    {
        var home = HomeTeamCombo.SelectedItem as TeamViewModel;
        var away = AwayTeamCombo.SelectedItem as TeamViewModel;
        HomeTeamCombo.SelectedItem = away;
        AwayTeamCombo.SelectedItem = home;

        if (away is not null) UpdateTeamCard(away, isHome: true);
        else { HomeEmptyState.Visibility = Visibility.Visible; HomeSelectedState.Visibility = Visibility.Collapsed; }

        if (home is not null) UpdateTeamCard(home, isHome: false);
        else { AwayEmptyState.Visibility = Visibility.Visible; AwaySelectedState.Visibility = Visibility.Collapsed; }
    }

    private void HomeTeamCard_Click(object sender, RoutedEventArgs e)
    {
        OpenPickerWithBlur("Select Home Team", team =>
        {
            HomeTeamCombo.SelectedItem = team;
            UpdateTeamCard(team, isHome: true);
        });
    }

    private void AwayTeamCard_Click(object sender, RoutedEventArgs e)
    {
        OpenPickerWithBlur("Select Away Team", team =>
        {
            AwayTeamCombo.SelectedItem = team;
            UpdateTeamCard(team, isHome: false);
        });
    }

    private void OpenPickerWithBlur(string title, Action<TeamViewModel> onSelected)
    {
        // blur
        var blur = new System.Windows.Media.Effects.BlurEffect { Radius = 0 };
        RootGrid.Effect = blur;
        var blurIn = new DoubleAnimation(0, 8, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurIn);

        // прозрачный overlay поверх RootGrid — клик по нему закрывает пикер
        var clickOverlay = new System.Windows.Shapes.Rectangle
        {
            Fill = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(1, 0, 0, 0)), // почти прозрачный но кликабельный
            IsHitTestVisible = true,
            Cursor = System.Windows.Input.Cursors.Arrow
        };
        Grid.SetRowSpan(clickOverlay, 3);
        RootGrid.Children.Add(clickOverlay);

        var picker = new TeamPickerWindow(_teams, title) { Owner = this };

        // клик по overlay = закрыть пикер
        clickOverlay.MouseDown += (_, _) =>
        {
            picker.DialogResult = false;
            picker.Close();
        };

        if (picker.ShowDialog() == true && picker.SelectedTeam is { } team)
            onSelected(team);

        // убираем overlay и blur
        RootGrid.Children.Remove(clickOverlay);

        var blurOut = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        blurOut.Completed += (_, _) => RootGrid.Effect = null;
        blur.BeginAnimation(System.Windows.Media.Effects.BlurEffect.RadiusProperty, blurOut);
    }

    private void UpdateTeamCard(TeamViewModel team, bool isHome)
    {
        if (isHome)
        {
            HomeEmptyState.Visibility = Visibility.Collapsed;
            HomeSelectedState.Visibility = Visibility.Visible;
            HomeTeamName.Text = team.TeamLongName;
            HomeInitial.Text = team.Initial;

            if (team.LogoPath is not null)
            {
                HomeLogoCard.Source = new BitmapImage(new Uri(team.LogoPath));
                HomeLogoCard.Visibility = Visibility.Visible;
                HomeBadge.Visibility = Visibility.Collapsed;
            }
            else
            {
                HomeLogoCard.Visibility = Visibility.Collapsed;
                HomeBadge.Visibility = Visibility.Visible;
            }
        }
        else
        {
            AwayEmptyState.Visibility = Visibility.Collapsed;
            AwaySelectedState.Visibility = Visibility.Visible;
            AwayTeamName.Text = team.TeamLongName;
            AwayInitial.Text = team.Initial;

            if (team.LogoPath is not null)
            {
                AwayLogoCard.Source = new BitmapImage(new Uri(team.LogoPath));
                AwayLogoCard.Visibility = Visibility.Visible;
                AwayBadgeCard.Visibility = Visibility.Collapsed;
            }
            else
            {
                AwayLogoCard.Visibility = Visibility.Collapsed;
                AwayBadgeCard.Visibility = Visibility.Visible;
            }
        }
    }

    private void RandomBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_teams.Count < 2 || _seasons.Count == 0) return;

        var rnd = new Random();

        for (int attempt = 0; attempt < 50; attempt++)
        {
            var season = _seasons[rnd.Next(_seasons.Count)];
            var home = _teams[rnd.Next(_teams.Count)];
            var away = _teams[rnd.Next(_teams.Count)];

            if (home.TeamApiId == away.TeamApiId) continue;

            try
            {
                FeatureBuilder.BuildFeatures(_matches, home.TeamApiId, away.TeamApiId, season);

                SeasonCombo.SelectedItem = season;
                HomeTeamCombo.SelectedItem = home;
                AwayTeamCombo.SelectedItem = away;

                UpdateTeamCard(home, isHome: true);
                UpdateTeamCard(away, isHome: false);

                Dispatcher.InvokeAsync(() => PredictBtn_Click(this, new RoutedEventArgs()),
                    DispatcherPriority.Background);
                return;
            }
            catch
            {
                continue;
            }
        }

        StatusText.Text = "⚠ Could not find a valid random match. Try again.";
    }

    private void RandomBtn_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var rotate = new DoubleAnimation
        {
            From = 0,
            To = 25,
            Duration = TimeSpan.FromMilliseconds(150),
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        var transform = new RotateTransform();
        RandomIcon.RenderTransform = transform;
        RandomIcon.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        transform.BeginAnimation(RotateTransform.AngleProperty, rotate);
    }

    private void ThemeToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        // 1. Capture a screenshot of the current state
        var bmp = new RenderTargetBitmap(
            (int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(this);

        var overlay = new System.Windows.Shapes.Rectangle
        {
            Width = ActualWidth,
            Height = ActualHeight,
            Fill = new ImageBrush(bmp),
            IsHitTestVisible = false
        };

        Grid.SetRowSpan(overlay, 3);

        // 2. Place the screenshot overlay on top
        RootGrid.Children.Add(overlay);

        // 3. Switch the theme
        _isDarkTheme = !_isDarkTheme;
        ThemeIcon.Kind = _isDarkTheme ? PackIconKind.WeatherSunny : PackIconKind.WeatherNight;

        var res = Application.Current.Resources;
        if (_isDarkTheme)
        {
            res["MaterialDesignBackground"] = Brush("#1A1C18");
            res["MaterialDesignPaper"] = Brush("#252822");
            res["MaterialDesignCardBackground"] = Brush("#252822");
            res["MaterialDesignDivider"] = Brush("#3A3D35");
            res["MaterialDesignToolBarBackground"] = Brush("#2A2D26");
            res["MaterialDesignBody"] = Brush("#E3E4DB");
            res["MaterialDesignBodyLight"] = Brush("#9DAA8F");
            res["AppOutlineBrush"] = Brush("#4A4D45");
            res["SurfaceTintBrush"] = Brush("#252E1A");
            res["SuccessSurfaceBrush"] = Brush("#1A2E1A");
            res["WarnSurfaceBrush"] = Brush("#2E2410");
            res["DangerSurfaceBrush"] = Brush("#2E1A1A");
            res["SecondaryAccentBrush"] = Brush("#2A3520");
            res["SecondaryHueMidBrush"] = Brush("#2A3520");
            res["MaterialDesign.Brush.Background"] = Brush("#1A1C18");
            res["MaterialDesign.Brush.Card"] = Brush("#252822");
            res["CardSurfaceBrush"] = Brush("#252822");
            res["MaterialDesign.Brush.ForegroundBase"] = Brush("#E3E4DB");
            res["MaterialDesign.Brush.Foreground"] = Brush("#E3E4DB");
            res["MaterialDesign.Brush.Body"] = Brush("#E3E4DB");
            res["MaterialDesign.Brush.BodyLight"] = Brush("#9DAA8F");
            res["MaterialDesignCheckBoxOff"] = Brush("#9DAA8F");
        }
        else
        {
            res["MaterialDesignBackground"] = Brush("#F7F8F2");
            res["MaterialDesignPaper"] = Brush("#FFFFFF");
            res["MaterialDesignCardBackground"] = Brush("#FFFFFF");
            res["MaterialDesignDivider"] = Brush("#D9DFCF");
            res["MaterialDesignToolBarBackground"] = Brush("#EEF3E6");
            res["MaterialDesignBody"] = Brush("#1B1C18");
            res["MaterialDesignBodyLight"] = Brush("#5F6656");
            res["AppOutlineBrush"] = Brush("#C5CCB8");
            res["SurfaceTintBrush"] = Brush("#F2F5EA");
            res["SuccessSurfaceBrush"] = Brush("#E8F4E5");
            res["WarnSurfaceBrush"] = Brush("#FFF2E1");
            res["DangerSurfaceBrush"] = Brush("#FCE7E5");
            res["SecondaryAccentBrush"] = Brush("#DDE8CB");
            res["SecondaryHueMidBrush"] = Brush("#DDE8CB");
            res["MaterialDesign.Brush.Background"] = Brush("#F7F8F2");
            res["MaterialDesign.Brush.Card"] = Brush("#FFFFFF");
            res["CardSurfaceBrush"] = Brush("#FFFFFF");
            res["MaterialDesign.Brush.ForegroundBase"] = Brush("#1B1C18");
            res["MaterialDesign.Brush.Foreground"] = Brush("#1B1C18");
            res["MaterialDesign.Brush.Body"] = Brush("#1B1C18");
            res["MaterialDesign.Brush.BodyLight"] = Brush("#5F6656");
            res["MaterialDesignCheckBoxOff"] = Brush("#6B7261");
        }

        // 4. Fade out the screenshot overlay
        var anim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(350));
        anim.Completed += (_, _) => RootGrid.Children.Remove(overlay);
        overlay.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private async Task LoadLogosAsync(List<TeamViewModel> teams, string logosDir)
    {
        // Step 1 — immediately show already-downloaded logos
        foreach (var vm in teams)
        {
            var localPath = Path.Combine(logosDir, $"{vm.TeamApiId}.png");
            if (File.Exists(localPath))
            {
                _logoCache[vm.TeamApiId] = localPath;
                vm.LogoPath = localPath; // direct assignment — already on the UI thread
            }
        }

        // Step 2 — download missing logos in the background
        var missing = teams.Where(t => !_logoCache.ContainsKey(t.TeamApiId)).ToList();
        foreach (var vm in missing)
        {
            var path = await TeamLogoService.GetLogoPathAsync(
                vm.TeamApiId, vm.TeamLongName, ApiKey, logosDir);
            _logoCache[vm.TeamApiId] = path;
            if (path is not null)
                _ = Application.Current.Dispatcher.BeginInvoke(() => vm.LogoPath = path);

            await Task.Delay(800);
        }
    }

    private void AddToHistory(TeamViewModel home, TeamViewModel away, string season, int label, float confPct)
    {
        static Brush Res(string key) => (Brush)System.Windows.Application.Current.FindResource(key);

        var (outcomeText, brushKey) = label switch
        {
            0 => ("Away Win", "DangerBrush"),
            1 => ("Draw", "WarnBrush"),
            2 => ("Home Win", "SuccessBrush"),
            _ => ("—", "MaterialDesignBodyLight"),
        };

        // Dedup by season + teams + outcome
        var dupKey = $"{home.TeamApiId}-{away.TeamApiId}-{season}-{label}";
        var existing = _history.FirstOrDefault(e => e.Key == dupKey);
        if (existing is not null)
            _history.Remove(existing);

        var entry = new HistoryEntry
        {
            Key = dupKey,
            HomeTeamId = home.TeamApiId,
            AwayTeamId = away.TeamApiId,
            HomeShort = home.TeamShortName,
            AwayShort = away.TeamShortName,
            HomeLogo = home.LogoPath,
            AwayLogo = away.LogoPath,
            HomeInitial = home.Initial,
            AwayInitial = away.Initial,
            Season = season,
            OutcomeText = outcomeText,
            OutcomeBrush = Res(brushKey),
            ConfidenceText = $"{confPct:0.0}%",
        };

        _history.Insert(0, entry);
        if (_history.Count > 6) _history.RemoveAt(_history.Count - 1);
        HistoryList.ItemsSource = null;
        HistoryList.ItemsSource = _history;
    }

    private async void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryEntry entry) return;

        // Reset selection so the same item can be clicked again later
        var picked = entry;
        HistoryList.SelectedIndex = -1;

        // Re-apply the match configuration
        if (_seasons.Contains(picked.Season))
            SeasonCombo.SelectedItem = picked.Season;

        // Force-trigger the season filter so teams are rebuilt for that season
        await Task.Delay(50);

        var home = _teams.FirstOrDefault(t => t.TeamApiId == picked.HomeTeamId);
        var away = _teams.FirstOrDefault(t => t.TeamApiId == picked.AwayTeamId);
        if (home is null || away is null) return;

        HomeTeamCombo.SelectedItem = home;
        AwayTeamCombo.SelectedItem = away;

        UpdateTeamCard(home, isHome: true);
        UpdateTeamCard(away, isHome: false);

        await Task.Delay(50);
        PredictBtn_Click(this, new RoutedEventArgs());
    }

    // ── Team picker — filter as you type ──
    private void TeamCombo_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.Template.FindName("PART_EditableTextBox", combo) is not TextBox tb) return;
        tb.TextChanged -= TeamEditable_TextChanged;
        tb.TextChanged += TeamEditable_TextChanged;
    }

    private void TeamEditable_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var combo = FindAncestor<ComboBox>(tb);
        if (combo is null) return;
        var view = combo.ItemsSource as System.Windows.Data.ListCollectionView;
        if (view is null) return;

        var text = tb.Text?.Trim() ?? string.Empty;

        // If the current text equals the selected team's full name, don't filter (just committed a pick)
        if (combo.SelectedItem is TeamViewModel sel &&
            string.Equals(text, sel.TeamLongName, StringComparison.Ordinal))
        {
            view.Filter = null;
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            view.Filter = null;
        }
        else
        {
            view.Filter = o => o is TeamViewModel t &&
                t.TeamLongName.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;
            if (!combo.IsDropDownOpen) combo.IsDropDownOpen = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T t) return t;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    public sealed class HistoryEntry
    {
        public string Key { get; init; } = "";
        public int HomeTeamId { get; init; }
        public int AwayTeamId { get; init; }
        public string HomeShort { get; init; } = "";
        public string AwayShort { get; init; } = "";
        public string? HomeLogo { get; init; }
        public string? AwayLogo { get; init; }
        public string HomeInitial { get; init; } = "?";
        public string AwayInitial { get; init; } = "?";
        public string Season { get; init; } = "";
        public string OutcomeText { get; init; } = "";
        public Brush OutcomeBrush { get; init; } = Brushes.Gray;
        public string ConfidenceText { get; init; } = "";
    }

    private void ShowResult(bool show)
    {
        EmptyState.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ResultHeader.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ResultScrollViewer.VerticalScrollBarVisibility =
            show ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

        if (show)
        {
            HeroBlock.Visibility = Visibility.Collapsed;
            ProbabilityBlock.Visibility = Visibility.Collapsed;
            TabBlock.Visibility = Visibility.Collapsed;
            HeroBlock.Opacity = 0;
            ProbabilityBlock.Opacity = 0;
            TabBlock.Opacity = 0;

            // Small delay to allow the UI layout to update before animating
            Dispatcher.InvokeAsync(() =>
            {
                HeroBlock.Visibility = Visibility.Visible;
                ProbabilityBlock.Visibility = Visibility.Visible;
                TabBlock.Visibility = Visibility.Visible;

                AnimateBlockIn(HeroBlock, 0);
                AnimateBlockIn(ProbabilityBlock, 120);
                AnimateBlockIn(TabBlock, 240);
            }, DispatcherPriority.Render);
        }
        else
        {
            HeroBlock.Visibility = Visibility.Collapsed;
            ProbabilityBlock.Visibility = Visibility.Collapsed;
            TabBlock.Visibility = Visibility.Collapsed;
        }
    }

    private void HighlightWinningBar(int label)
    {
        // Reset all three to default state
        AwayBorder.BorderThickness = new Thickness(0);
        DrawBorder.BorderThickness = new Thickness(0);
        HomeBorder.BorderThickness = new Thickness(0);

        AwayBorder.Opacity = 0.6;
        DrawBorder.Opacity = 0.6;
        HomeBorder.Opacity = 0.6;

        // Highlight the winner
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
                0 => new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50)),
                1 => new SolidColorBrush(Color.FromRgb(0xFF, 0xA7, 0x26)),
                2 => new SolidColorBrush(Color.FromRgb(0x66, 0xBB, 0x6A)),
                _ => Brushes.Transparent
            };
            winner.Opacity = 1.0;
        }
    }

    private void AnimateBar(ProgressBar bar, double targetValue, double delaySeconds)
    {
        var animation = new DoubleAnimation
        {
            From = 0,
            To = targetValue,
            Duration = TimeSpan.FromMilliseconds(850),
            BeginTime = TimeSpan.FromSeconds(delaySeconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        bar.BeginAnimation(ProgressBar.ValueProperty, animation);
    }

    private void AnimatePct(TextBlock textBlock, double targetPct, double delaySeconds)
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16) // ~60fps
        };

        var startTime = DateTime.Now + TimeSpan.FromSeconds(delaySeconds);
        var duration = TimeSpan.FromMilliseconds(850);

        timer.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            if (now < startTime) return;

            var elapsed = (now - startTime).TotalMilliseconds;
            var progress = Math.Min(elapsed / duration.TotalMilliseconds, 1.0);

            // Cubic ease-out
            var eased = 1 - Math.Pow(1 - progress, 3);
            var current = targetPct * eased;

            textBlock.Text = $"{current:0.0}%";

            if (progress >= 1.0)
            {
                textBlock.Text = $"{targetPct:0.0}%";
                timer.Stop();
            }
        };

        timer.Start();
    }

    private void AnimateBlockIn(UIElement element, double delayMs)
    {
        var translate = new TranslateTransform { Y = 24 };
        element.RenderTransform = translate;
        element.Opacity = 0;

        var opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var translateAnim = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(500))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        element.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        translate.BeginAnimation(TranslateTransform.YProperty, translateAnim);
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

    private string? GetLogoPath(int teamApiId)
        => _logoCache.TryGetValue(teamApiId, out var path) ? path : null;

    private static SolidColorBrush Brush(string hex)
        => new((Color)ColorConverter.ConvertFromString(hex));

    public sealed class FeatureRow
    {
        public string Metric { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
        public PackIconKind Icon { get; init; }
        public float Home { get; init; }
        public float Away { get; init; }
        public float Diff { get; init; }

        // Display strings
        public string HomeText => Home.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        public string AwayText => Away.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        public string DiffText { get; init; } = "";

        // Diverging-bar proportions (precomputed star units)
        public GridLength HomeFillStar { get; init; } = new(0, GridUnitType.Star);
        public GridLength HomeEmptyStar { get; init; } = new(1, GridUnitType.Star);
        public GridLength AwayFillStar { get; init; } = new(0, GridUnitType.Star);
        public GridLength AwayEmptyStar { get; init; } = new(1, GridUnitType.Star);

        // Bar & value brushes (may be swapped for inverted metrics like "goals against")
        public Brush HomeBarBrush { get; init; } = Brushes.Transparent;
        public Brush AwayBarBrush { get; init; } = Brushes.Transparent;
        public Brush HomeValueBrush { get; init; } = Brushes.Black;
        public Brush AwayValueBrush { get; init; } = Brushes.Black;

        // Category pill brushes
        public Brush CategoryBgBrush { get; init; } = Brushes.Transparent;
        public Brush CategoryFgBrush { get; init; } = Brushes.Black;

        // Delta pill
        public Brush DeltaBg { get; init; } = Brushes.Transparent;
        public Brush DeltaFg { get; init; } = Brushes.Black;
        public PackIconKind DeltaIcon { get; init; } = PackIconKind.MinusThick;
    }

    private static List<FeatureRow> BuildFeatureRows(MatchData f)
    {
        static Brush Res(string key) => (Brush)System.Windows.Application.Current.FindResource(key);

        FeatureRow Make(
            string name, string desc, string category, PackIconKind icon,
            float home, float away, float diff, bool isInverted = false)
        {
            // Normalise per-row: biggest absolute value → 100 % fill
            var max = Math.Max(Math.Abs(home), Math.Abs(away));
            var homeRatio = max > 0 ? Math.Abs(home) / max : 0;
            var awayRatio = max > 0 ? Math.Abs(away) / max : 0;

            // Bar colours (swap for "lower is better" metrics)
            var homeBar = Res(isInverted ? "AwayBarBrush" : "HomeBarBrush");
            var awayBar = Res(isInverted ? "HomeBarBrush" : "AwayBarBrush");
            var homeValue = Res(isInverted ? "AwayValueBrush" : "HomeValueBrush");
            var awayValue = Res(isInverted ? "HomeValueBrush" : "AwayValueBrush");

            // Category pill
            var (catBgKey, catFgKey) = category switch
            {
                "Attack" => ("CatAttackBg", "CatAttackFg"),
                "Defense" => ("CatDefenseBg", "CatDefenseFg"),
                "Form" => ("CatFormBg", "CatFormFg"),
                "Momentum" => ("CatMomentumBg", "CatMomentumFg"),
                _ => ("BarTrackBrush", "MaterialDesignBody"),
            };

            // Delta pill: "better for home" depends on inversion and sign
            var homeAdvantage = isInverted ? diff < 0 : diff > 0;
            var awayAdvantage = isInverted ? diff > 0 : diff < 0;

            string deltaBgKey, deltaFgKey;
            PackIconKind deltaIcon;
            if (Math.Abs(diff) < 1e-4f) { deltaBgKey = "DeltaNeuBg"; deltaFgKey = "DeltaNeuFg"; deltaIcon = PackIconKind.MinusThick; }
            else if (homeAdvantage) { deltaBgKey = "DeltaPosBg"; deltaFgKey = "DeltaPosFg"; deltaIcon = PackIconKind.TrendingUp; }
            else { deltaBgKey = "DeltaNegBg"; deltaFgKey = "DeltaNegFg"; deltaIcon = PackIconKind.TrendingDown; }

            var diffStr = diff.ToString("+0.##;-0.##;0", System.Globalization.CultureInfo.InvariantCulture);

            return new FeatureRow
            {
                Metric = name,
                Description = desc,
                Category = category,
                Icon = icon,
                Home = home,
                Away = away,
                Diff = diff,
                DiffText = diffStr,
                HomeFillStar = new GridLength(Math.Max(homeRatio, 0.001), GridUnitType.Star),
                HomeEmptyStar = new GridLength(Math.Max(1 - homeRatio, 0.001), GridUnitType.Star),
                AwayFillStar = new GridLength(Math.Max(awayRatio, 0.001), GridUnitType.Star),
                AwayEmptyStar = new GridLength(Math.Max(1 - awayRatio, 0.001), GridUnitType.Star),
                HomeBarBrush = homeBar,
                AwayBarBrush = awayBar,
                HomeValueBrush = homeValue,
                AwayValueBrush = awayValue,
                CategoryBgBrush = Res(catBgKey),
                CategoryFgBrush = Res(catFgKey),
                DeltaBg = Res(deltaBgKey),
                DeltaFg = Res(deltaFgKey),
                DeltaIcon = deltaIcon,
            };
        }

        return new List<FeatureRow>
        {
            Make("Avg Goals For",     "Mean goals scored, last 5 matches", "Attack",
                 PackIconKind.Soccer,
                 f.HomeAvgGoalsFor, f.AwayAvgGoalsFor, f.AvgGoalsForDiff),
            Make("Avg Goals Against", "Goals conceded — lower is better",   "Defense",
                 PackIconKind.ShieldHalfFull,
                 f.HomeAvgGoalsAgainst, f.AwayAvgGoalsAgainst, f.AvgGoalsAgainstDiff,
                 isInverted: true),
            Make("Points Per Game",   "Season pace (3 for win, 1 for draw)", "Form",
                 PackIconKind.Trophy,
                 f.HomePointsPerGame, f.AwayPointsPerGame, f.PointsPerGameDiff),
            Make("Win Rate",          "Share of matches won in recent window", "Form",
                 PackIconKind.Percent,
                 f.HomeWinRate, f.AwayWinRate, f.WinRateDiff),
            Make("Goal-Diff Differential", "Net momentum — Home GD minus Away GD", "Momentum",
                 PackIconKind.TrendingUp,
                 f.GoalDiffDiff, 0f, f.GoalDiffDiff),
        };
    }
}