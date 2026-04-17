using System.Windows;
using System.Windows.Controls;

namespace SportMatchPredictor.App
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ToolTipService.InitialShowDelayProperty.OverrideMetadata(
                typeof(DependencyObject),
                new FrameworkPropertyMetadata(300));
        }
    }
}