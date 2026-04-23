using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace SportMatchPredictor.App;

public partial class TeamPickerWindow : Window
{
    private readonly List<TeamViewModel> _allTeams;
    public TeamViewModel? SelectedTeam { get; private set; }

    private bool _isClosing = false;

    public TeamPickerWindow(List<TeamViewModel> teams, string title)
    {
        InitializeComponent();
        _allTeams = teams;
        TitleText.Text = title;
        TeamsList.ItemsSource = _allTeams;
        SearchBox.Focus();

        // Esc closes from anywhere
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
                SafeClose();
        };

        Deactivated += (_, _) => SafeClose();

        // fade in animation
        Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Loaded += (_, _) => BeginAnimation(OpacityProperty, fadeIn);
    }

    private void SafeClose()
    {
        if (_isClosing) return;
        _isClosing = true;
        try { DialogResult = false; } catch { }
        try { Close(); } catch { }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        if (string.IsNullOrEmpty(query))
        {
            TeamsList.ItemsSource = _allTeams;
        }
        else
        {
            TeamsList.ItemsSource = _allTeams
                .Where(t => t.TeamLongName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    private void TeamCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is TeamViewModel team)
        {
            SelectedTeam = team;
            DialogResult = true;
            Close();
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => SafeClose();

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }
}