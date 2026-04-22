using SportMatchPredictor.ML;
using SportMatchPredictor.ML.Data;
using SportMatchPredictor.ML.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MaterialDesignThemes.Wpf;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;

namespace SportMatchPredictor.App;

public partial class MainWindow : Window
{
    private List<TeamViewModel> _teams = new();
    private IReadOnlyList<RawMatchRecord> _matches = Array.Empty<RawMatchRecord>();
    private IReadOnlyList<string> _seasons = Array.Empty<string>();
    private bool _isDarkTheme = false;

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
            _seasons = seasons;
            SeasonCombo.ItemsSource = seasons;
            SeasonCombo.SelectedItem = seasons.LastOrDefault();

            HomeTeamCombo.ItemsSource = _teams;
            AwayTeamCombo.ItemsSource = _teams;
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

    private async Task LoadLogosAsync(List<TeamViewModel> teams, string logosDir)
    {
        // Шаг 1 — мгновенно показываем все уже скачанные логотипы
        foreach (var vm in teams)
        {
            var localPath = Path.Combine(logosDir, $"{vm.TeamApiId}.png");
            if (File.Exists(localPath))
            {
                _logoCache[vm.TeamApiId] = localPath;
                vm.LogoPath = localPath; // напрямую, без Dispatcher — мы уже в UI потоке
            }
        }

        // Шаг 2 — в фоне докачиваем те у которых нет файла
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

            AddToHistory(home.TeamLongName, away.TeamLongName, season, label, confPct);

            // Home Win (2) — HomeLogo слева, остальные скрыты
            // Away Win (0) — AwayLogo слева, остальные скрыты  
            // Draw (1)     — HomeLogo слева, AwayLogoRight справа
            HomeLogo.Source = (label == 2 || label == 1) && home.LogoPath is not null
                ? new System.Windows.Media.Imaging.BitmapImage(new Uri(home.LogoPath))
                : null;
            AwayLogo.Source = label == 0 && away.LogoPath is not null
                ? new System.Windows.Media.Imaging.BitmapImage(new Uri(away.LogoPath))
                : null;
            AwayLogoRight.Source = label == 1 && away.LogoPath is not null
                ? new System.Windows.Media.Imaging.BitmapImage(new Uri(away.LogoPath))
                : null;

            HomeLogo.Visibility = HomeLogo.Source is not null ? Visibility.Visible : Visibility.Collapsed;
            AwayLogo.Visibility = AwayLogo.Source is not null ? Visibility.Visible : Visibility.Collapsed;
            AwayLogoRight.Visibility = AwayLogoRight.Source is not null ? Visibility.Visible : Visibility.Collapsed;

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
            Duration = TimeSpan.FromMilliseconds(850),
            BeginTime = TimeSpan.FromSeconds(delaySeconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        bar.BeginAnimation(System.Windows.Controls.ProgressBar.ValueProperty, animation);
    }

    private void AnimatePct(System.Windows.Controls.TextBlock textBlock, double targetPct, double delaySeconds)
    {
        var timer = new System.Windows.Threading.DispatcherTimer
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

            // EaseOut кубическая
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
        HomeLogo.Source = null;
        AwayLogo.Source = null;
        AwayLogoRight.Source = null;
        HomeLogo.Visibility = Visibility.Collapsed;
        AwayLogo.Visibility = Visibility.Collapsed;
        AwayLogoRight.Visibility = Visibility.Collapsed;
        ShowResult(false);
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

    private void SwapBtn_Click(object sender, RoutedEventArgs e)
    {
        var home = HomeTeamCombo.SelectedItem;
        var away = AwayTeamCombo.SelectedItem;
        HomeTeamCombo.SelectedItem = away;
        AwayTeamCombo.SelectedItem = home;
    }

    private void ShowResult(bool show)
    {
        EmptyState.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        ResultHeader.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ResultScrollViewer.VerticalScrollBarVisibility =
            show ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

        if (show)
        {
            // сначала скрываем и сбрасываем
            HeroBlock.Visibility = Visibility.Collapsed;
            ProbabilityBlock.Visibility = Visibility.Collapsed;
            TabBlock.Visibility = Visibility.Collapsed;
            HeroBlock.Opacity = 0;
            ProbabilityBlock.Opacity = 0;
            TabBlock.Opacity = 0;

            // небольшая задержка чтобы UI успел обновиться
            Dispatcher.InvokeAsync(() =>
            {
                HeroBlock.Visibility = Visibility.Visible;
                ProbabilityBlock.Visibility = Visibility.Visible;
                TabBlock.Visibility = Visibility.Visible;

                AnimateBlockIn(HeroBlock, 0);
                AnimateBlockIn(ProbabilityBlock, 120);
                AnimateBlockIn(TabBlock, 240);
            }, System.Windows.Threading.DispatcherPriority.Render);
        }
        else
        {
            HeroBlock.Visibility = Visibility.Collapsed;
            ProbabilityBlock.Visibility = Visibility.Collapsed;
            TabBlock.Visibility = Visibility.Collapsed;
        }
    }

    private void AnimateBlockIn(UIElement element, double delayMs)
    {
        // сбрасываем трансформацию
        var translate = new TranslateTransform { Y = 24 };
        element.RenderTransform = translate;
        element.Opacity = 0;

        // анимация Opacity
        var opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        // анимация движения снизу вверх
        var translateAnim = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(500))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        element.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        translate.BeginAnimation(TranslateTransform.YProperty, translateAnim);
    }

    private void RandomBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_teams.Count < 2 || _seasons.Count == 0) return;

        var rnd = new Random();

        // пробуем несколько раз найти валидную комбинацию
        for (int attempt = 0; attempt < 50; attempt++)
        {
            var season = _seasons[rnd.Next(_seasons.Count)];
            var home = _teams[rnd.Next(_teams.Count)];
            var away = _teams[rnd.Next(_teams.Count)];

            if (home.TeamApiId == away.TeamApiId) continue;

            try
            {
                // проверяем что FeatureBuilder не бросит исключение
                FeatureBuilder.BuildFeatures(_matches, home.TeamApiId, away.TeamApiId, season);

                // нашли валидную комбинацию — применяем
                SeasonCombo.SelectedItem = season;
                HomeTeamCombo.SelectedItem = home;
                AwayTeamCombo.SelectedItem = away;

                Dispatcher.InvokeAsync(() => PredictBtn_Click(this, new RoutedEventArgs()),
                    System.Windows.Threading.DispatcherPriority.Background);
                return;
            }
            catch
            {
                // эта комбинация не подходит — пробуем следующую
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
        // 1. Скриншот текущего состояния
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

        System.Windows.Controls.Grid.SetRowSpan(overlay, 3);

        // 2. Кладём поверх интерфейса
        RootGrid.Children.Add(overlay);

        // 3. Меняем тему
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

        // 4. Плавно растворяем скриншот
        var anim = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(350));
        anim.Completed += (_, _) => RootGrid.Children.Remove(overlay);
        overlay.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private static System.Windows.Media.SolidColorBrush Brush(string hex)
        => new((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));

    private void SeasonCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        ValidateSelection();
    }
}
