using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

public class StoragePage : UserControl, IPage
{
    string _drive = AppPaths.SystemDrive;
    bool _advanced;
    readonly StackPanel _driveTabs = new() { Orientation = Orientation.Horizontal };
    readonly RadioButton _segSimple, _segAdv;
    readonly ContentControl _body = new() { Focusable = false };
    CancellationTokenSource _scanCts;

    // Vue simple
    readonly BlockTreemap _blocks = new() { Height = 430 };
    readonly TextBlock _mapTitle = Ui.Text("", "H3");
    readonly TextBlock _mapSub = Ui.Text("", "Small", 13);
    readonly StackPanel _crumbs = new() { Orientation = Orientation.Horizontal };
    readonly StackPanel _side = new();
    readonly StackPanel _bigFiles = new();
    DirNode _simpleDir;
    object _simpleSel;

    // Vue avancée
    readonly FileTreemap _map = new();
    readonly StackPanel _treeRows = new();
    readonly ScrollViewer _treeScroll;
    readonly StackPanel _extRows = new();
    readonly TextBlock _selPath = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _selInfo = Ui.Text("", "Small", 13);
    readonly TextBlock _hover = Ui.Text("Survole un rectangle pour voir le fichier", "Small", 13);
    readonly HashSet<DirNode> _open = new();
    DirNode _advDir;
    FileEntry _advFile;
    int _hlExt = -1;

    StorageScan Scan => AppState.Scans.TryGetValue(_drive, out var s) ? s : null;

    public StoragePage()
    {
        var root = new Grid { Margin = new Thickness(40, 30, 40, 24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        Ui.Grid2(head, "*", "auto", "auto", "auto");
        var hs = new StackPanel();
        hs.Children.Add(Ui.Text("Où va ton espace disque ?", "H1"));
        hs.Children.Add(Ui.Text("Clique sur un bloc pour voir ce qu'il contient, double-clique pour entrer dedans.", "Small", 14).With(t => t.Margin = new Thickness(0, 6, 0, 0)));
        head.Children.Add(hs);
        var seg = SegBox(out var segPanel);
        _segSimple = new RadioButton { Style = (Style)Application.Current.Resources["Seg"], Content = "Simple", GroupName = "stmode", IsChecked = true };
        _segAdv = new RadioButton { Style = (Style)Application.Current.Resources["Seg"], Content = "Avancé", GroupName = "stmode" };
        _segSimple.Checked += (_, _) => { _advanced = false; ShowBody(); };
        _segAdv.Checked += (_, _) => { _advanced = true; ShowBody(); };
        segPanel.Children.Add(_segSimple); segPanel.Children.Add(_segAdv);
        seg.Margin = new Thickness(16, 0, 10, 0); seg.VerticalAlignment = VerticalAlignment.Bottom;
        head.Children.Add(seg.At(1));
        var dt = SegBox(out var dtp); dtp.Children.Add(_driveTabs); dt.VerticalAlignment = VerticalAlignment.Bottom; dt.Margin = new Thickness(0, 0, 10, 0);
        head.Children.Add(dt.At(2));
        var rescan = Ui.IconBtn("", "Analyser à nouveau", (_, _) => StartScan());
        rescan.VerticalAlignment = VerticalAlignment.Bottom; rescan.Height = 40; rescan.Width = 40;
        head.Children.Add(rescan.At(3));
        root.Children.Add(head);
        root.Children.Add(_body.At(0, 1));
        Content = root;

        _blocks.BlockClicked += t => { _simpleSel = t; ShowSide(); };
        _blocks.BlockDoubleClicked += t => { if (t is DirNode d && d.Dirs.Count + d.Files.Count > 0) { _simpleDir = d; _simpleSel = null; BuildSimple(); } };

        _treeScroll = new ScrollViewer { Content = _treeRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        _map.TileClicked += (d, f) => { _advDir = d; _advFile = f; ExpandTo(d); BuildTree(); UpdateAdvSel(); _map.SelectedDir = d; _map.SelectedFile = f; _map.InvalidateVisual(); };
        _map.HoverChanged += s => _hover.Text = s ?? "Survole un rectangle pour voir le fichier";
    }

    static Border SegBox(out StackPanel panel)
    {
        panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4) };
        var b = new Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = panel };
        b.SetResourceReference(Border.BackgroundProperty, "Surface"); b.SetResourceReference(Border.BorderBrushProperty, "Line");
        return b;
    }

    public void OnShow(object arg)
    {
        if (arg is string root && root.Length >= 2) _drive = root.Substring(0, 2).ToUpperInvariant() + "\\";
        AppState.RefreshDrives();
        _driveTabs.Children.Clear();
        foreach (var d in AppState.Drives)
        {
            var rb = new RadioButton { Style = (Style)Application.Current.Resources["Seg"], Content = d.Letter, GroupName = "stdrive", IsChecked = d.Root.Equals(_drive, StringComparison.OrdinalIgnoreCase), ToolTip = $"{d.Role} · {Fmt.Size(d.Free)} libres / {Fmt.Size(d.Size)}" };
            var r = d.Root;
            rb.Checked += (_, _) => { _drive = r; _simpleDir = null; _advDir = null; _advFile = null; _open.Clear(); ShowBody(); };
            _driveTabs.Children.Add(rb);
        }
        ShowBody();
    }

