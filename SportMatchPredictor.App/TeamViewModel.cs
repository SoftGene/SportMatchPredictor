using SportMatchPredictor.ML.Services;
using System.ComponentModel;

namespace SportMatchPredictor.App;

public sealed class TeamViewModel : INotifyPropertyChanged
{
    private string? _logoPath;

    public TeamRecord Record { get; }
    public int TeamApiId => Record.TeamApiId;
    public string TeamLongName => Record.TeamLongName;
    public string TeamShortName => Record.TeamShortName;

    public string? LogoPath
    {
        get => _logoPath;
        set
        {
            _logoPath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LogoPath)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public TeamViewModel(TeamRecord record) => Record = record;

    public override string ToString() => Record.TeamLongName;
}
