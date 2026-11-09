using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using NettoyeurPC.Core;

namespace NettoyeurPC;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Crash(a.ExceptionObject as Exception);
        DispatcherUnhandledException += (_, a) => { Crash(a.Exception); a.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Tâche non observée", a.Exception); a.SetObserved(); };

        if (e.Args.Any(a => a.Equals("--auto", StringComparison.OrdinalIgnoreCase)))
        {
            await AutoClean.RunHeadless();
            Shutdown();
            return;
        }

        Log.Info($"Démarrage de Nettoyeur PC {typeof(App).Assembly.GetName().Version} depuis {AppPaths.ExePath}");
        ApplyAccent(Settings.Current.Accent);
        var w = new UI.MainWindow();
        MainWindow = w;
        w.Show();
    }

    public static void ApplyAccent(string hex)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            Current.Resources["Accent"] = new SolidColorBrush(c);
            Current.Resources["AccentColor"] = c;
        }
        catch { }
    }

    static bool _crashShown;
    static void Crash(Exception ex)
    {
        Log.Error("Erreur inattendue", ex);
        if (_crashShown) return;
        _crashShown = true;
        try
        {
            MessageBox.Show("Une erreur inattendue s'est produite. Elle a été notée dans le journal :\n" + Log.File + "\n\n" + ex?.Message,
                "Nettoyeur PC", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
        _crashShown = false;
    }
}
