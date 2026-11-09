using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

public class CleanPage : UserControl, IPage
{
    readonly TextBlock _kicker = Ui.Text("", "Small", 13);
    readonly TextBlock _title = Ui.Text("", "H1");
    readonly StackBar _bar = new() { Height = 14, Margin = new Thickness(0, 18, 0, 10) };
    readonly WrapPanel _legend = new();
    readonly StackPanel _list = new();
    readonly StackPanel _detail = new();
    readonly TextBlock _selText = Ui.Text("", "H3");
    readonly TextBlock _selSub = Ui.Text("", "Small", 13);
    readonly CheckBox _restore = new() { Content = "Point de restauration avant", Margin = new Thickness(0, 0, 20, 0) };
    readonly Button _cleanBtn;
    readonly Grid _main = new();
    readonly Border _scanning;
    readonly TextBlock _scanText = Ui.Text("", "Body");
    string _filter = "all";
    Rule _selected;

    public CleanPage()
    {
        // ---- État « analyse en cours »
        var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Width = 420 };
        sp.Children.Add(Ui.Text("Analyse en cours…", "H2"));
        _scanText.Margin = new Thickness(0, 10, 0, 16); _scanText.TextTrimming = TextTrimming.CharacterEllipsis; _scanText.TextWrapping = TextWrapping.NoWrap; sp.Children.Add(_scanText);
        sp.Children.Add(new ProgressBar { Style = (Style)Application.Current.Resources["Bar"], IsIndeterminate = true, Height = 6 });
        _scanning = Ui.Card(sp, new Thickness(28));
        _scanning.HorizontalAlignment = HorizontalAlignment.Center; _scanning.VerticalAlignment = VerticalAlignment.Center;

