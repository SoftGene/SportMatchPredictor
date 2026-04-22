using System.Windows;
using System.Windows.Controls;

namespace SportMatchPredictor.App
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
            {
                System.IO.File.WriteAllText("crash.log", ex.ExceptionObject.ToString());
            };

            base.OnStartup(e);
            ToolTipService.InitialShowDelayProperty.OverrideMetadata(
                typeof(DependencyObject),
                new FrameworkPropertyMetadata(300));

            DispatcherUnhandledException += (_, ex) =>
            {
                MessageBox.Show(ex.Exception.ToString(), "Startup Error");
                ex.Handled = true;
            };


        }
    }
}