    void ShowBody()
    {
        if (Scan == null) { _body.Content = Prompt(); return; }
        if (_advanced) BuildAdvanced(); else BuildSimple();
    }

    FrameworkElement Prompt()
    {
        var d = AppState.Drives.FirstOrDefault(x => x.Root.Equals(_drive, StringComparison.OrdinalIgnoreCase));
        var sp = new StackPanel { Width = 460 };
        sp.Children.Add(Ui.Text($"Analyser le disque {_drive.TrimEnd('\\')}", "H2"));
        sp.Children.Add(Ui.Text($"L'appli parcourt tous les dossiers pour mesurer ce qui prend de la place{(d != null ? $" ({Fmt.Size(d.Size - d.Free)} utilisés)" : "")}. Ça prend en général moins d'une minute, et rien n'est modifié.", "Body").With(t => t.Margin = new Thickness(0, 10, 0, 18)));
        sp.Children.Add(Ui.Btn("Lancer l'analyse", "Primary", (_, _) => StartScan(), "").With(b => b.HorizontalAlignment = HorizontalAlignment.Left));
        var c = Ui.Card(sp, new Thickness(30)); c.HorizontalAlignment = HorizontalAlignment.Center; c.VerticalAlignment = VerticalAlignment.Center;
        return c;
    }

    async void StartScan()
    {
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;
        var text = Ui.Text("Préparation…", "Body").With(t => { t.Margin = new Thickness(0, 10, 0, 16); t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; });
        var sp = new StackPanel { Width = 460 };
        sp.Children.Add(Ui.Text($"Analyse de {_drive.TrimEnd('\\')} en cours…", "H2"));
        sp.Children.Add(text);
        sp.Children.Add(new ProgressBar { Style = (Style)Application.Current.Resources["Bar"], IsIndeterminate = true, Height = 6 });
        sp.Children.Add(Ui.Btn("Arrêter", "Ghost", (_, _) => _scanCts.Cancel()).With(b => { b.HorizontalAlignment = HorizontalAlignment.Right; b.Margin = new Thickness(0, 18, 0, 0); }));
        var c = Ui.Card(sp, new Thickness(30)); c.HorizontalAlignment = HorizontalAlignment.Center; c.VerticalAlignment = VerticalAlignment.Center;
        _body.Content = c;
        var drive = _drive;
        try
        {
            var s = await StorageScan.RunAsync(drive, new Progress<string>(t => text.Text = t), ct);
            if (ct.IsCancellationRequested) { _body.Content = Prompt(); return; }
            AppState.Scans[drive] = s;
            _simpleDir = null; _advDir = null; _advFile = null; _open.Clear();
            if (drive == _drive) ShowBody();
        }
        catch (OperationCanceledException) { _body.Content = Prompt(); }
        catch (Exception ex) { Log.Error("Analyse du disque", ex); _body.Content = Prompt(); }
    }

    // =============================================================== Vue simple
    static readonly (string Bg, string Fg)[] Tones = { ("#1E4D44", "#CFF5EC"), ("#22324F", "#D6E4FF"), ("#2E2A44", "#E3DAFF"), ("#4A3714", "#FFE6B8"), ("#3A2433", "#FFD6EC"), ("#2A2F3A", "#D2D7E0") };
    sealed class Group { public string Name; public long Size; public DirNode Parent; public bool Files; }
    sealed class Free { public long Size; }