        // ---- Page principale
        _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new StackPanel { Margin = new Thickness(40, 30, 40, 0) };
        var hg = new Grid(); Ui.Grid2(hg, "*", "auto");
        var hs = new StackPanel(); hs.Children.Add(_kicker); _title.Margin = new Thickness(0, 6, 0, 0); hs.Children.Add(_title);
        hg.Children.Add(hs);
        var re = Ui.Btn("Relancer l'analyse", "Ghost", (_, _) => StartScan(true), ""); re.VerticalAlignment = VerticalAlignment.Bottom;
        hg.Children.Add(re.At(1));
        top.Children.Add(hg);
        top.Children.Add(_bar);
        top.Children.Add(_legend);
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 16) };
        foreach (var (k, l) in new[] { ("all", "Tout"), ("safe", "Sans risque"), ("check", "À vérifier et avancé") })
        {
            var rb = new RadioButton { Style = (Style)Application.Current.Resources["Chip"], Content = l, GroupName = "cleanfilter", IsChecked = k == "all" };
            rb.Checked += (_, _) => { _filter = k; BuildList(); };
            chips.Children.Add(rb);
        }
        top.Children.Add(chips);
        _main.Children.Add(top);

        var body = new Grid { Margin = new Thickness(40, 0, 40, 16) };
        Ui.Grid2(body, "2*", "20", "1*");
        body.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 6, 0) }.At(0));
        var dc = Ui.Card(new ScrollViewer { Content = _detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dc.VerticalAlignment = VerticalAlignment.Top; dc.MinWidth = 300;
        body.Children.Add(dc.At(2));
        _main.Children.Add(body.At(0, 1));

        var act = new Grid { Margin = new Thickness(40, 14, 40, 14) };
        Ui.Grid2(act, "*", "auto", "auto");
        var st = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; st.Children.Add(_selText); st.Children.Add(_selSub);
        act.Children.Add(st);
        _restore.IsChecked = Settings.Current.RestorePointBeforeClean;
        _restore.Checked += (_, _) => { Settings.Current.RestorePointBeforeClean = true; Settings.Current.Save(); };
        _restore.Unchecked += (_, _) => { Settings.Current.RestorePointBeforeClean = false; Settings.Current.Save(); };
        _restore.Foreground = Ui.Res("Text2");
        act.Children.Add(_restore.At(1));
        _cleanBtn = Ui.Btn("Nettoyer", "Primary", async (_, _) => await Clean());
        act.Children.Add(_cleanBtn.At(2));
        var actBorder = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = act };
        actBorder.SetResourceReference(Border.BackgroundProperty, "Sidebar");
        actBorder.SetResourceReference(Border.BorderBrushProperty, "Line");
        _main.Children.Add(actBorder.At(0, 2));

        Content = _main;
    }

    public async void OnShow(object arg)
    {
        if (arg as string == "rescan") { StartScan(true); return; }
        if (!AppState.Session.Scanned)
        {
            if (AppState.ScanTask == null || AppState.ScanTask.IsCompleted) { StartScan(false); return; }
            Content = _scanning; _scanText.Text = "Recherche des fichiers inutiles…";
            await AppState.ScanTask;
        }
        if (arg is string id && AppState.Session.Rules.FirstOrDefault(r => r.Id == id) is Rule r) _selected = r;
        Content = _main;
        Build();
    }

    async void StartScan(bool force)
    {
        Content = _scanning;
        _scanText.Text = "Préparation…";
        AppState.Session = new CleanSession();
        var s = AppState.Session;
        var prog = new Progress<string>(t => _scanText.Text = t);
        AppState.ScanTask = s.ScanAsync(prog);
        try { await AppState.ScanTask; } catch (Exception ex) { Log.Error("Analyse", ex); }
        AppState.Notify();
        _selected = null;
        Content = _main;
        Build();
    }

    IEnumerable<Rule> Visible => AppState.Session.Rules.Where(r => r.Size > 0 || r.Size == -2);

    void Build()
    {
        var s = AppState.Session;
        var vis = Visible.ToList();
        _kicker.Text = $"Analyse terminée en {s.ScanDuration.TotalSeconds:0} s · {s.Rules.Count} emplacements vérifiés · {Fmt.Ago(s.ScanDate)}";
        _title.Text = vis.Count == 0 ? "Ton PC est propre : rien à nettoyer" : $"{Fmt.Size(s.Total)} récupérables sur ton PC";

        var cats = Rules.CategoryOrder.Select(c => (Cat: c, Size: vis.Where(r => r.Category == c && r.Size > 0).Sum(r => r.Size))).Where(x => x.Size > 0).ToList();
        _bar.Parts = cats.Select(c => ((double)c.Size, Ui.Hex(Rules.CategoryColor[c.Cat]))).ToList();
        _bar.InvalidateVisual();
        _legend.Children.Clear();
        foreach (var c in cats)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 6) };
            sp.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = Ui.Hex(Rules.CategoryColor[c.Cat]), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(Ui.Text($"{c.Cat} · {Fmt.Size(c.Size)}", "Small", 13).With(t => t.Margin = new Thickness(8, 0, 0, 0)));
            _legend.Children.Add(sp);
        }
        _selected ??= vis.Where(r => r.Size > 0).OrderByDescending(r => r.Size).FirstOrDefault();
        BuildList();
        ShowDetail();
        UpdateSelection();
    }

    void BuildList()
    {
        _list.Children.Clear();
        var vis = Visible.Where(r => _filter == "all" || (_filter == "safe" ? r.Risk == Risk.Safe : r.Risk != Risk.Safe)).ToList();
        if (vis.Count == 0) { _list.Children.Add(Ui.Card(Ui.Text("Rien dans cette catégorie.", "Body"))); return; }
        foreach (var cat in Rules.CategoryOrder)
        {
            var rules = vis.Where(r => r.Category == cat).OrderByDescending(r => r.Size).ToList();
            if (rules.Count == 0) continue;
            var sp = new StackPanel();
            var head = new Grid { Margin = new Thickness(18, 14, 18, 12) };
            Ui.Grid2(head, "auto", "*", "auto");
            head.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = Ui.Hex(Rules.CategoryColor[cat]), Margin = new Thickness(0, 0, 12, 0) });
            head.Children.Add(Ui.Text(cat, "H3").At(1));
            head.Children.Add(Ui.Text(Fmt.Size(rules.Where(r => r.Size > 0).Sum(r => r.Size)), "Small", 13).At(2));
            sp.Children.Add(head);
            foreach (var r in rules) sp.Children.Add(Row(r));
            var card = new Border { Style = (Style)Application.Current.Resources["Card"], Padding = new Thickness(0, 0, 0, 6), Margin = new Thickness(0, 0, 0, 14), Child = sp };
            _list.Children.Add(card);
        }
    }

    FrameworkElement Row(Rule r)
    {
        var g = new Grid { Margin = new Thickness(18, 0, 14, 0), Height = 44 };
        Ui.Grid2(g, "auto", "*", "auto", "96", "auto");
        var cb = new CheckBox { IsChecked = r.Checked, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(cb, r.Name);
        cb.Checked += (_, _) => { r.Checked = true; UpdateSelection(); };
        cb.Unchecked += (_, _) => { r.Checked = false; UpdateSelection(); };
        g.Children.Add(cb);
        var name = Ui.Text(r.Name, "Body", 14, Ui.Res("Text")); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Margin = new Thickness(12, 0, 12, 0); name.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(name.At(1));
        g.Children.Add(Ui.Pill(r.RiskLabel, r.Risk).At(2));
        var size = Ui.Text(r.Size == -2 ? "à l'usage" : Fmt.Size(r.Size), "Body", 14, Ui.Res("Text")); size.TextAlignment = TextAlignment.Right; size.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(size.At(3));
        var more = Ui.IconBtn("", "Voir le détail", (_, _) => { _selected = r; ShowDetail(); BuildList(); });
        more.Margin = new Thickness(12, 0, 0, 0);
        g.Children.Add(more.At(4));
        var row = new Border { Child = g, Background = r == _selected ? Ui.Res("Surface3") : Brushes.Transparent, Cursor = Cursors.Hand };
        row.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is FrameworkElement fe && (fe.TemplatedParent is CheckBox || fe is CheckBox)) return; _selected = r; ShowDetail(); BuildList(); };
        return row;
    }

    void ShowDetail()
    {
        _detail.Children.Clear();
        var r = _selected;
        if (r == null) { _detail.Children.Add(Ui.Text("Sélectionne un élément pour voir le détail.", "Body")); return; }
        var k = Ui.Text(r.Category.ToUpperInvariant(), "Small", 12); _detail.Children.Add(k);
        var t = Ui.Text(r.Name, "H2"); t.Margin = new Thickness(0, 8, 0, 12); _detail.Children.Add(t);
        var pills = new WrapPanel();
        pills.Children.Add(Ui.Pill(r.RiskLabel, r.Risk).With(p => p.Margin = new Thickness(0, 0, 8, 6)));
        pills.Children.Add(Ui.Pill(r.Size == -2 ? "Gain mesuré à l'usage" : Fmt.Size(r.Size), "Surface3", "Text2").With(p => p.Margin = new Thickness(0, 0, 8, 6)));
        if (r.UseQuarantine) pills.Children.Add(Ui.Pill($"Quarantaine {Settings.Current.QuarantineDays} jours", "InfoBg", "InfoFg").With(p => p.Margin = new Thickness(0, 0, 8, 6)));
        else if (r.Irreversible) pills.Children.Add(Ui.Pill("Définitif", "DangerBg", "DangerFg").With(p => p.Margin = new Thickness(0, 0, 8, 6)));
        else if (r.CustomApply != null && r.Category.StartsWith("Système")) pills.Children.Add(Ui.Pill("Annulable", "InfoBg", "InfoFg").With(p => p.Margin = new Thickness(0, 0, 8, 6)));
        _detail.Children.Add(pills);
        var what = Ui.Text(r.What, "Body", 14, Ui.Res("Text")); what.Margin = new Thickness(0, 10, 0, 14); _detail.Children.Add(what);
        var after = new StackPanel();
        after.Children.Add(Ui.Text("Après le nettoyage", "Small", 12, Ui.Res("Accent"), FontWeights.SemiBold));
        after.Children.Add(Ui.Text(r.After, "Body", 14, Ui.Res("Text")).With(x => x.Margin = new Thickness(0, 6, 0, 0)));
        var ab = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Child = after };
        ab.SetResourceReference(Border.BackgroundProperty, "Surface2");
        _detail.Children.Add(ab);
        var where = r.Where ?? r.Targets().Select(x => x.Path).FirstOrDefault(p => Directory.Exists(p) || File.Exists(p)) ?? r.Targets().Select(x => x.Path).FirstOrDefault();
        if (!string.IsNullOrEmpty(where))
        {
            _detail.Children.Add(Ui.Text("Emplacement", "Small", 12).With(x => x.Margin = new Thickness(0, 16, 0, 6)));
            var code = new TextBox { Text = where, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = (FontFamily)Application.Current.Resources["Mono"], FontSize = 12, BorderThickness = new Thickness(0), Padding = new Thickness(10) };
            code.Background = Ui.Hex("#12151B"); code.Foreground = Ui.Res("Text2");
            _detail.Children.Add(code);
            var real = Environment.ExpandEnvironmentVariables(where.Replace("%LOCALAPPDATA%", AppPaths.Local).Replace("%APPDATA%", AppPaths.Roaming).Replace("%PROGRAMDATA%", AppPaths.ProgramData));
            if (Directory.Exists(real) || File.Exists(real))
                _detail.Children.Add(Ui.Btn("Ouvrir l'emplacement", "Ghost", (_, _) => Shell.OpenInExplorer(real), "").With(b => { b.Margin = new Thickness(0, 10, 0, 0); b.HorizontalAlignment = HorizontalAlignment.Left; }));
        }
    }

    void UpdateSelection()
    {
        var sel = Visible.Where(r => r.Checked).ToList();
        long tot = sel.Where(r => r.Size > 0).Sum(r => r.Size);
        _selText.Text = $"{sel.Count} élément{(sel.Count > 1 ? "s" : "")} sélectionné{(sel.Count > 1 ? "s" : "")} · {Fmt.Size(tot)}";
        var q = sel.Count(r => r.UseQuarantine);
        _selSub.Text = q > 0 ? $"Les caches sont supprimés directement · les fichiers « à vérifier » partent {Settings.Current.QuarantineDays} jours en quarantaine"
                             : "Uniquement des caches et fichiers temporaires : supprimés directement";
        _cleanBtn.Content = sel.Count == 0 ? "Nettoyer" : "Nettoyer " + Fmt.Size(tot);
        _cleanBtn.IsEnabled = sel.Count > 0;
    }

    async Task Clean()
    {
        var sel = Visible.Where(r => r.Checked).ToList();
        if (sel.Count == 0) return;
        var total = sel.Where(r => r.Size > 0).Sum(r => r.Size);
        var warn = new StackPanel();
        var irr = sel.Where(r => r.Irreversible).ToList();
        var slow = sel.Where(r => r.Slow).ToList();
        if (irr.Count > 0) warn.Children.Add(Ui.Text("Définitif : " + string.Join(", ", irr.Select(r => r.Name)), "Small", 13, Ui.Res("WarnFg")).With(x => x.Margin = new Thickness(0, 0, 0, 8)));
        if (slow.Count > 0) warn.Children.Add(Ui.Text("Opérations longues (plusieurs minutes) : " + string.Join(", ", slow.Select(r => r.Name)), "Small", 13).With(x => x.Margin = new Thickness(0, 0, 0, 8)));
        if (System.Diagnostics.Process.GetProcesses().Any(p => p.ProcessName is "chrome" or "msedge" or "firefox" or "brave" or "discord" or "Discord"))
            warn.Children.Add(Ui.Text("Des applis sont ouvertes (navigateur, Discord…) : leurs fichiers en cours d'utilisation seront simplement ignorés.", "Small", 13));
        if (!Dialog.Confirm("Confirmer le nettoyage", $"{sel.Count} élément{(sel.Count > 1 ? "s" : "")} vont être nettoyés, pour environ {Fmt.Size(total)} récupérés.", "Nettoyer", extra: warn.Children.Count > 0 ? warn : null)) return;

        var mw = MainWindow.Instance;
        var ct = mw.ShowBusy("Nettoyage en cours…", cancellable: true);
        HistoryEvent ev = null;
        try { ev = await AppState.Session.CleanAsync(sel, _restore.IsChecked == true, new Progress<string>(mw.BusyStatus), ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("Nettoyage", ex); }
        mw.HideBusy();
        AppState.RefreshDrives();
        AppState.Notify();
        Build();
        if (ev != null) { AppState.LastResult = ev; mw.Go("history", ev); }
    }
}

public static class FluentExt
{
    public static T With<T>(this T o, Action<T> a) { a(o); return o; }
}
