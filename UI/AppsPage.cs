using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

public class AppsPage : UserControl, IPage
{
    List<AppInfo> _apps = new();
    string _drive = "";          // "" = tous
    string _sort = "size";
    readonly StackPanel _chips = new() { Orientation = Orientation.Horizontal };
    readonly TextBox _filter = new() { Width = 240, Height = 36, VerticalContentAlignment = VerticalAlignment.Center };
    readonly StackPanel _rows = new();
    readonly TextBlock _sub = Ui.Text("Lecture des programmes installés…", "Small", 14);
    readonly TextBlock _count = Ui.Text("", "Small", 13);
    readonly Button _uninstallSel;
    readonly HashSet<AppInfo> _checked = new();
    readonly Dictionary<AppInfo, TextBlock> _sizeLabels = new();
    readonly Dictionary<string, ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);
    CancellationTokenSource _measureCts;
    bool _loaded, _loading;
    const int MaxRows = 400;

    public AppsPage()
    {
        var root = new Grid { Margin = new Thickness(40, 30, 40, 24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        Ui.Grid2(head, "*", "auto");
        var hs = new StackPanel();
        hs.Children.Add(Ui.Text("Applications", "H1"));
        hs.Children.Add(_sub.With(t => t.Margin = new Thickness(0, 6, 0, 0)));
        head.Children.Add(hs);
        var refresh = Ui.IconBtn("", "Actualiser la liste", (_, _) => Load());
        refresh.Width = 40; refresh.Height = 40; refresh.VerticalAlignment = VerticalAlignment.Bottom;
        head.Children.Add(refresh.At(1));
        root.Children.Add(head);

        // Barre de filtres
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        Ui.Grid2(bar, "*", "auto", "auto");
        bar.Children.Add(new ScrollViewer { Content = _chips, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var sortBox = new Border { CornerRadius = new CornerRadius(9), Padding = new Thickness(3), Margin = new Thickness(12, 0, 12, 0), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        sortBox.SetResourceReference(Border.BackgroundProperty, "Surface");
        sortBox.SetResourceReference(Border.BorderBrushProperty, "Line2");
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (k, l) in new[] { ("size", "Taille"), ("name", "Nom"), ("date", "Date") })
        {
            var rb = new RadioButton { Style = (Style)Application.Current.Resources["Seg"], Content = l, GroupName = "appsort", IsChecked = k == _sort };
            rb.Checked += (_, _) => { _sort = k; Fill(); };
            sp.Children.Add(rb);
        }
        sortBox.Child = sp;
        bar.Children.Add(sortBox.At(1));
        var fg = new Grid();
        fg.Children.Add(_filter);
        var ph = Ui.Text("Rechercher un programme…", "Small", 13);
        ph.IsHitTestVisible = false; ph.Margin = new Thickness(12, 0, 0, 0); ph.VerticalAlignment = VerticalAlignment.Center;
        fg.Children.Add(ph);
        _filter.TextChanged += (_, _) => { ph.Visibility = _filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Fill(); };
        bar.Children.Add(fg.At(2));
        root.Children.Add(bar.At(0, 1));

        // Liste
        var header = new Grid { Margin = new Thickness(20, 0, 20, 8) };
        Cols(header);
        void H(string t, int c, HorizontalAlignment a = HorizontalAlignment.Left) => header.Children.Add(Ui.Text(t, "Small", 12).With(x => { x.HorizontalAlignment = a; }).At(c));
        H("Programme", 2); H("Disque", 3); H("Taille", 4, HorizontalAlignment.Right); H("Installé le", 5, HorizontalAlignment.Right);
        var list = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        list.Children.Add(header);
        var sv = new ScrollViewer { Content = _rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        list.Children.Add(sv);
        var card = Ui.Card(list, new Thickness(0, 14, 0, 6));
        root.Children.Add(card.At(0, 2));

        // Barre d'action
        var act = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        Ui.Grid2(act, "*", "auto");
        var tip = Ui.Text("Désinstaller ouvre l'assistant du programme au premier plan : réponds à ses questions et refuse le redémarrage s'il le propose. Déplacer libère la place sans réinstaller : le programme continue de marcher.", "Small", 12);
        tip.TextWrapping = TextWrapping.Wrap; tip.VerticalAlignment = VerticalAlignment.Center; tip.Margin = new Thickness(0, 0, 20, 0);
        act.Children.Add(tip);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_count.With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 14, 0); }));
        _uninstallSel = Ui.Btn("Désinstaller la sélection", "Danger", async (_, _) => await Uninstall(_checked.ToList()), "");
        _uninstallSel.Height = 40; _uninstallSel.IsEnabled = false;
        right.Children.Add(_uninstallSel);
        act.Children.Add(right.At(1));
        root.Children.Add(act.At(0, 3));
        Content = root;
    }

    static void Cols(Grid g) => Ui.Grid2(g, "34", "46", "*", "90", "100", "110", "150");

    public void OnShow(object arg)
    {
        if (!_loaded || arg as string == "refresh") Load();
    }

    async void Load()
    {
        if (_loading) return;
        _loading = true; _loaded = true;
        _measureCts?.Cancel();
        _sub.Text = "Lecture des programmes installés…";
        _rows.Children.Clear();
        _rows.Children.Add(Ui.Text("Un instant…", "Body").With(t => t.Margin = new Thickness(20)));
        try
        {
            _apps = await Task.Run(Apps.List);
            _checked.Clear();
            BuildChips(); Fill();
            Log.Info($"Applications : {_apps.Count} programmes");
            // Taille réelle des dossiers + icônes, en arrière-plan
            var cts = _measureCts = new CancellationTokenSource();
            var list = _apps.ToList();
            _ = Task.Run(() =>
            {
                foreach (var a in list) { if (cts.IsCancellationRequested) return; LoadIcon(a); }
                Dispatcher.BeginInvoke(() => { if (!cts.IsCancellationRequested) Fill(); });
                try { Apps.MeasureAll(list, a => Dispatcher.BeginInvoke(() => { if (_sizeLabels.TryGetValue(a, out var t)) t.Text = SizeText(a); }), cts.Token); } catch (OperationCanceledException) { return; }
                Dispatcher.BeginInvoke(() => { if (!cts.IsCancellationRequested) { BuildChips(); UpdateSub(); } });
            });
        }
        catch (Exception ex) { Log.Error("Lecture des programmes", ex); _sub.Text = "Impossible de lire la liste des programmes."; }
        finally { _loading = false; }
    }

    void UpdateSub()
    {
        var total = _apps.Sum(a => Math.Max(0, a.Size));
        var moved = _apps.Count(a => a.IsJunction);
        _sub.Text = $"{_apps.Count} programmes installés · {Fmt.Size(total)}" + (moved > 0 ? $" · {moved} déplacé(s) sur un autre disque" : "");
    }

    void BuildChips()
    {
        UpdateSub();
        _chips.Children.Clear();
        void Chip(string key, string label)
        {
            var rb = new RadioButton { Style = (Style)Application.Current.Resources["Chip"], Content = label, GroupName = "appdrive", IsChecked = key == _drive };
            rb.Checked += (_, _) => { _drive = key; Fill(); };
            _chips.Children.Add(rb);
        }
        Chip("", $"Tous · {_apps.Count}");
        foreach (var g in _apps.GroupBy(a => a.Drive).OrderBy(g => g.Key == "?" ? "ZZ" : g.Key))
        {
            var size = g.Sum(a => Math.Max(0, a.Size));
            Chip(g.Key, g.Key == "?" ? $"Emplacement inconnu · {g.Count()}" : $"{g.Key} · {g.Count()} · {Fmt.Size(size)}");
        }
    }

    static string SizeText(AppInfo a) => a.Size > 0 ? Fmt.Size(a.Size) : "—";

    void Fill()
    {
        _rows.Children.Clear(); _sizeLabels.Clear();
        var f = _filter.Text.Trim();
        IEnumerable<AppInfo> q = _apps;
        if (_drive != "") q = q.Where(a => a.Drive == _drive);
        if (f.Length > 0) q = q.Where(a => a.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase) || a.Publisher.Contains(f, StringComparison.CurrentCultureIgnoreCase));
        q = _sort switch
        {
            "name" => q.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            "date" => q.OrderByDescending(a => a.Installed),
            _ => q.OrderByDescending(a => a.Size),
        };
        var items = q.ToList();
        foreach (var a in items.Take(MaxRows)) _rows.Children.Add(Row(a));
        if (items.Count > MaxRows) _rows.Children.Add(Ui.Text($"… et {items.Count - MaxRows} autres : affine la recherche.", "Small", 13).With(t => t.Margin = new Thickness(20, 10, 0, 10)));
        if (items.Count == 0) _rows.Children.Add(Ui.Text("Aucun programme ne correspond.", "Body").With(t => t.Margin = new Thickness(20)));
        UpdateSel();
    }

    UIElement Row(AppInfo a)
    {
        var g = new Grid { Margin = new Thickness(20, 0, 20, 0), MinHeight = 58, Background = Brushes.Transparent };
        Cols(g);
        var cb = new CheckBox { IsChecked = _checked.Contains(a), VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(cb, "Sélectionner " + a.Name);
        cb.Checked += (_, _) => { _checked.Add(a); UpdateSel(); };
        cb.Unchecked += (_, _) => { _checked.Remove(a); UpdateSel(); };
        g.Children.Add(cb.At(0));
        g.Children.Add(IconTile(a).At(1));

        var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 8, 12, 8) };
        var nl = new StackPanel { Orientation = Orientation.Horizontal };
        nl.Children.Add(new TextBlock { Text = a.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = a.Name });
        if (a.IsJunction) nl.Children.Add(Ui.Pill($"déplacé → {a.JunctionTarget?[..2]}", "InfoBg", "InfoFg").With(p => p.Margin = new Thickness(10, 0, 0, 0)));
        name.Children.Add(nl);
        var meta = string.Join(" · ", new[] { a.Publisher, a.Version }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var loc = a.IsJunction ? $"{a.AppDir} → {a.JunctionTarget}" : a.AppDir ?? "Emplacement inconnu";
        name.Children.Add(new TextBlock { Text = meta.Length > 0 ? meta : loc, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = loc }.With(t => t.SetResourceReference(TextBlock.ForegroundProperty, "Muted")));
        g.Children.Add(name.At(2));

        g.Children.Add(Ui.Text(a.Drive == "?" ? "—" : a.Drive, "Body", 13).With(t => t.VerticalAlignment = VerticalAlignment.Center).At(3));
        var sz = Ui.Text(SizeText(a), "Body", 13).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.HorizontalAlignment = HorizontalAlignment.Right; t.SetResourceReference(TextBlock.ForegroundProperty, "Text"); });
        _sizeLabels[a] = sz;
        g.Children.Add(sz.At(4));
        g.Children.Add(Ui.Text(a.Installed == default ? "—" : Fmt.Date(a.Installed), "Body", 13).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.HorizontalAlignment = HorizontalAlignment.Right; }).At(5));

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var open = Ui.IconBtn("", "Ouvrir l'emplacement", (_, _) => Shell.OpenInExplorer(a.IsJunction ? a.JunctionTarget : a.AppDir));
        open.IsEnabled = a.AppDir != null;
        var move = a.IsJunction
            ? Ui.IconBtn("", $"Remettre sur {a.AppDir?[..2]}", async (_, _) => await MoveBack(a))
            : Ui.IconBtn("", "Déplacer vers un autre disque (sans réinstaller)", async (_, _) => await Move(a));
        var un = Ui.IconBtn("", "Désinstaller", async (_, _) => await Uninstall(new List<AppInfo> { a }));
        foreach (var b in new[] { open, move, un }) { b.Margin = new Thickness(6, 0, 0, 0); btns.Children.Add(b); }
        g.Children.Add(btns.At(6));

        var sep = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = g };
        sep.SetResourceReference(Border.BorderBrushProperty, "Line");
        sep.MouseEnter += (_, _) => sep.SetResourceReference(Border.BackgroundProperty, "Surface2");
        sep.MouseLeave += (_, _) => sep.Background = Brushes.Transparent;
        return sep;
    }

    // ------------------------------------------------------------------ Icônes
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint n);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

    void LoadIcon(AppInfo a)
    {
        var src = a.DisplayIcon;
        if (string.IsNullOrWhiteSpace(src)) return;
        lock (_icons) if (_icons.ContainsKey(a.Name)) return;
        try
        {
            var s = Environment.ExpandEnvironmentVariables(src.Trim());
            int idx = 0;
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^(.*?),\s*(-?\d+)$");
            if (m.Success) { s = m.Groups[1].Value; idx = int.Parse(m.Groups[2].Value); }
            s = s.Trim().Trim('"');
            if (!File.Exists(s)) return;
            var large = new IntPtr[1];
            if (ExtractIconEx(s, idx, large, null, 1) == 0 || large[0] == IntPtr.Zero) return;
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                bmp.Freeze();
                lock (_icons) _icons[a.Name] = bmp;
            }
            finally { DestroyIcon(large[0]); }
        }
        catch { }
    }

    static readonly string[] TileColors = { "#2E5E55", "#3B4A6B", "#5B3F63", "#6B4E2E", "#2F5A3A", "#5E3434", "#2F4F6B" };
    UIElement IconTile(AppInfo a)
    {
        var b = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        ImageSource img; lock (_icons) _icons.TryGetValue(a.Name, out img);
        if (img != null) { b.Child = new Image { Source = img, Width = 28, Height = 28 }; return b; }
        b.Background = Ui.Hex(TileColors[Math.Abs(a.Name.GetHashCode()) % TileColors.Length]);
        b.Child = new TextBlock { Text = a.Name[..1].ToUpperInvariant(), FontWeight = FontWeights.Bold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        return b;
    }

    void UpdateSel()
    {
        _checked.RemoveWhere(a => !_apps.Contains(a));
        _uninstallSel.IsEnabled = _checked.Count > 0;
        _count.Text = _checked.Count == 0 ? "" : $"{_checked.Count} sélectionné(s)";
    }

    // ------------------------------------------------------------------ Désinstallation
    async Task Uninstall(List<AppInfo> sel)
    {
        if (sel.Count == 0) return;
        var names = string.Join("\n", sel.Take(12).Select(a => "•  " + a.Name)) + (sel.Count > 12 ? $"\n… et {sel.Count - 12} autres" : "");
        if (!Dialog.Confirm(sel.Count == 1 ? $"Désinstaller {sel[0].Name} ?" : $"Désinstaller {sel.Count} programmes ?",
            names + "\n\nChaque programme ouvre son propre assistant de désinstallation au premier plan : réponds à ses questions (et refuse le redémarrage s'il le propose). L'appli attend la fin de chacun avant de passer au suivant.",
            "Désinstaller", danger: true)) return;

        var ct = MainWindow.Instance.ShowBusy("Désinstallation", true, "Ne plus attendre");
        int i = 0;
        var done = new List<string>(); var still = new List<string>();
        try
        {
            foreach (var a in sel)
            {
                i++;
                MainWindow.Instance.ShowBusy($"Désinstallation de {a.Name}" + (sel.Count > 1 ? $" ({i}/{sel.Count})" : ""), true, "Ne plus attendre", keepToken: true);
                MainWindow.Instance.BusyStatus("Réponds à la fenêtre de désinstallation. Si elle est bloquée ou cachée : « Ne plus attendre ».");
                var finished = await Task.Run(() => Apps.RunUninstaller(a, MainWindow.Instance.BusyStatus, ct));
                if (!finished) { MainWindow.Instance.ResetBusyToken(); ct = MainWindow.Instance.BusyToken; }
                (await Task.Run(() => Apps.StillInstalled(a)) ? still : done).Add(a.Name);
            }
        }
        finally
        {
            MainWindow.Instance.HideBusy();
            MainWindow.Instance.Activate();
        }
        if (done.Count > 0)
            History.Add(new HistoryEvent { Title = done.Count == 1 ? $"{done[0]} désinstallé" : $"{done.Count} programmes désinstallés", Sub = string.Join(", ", done) });
        var msg = (done.Count > 0 ? "Désinstallé : " + string.Join(", ", done) + "\n\n" : "") +
                  (still.Count > 0 ? "Toujours présent (annulé, ou désinstallation qui continue en arrière-plan) : " + string.Join(", ", still) + "\n\n" : "") +
                  "Des restes peuvent subsister dans le registre : l'écran Registre sait les trouver.";
        Dialog.Info("Désinstallation terminée", msg.Trim());
        AppState.RefreshDrives(); AppState.Notify();
        Load();
    }

    // ------------------------------------------------------------------ Déplacement
    async Task Move(AppInfo a)
    {
        var why = Apps.CheckMovable(a);
        if (why != null) { Dialog.Info("Déplacement impossible", why); return; }

        MainWindow.Instance.ShowBusy("Vérification…");
        List<(int Id, string Name)> procs; List<(string Name, string Display, bool Running)> svcs; long size;
        try
        {
            (procs, svcs, size) = await Task.Run(() => (Apps.ProcessesIn(a.AppDir), Apps.ServicesIn(a.AppDir), a.RealSize >= 0 ? a.RealSize : Quarantine.DirSize(a.AppDir)));
        }
        finally { MainWindow.Instance.HideBusy(); }
        if (procs.Count > 0)
        {
            Dialog.Info("Programme ouvert", $"Ferme d'abord {a.Name} (et ses fenêtres en arrière-plan) puis recommence.\n\nEn cours : " + string.Join(", ", procs.Select(p => p.Name).Distinct()));
            return;
        }
        var shared = _apps.Where(o => o != a && o.AppDir != null && (o.AppDir.Equals(a.AppDir, StringComparison.OrdinalIgnoreCase) || o.AppDir.StartsWith(a.AppDir + "\\", StringComparison.OrdinalIgnoreCase))).Select(o => o.Name).ToList();

        // Choix du disque
        var drives = DriveInfo.GetDrives().Where(d => { try { return d.IsReady && d.DriveType == DriveType.Fixed && !d.Name.StartsWith(a.AppDir[..2], StringComparison.OrdinalIgnoreCase); } catch { return false; } })
            .OrderByDescending(d => d.AvailableFreeSpace).ToList();
        if (drives.Count == 0) { Dialog.Info("Aucun autre disque", "Il n'y a pas d'autre disque interne pour accueillir ce programme."); return; }
        var extra = new StackPanel();
        extra.Children.Add(Ui.Text("Vers quel disque ?", "Small", 13).With(t => t.Margin = new Thickness(0, 0, 0, 8)));
        var chips = new WrapPanel();
        var dest = Ui.Text("", "Small", 12).With(t => { t.Margin = new Thickness(0, 10, 0, 0); t.TextWrapping = TextWrapping.Wrap; t.FontFamily = (FontFamily)Application.Current.Resources["Mono"]; });
        string target = null;
        foreach (var d in drives)
        {
            var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name[..2] : $"{d.Name[..2]} {d.VolumeLabel}";
            var rb = new RadioButton { Style = (Style)Application.Current.Resources["Chip"], Content = $"{label} · {Fmt.Size(d.AvailableFreeSpace)} libres", GroupName = "movedrive", Margin = new Thickness(0, 0, 8, 8), IsEnabled = d.AvailableFreeSpace > size + 1_000_000_000L };
            var root = d.RootDirectory.FullName;
            rb.Checked += (_, _) => { target = Apps.DefaultTarget(a, root); dest.Text = "→ " + target; };
            chips.Children.Add(rb);
        }
        extra.Children.Add(chips);
        extra.Children.Add(dest);
        var firstOk = chips.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsEnabled);
        if (firstOk == null) { Dialog.Info("Pas assez de place", $"Aucun disque n'a assez d'espace libre pour {Fmt.Size(size)}."); return; }
        firstOk.IsChecked = true;

        var warn = new List<string>();
        if (svcs.Count > 0) warn.Add("Service(s) Windows arrêté(s) pendant le déplacement puis relancé(s) : " + string.Join(", ", svcs.Select(s => s.Display)));
        if (shared.Count > 0) warn.Add("Ce dossier contient aussi : " + string.Join(", ", shared.Take(5)) + " (déplacé(s) en même temps).");
        var text = $"{Fmt.Size(size)} vont être copiés sur l'autre disque, vérifiés, puis l'original sera remplacé par un lien : Windows et le programme continuent de le trouver au même endroit, sans réinstallation.\n\nAnnulable depuis l'Historique." +
                   (warn.Count > 0 ? "\n\n" + string.Join("\n", warn) : "");
        if (!Dialog.Confirm($"Déplacer {a.Name} ?", text, "Déplacer", extra: extra) || target == null) return;

        var ct = MainWindow.Instance.ShowBusy($"Déplacement de {a.Name}", true);
        (bool Ok, string Message) r;
        try
        {
            r = await Task.Run(() =>
            {
                foreach (var s in svcs.Where(s => s.Running)) { MainWindow.Instance.BusyStatus("Arrêt du service " + s.Display + "…"); Shell.Run("net.exe", $"stop \"{s.Name}\" /y", 60000); }
                try { return Apps.Move(a, target, MainWindow.Instance.BusyStatus, ct); }
                finally { foreach (var s in svcs.Where(s => s.Running)) Shell.Run("net.exe", $"start \"{s.Name}\"", 60000); }
            });
        }
        finally { MainWindow.Instance.HideBusy(); }
        Dialog.Info(r.Ok ? "C'est fait" : "Déplacement non effectué", r.Message);
        AppState.RefreshDrives(); AppState.Notify();
        Load();
    }

    async Task MoveBack(AppInfo a)
    {
        var ev = History.Events.FirstOrDefault(e => e.CanUndo && e.Sub != null && e.Sub.Contains(a.AppDir + " → ", StringComparison.OrdinalIgnoreCase));
        if (ev == null)
        {
            Dialog.Info("Remettre sur " + a.AppDir[..2], $"Ce déplacement n'a pas été fait par l'appli (ou n'est plus dans l'historique).\n\nLe programme est en réalité dans :\n{a.JunctionTarget}");
            return;
        }
        if (!Dialog.Confirm($"Remettre {a.Name} sur {a.AppDir[..2]} ?", $"Les fichiers reviennent dans {a.AppDir}. Ferme le programme avant de continuer.", "Remettre")) return;
        MainWindow.Instance.ShowBusy($"Retour de {a.Name} sur {a.AppDir[..2]}");
        MainWindow.Instance.BusyStatus("Copie des fichiers…");
        (int Ok, int Failed) r;
        try { r = await Task.Run(() => History.Undo(ev)); }
        finally { MainWindow.Instance.HideBusy(); }
        Dialog.Info(r.Failed == 0 ? "C'est fait" : "Retour incomplet", r.Failed == 0 ? $"{a.Name} est de retour dans {a.AppDir}." : "Le retour ne s'est pas fait entièrement : le détail est dans le journal.");
        AppState.RefreshDrives(); AppState.Notify();
        Load();
    }
}
