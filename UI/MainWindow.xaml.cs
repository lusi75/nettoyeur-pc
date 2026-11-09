using System.Windows;
using System.Windows.Controls;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

public partial class MainWindow : Window
{
    public static MainWindow Instance { get; private set; }
    readonly Dictionary<string, FrameworkElement> _pages = new();
    CancellationTokenSource _busyCts;

    public MainWindow()
    {
        Instance = this;
        InitializeComponent();
        VersionText.Text = "Version " + typeof(App).Assembly.GetName().Version?.ToString(2);
        SourceInitialized += (_, _) => Ui.DarkTitleBar(this);
        Loaded += (_, _) =>
        {
            ProtectionText.Text = $"Annulation possible {Settings.Current.QuarantineDays} jours";
            if (Settings.Current.AutoPurgeQuarantine) Task.Run(() => Quarantine.Purge(Settings.Current.QuarantineDays));
            NavHome.IsChecked = true;
            _gameSync = true; GameSwitch.IsChecked = Settings.Current.GameModeOn; _gameSync = false;
            GameText.Text = Settings.Current.GameModeOn ? "Activé" : "Désactivé";
        };
    }

    public void RefreshProtection() => ProtectionText.Text = $"Annulation possible {Settings.Current.QuarantineDays} jours";

    FrameworkElement Page(string key) => _pages.TryGetValue(key, out var p) ? p : _pages[key] = key switch
    {
        "home" => new HomePage(),
        "clean" => new CleanPage(),
        "storage" => new StoragePage(),
        "history" => new HistoryPage(),
        "apps" => new AppsPage(),
        "startup" => new StartupPage(),
        "registry" => new RegistryPage(),
        "phone" => new PhonePage(),
        _ => new SettingsPage(),
    };

    object _navArg;
    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        var key = (string)((RadioButton)sender).Tag;
        var p = Page(key);
        Host.Content = p;
        try { (p as IPage)?.OnShow(_navArg); } catch (Exception ex) { Log.Error("Affichage de l'écran " + key, ex); }
        _navArg = null;
    }

    public void Go(string key, object arg = null)
    {
        _navArg = arg;
        var rb = key switch { "home" => NavHome, "clean" => NavClean, "storage" => NavStorage, "history" => NavHistory, "apps" => NavApps, "startup" => NavStartup, "registry" => NavRegistry, "phone" => NavPhone, _ => NavSettings };
        if (rb.IsChecked == true) { (Page(key) as IPage)?.OnShow(arg); _navArg = null; }
        else rb.IsChecked = true;
    }

    /// <summary>Affiche le voile d'attente pendant une tâche longue. Renvoie un jeton d'annulation si cancellable.</summary>
    public CancellationToken ShowBusy(string title, bool cancellable = false, string cancelText = null, bool keepToken = false)
    {
        if (!keepToken || _busyCts == null) { _busyCts = new CancellationTokenSource(); BusyCancel.IsEnabled = true; }
        BusyTitle.Text = title; BusyText.Text = "";
        BusyCancel.Content = cancelText ?? "Arrêter";
        BusyCancel.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        Busy.Visibility = Visibility.Visible;
        return _busyCts.Token;
    }
    public CancellationToken BusyToken => _busyCts?.Token ?? default;
    public void ResetBusyToken() { _busyCts = new CancellationTokenSource(); BusyCancel.IsEnabled = true; }
    public void BusyStatus(string text) => Dispatcher.BeginInvoke(() => BusyText.Text = text);
    public void HideBusy() => Busy.Visibility = Visibility.Collapsed;
    void BusyCancel_Click(object sender, RoutedEventArgs e) { _busyCts?.Cancel(); BusyCancel.IsEnabled = false; BusyText.Text = "Arrêt en cours…"; }

    // ---- Mode jeu
    bool _gameSync;
    async void GameSwitch_Changed(object sender, RoutedEventArgs e)
    {
        if (_gameSync) return;
        var on = GameSwitch.IsChecked == true;
        if (on)
        {
            var apps = await Task.Run(GameMode.RunningApps);
            var extra = new StackPanel();
            List<CheckBox> boxes = new();
            if (apps.Count > 0)
            {
                extra.Children.Add(Ui.Text("Fermer pendant le jeu (relancées à la sortie) :", "Small", 13).With(t => t.Margin = new Thickness(0, 0, 0, 8)));
                var wrap = new StackPanel();
                foreach (var a in apps)
                {
                    var cb = new CheckBox { Content = $"{a.Name}  ·  {a.Title}", Tag = a.Name, Margin = new Thickness(0, 0, 0, 6), IsChecked = Settings.Current.GameModeApps.Contains(a.Name, StringComparer.OrdinalIgnoreCase) };
                    boxes.Add(cb); wrap.Children.Add(cb);
                }
                extra.Children.Add(new ScrollViewer { Content = wrap, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            }
            if (!Dialog.Confirm("Activer le mode jeu ?", "• Plan d'alimentation « Performances élevées » (le processeur ne se bride plus)\n• Nettoyage automatique mis en pause\n• Les applis cochées sont fermées\n\nTout revient comme avant quand tu le désactives.", "Activer", extra: extra))
            { _gameSync = true; GameSwitch.IsChecked = false; _gameSync = false; return; }
            var close = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Tag).ToList();
            // garde aussi les choix des applis qui ne sont pas ouvertes en ce moment
            close.AddRange(Settings.Current.GameModeApps.Where(n => !apps.Any(a => a.Name.Equals(n, StringComparison.OrdinalIgnoreCase))));
            ShowBusy("Activation du mode jeu");
            string r;
            try { r = await Task.Run(() => GameMode.Enable(close, BusyStatus)); } finally { HideBusy(); }
            GameText.Text = "Activé";
            GameCard.ToolTip = "Mode jeu actif :\n" + r;
        }
        else
        {
            ShowBusy("Désactivation du mode jeu");
            string r;
            try { r = await Task.Run(() => GameMode.Disable(BusyStatus)); } finally { HideBusy(); }
            GameText.Text = "Désactivé";
            GameCard.ToolTip = "Retour à la normale :\n" + r;
        }
        AppState.Notify();
    }
}
