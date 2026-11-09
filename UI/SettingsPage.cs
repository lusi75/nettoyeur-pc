using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

public class SettingsPage : UserControl, IPage
{
    static readonly string[] Accents = { "#3DD6B5", "#5B9DFF", "#F5A524", "#B48CFF" };
    static readonly int[] DurationChoices = { 3, 7, 14, 30 };

    readonly StackPanel _swatches = new() { Orientation = Orientation.Horizontal };
    readonly ComboBox _drive = new() { Width = 320, Height = 36, HorizontalAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Center };
    readonly StackPanel _durations = new() { Orientation = Orientation.Horizontal };
    readonly CheckBox _restore = new() { Content = "Créer un point de restauration avant chaque nettoyage" };
    readonly TextBlock _qInfo = Ui.Text("", "Small", 13);
    bool _loading;

    public SettingsPage()
    {
        var root = new StackPanel { Margin = new Thickness(40, 30, 40, 40), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        root.Children.Add(Ui.Text("Paramètres", "H1").With(t => t.Margin = new Thickness(0, 0, 0, 22)));

        // ---- Apparence
        var ap = new StackPanel();
        ap.Children.Add(Ui.Text("Couleur d'accent", "H3"));
        ap.Children.Add(Ui.Text("La couleur des boutons principaux, des cases cochées et des graphiques.", "Small", 13).With(t => t.Margin = new Thickness(0, 4, 0, 14)));
        ap.Children.Add(_swatches);
        root.Children.Add(Section(ap));

        // ---- Quarantaine
        var q = new StackPanel();
        q.Children.Add(Ui.Text("Quarantaine", "H3"));
        q.Children.Add(Ui.Text("Les fichiers « à vérifier » et ceux que tu supprimes depuis Stockage ne sont pas effacés tout de suite : ils sont mis de côté sur un autre disque, restaurables depuis l'Historique, puis supprimés automatiquement.", "Small", 13).With(t => t.Margin = new Thickness(0, 4, 0, 16)));
        q.Children.Add(Ui.Text("Disque de quarantaine", "Small", 13).With(t => t.Margin = new Thickness(0, 0, 0, 8)));
        _drive.SetResourceReference(BackgroundProperty, "Surface2");
        _drive.SetResourceReference(ForegroundProperty, "Text");
        _drive.SetResourceReference(BorderBrushProperty, "Line2");
        q.Children.Add(_drive);
        q.Children.Add(Ui.Text("Durée avant suppression définitive", "Small", 13).With(t => t.Margin = new Thickness(0, 18, 0, 8)));
        q.Children.Add(_durations);
        _qInfo.Margin = new Thickness(0, 16, 0, 0);
        q.Children.Add(_qInfo);
        var qb = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        qb.Children.Add(Ui.Btn("Ouvrir le dossier de quarantaine", "Ghost", (_, _) =>
        {
            try { Shell.OpenInExplorer(Quarantine.RootFor(AppPaths.SystemDrive)); } catch (Exception ex) { Log.Error("Dossier de quarantaine", ex); }
        }, "").With(b => b.Margin = new Thickness(0, 0, 10, 0)));
        qb.Children.Add(Ui.Btn("Vider la quarantaine maintenant", "Danger", async (_, _) => await PurgeNow(), ""));
        q.Children.Add(qb);
        root.Children.Add(Section(q));

        // ---- Sécurité
        var s = new StackPanel();
        s.Children.Add(Ui.Text("Sécurité", "H3").With(t => t.Margin = new Thickness(0, 0, 0, 14)));
        s.Children.Add(_restore);
        s.Children.Add(Ui.Text("Un point de restauration permet de remettre Windows dans l'état d'avant, depuis les outils de récupération de Windows.", "Small", 13).With(t => t.Margin = new Thickness(30, 6, 0, 0)));
        root.Children.Add(Section(s));

        // ---- Installation
        var inst = new StackPanel();
        inst.Children.Add(Ui.Text("Installation", "H3"));
        inst.Children.Add(Ui.Text("Copie l'appli dans Program Files et crée un raccourci « Nettoyeur PC » sur le Bureau et dans le menu Démarrer, qui l'ouvre sans la fenêtre de confirmation de Windows.", "Small", 13).With(t => t.Margin = new Thickness(0, 4, 0, 14)));
        inst.Children.Add(Ui.Btn("Installer et créer les raccourcis (sans confirmation)", "Ghost", async (_, _) => await Install(), "").With(b => b.HorizontalAlignment = HorizontalAlignment.Left));
        root.Children.Add(Section(inst));

        // ---- Journaux
        var lg = new StackPanel();
        lg.Children.Add(Ui.Text("Journaux", "H3"));
        lg.Children.Add(Ui.Text("Tout ce que fait l'appli est noté, mois par mois. Utile en cas de souci.", "Small", 13).With(t => t.Margin = new Thickness(0, 4, 0, 14)));
        lg.Children.Add(Ui.Btn("Ouvrir le dossier des journaux", "Ghost", (_, _) =>
        {
            try { Directory.CreateDirectory(Log.Dir); Shell.OpenInExplorer(Log.Dir); } catch (Exception ex) { Log.Error("Dossier des journaux", ex); }
        }, "").With(b => b.HorizontalAlignment = HorizontalAlignment.Left));
        root.Children.Add(Section(lg));

        // ---- À propos
        var ab = new StackPanel();
        ab.Children.Add(Ui.Text("À propos", "H3"));
        ab.Children.Add(Ui.Text("Nettoyeur PC " + (typeof(App).Assembly.GetName().Version?.ToString(2) ?? "2.0"), "Body", 14, Ui.Res("Text")).With(t => t.Margin = new Thickness(0, 10, 0, 8)));
        ab.Children.Add(Info("Application", AppPaths.ExePath));
        ab.Children.Add(Info("Données et réglages", AppPaths.Data));
        ab.Children.Add(Ui.Text("Le robot Android est reproduit à partir d'un travail créé et partagé par Google, utilisé selon les conditions de la licence Creative Commons Attribution 3.0. Outils téléphone : libimobiledevice (LGPL) et Android SDK Platform-Tools (Google).", "Small", 12).With(t => { t.TextWrapping = TextWrapping.Wrap; t.Margin = new Thickness(0, 12, 0, 0); }));
        root.Children.Add(Section(ab));

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        // ---- Événements
        foreach (var hex in Accents)
        {
            var b = new Button { Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0), Cursor = Cursors.Hand, Tag = hex, ToolTip = hex, FocusVisualStyle = null };
            System.Windows.Automation.AutomationProperties.SetName(b, "Couleur " + hex);
            var tpl = new ControlTemplate(typeof(Button));
            var el = new FrameworkElementFactory(typeof(Border));
            el.SetValue(Border.CornerRadiusProperty, new CornerRadius(20));
            el.SetValue(Border.BackgroundProperty, Ui.Hex(hex));
            el.SetValue(Border.BorderThicknessProperty, new Thickness(3));
            el.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            tpl.VisualTree = el;
            b.Template = tpl;
            b.Click += (_, _) =>
            {
                Settings.Current.Accent = hex; Settings.Current.Save();
                App.ApplyAccent(hex);
                UpdateSwatches();
                AppState.Notify();
            };
            _swatches.Children.Add(b);
        }

        foreach (var d in DurationChoices)
        {
            var rb = new RadioButton { Style = (Style)Application.Current.Resources["Chip"], GroupName = "qdays", Content = $"{d} jours", Tag = d };
            rb.Checked += (_, _) =>
            {
                if (_loading) return;
                Settings.Current.QuarantineDays = d; Settings.Current.Save();
                MainWindow.Instance?.RefreshProtection();
                UpdateInfo();
            };
            _durations.Children.Add(rb);
        }

        _drive.SelectionChanged += (_, _) =>
        {
            if (_loading || _drive.SelectedItem is not ComboBoxItem it) return;
            Settings.Current.QuarantineDrive = (string)it.Tag ?? ""; Settings.Current.Save();
        };
        _restore.Checked += (_, _) => { if (!_loading) { Settings.Current.RestorePointBeforeClean = true; Settings.Current.Save(); } };
        _restore.Unchecked += (_, _) => { if (!_loading) { Settings.Current.RestorePointBeforeClean = false; Settings.Current.Save(); } };
    }

