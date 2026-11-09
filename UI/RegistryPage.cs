using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

/// <summary>Écran « Registre » : entrées orphelines laissées par des logiciels désinstallés. Sauvegarde .reg avant toute correction.</summary>
public class RegistryPage : UserControl, IPage
{
    const int PageSize = 150;          // lignes créées à la fois dans une catégorie (évite de geler l'interface)
    const int CollapseAbove = 40;      // catégories plus grandes repliées par défaut

    readonly StackPanel _root = new() { Margin = new Thickness(40, 30, 40, 30), MaxWidth = 1400 };
    readonly Button _scanBtn, _fixBtn, _restoreBtn;
    readonly Border _progressCard;
    readonly TextBlock _progressText = new() { Margin = new Thickness(0, 0, 0, 10) };
    readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14), Visibility = Visibility.Collapsed };
    readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
    readonly StackPanel _cats = new();
    readonly Border _empty;
    readonly TextBlock _selText = new() { VerticalAlignment = VerticalAlignment.Center };
    readonly Border _actionBar;

    List<RegFinding> _all = new();
    readonly Dictionary<RegFinding, CheckBox> _rowBoxes = new();
    readonly Dictionary<string, CheckBox> _catBoxes = new();
    bool _busy, _syncing;
    CancellationTokenSource _cts;

    public RegistryPage()
    {
        // ---- En-tête
        var head = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        Ui.Grid2(head, "*", "auto");
        var ht = new StackPanel();
        ht.Children.Add(Ui.Text("Registre", "H1"));
        ht.Children.Add(Ui.Text("Recherche prudente des entrées laissées par des logiciels désinstallés. Une entrée n'est signalée que si le fichier visé est sur un disque présent et n'existe vraiment plus. Une sauvegarde .reg est créée avant chaque correction, et tout s'annule depuis l'Historique.", "Body")
            .With(t => { t.Margin = new Thickness(0, 6, 0, 0); t.TextWrapping = TextWrapping.Wrap; t.MaxWidth = 820; }));
        head.Children.Add(ht);
        var hb = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        _restoreBtn = Ui.Btn("Restaurer une sauvegarde", "Ghost", (_, _) => RestoreBackup(), "");
        _restoreBtn.Margin = new Thickness(0, 0, 10, 0);
        _scanBtn = Ui.Btn("Analyser le registre", "Primary", (_, _) => _ = Scan(), "");
        hb.Children.Add(_restoreBtn); hb.Children.Add(_scanBtn);
        head.Children.Add(hb.At(1));
        _root.Children.Add(head);

        // ---- Progression
        var ps = new StackPanel();
        _progressText.SetResourceReference(TextBlock.ForegroundProperty, "Text2");
        ps.Children.Add(_progressText);
        ps.Children.Add(new ProgressBar { Style = (Style)Application.Current.Resources["Bar"], IsIndeterminate = true, Height = 6 });
        var stop = Ui.Btn("Arrêter", "Ghost", (_, _) => _cts?.Cancel());
        stop.HorizontalAlignment = HorizontalAlignment.Right; stop.Margin = new Thickness(0, 14, 0, 0);
        ps.Children.Add(stop);
        _progressCard = Ui.Card(ps);
        _progressCard.Margin = new Thickness(0, 0, 0, 16);
        _progressCard.Visibility = Visibility.Collapsed;
        _root.Children.Add(_progressCard);

        // ---- Barre de sélection
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        _summary.FontWeight = FontWeights.SemiBold;
        _toolbar.Children.Add(_summary);
        foreach (var (label, act) in new (string, Action)[]
        {
            ("Tout cocher", () => SetAll(_ => true)),
            ("Cocher les « Sûr »", () => SetAll(f => f.Risk == Risk.Safe)),
            ("Tout décocher", () => SetAll(_ => false)),
        })
        {
            var b = Ui.Btn(label, "Ghost", (_, _) => act()); b.Margin = new Thickness(0, 0, 8, 0); b.Height = 34;
            _toolbar.Children.Add(b);
        }
        _root.Children.Add(_toolbar);

        // ---- Écran vide
        var es = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 30, 0, 30) };
        var ei = Ui.Icon("", 40); ei.HorizontalAlignment = HorizontalAlignment.Center; ei.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
        es.Children.Add(ei);
        es.Children.Add(Ui.Text("Lance une analyse pour vérifier le registre", "H3").With(t => { t.Margin = new Thickness(0, 14, 0, 6); t.HorizontalAlignment = HorizontalAlignment.Center; }));
        es.Children.Add(Ui.Text("Programmes désinstallés, démarrage, « Ouvrir avec », DLL partagées, polices, pare-feu, menu clic droit, services et tâches planifiées.", "Body")
            .With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.TextAlignment = TextAlignment.Center; t.TextWrapping = TextWrapping.Wrap; t.MaxWidth = 560; }));
        _empty = Ui.Card(es);
        _root.Children.Add(_empty);

        _root.Children.Add(_cats);

        // ---- Barre d'action en bas
        var ab = new Grid();
        Ui.Grid2(ab, "*", "auto");
        _selText.SetResourceReference(TextBlock.ForegroundProperty, "Text2");
        ab.Children.Add(_selText);
        _fixBtn = Ui.Btn("Corriger la sélection", "Primary", (_, _) => _ = Fix(), "");
        ab.Children.Add(_fixBtn.At(1));
        _actionBar = new Border { Padding = new Thickness(40, 14, 40, 14), BorderThickness = new Thickness(0, 1, 0, 0), Child = ab, Visibility = Visibility.Collapsed };
        _actionBar.SetResourceReference(Border.BackgroundProperty, "Sidebar");
        _actionBar.SetResourceReference(Border.BorderBrushProperty, "Line");

        var dock = new DockPanel();
        DockPanel.SetDock(_actionBar, Dock.Bottom);
        dock.Children.Add(_actionBar);
        dock.Children.Add(new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = dock;
    }

    public void OnShow(object arg)
    {
        if (arg as string == "scan" && !_busy) _ = Scan();
    }

    // ------------------------------------------------------------------ analyse
    async Task Scan()
    {
        if (_busy) return;
        _busy = true; _scanBtn.IsEnabled = false; _fixBtn.IsEnabled = false; _restoreBtn.IsEnabled = false;
        _empty.Visibility = Visibility.Collapsed; _toolbar.Visibility = Visibility.Collapsed; _actionBar.Visibility = Visibility.Collapsed;
        _cats.Children.Clear(); _rowBoxes.Clear(); _catBoxes.Clear();
        _progressText.Text = "Préparation…"; _progressCard.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _all = await Task.Run(() => RegistryScan.Find(s => Dispatcher.BeginInvoke(() => _progressText.Text = "Vérification : " + s + "…"), ct));
            Log.Info($"Analyse du registre : {_all.Count} entrée(s) orpheline(s) en {sw.Elapsed.TotalSeconds:0.0} s");
        }
        catch (OperationCanceledException) { _all = new(); Log.Info("Analyse du registre arrêtée."); }
        catch (Exception ex) { _all = new(); Log.Error("Analyse du registre", ex); Dialog.Info("Analyse impossible", "L'analyse du registre a échoué. Le détail est dans le journal."); }
        finally
        {
            _progressCard.Visibility = Visibility.Collapsed;
            _busy = false; _scanBtn.IsEnabled = true; _fixBtn.IsEnabled = true; _restoreBtn.IsEnabled = true;
        }
        Render(scanned: true);
    }

    void Render(bool scanned)
    {
        _cats.Children.Clear(); _rowBoxes.Clear(); _catBoxes.Clear();
        if (_all.Count == 0)
        {
            _toolbar.Visibility = Visibility.Collapsed; _actionBar.Visibility = Visibility.Collapsed;
            if (scanned)
            {
                var s = new StackPanel { Orientation = Orientation.Horizontal };
                var i = Ui.Icon("", 22); i.SetResourceReference(TextBlock.ForegroundProperty, "SafeFg"); s.Children.Add(i);
                var tx = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
                tx.Children.Add(Ui.Text("Registre propre", "H3"));
                tx.Children.Add(Ui.Text("Aucune entrée orpheline trouvée. Rien à corriger.", "Body").With(t => t.Margin = new Thickness(0, 4, 0, 0)));
                s.Children.Add(tx);
                _cats.Children.Add(Ui.Card(s));
            }
            else _empty.Visibility = Visibility.Visible;
            return;
        }
        _empty.Visibility = Visibility.Collapsed;
        _toolbar.Visibility = Visibility.Visible; _actionBar.Visibility = Visibility.Visible;
        _summary.Text = $"{Fmt.Num(_all.Count)} entrée(s) orpheline(s) dans {_all.Select(f => f.Cat).Distinct().Count()} catégorie(s)";

        var order = RegistryScan.CategoryOrder.ToList();
        foreach (var g in _all.GroupBy(f => f.Cat).OrderBy(g => order.IndexOf(g.Key) is var i && i < 0 ? 99 : i))
            _cats.Children.Add(BuildCategory(g.Key, g.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList()));
        UpdateSelection();
    }

    Border BuildCategory(string cat, List<RegFinding> items)
    {
        var body = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        bool expanded = items.Count <= CollapseAbove;
        int shown = 0;

        var chevron = Ui.Icon(expanded ? "" : "", 12);
        chevron.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        var cb = new CheckBox { VerticalAlignment = VerticalAlignment.Center, IsThreeState = false, Margin = new Thickness(10, 0, 12, 0) };
        System.Windows.Automation.AutomationProperties.SetName(cb, "Sélectionner la catégorie " + cat);
        _catBoxes[cat] = cb;
        cb.Click += (_, _) =>
        {
            var on = cb.IsChecked == true;
            foreach (var f in items) f.Checked = on;
            SyncBoxes(); UpdateSelection();
        };

        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(Ui.Text(cat, "H3"));
        title.Children.Add(Ui.Text($"{Fmt.Num(items.Count)}", "Small").With(t => { t.Margin = new Thickness(10, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
        var risky = items.Any(f => f.Risk != Risk.Safe);
        var pill = risky ? Ui.Pill("Modéré", Risk.Check) : Ui.Pill("Sûr", Risk.Safe);
        pill.Margin = new Thickness(12, 0, 0, 0);
        title.Children.Add(pill);

        var hdr = new Grid { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand };
        Ui.Grid2(hdr, "auto", "auto", "*", "auto");
        hdr.Children.Add(chevron.At(0));
        hdr.Children.Add(cb.At(1));
        hdr.Children.Add(title.At(2));
        var hint = Ui.Text(CategoryHint(cat), "Small", 12);
        hint.VerticalAlignment = VerticalAlignment.Center; hint.TextTrimming = TextTrimming.CharacterEllipsis; hint.MaxWidth = 420;
        hdr.Children.Add(hint.At(3));

        var more = Ui.Btn("", "LinkBtn");
        more.Margin = new Thickness(0, 8, 0, 0); more.HorizontalAlignment = HorizontalAlignment.Left;

        void ShowMore()
        {
            body.Children.Remove(more);
            var end = Math.Min(items.Count, shown + PageSize);
            for (; shown < end; shown++) body.Children.Add(Row(items[shown], shown > 0));
            if (shown < items.Count) { more.Content = $"Afficher {Math.Min(PageSize, items.Count - shown)} de plus ({Fmt.Num(items.Count - shown)} restantes)"; body.Children.Add(more); }
        }
        more.Click += (_, _) => ShowMore();

        void SetExpanded(bool on)
        {
            expanded = on;
            chevron.Text = on ? "" : "";
            body.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on && shown == 0) ShowMore();
        }
        hdr.MouseLeftButtonUp += (s, e) => { if (e.OriginalSource is DependencyObject d && IsInside(d, cb)) return; SetExpanded(!expanded); };

        var sp = new StackPanel();
        sp.Children.Add(hdr);
        sp.Children.Add(body);
        body.Visibility = Visibility.Collapsed;
        if (expanded) SetExpanded(true);
        else
        {
            var note = Ui.Text($"{Fmt.Num(items.Count)} entrées, repliées pour garder l'écran fluide. Clique sur le titre pour les voir, ou coche la catégorie pour tout corriger d'un coup.", "Small", 12);
            note.Margin = new Thickness(46, 8, 0, 0); note.TextWrapping = TextWrapping.Wrap;
            sp.Children.Add(note);
            body.IsVisibleChanged += (_, _) => note.Visibility = body.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        }

        var card = Ui.Card(sp, new Thickness(20, 16, 20, 16));
        card.Margin = new Thickness(0, 0, 0, 12);
        return card;
    }

    static bool IsInside(DependencyObject d, DependencyObject target)
    {
        while (d != null) { if (d == target) return true; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d); }
        return false;
    }

    UIElement Row(RegFinding f, bool separator)
    {
        var g = new Grid { Margin = new Thickness(0), MinHeight = 46 };
        Ui.Grid2(g, "34", "*", "16", "1.1*", "12", "auto");

        var cb = new CheckBox { IsChecked = f.Checked, VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(cb, "Sélectionner " + f.Name);
        cb.Checked += (_, _) => { if (_syncing) return; f.Checked = true; UpdateSelection(); };
        cb.Unchecked += (_, _) => { if (_syncing) return; f.Checked = false; UpdateSelection(); };
        _rowBoxes[f] = cb;
        g.Children.Add(cb.At(0));

        var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 6) };
        var name = Ui.Text(f.Name, "Body"); name.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = f.Name;
        txt.Children.Add(name);
        var det = Ui.Text(f.Detail, "Small", 12); det.TextTrimming = TextTrimming.CharacterEllipsis;
        det.ToolTip = new TextBlock { Text = f.Detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 };
        txt.Children.Add(det);
        g.Children.Add(txt.At(1));

        var loc = new TextBlock { Text = f.Location, FontFamily = (FontFamily)Application.Current.Resources["Mono"], FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        loc.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        loc.ToolTip = new TextBlock { Text = f.Location, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 };
        g.Children.Add(loc.At(3));

        var pill = f.Risk == Risk.Safe ? Ui.Pill("Sûr", Risk.Safe) : Ui.Pill("Modéré", Risk.Check);
        pill.ToolTip = f.Risk == Risk.Safe ? "Entrée orpheline sans effet sur Windows : la retirer ne change rien au fonctionnement." : "Service, tâche ou extension du clic droit : vérifie que le logiciel n'est plus utilisé avant de corriger.";
        g.Children.Add(pill.At(5));

        var b = new Border { Child = g, BorderThickness = new Thickness(0, separator ? 1 : 0, 0, 0) };
        b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return b;
    }

    static string CategoryHint(string cat) => cat switch
    {
        "Programmes désinstallés" => "Restent affichés dans « Programmes et fonctionnalités »",
        "Démarrage" => "Lancements au démarrage vers un programme supprimé",
        "Chemins d'applications" => "Raccourcis « Exécuter » vers un programme supprimé",
        "Ouvrir avec" => "Programmes supprimés proposés dans « Ouvrir avec »",
        "DLL partagées" => "Compteurs de fichiers partagés supprimés",
        "Dossiers d'installation" => "Dossiers d'installation qui n'existent plus",
        "Polices" => "Polices déclarées sans fichier",
        "Historique (MuiCache)" => "Noms mémorisés de programmes supprimés",
        "Historique (compatibilité)" => "Réglages de compatibilité de programmes supprimés",
        "Pare-feu" => "Règles pour des programmes supprimés",
        "Menu clic droit" => "Extensions cassées, source de lenteurs de l'Explorateur",
        "Services" => "Services dont le programme n'existe plus",
        "Tâches planifiées" => "Tâches qui lancent un programme supprimé",
        _ => "",
    };

    // ------------------------------------------------------------------ sélection
    void SetAll(Func<RegFinding, bool> rule)
    {
        foreach (var f in _all) f.Checked = rule(f);
        SyncBoxes(); UpdateSelection();
    }

    void SyncBoxes()
    {
        _syncing = true;
        foreach (var (f, cb) in _rowBoxes) cb.IsChecked = f.Checked;
        _syncing = false;
    }

    void UpdateSelection()
    {
        var n = 0; var mod = 0;
        foreach (var f in _all) if (f.Checked) { n++; if (f.Risk != Risk.Safe) mod++; }
        _selText.Text = n == 0 ? "Aucune entrée sélectionnée." :
            $"{Fmt.Num(n)} / {Fmt.Num(_all.Count)} entrée(s) sélectionnée(s)" + (mod > 0 ? $", dont {mod} « Modéré »" : "") + ". Sauvegarde .reg automatique avant correction.";
        _fixBtn.IsEnabled = n > 0 && !_busy;
        _syncing = true;
        foreach (var g in _all.GroupBy(f => f.Cat))
            if (_catBoxes.TryGetValue(g.Key, out var cb))
            {
                var c = g.Count(f => f.Checked);
                cb.IsChecked = c == 0 ? false : c == g.Count() ? true : null;
            }
        _syncing = false;
    }

    // ------------------------------------------------------------------ correction
    async Task Fix()
    {
        var sel = _all.Where(f => f.Checked).ToList();
        if (sel.Count == 0 || _busy) return;
        var mod = sel.Count(f => f.Risk != Risk.Safe);
        var msg = $"{Fmt.Num(sel.Count)} entrée(s) du registre vont être retirées.\n\nUne sauvegarde complète des clés concernées est créée d'abord : tu pourras tout remettre depuis l'Historique ou avec « Restaurer une sauvegarde ».";
        if (mod > 0) msg += $"\n\n{mod} entrée(s) « Modéré » (services, tâches, menu clic droit) sont incluses.";
        if (!Dialog.Confirm("Corriger le registre ?", msg, "Sauvegarder et corriger")) return;

        _busy = true; _fixBtn.IsEnabled = false; _scanBtn.IsEnabled = false;
        var mw = MainWindow.Instance;
        mw.ShowBusy("Correction du registre");
        int ok = 0, ko = 0;
        string regFile = null;
        List<(string, string)> tasks = new();
        var fixedSet = new HashSet<RegFinding>();
        try
        {
            await Task.Run(() =>
            {
                mw.BusyStatus("Sauvegarde des clés concernées…");
                (regFile, tasks) = RegistryScan.Backup(sel);
                int i = 0;
                foreach (var f in sel)
                {
                    if (++i % 25 == 0) mw.BusyStatus($"Correction… {i} / {sel.Count}");
                    try { RegistryScan.Repair(f); ok++; lock (fixedSet) fixedSet.Add(f); Log.Info($"Registre corrigé : [{f.Cat}] {f.Name}"); }
                    catch (Exception ex) { ko++; Log.Warn($"Registre, échec : [{f.Cat}] {f.Name} ({ex.Message})"); }
                }
            });
        }
        catch (Exception ex) { Log.Error("Correction du registre", ex); }
        finally { mw.HideBusy(); _busy = false; _scanBtn.IsEnabled = true; }

        if (regFile != null && ok > 0)
        {
            var ev = new HistoryEvent
            {
                Title = "Registre corrigé",
                Sub = $"{Fmt.Num(ok)} entrée(s) orpheline(s) retirée(s)" + (ko > 0 ? $", {ko} échec(s)" : ""),
            };
            ev.UndoCommands.Add($"reg.exe|import \"{regFile}\"");
            foreach (var (full, xml) in tasks) ev.UndoCommands.Add($"schtasks.exe|/create /tn \"{full}\" /xml \"{xml}\" /f");
            History.Add(ev);
            AppState.Notify();
        }

        _all = _all.Where(f => !fixedSet.Contains(f)).ToList();
        Render(scanned: true);
        Dialog.Info(ko == 0 ? "Registre corrigé" : "Correction terminée",
            $"{Fmt.Num(ok)} entrée(s) corrigée(s)" + (ko > 0 ? $", {ko} n'ont pas pu l'être (détail dans le journal)" : "") + ".\n\nSauvegarde : " + (regFile ?? "—"));
    }

    // ------------------------------------------------------------------ restauration
    void RestoreBackup()
    {
        var files = RegistryScan.Backups();
        if (files.Count == 0) { Dialog.Info("Aucune sauvegarde", "Aucune sauvegarde du registre n'a encore été créée. Elles apparaissent ici après une correction."); return; }

        var list = new StackPanel();
        RadioButton first = null;
        foreach (var f in files.Take(15))
        {
            var rb = new RadioButton { GroupName = "regbak", Tag = f.FullName, Margin = new Thickness(0, 0, 0, 8), VerticalContentAlignment = VerticalAlignment.Center };
            rb.SetResourceReference(ForegroundProperty, "Text");
            rb.Content = new TextBlock { Text = $"{Fmt.DateTime(f.LastWriteTime)}  ·  {Fmt.Size(f.Length)}  ·  {f.Name}", Margin = new Thickness(6, 0, 0, 0) };
            first ??= rb;
            list.Children.Add(rb);
        }
        first.IsChecked = true;
        var open = Ui.Btn("Ouvrir le dossier des sauvegardes", "LinkBtn", (_, _) => Shell.OpenInExplorer(RegistryScan.BackupDir));
        open.HorizontalAlignment = HorizontalAlignment.Left; open.Margin = new Thickness(0, 6, 0, 0);
        list.Children.Add(open);
        var sv = new ScrollViewer { Content = list, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        if (!Dialog.Confirm("Restaurer une sauvegarde", "Les clés sauvegardées seront remises telles qu'elles étaient avant la correction (les tâches planifiées sauvegardées en même temps aussi).", "Restaurer", "Annuler", false, sv)) return;
        var file = list.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true)?.Tag as string;
        if (file == null) return;
        var mw = MainWindow.Instance;
        mw.ShowBusy("Restauration du registre");
        Task.Run(() => RegistryScan.Restore(file)).ContinueWith(t => Dispatcher.Invoke(() =>
        {
            mw.HideBusy();
            if (t.Result) Dialog.Info("Sauvegarde restaurée", "Le registre a été remis comme avant. Relance une analyse si tu veux vérifier.");
            else Dialog.Info("Restauration incomplète", "Certaines clés n'ont pas pu être restaurées. Le détail est dans le journal.");
        }));
    }
}