    void BuildSimple()
    {
        var scan = Scan; if (scan == null) return;
        _simpleDir ??= scan.Root;
        var d = AppState.Drives.FirstOrDefault(x => x.Root.Equals(_drive, StringComparison.OrdinalIgnoreCase));
        var dir = _simpleDir;
        _mapTitle.Text = dir == scan.Root ? $"{_drive.TrimEnd('\\')} {d?.Role} · {Fmt.Size(d?.Size ?? 0)}" : dir.FullPath;
        _mapSub.Text = dir == scan.Root ? $"{Fmt.Size(d?.Size - d?.Free ?? 0)} utilisés · {Fmt.Size(d?.Free ?? 0)} libres" : $"{Fmt.Size(dir.Size)} · {Fmt.Num(dir.FileCount)} fichiers · {Fmt.Num(dir.DirCount)} dossiers";

        // Fil d'Ariane
        _crumbs.Children.Clear();
        var chain = new List<DirNode>(); for (var x = dir; x != null; x = x.Parent) chain.Insert(0, x);
        foreach (var x in chain)
        {
            var target = x;
            var b = new Button { Style = (Style)Application.Current.Resources["LinkBtn"], Content = x == scan.Root ? _drive.TrimEnd('\\') : x.Name, Margin = new Thickness(0, 0, 6, 0) };
            if (x == dir) b.Foreground = Ui.Res("Text");
            b.Click += (_, _) => { _simpleDir = target; _simpleSel = null; BuildSimple(); };
            _crumbs.Children.Add(b);
            if (x != dir) _crumbs.Children.Add(Ui.Text("›", "Small", 13).With(t => t.Margin = new Thickness(0, 0, 6, 0)));
        }

        // Blocs : 11 plus gros sous-dossiers + « autres » + fichiers + espace libre (à la racine)
        var blocks = new List<BlockTreemap.Block>();
        int i = 0;
        foreach (var c in dir.Dirs.Take(11)) { var t = Tones[i++ % (Tones.Length - 1)]; blocks.Add(new BlockTreemap.Block { Label = c.Name, Size = c.Size, Fill = Ui.Hex(t.Bg), Fg = Ui.Hex(t.Fg), Tag = c }); }
        var rest = dir.Dirs.Skip(11).Sum(c => c.Size);
        if (rest > 0) blocks.Add(new BlockTreemap.Block { Label = $"Autres dossiers ({dir.Dirs.Count - 11})", Size = rest, Fill = Ui.Hex(Tones[5].Bg), Fg = Ui.Hex(Tones[5].Fg), Tag = new Group { Name = "Autres dossiers", Size = rest, Parent = dir } });
        var files = dir.Files.Sum(f => f.Size);
        if (files > 0)
        {
            if (dir.Files.Count <= 3) foreach (var f in dir.Files) blocks.Add(new BlockTreemap.Block { Label = f.Name, Size = f.Size, Fill = Ui.Hex("#4A3714"), Fg = Ui.Hex("#FFE6B8"), Tag = f });
            else blocks.Add(new BlockTreemap.Block { Label = $"Fichiers ({dir.Files.Count})", Size = files, Fill = Ui.Hex("#4A3714"), Fg = Ui.Hex("#FFE6B8"), Tag = new Group { Name = "Fichiers", Size = files, Parent = dir, Files = true } });
        }
        if (dir == scan.Root && d != null && d.Free > 0) blocks.Add(new BlockTreemap.Block { Label = "Espace libre", Size = d.Free, Fill = Ui.Hex("#12151B"), Fg = Ui.Hex("#9AA3B2"), Tag = new Free { Size = d.Free } });
        _blocks.SetBlocks(blocks);
        _simpleSel ??= dir.Dirs.FirstOrDefault();
        _blocks.SelectedTag = _simpleSel;

        foreach (var e in new FrameworkElement[] { _blocks, _crumbs, _mapTitle, _mapSub, _side, _bigFiles }) DetachFromParent(e);
        var mapHead = new Grid(); Ui.Grid2(mapHead, "*", "auto");
        var mh = new StackPanel(); mh.Children.Add(_crumbs); _mapTitle.Margin = new Thickness(0, 6, 0, 0); mh.Children.Add(_mapTitle);
        mapHead.Children.Add(mh);
        _mapSub.VerticalAlignment = VerticalAlignment.Bottom;
        mapHead.Children.Add(_mapSub.At(1));
        var mapCard = new StackPanel(); mapCard.Children.Add(mapHead);
        _blocks.Margin = new Thickness(-3, 12, -3, -3);
        mapCard.Children.Add(_blocks);

        var top = new Grid(); Ui.Grid2(top, "3*", "20", "2*");
        top.Children.Add(Ui.Card(mapCard, new Thickness(18)).At(0));
        top.Children.Add(Ui.Card(_side).At(2));

        BuildBigFiles(scan);
        var all = new StackPanel();
        all.Children.Add(top);
        var bf = new StackPanel();
        var bfh = new Grid(); Ui.Grid2(bfh, "*", "auto");
        bfh.Children.Add(Ui.Text("Gros fichiers personnels", "H3"));
        bfh.Children.Add(Ui.Text("Voir l'emplacement, déplacer vers un autre disque ou supprimer (récupérable dans l'Historique)", "Small", 13).At(1));
        bf.Children.Add(bfh);
        _bigFiles.Margin = new Thickness(0, 12, 0, 0);
        bf.Children.Add(_bigFiles);
        all.Children.Add(Ui.Card(bf).With(c => c.Margin = new Thickness(0, 20, 0, 0)));
        _body.Content = new ScrollViewer { Content = all, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        ShowSide();
    }

    static void DetachFromParent(FrameworkElement e)
    {
        switch (e.Parent)
        {
            case Panel p: p.Children.Remove(e); break;
            case Border b: b.Child = null; break;
            case ContentControl c: c.Content = null; break;
            case Decorator dd: dd.Child = null; break;
        }
    }

    static string Hint(string name, string path)
    {
        var n = name.ToLowerInvariant();
        if (n == "windows") return "Le système lui-même. Seuls le cache des mises à jour et les anciens composants peuvent être réduits, depuis Nettoyage.";
        if (n.StartsWith("program files")) return "Les programmes installés. Pour gagner de la place, désinstalle ceux qui ne servent plus.";
        if (n == "programdata") return "Données partagées des programmes (caches, installeurs, licences).";
        if (n == "users") return "Les dossiers des comptes Windows : documents, téléchargements et données des applis (AppData).";
        if (n == "appdata") return "Données des applis : caches de navigateurs, de jeux et de launchers. Beaucoup se nettoient depuis Nettoyage.";
        if (n == "pagefile.sys") return "Mémoire de secours de Windows. Réglable depuis Nettoyage → Système (avancé).";
        if (n == "hiberfil.sys") return "Fichier d'hibernation. Supprimable depuis Nettoyage → Système (avancé).";
        if (n == Quarantine.FolderName.ToLowerInvariant()) return "Fichiers en quarantaine de Nettoyeur PC, supprimés automatiquement après quelques jours.";
        return null;
    }

    void ShowSide()
    {
        _side.Children.Clear();
        _side.Children.Add(Ui.Text("DANS CE BLOC", "Small", 12));
        switch (_simpleSel)
        {
            case DirNode d:
                _side.Children.Add(Ui.Text($"{d.Name} · {Fmt.Size(d.Size)}", "H2").With(t => t.Margin = new Thickness(0, 8, 0, 6)));
                _side.Children.Add(Ui.Text(Hint(d.Name, d.FullPath) ?? $"{Fmt.Num(d.FileCount)} fichiers dans {Fmt.Num(d.DirCount)} dossiers.", "Body"));
                foreach (var k in d.Dirs.Take(6).Cast<object>().Concat(d.Files.Take(Math.Max(0, 6 - Math.Min(6, d.Dirs.Count)))).OrderByDescending(o => o is DirNode x ? x.Size : ((FileEntry)o).Size).Take(6))
                {
                    var (nm, sz) = k is DirNode x ? (x.Name, x.Size) : (((FileEntry)k).Name, ((FileEntry)k).Size);
                    var g = new Grid { Margin = new Thickness(0, 14, 0, 0) }; Ui.Grid2(g, "*", "auto");
                    g.RowDefinitions.Add(new RowDefinition()); g.RowDefinitions.Add(new RowDefinition());
                    g.Children.Add(Ui.Text(nm, "Body", 14, Ui.Res("Text")).With(t => { t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; }));
                    g.Children.Add(Ui.Text(Fmt.Size(sz), "Body").At(1));
                    g.Children.Add(Ui.Bar(d.Size > 0 ? 100.0 * sz / d.Size : 0, null, 6).With(b => b.Margin = new Thickness(0, 6, 0, 0)).At(0, 1, 2));
                    _side.Children.Add(g);
                }
                var btns = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
                btns.Children.Add(Ui.Btn("Entrer dans le dossier", "Ghost", (_, _) => { _simpleDir = d; _simpleSel = null; BuildSimple(); }, "").With(b => b.Margin = new Thickness(0, 0, 8, 8)));
                btns.Children.Add(Ui.Btn("Ouvrir l'emplacement", "Ghost", (_, _) => Shell.OpenInExplorer(d.FullPath), "").With(b => b.Margin = new Thickness(0, 0, 8, 8)));
                _side.Children.Add(btns);
                break;
            case FileEntry f:
                _side.Children.Add(Ui.Text($"{f.Name} · {Fmt.Size(f.Size)}", "H2").With(t => t.Margin = new Thickness(0, 8, 0, 6)));
                _side.Children.Add(Ui.Text(Hint(f.Name, f.FullPath) ?? f.FullPath, "Body"));
                _side.Children.Add(Ui.Btn("Ouvrir l'emplacement", "Ghost", (_, _) => Shell.OpenInExplorer(f.FullPath), "").With(b => { b.Margin = new Thickness(0, 16, 0, 0); b.HorizontalAlignment = HorizontalAlignment.Left; }));
                break;
            case Group g:
                _side.Children.Add(Ui.Text($"{g.Name} · {Fmt.Size(g.Size)}", "H2").With(t => t.Margin = new Thickness(0, 8, 0, 6)));
                var items = g.Files ? g.Parent.Files.Take(8).Select(f => (f.Name, f.Size)) : g.Parent.Dirs.Skip(11).Take(8).Select(x => (x.Name, x.Size));
                foreach (var (nm, sz) in items)
                {
                    var gr = new Grid { Margin = new Thickness(0, 10, 0, 0) }; Ui.Grid2(gr, "*", "auto");
                    gr.Children.Add(Ui.Text(nm, "Body", 14, Ui.Res("Text")).With(t => { t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; }));
                    gr.Children.Add(Ui.Text(Fmt.Size(sz), "Body").At(1));
                    _side.Children.Add(gr);
                }
                break;
            case Free fr:
                var dd = AppState.Drives.FirstOrDefault(x => x.Root.Equals(_drive, StringComparison.OrdinalIgnoreCase));
                _side.Children.Add(Ui.Text($"Espace libre · {Fmt.Size(fr.Size)}", "H2").With(t => t.Margin = new Thickness(0, 8, 0, 6)));
                var pct = dd?.FreePct ?? 0;
                _side.Children.Add(Ui.Text(pct >= 20 ? $"{Fmt.Pct(pct)} du disque est libre : c'est confortable." : pct >= 10 ? $"{Fmt.Pct(pct)} du disque est libre. Vise au moins 20 % pour un SSD." : $"Seulement {Fmt.Pct(pct)} de libre : Windows risque de ralentir. Fais un nettoyage.", "Body"));
                break;
            default:
                _side.Children.Add(Ui.Text("Clique sur un bloc.", "Body").With(t => t.Margin = new Thickness(0, 8, 0, 0)));
                break;
        }
    }

    void BuildBigFiles(StorageScan scan)
    {
        _bigFiles.Children.Clear();
        var list = scan.BiggestFiles(8, true).Where(f => f.Size > 50L << 20).ToList();
        if (list.Count == 0) { _bigFiles.Children.Add(Ui.Text("Aucun gros fichier personnel trouvé sur ce disque.", "Body")); return; }
        foreach (var f in list) _bigFiles.Children.Add(FileRow(f));
    }

    FrameworkElement FileRow(FileEntry f)
    {
        var g = new Grid(); Ui.Grid2(g, "auto", "*", "auto", "auto");
        var ext = Path.GetExtension(f.Name).TrimStart('.').ToUpperInvariant();
        var ic = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 14, 0), Child = Ui.Text(ext.Length is > 0 and <= 5 ? ext : "•", "Small", 11, Ui.Res("Text2"), FontWeights.Bold).With(t => { t.HorizontalAlignment = HorizontalAlignment.Center; t.VerticalAlignment = VerticalAlignment.Center; }) };
        ic.SetResourceReference(Border.BackgroundProperty, "Track");
        g.Children.Add(ic);
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameT = Ui.Text(f.Name, "H3", 14).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis);
        sp.Children.Add(nameT);
        DateTime mod = default; try { mod = File.GetLastWriteTime(f.FullPath); } catch { }
        sp.Children.Add(Ui.Text($"{f.Parent.FullPath} · modifié {Fmt.Ago(mod)}", "Small", 12).With(t => { t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; }));
        g.Children.Add(sp.At(1));
        g.Children.Add(Ui.Text(Fmt.Size(f.Size), "Body", 14, Ui.Res("Text")).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(12, 0, 14, 0); }).At(2));
        var actions = new ContentControl { Focusable = false };
        g.Children.Add(actions.At(3));
        void Normal()
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            p.Children.Add(Ui.IconBtn("", "Ouvrir l'emplacement", (_, _) => Shell.OpenInExplorer(f.FullPath)).With(b => b.Margin = new Thickness(0, 0, 6, 0)));
            p.Children.Add(Ui.Btn("Déplacer", "Ghost", async (_, _) => { if (await MoveItem(f.FullPath, f.Size, f.Name)) { Scan?.Remove(f); BuildSimple(); } }).With(b => { b.Height = 34; b.Margin = new Thickness(0, 0, 6, 0); }));
            p.Children.Add(Ui.IconBtn("", "Supprimer", (_, _) => Confirm(), danger: true));
            actions.Content = p;
        }
        void Confirm()
        {
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            p.Children.Add(Ui.Text("Supprimer ?", "Body", 13, Ui.Res("WarnFg")).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 10, 0); }));
            var yes = Ui.Btn("Supprimer", "Primary", async (_, _) =>
            {
                var ev = await DeleteItem(f.FullPath, f.Size, f.Name, ask: false);
                if (ev == null) { Normal(); return; }
                Scan?.Remove(f);
                nameT.TextDecorations = TextDecorations.Strikethrough; nameT.Foreground = Ui.Res("Muted");
                var q = new StackPanel { Orientation = Orientation.Horizontal };
                q.Children.Add(Ui.Text("Supprimé", "Small", 13).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 10, 0); }));
                q.Children.Add(Ui.Btn("Restaurer", "Ghost", async (_, _) =>
                {
                    await Task.Run(() => History.Undo(ev));
                    nameT.TextDecorations = null; nameT.Foreground = Ui.Res("Text");
                    AppState.RefreshDrives(); Normal();
                }).With(b => b.Height = 34));
                actions.Content = q;
            });
            yes.Height = 34; yes.SetResourceReference(BackgroundProperty, "DangerFg"); yes.Margin = new Thickness(0, 0, 6, 0);
            p.Children.Add(yes);
            p.Children.Add(Ui.Btn("Annuler", "Ghost", (_, _) => Normal()).With(b => b.Height = 34));
            actions.Content = p;
        }
        Normal();
        var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 10), Child = g };
        row.SetResourceReference(Border.BackgroundProperty, "Surface2");
        return row;
    }

    // =============================================================== Actions communes
    /// <summary>Déplace un fichier/dossier vers un autre disque en gardant son chemin. Annulable depuis l'Historique.</summary>
    async Task<bool> MoveItem(string path, long size, string name)
    {
        if (Shell.IsProtected(path)) { Dialog.Info("Impossible", "Cet élément fait partie de Windows ou d'un dossier système : il ne peut pas être déplacé d'ici."); return false; }
        var other = AppState.Drives.Where(d => !path.StartsWith(d.Root, StringComparison.OrdinalIgnoreCase)).OrderByDescending(d => d.Free).FirstOrDefault();
        var def = other != null ? Path.Combine(other.Root, "BACKUP") : "";
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = $"Où déplacer « {name} » ? (son chemin d'origine sera recréé dedans)" };
        if (other != null) { try { Directory.CreateDirectory(def); dlg.InitialDirectory = def; } catch { } }
        if (dlg.ShowDialog() != true) return false;
        var root = Path.GetPathRoot(path)!;
        var dest = Path.Combine(dlg.FolderName, root.TrimEnd('\\', ':'), path.Substring(root.Length));
        var mw = MainWindow.Instance;
        mw.ShowBusy($"Déplacement de {name}…"); mw.BusyStatus($"{Fmt.Size(size)} vers {dlg.FolderName}");
        try
        {
            await Task.Run(() => { Directory.CreateDirectory(Path.GetDirectoryName(dest)!); Quarantine.MoveAny(path, dest); });
            var ev = new HistoryEvent { Title = $"Déplacé : {name}", Sub = $"{Fmt.Size(size)} vers {Path.GetDirectoryName(dest)}", Bytes = size };
            ev.Items.Add(new QItem { Original = path, Stored = dest, Size = size });
            History.Add(ev);
            return true;
        }
        catch (Exception ex) { Log.Error("Déplacement de " + path, ex); Dialog.Info("Déplacement impossible", ex.Message); return false; }
        finally { mw.HideBusy(); AppState.RefreshDrives(); AppState.Notify(); }
    }

    /// <summary>« Supprime » en envoyant en quarantaine (restaurable pendant N jours).</summary>
    async Task<HistoryEvent> DeleteItem(string path, long size, string name, bool ask = true)
    {
        if (Shell.IsProtected(path)) { Dialog.Info("Impossible", "Cet élément fait partie de Windows ou d'un dossier système : il ne peut pas être supprimé d'ici."); return null; }
        if (ask && !Dialog.Confirm("Supprimer ?", $"« {name} » ({Fmt.Size(size)}) part en quarantaine : tu peux le restaurer pendant {Settings.Current.QuarantineDays} jours depuis l'Historique.", "Supprimer", danger: true)) return null;
        var mw = MainWindow.Instance;
        mw.ShowBusy($"Suppression de {name}…");
        try
        {
            var ev = new HistoryEvent { Title = $"Supprimé : {name}", Bytes = size };
            await Task.Run(() =>
            {
                ev.Batch = Quarantine.NewBatch(path);
                if (File.Exists(path)) { var q = Quarantine.Put(ev.Batch, path) ?? throw new IOException("Le fichier est peut-être ouvert dans une autre appli."); ev.Items.Add(q); }
                else
                {
                    var root = Path.GetPathRoot(path)!;
                    var stored = Path.Combine(ev.Batch, root.TrimEnd('\\', ':'), path.Substring(root.Length));
                    Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
                    Quarantine.MoveAny(path, stored);
                    ev.Items.Add(new QItem { Original = path, Stored = stored, Size = size });
                }
            });
            ev.Sub = $"{Fmt.Size(size)} en quarantaine jusqu'au {Fmt.Date(DateTime.Now.AddDays(Settings.Current.QuarantineDays))}";
            History.Add(ev);
            return ev;
        }
        catch (Exception ex) { Log.Error("Suppression de " + path, ex); Dialog.Info("Suppression impossible", ex.Message); return null; }
        finally { mw.HideBusy(); AppState.RefreshDrives(); AppState.Notify(); }
    }

    // =============================================================== Vue avancée
    void BuildAdvanced()
    {
        var scan = Scan; if (scan == null) return;
        _advDir ??= scan.Root;
        if (_open.Count == 0) { _open.Add(scan.Root); if (scan.Root.Dirs.Count > 0) _open.Add(scan.Root.Dirs[0]); }

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 220 });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.25, GridUnitType.Star), MinHeight = 240 });

        // Barre d'actions
        var bar = new Grid(); Ui.Grid2(bar, "*", "auto", "auto", "auto", "auto", "auto");
        _selPath.FontFamily = (FontFamily)Application.Current.Resources["Mono"]; _selPath.Foreground = Ui.Res("Text");
        bar.Children.Add(Detach(_selPath));
        bar.Children.Add(Detach(_selInfo).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(12, 0, 12, 0); }).At(1));
        bar.Children.Add(Ui.Btn("Emplacement", "Ghost", (_, _) => Shell.OpenInExplorer(SelPath), "").With(b => { b.Height = 34; b.Margin = new Thickness(0, 0, 6, 0); }).At(2));
        bar.Children.Add(Ui.Btn("Copier le chemin", "Ghost", (_, _) => { try { Clipboard.SetText(SelPath); } catch { } }, "").With(b => { b.Height = 34; b.Margin = new Thickness(0, 0, 6, 0); }).At(3));
        bar.Children.Add(Ui.Btn("Déplacer vers…", "Ghost", async (_, _) => await AdvAction(false), "").With(b => { b.Height = 34; b.Margin = new Thickness(0, 0, 6, 0); }).At(4));
        bar.Children.Add(Ui.Btn("Supprimer", "Danger", async (_, _) => await AdvAction(true), "").With(b => b.Height = 34).At(5));
        var barCard = Ui.Card(bar, new Thickness(12, 8, 8, 8)); barCard.Margin = new Thickness(0, 0, 0, 14);
        grid.Children.Add(barCard);

        // Arborescence + types
        var mid = new Grid { Margin = new Thickness(0, 0, 0, 14) }; Ui.Grid2(mid, "3*", "14", "1*");
        var tree = new DockPanel();
        var th = new Grid { Height = 34, Margin = new Thickness(12, 0, 12, 0) }; TreeCols(th);
        string[] heads = { "Nom", "% du parent", "Taille", "Fichiers", "Dossiers", "Modifié" };
        for (int i = 0; i < heads.Length; i++) th.Children.Add(Ui.Text(heads[i], "Small", 12.5).With(t => { t.VerticalAlignment = VerticalAlignment.Center; if (i >= 2) t.TextAlignment = TextAlignment.Right; }).At(i));
        var thb = new Border { BorderThickness = new Thickness(0, 0, 0, 1), Child = th }; thb.SetResourceReference(Border.BorderBrushProperty, "Line");
        DockPanel.SetDock(thb, Dock.Top); tree.Children.Add(thb);
        DetachFromParent(_treeScroll); tree.Children.Add(_treeScroll);
        mid.Children.Add(Ui.Card(tree, new Thickness(0, 4, 0, 4)).At(0));
        var ext = new DockPanel();
        var eh = Ui.Text("Par type de fichier", "H3", 14).With(t => t.Margin = new Thickness(4, 0, 0, 8)); DockPanel.SetDock(eh, Dock.Top); ext.Children.Add(eh);
        DetachFromParent(_extRows);
        ext.Children.Add(new ScrollViewer { Content = _extRows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        mid.Children.Add(Ui.Card(ext, new Thickness(12)).At(2));
        grid.Children.Add(mid.At(0, 1));

        // Carte détaillée
        var mapPanel = new DockPanel();
        var mh = new Grid { Margin = new Thickness(2, 0, 2, 10) }; Ui.Grid2(mh, "auto", "*");
        mh.Children.Add(Ui.Text("Chaque rectangle est un fichier, groupé par dossier. La couleur indique le type.", "Small", 13));
        mh.Children.Add(Detach(_hover).With(t => { t.TextAlignment = TextAlignment.Right; t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; t.Margin = new Thickness(16, 0, 0, 0); }).At(1));
        DockPanel.SetDock(mh, Dock.Top); mapPanel.Children.Add(mh);
        DetachFromParent(_map);
        var mapBorder = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _map };
        mapPanel.Children.Add(mapBorder);
        grid.Children.Add(Ui.Card(mapPanel, new Thickness(12)).At(0, 2));

        _body.Content = grid;
        _map.SetScan(scan);
        _map.SelectedDir = _advDir; _map.SelectedFile = _advFile; _map.HighlightExt = _hlExt;
        BuildTree(); BuildExt(); UpdateAdvSel();
    }

    static T Detach<T>(T e) where T : FrameworkElement { DetachFromParent(e); return e; }

    static void TreeCols(Grid g) => Ui.Grid2(g, "*", "150", "96", "90", "90", "100");

    string SelPath => _advFile?.FullPath ?? _advDir?.FullPath ?? _drive;

    void UpdateAdvSel()
    {
        _selPath.Text = SelPath;
        if (_advFile != null) _selInfo.Text = Fmt.Size(_advFile.Size);
        else if (_advDir != null) _selInfo.Text = $"{Fmt.Size(_advDir.Size)} · {Fmt.Num(_advDir.FileCount)} fichiers · {Fmt.Num(_advDir.DirCount)} dossiers";
    }

    void ExpandTo(DirNode d) { for (var x = d; x != null; x = x.Parent) _open.Add(x); }

    void BuildTree()
    {
        _treeRows.Children.Clear();
        var scan = Scan; if (scan == null) return;
        void Walk(DirNode n, int depth, DirNode parent)
        {
            _treeRows.Children.Add(TreeRow(n, null, depth, parent));
            if (!_open.Contains(n)) return;
            var children = n.Dirs.Cast<object>().Concat(n.Files).OrderByDescending(o => o is DirNode d ? d.Size : ((FileEntry)o).Size).ToList();
            foreach (var c in children.Take(200))
            {
                if (c is DirNode d) Walk(d, depth + 1, n);
                else _treeRows.Children.Add(TreeRow(null, (FileEntry)c, depth + 1, n));
            }
            if (children.Count > 200)
            {
                var restSize = children.Skip(200).Sum(o => o is DirNode d ? d.Size : ((FileEntry)o).Size);
                _treeRows.Children.Add(Ui.Text($"… {Fmt.Num(children.Count - 200)} autres éléments ({Fmt.Size(restSize)})", "Small", 12.5).With(t => t.Margin = new Thickness(12 + (depth + 1) * 18 + 22, 6, 0, 6)));
            }
        }
        Walk(scan.Root, 0, null);
    }

    FrameworkElement TreeRow(DirNode d, FileEntry f, int depth, DirNode parent)
    {
        var g = new Grid { Height = 30, Margin = new Thickness(12, 0, 12, 0) }; TreeCols(g);
        long size = d?.Size ?? f.Size;
        var name = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(depth * 18, 0, 0, 0) };
        var chev = Ui.Text(d != null && (d.Dirs.Count + d.Files.Count) > 0 ? (_open.Contains(d) ? "" : "") : "", "Small", 10);
        chev.FontFamily = (FontFamily)Application.Current.Resources["Icons"]; chev.Width = 18; chev.VerticalAlignment = VerticalAlignment.Center;
        name.Children.Add(chev);
        var extIdx = d != null ? d.TopExt : f.Ext;
        name.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = extIdx >= 0 && _map.ExtColors.TryGetValue(extIdx, out var c) ? new SolidColorBrush(c) : Ui.Hex("#3A404C") });
        var label = d == null ? f.Name : d == Scan.Root ? _drive.TrimEnd('\\') : d.Name;
        name.Children.Add(Ui.Text(label, "Body", 13.5, Ui.Res("Text"), d != null ? FontWeights.SemiBold : FontWeights.Normal).With(t => { t.TextWrapping = TextWrapping.NoWrap; t.TextTrimming = TextTrimming.CharacterEllipsis; t.VerticalAlignment = VerticalAlignment.Center; }));
        g.Children.Add(name);
        double pct = parent != null && parent.Size > 0 ? 100.0 * size / parent.Size : 100;
        var pc = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) }; Ui.Grid2(pc, "*", "44");
        bool selected = (f != null && f == _advFile) || (f == null && d == _advDir && _advFile == null);
        pc.Children.Add(Ui.Bar(pct, selected ? null : Ui.Hex("#5B6475"), 6));
        pc.Children.Add(Ui.Text(Fmt.Pct(pct), "Small", 12).With(t => t.TextAlignment = TextAlignment.Right).At(1));
        g.Children.Add(pc.At(1));
        TextBlock Num(string s) => Ui.Text(s, "Body", 13).With(t => { t.TextAlignment = TextAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; });
        g.Children.Add(Num(Fmt.Size(size)).With(t => t.Foreground = Ui.Res("Text")).At(2));
        g.Children.Add(Num(d != null ? Fmt.Num(d.FileCount) : "").At(3));
        g.Children.Add(Num(d != null ? Fmt.Num(d.DirCount) : "").At(4));
        DateTime mod = d?.Modified ?? default;
        g.Children.Add(Num(Fmt.Date(mod)).With(t => t.Foreground = Ui.Res("Muted")).At(5));
        var row = new Border { Child = g, Cursor = Cursors.Hand, Background = selected ? Ui.Hex("#1F2A2A") : Brushes.Transparent };
        row.MouseLeftButtonUp += (_, _) =>
        {
            if (d != null)
            {
                if (_advDir == d && _advFile == null && (d.Dirs.Count + d.Files.Count) > 0) { if (!_open.Remove(d)) _open.Add(d); }
                else _open.Add(d);
                _advDir = d; _advFile = null;
            }
            else { _advDir = parent; _advFile = f; }
            _map.SelectedDir = _advDir; _map.SelectedFile = _advFile; _map.InvalidateVisual();
            BuildTree(); UpdateAdvSel();
        };
        row.MouseEnter += (_, _) => { if (!selected) row.Background = Ui.Hex("#1A1E26"); };
        row.MouseLeave += (_, _) => { if (!selected) row.Background = Brushes.Transparent; };
        return row;
    }

    void BuildExt()
    {
        _extRows.Children.Clear();
        var scan = Scan; if (scan == null) return;
        long total = Math.Max(1, scan.Root.Size);
        foreach (var e in scan.Extensions.Take(18))
        {
            var g = new Grid { Height = 30 }; Ui.Grid2(g, "auto", "64", "*", "auto");
            g.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Margin = new Thickness(6, 0, 10, 0),
                Background = _map.ExtColors.TryGetValue(e.Index, out var c) ? new SolidColorBrush(c) : Ui.Hex("#5B6475") });
            g.Children.Add(Ui.Text(e.Ext, "Body", 13, Ui.Res("Text"), FontWeights.SemiBold).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.TextTrimming = TextTrimming.CharacterEllipsis; t.TextWrapping = TextWrapping.NoWrap; }).At(1));
            g.Children.Add(Ui.Text($"{Fmt.Num(e.Count)} fichiers", "Small", 12).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.TextWrapping = TextWrapping.NoWrap; }).At(2));
            g.Children.Add(Ui.Text($"{Fmt.Size(e.Size)}  {Fmt.Pct(100.0 * e.Size / total)}", "Body", 13, Ui.Res("Text")).With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.Margin = new Thickness(0, 0, 6, 0); }).At(3));
            var idx = e.Index;
            var row = new Border { Child = g, CornerRadius = new CornerRadius(7), Cursor = Cursors.Hand, Background = _hlExt == idx ? Ui.Res("Surface3") : Brushes.Transparent, ToolTip = "Cliquer pour mettre ce type en évidence sur la carte" };
            row.MouseLeftButtonUp += (_, _) => { _hlExt = _hlExt == idx ? -1 : idx; _map.HighlightExt = _hlExt; _map.InvalidateVisual(); BuildExt(); };
            _extRows.Children.Add(row);
        }
    }

    async Task AdvAction(bool delete)
    {
        var scan = Scan; if (scan == null) return;
        if (_advFile != null)
        {
            var f = _advFile;
            bool ok = delete ? await DeleteItem(f.FullPath, f.Size, f.Name) != null : await MoveItem(f.FullPath, f.Size, f.Name);
            if (ok) { scan.Remove(f); _advFile = null; }
        }
        else if (_advDir != null && _advDir != scan.Root)
        {
            var d = _advDir;
            bool ok = delete ? await DeleteItem(d.FullPath, d.Size, d.Name) != null : await MoveItem(d.FullPath, d.Size, d.Name);
            if (ok) { scan.Remove(d); _advDir = d.Parent; }
        }
        else { Dialog.Info("Sélectionne un élément", "Choisis un fichier ou un dossier dans l'arborescence ou sur la carte."); return; }
        _map.SelectedDir = _advDir; _map.SelectedFile = _advFile;
        _map.Relayout(); BuildTree(); BuildExt(); UpdateAdvSel();
    }
}
