using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

public class HistoryPage : UserControl, IPage
{
    readonly StackPanel _root = new() { Margin = new Thickness(40, 30, 40, 40), MaxWidth = 1400 };
    readonly Border _banner = new() { CornerRadius = new CornerRadius(16), Padding = new Thickness(28, 24, 28, 24), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 20), Visibility = Visibility.Collapsed };
    readonly Grid _safety = new() { Margin = new Thickness(0, 0, 0, 20) };
    readonly StackPanel _events = new();
    readonly Border _autoCard;
    readonly ToggleButton _autoSwitch = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly StackPanel _days = new() { Orientation = Orientation.Horizontal };
    readonly CheckBox _warnLow = new() { Content = "M'avertir à l'ouverture si un disque passe sous 15 % d'espace libre", Margin = new Thickness(0, 0, 0, 12) };
    readonly CheckBox _purge = new() { Margin = new Thickness(0, 0, 0, 12) };
    readonly CheckBox _browsers = new() { Content = "Inclure le cache des navigateurs" };
    readonly ScrollViewer _scroll;
    string _restorePoints = "…";
    bool _loading;

    static readonly (string Code, string Label)[] Days = { ("MON", "Lu"), ("TUE", "Ma"), ("WED", "Me"), ("THU", "Je"), ("FRI", "Ve"), ("SAT", "Sa"), ("SUN", "Di") };

    public HistoryPage()
    {
        _banner.Background = Ui.Hex("#14271F");
        _banner.BorderBrush = Ui.Hex("#2B4A43");
        _root.Children.Add(_banner);

        Ui.Grid2(_safety, "*", "16", "*", "16", "*");
        _root.Children.Add(_safety);

        var two = new Grid();
        Ui.Grid2(two, "3*", "20", "2*");

        // ---- Historique
        var hs = new StackPanel();
        hs.Children.Add(Ui.Text("Historique", "H3").With(t => t.Margin = new Thickness(0, 0, 0, 10)));
        hs.Children.Add(_events);
        var hc = Ui.Card(hs, new Thickness(22, 20, 22, 14));
        hc.VerticalAlignment = VerticalAlignment.Top;
        two.Children.Add(hc.At(0));

        // ---- Nettoyage automatique
        _autoSwitch.Style = (Style)Application.Current.Resources["Switch"];
        System.Windows.Automation.AutomationProperties.SetName(_autoSwitch, "Activer le nettoyage automatique");
        var a = new StackPanel();
        var ah = new Grid(); Ui.Grid2(ah, "*", "auto");
        ah.Children.Add(Ui.Text("Nettoyage automatique", "H3").With(t => t.VerticalAlignment = VerticalAlignment.Center));
        ah.Children.Add(_autoSwitch.At(1));
        a.Children.Add(ah);
        a.Children.Add(Ui.Text("Une fois par semaine à 20 h, en arrière-plan, uniquement les éléments « Sans risque ». Rien n'est demandé, rien n'est mis en quarantaine.", "Body").With(t => t.Margin = new Thickness(0, 12, 0, 16)));
        a.Children.Add(Ui.Text("Jour", "Small", 13).With(t => t.Margin = new Thickness(0, 0, 0, 8)));
        a.Children.Add(_days);
        var opts = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
        opts.Children.Add(_warnLow); opts.Children.Add(_purge); opts.Children.Add(_browsers);
        a.Children.Add(opts);
        a.Children.Add(Ui.Text("Pour que la tâche planifiée fonctionne, l'appli est copiée dans Program Files à l'activation.", "Small", 12).With(t => t.Margin = new Thickness(0, 16, 0, 0)));
        _autoCard = Ui.Card(a);
        _autoCard.VerticalAlignment = VerticalAlignment.Top;
        two.Children.Add(_autoCard.At(2));
        _root.Children.Add(two);

        foreach (var (code, label) in Days)
        {
            var rb = new RadioButton { Style = (Style)Application.Current.Resources["Chip"], GroupName = "autoday", Tag = code, Width = 44, Height = 44, Margin = new Thickness(0, 0, 6, 0) };
            rb.Content = new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center };
            rb.Checked += async (_, _) =>
            {
                if (_loading) return;
                Settings.Current.AutoDay = code; Settings.Current.Save();
                if (Settings.Current.AutoClean)
                {
                    var ok = await Task.Run(() => AutoClean.Enable(code));
                    if (!ok) Dialog.Info("Planification impossible", "La tâche planifiée n'a pas pu être modifiée. Le détail est dans le journal.");
                }
            };
            _days.Children.Add(rb);
        }

        _autoSwitch.Checked += async (_, _) => await SetAuto(true);
        _autoSwitch.Unchecked += async (_, _) => await SetAuto(false);
        _warnLow.Checked += (_, _) => Save(s => s.WarnLowSpace = true);
        _warnLow.Unchecked += (_, _) => Save(s => { s.WarnLowSpace = false; s.LowSpaceWarning = ""; });
        _purge.Checked += (_, _) => Save(s => s.AutoPurgeQuarantine = true);
        _purge.Unchecked += (_, _) => Save(s => s.AutoPurgeQuarantine = false);
        _browsers.Checked += (_, _) => Save(s => s.AutoIncludeBrowsers = true);
        _browsers.Unchecked += (_, _) => Save(s => s.AutoIncludeBrowsers = false);

        _scroll = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = _scroll;
        AppState.Changed += () => { if (IsLoaded) Refresh(); };
    }

    void Save(Action<Settings> a) { if (_loading) return; a(Settings.Current); Settings.Current.Save(); }

    async Task SetAuto(bool on)
    {
        if (_loading) return;
        var day = Settings.Current.AutoDay;
        var ok = await Task.Run(() => { if (on) return AutoClean.Enable(day); AutoClean.Disable(); return true; });
        if (!ok)
        {
            Dialog.Info("Planification impossible", "Le nettoyage automatique n'a pas pu être activé. Le détail est dans le journal.");
            _loading = true; _autoSwitch.IsChecked = !on; _loading = false;
            return;
        }
        Settings.Current.AutoClean = on; Settings.Current.Save();
        AppState.Notify();
    }

    public void OnShow(object arg)
    {
        HistoryEvent ev = arg as HistoryEvent;
        if (ev == null && AppState.LastResult != null && (DateTime.Now - AppState.LastResult.Date).TotalMinutes < 10) ev = AppState.LastResult;
        ShowBanner(ev);
        Refresh();
        Task.Run(() =>
        {
            var n = SystemInfo.RestorePointCount();
            Dispatcher.BeginInvoke(() => { _restorePoints = n.ToString(); BuildSafety(); });
        });
        if (arg as string == "auto")
            Dispatcher.BeginInvoke(() =>
            {
                _autoCard.BringIntoView();
                _autoCard.SetResourceReference(Border.BorderBrushProperty, "Accent");
                _autoCard.BorderThickness = new Thickness(2);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        else
        {
            _autoCard.SetResourceReference(Border.BorderBrushProperty, "Line");
            _autoCard.BorderThickness = new Thickness(1);
            _scroll.ScrollToTop();
        }
    }

    void ShowBanner(HistoryEvent ev)
    {
        if (ev == null) { _banner.Visibility = Visibility.Collapsed; return; }
        _banner.Visibility = Visibility.Visible;
        var g = new Grid();
        Ui.Grid2(g, "auto", "*", "auto");
        var check = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 22, 0), VerticalAlignment = VerticalAlignment.Center };
        check.SetResourceReference(Border.BackgroundProperty, "Accent");
        var ic = Ui.Icon("", 24); ic.HorizontalAlignment = HorizontalAlignment.Center; ic.FontWeight = FontWeights.Bold; ic.SetResourceReference(TextBlock.ForegroundProperty, "OnAccent");
        check.Child = ic;
        g.Children.Add(check);

        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(Ui.Text($"Nettoyage terminé : {Fmt.Size(ev.Bytes)} libérés", "H2", 24));
        var sub = !string.IsNullOrEmpty(ev.Batch)
            ? $"Les fichiers « à vérifier » sont en quarantaine jusqu'au {Fmt.Date(ev.Date.AddDays(Settings.Current.QuarantineDays))}. Tu peux tout annuler d'ici là."
            : "Tout est déjà libéré : il s'agissait de caches et de fichiers temporaires.";
        t.Children.Add(Ui.Text(sub, "Body").With(x => x.Margin = new Thickness(0, 6, 0, 0)));
        g.Children.Add(t.At(1));

        var sys = AppState.Drives.FirstOrDefault(d => d.IsSystem);
        if (sys != null && sys.Size > 0)
        {
            var after = sys.Free; var before = Math.Max(0, sys.Free - ev.Bytes);
            var b = new StackPanel { Width = 300, Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var lg = new Grid(); Ui.Grid2(lg, "*", "auto");
            lg.Children.Add(Ui.Text($"{sys.Letter} avant · {Fmt.Size(before)} libres", "Small", 12.5));
            lg.Children.Add(Ui.Text($"après · {Fmt.Size(after)}", "Small", 12.5).At(1));
            b.Children.Add(lg);
            var bar = new StackBar { Height = 10, Margin = new Thickness(0, 8, 0, 0) };
            bar.Parts = new List<(double, Brush)>
            {
                ((double)(sys.Size - after), Ui.Hex("#5B6475")),
                ((double)(after - before), Ui.Res("Accent")),
                ((double)before, Ui.Hex("#252A34")),
            };
            b.Children.Add(bar);
            g.Children.Add(b.At(2));
        }
        _banner.Child = g;
    }

    void Refresh()
    {
        _loading = true;
        var s = Settings.Current;
        _autoSwitch.IsChecked = s.AutoClean;
        foreach (RadioButton rb in _days.Children) rb.IsChecked = (string)rb.Tag == s.AutoDay;
        _warnLow.IsChecked = s.WarnLowSpace;
        _purge.Content = $"Vider la quarantaine après {s.QuarantineDays} jours";
        _purge.IsChecked = s.AutoPurgeQuarantine;
        _browsers.IsChecked = s.AutoIncludeBrowsers;
        _loading = false;
        BuildSafety();
        BuildEvents();
    }

    void BuildSafety()
    {
        _safety.Children.Clear();
        var q = Quarantine.TotalSize();
        var next = Quarantine.NextPurge();
        var undoable = History.Events.Count(e => e.CanUndo);
        _safety.Children.Add(Safety("Quarantaine", Fmt.Size(q), q > 0 && next != default ? $"Suppression définitive le {Fmt.Date(next)}" : "Vide").At(0));
        _safety.Children.Add(Safety("Actions annulables", undoable.ToString(), undoable > 0 ? "Bouton « Annuler » dans l'historique" : "Aucune pour l'instant").At(2));
        _safety.Children.Add(Safety("Points de restauration", _restorePoints, "Créés par Windows et avant les nettoyages").At(4));
    }

    static FrameworkElement Safety(string label, string value, string sub)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(label, "Small", 13));
        sp.Children.Add(Ui.Text(value, "Big", 26).With(t => t.Margin = new Thickness(0, 8, 0, 6)));
        sp.Children.Add(Ui.Text(sub, "Small", 13));
        return Ui.Card(sp, new Thickness(20));
    }

    void BuildEvents()
    {
        _events.Children.Clear();
        var list = History.Events.Take(50).ToList();
        if (list.Count == 0)
        {
            _events.Children.Add(Ui.Text("Aucune action pour l'instant. Les nettoyages, déplacements et suppressions apparaîtront ici, avec un bouton pour les annuler.", "Body").With(t => t.Margin = new Thickness(0, 4, 0, 10)));
            return;
        }
        foreach (var e in list) _events.Children.Add(EventRow(e));
    }

    FrameworkElement EventRow(HistoryEvent e)
    {
        var g = new Grid { Margin = new Thickness(0, 0, 0, 0), MinHeight = 58 };
        Ui.Grid2(g, "60", "auto", "*", "auto");
        var when = Ui.Text(e.Date.Date == DateTime.Today ? Fmt.Time(e.Date) : e.Date.ToString("dd/MM"), "Small", 13);
        when.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(when);

        var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        if (e.CanUndo) dot.SetResourceReference(Border.BackgroundProperty, "Accent");
        else dot.Background = Ui.Hex(e.Undone ? "#F5A524" : "#5B6475");
        g.Children.Add(dot.At(1));

        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 12, 10) };
        var title = Ui.Text(e.Title ?? "", "H3", 14);
        title.TextTrimming = TextTrimming.CharacterEllipsis;
        if (e.Undone) { title.TextDecorations = TextDecorations.Strikethrough; title.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); }
        t.Children.Add(title);
        if (!string.IsNullOrEmpty(e.Sub)) t.Children.Add(Ui.Text(e.Sub, "Small", 13).With(x => x.Margin = new Thickness(0, 2, 0, 0)));
        g.Children.Add(t.At(2));

        if (e.CanUndo)
        {
            var b = Ui.Btn("Annuler", "Ghost", async (_, _) => await Undo(e), "");
            b.Height = 34; b.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(b.At(3));
        }
        else if (e.Undone || e.Expired)
        {
            var lbl = Ui.Text(e.Undone ? "Annulé" : "Expiré", "Small", 13);
            lbl.VerticalAlignment = VerticalAlignment.Center;
            if (e.Undone) lbl.SetResourceReference(TextBlock.ForegroundProperty, "WarnFg");
            g.Children.Add(lbl.At(3));
        }

        var row = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = g };
        row.SetResourceReference(Border.BorderBrushProperty, "Line");
        return row;
    }

    async Task Undo(HistoryEvent e)
    {
        var what = e.Items.Count > 0 ? $"{e.Items.Count} fichier{(e.Items.Count > 1 ? "s" : "")} vont être remis à leur place d'origine." : "Les réglages modifiés vont être rétablis.";
        if (!Dialog.Confirm("Annuler cette action ?", $"« {e.Title} »\n\n{what}", "Annuler l'action", "Garder")) return;
        var mw = MainWindow.Instance;
        mw.ShowBusy("Annulation en cours…");
        mw.BusyStatus(e.Title);
        (int Ok, int Failed) res = (0, 0);
        try { res = await Task.Run(() => History.Undo(e)); }
        catch (Exception ex) { Log.Error("Annulation", ex); }
        mw.HideBusy();
        AppState.RefreshDrives();
        AppState.Notify();
        Refresh();
        if (res.Failed > 0)
            Dialog.Info("Annulation partielle", $"{res.Ok} élément(s) restauré(s), {res.Failed} n'ont pas pu l'être (fichier introuvable ou utilisé). Le détail est dans le journal.");
    }
}