    static Border Section(UIElement child)
    {
        var c = Ui.Card(child);
        c.Margin = new Thickness(0, 0, 0, 16);
        return c;
    }

    static FrameworkElement Info(string label, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        Ui.Grid2(g, "170", "*");
        g.Children.Add(Ui.Text(label, "Small", 13));
        var v = new TextBox { Text = value ?? "", IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0), FontFamily = (FontFamily)Application.Current.Resources["Mono"], FontSize = 12, TextWrapping = TextWrapping.Wrap };
        v.SetResourceReference(ForegroundProperty, "Text2");
        g.Children.Add(v.At(1));
        return g;
    }

    public void OnShow(object arg)
    {
        _loading = true;
        var s = Settings.Current;
        UpdateSwatches();

        _drive.Items.Clear();
        var auto = new ComboBoxItem { Content = "Automatique (disque le plus libre)", Tag = "" };
        _drive.Items.Add(auto);
        ComboBoxItem selected = auto;
        foreach (var d in SystemInfo.Drives())
        {
            var it = new ComboBoxItem { Content = $"{d.Letter}  {d.Label}  ·  {Fmt.Size(d.Free)} libres", Tag = d.Letter };
            _drive.Items.Add(it);
            if (!string.IsNullOrEmpty(s.QuarantineDrive) && d.Letter.StartsWith(s.QuarantineDrive, StringComparison.OrdinalIgnoreCase)) selected = it;
        }
        _drive.SelectedItem = selected;

        foreach (RadioButton rb in _durations.Children) rb.IsChecked = (int)rb.Tag == s.QuarantineDays;
        if (!DurationChoices.Contains(s.QuarantineDays)) ((RadioButton)_durations.Children[1]).IsChecked = true;
        _restore.IsChecked = s.RestorePointBeforeClean;
        _loading = false;
        UpdateInfo();
    }

    void UpdateSwatches()
    {
        foreach (Button b in _swatches.Children)
            b.BorderBrush = string.Equals((string)b.Tag, Settings.Current.Accent, StringComparison.OrdinalIgnoreCase) ? Ui.Res("Text") : Brushes.Transparent;
    }

    void UpdateInfo()
    {
        var size = Quarantine.TotalSize();
        var next = Quarantine.NextPurge();
        _qInfo.Text = size > 0
            ? $"Actuellement : {Fmt.Size(size)} en quarantaine · prochaine suppression le {Fmt.Date(next)}."
            : "La quarantaine est vide.";
    }

    async Task PurgeNow()
    {
        var size = Quarantine.TotalSize();
        if (size <= 0) { Dialog.Info("Quarantaine vide", "Il n'y a rien à supprimer."); return; }
        if (!Dialog.Confirm("Vider la quarantaine ?", $"{Fmt.Size(size)} vont être supprimés définitivement. Ces actions ne pourront plus être annulées depuis l'Historique.", "Supprimer définitivement", danger: true)) return;
        var mw = MainWindow.Instance;
        mw.ShowBusy("Suppression de la quarantaine…");
        long freed = 0;
        try { freed = await Task.Run(() => Quarantine.Purge(0)); }
        catch (Exception ex) { Log.Error("Vidage de la quarantaine", ex); }
        mw.HideBusy();
        AppState.RefreshDrives();
        AppState.Notify();
        UpdateInfo();
        Dialog.Info("Quarantaine vidée", $"{Fmt.Size(freed)} libérés.");
    }

    async Task Install()
    {
        var mw = MainWindow.Instance;
        mw.ShowBusy("Installation…");
        string path = null; Exception err = null;
        try { path = await Task.Run(AutoClean.InstallCopy); }
        catch (Exception ex) { err = ex; Log.Error("Installation", ex); }
        mw.HideBusy();
        if (err != null) Dialog.Info("Installation impossible", "L'appli n'a pas pu être copiée : " + err.Message);
        else Dialog.Info("Installation terminée", $"L'appli est installée dans :\n{path}\n\nUtilise le raccourci « Nettoyeur PC » du Bureau ou du menu Démarrer : elle s'ouvrira sans la fenêtre de confirmation de Windows.");
    }
}
