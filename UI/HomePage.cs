using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

public class HomePage : UserControl, IPage
{
    readonly TextBlock _greeting = Ui.Text("", "H1");
    readonly TextBlock _sub = Ui.Text("", "Small", 14);
    readonly Ring _ring = new() { Width = 168, Height = 168 };
    readonly TextBlock _score = new() { FontSize = 46, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
    readonly Border _badge = new();
    readonly TextBlock _headline = Ui.Text("", "H2", 23);
    readonly TextBlock _issues = Ui.Text("", "Body");
    readonly UniformGrid4 _cards = new();
    readonly StackPanel _drives = new();
    readonly StackPanel _recos = new();
    readonly Border _warn = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 12), Visibility = Visibility.Collapsed };

    public HomePage()
    {
        _score.SetResourceReference(TextBlock.FontFamilyProperty, "Display");
        var root = new StackPanel { Margin = new Thickness(40, 30, 40, 40), MaxWidth = 1400 };

        // En-tête
        var head = new Grid();
        Ui.Grid2(head, "*", "auto");
        var hs = new StackPanel(); hs.Children.Add(_greeting); _sub.Margin = new Thickness(0, 6, 0, 0); hs.Children.Add(_sub);
        head.Children.Add(hs);
        var rescan = Ui.Btn("Actualiser", "Ghost", async (_, _) => { AppState.RefreshDrives(); Refresh(); await AppState.EnsureScan(true); }, "");
        rescan.VerticalAlignment = VerticalAlignment.Bottom;
        head.Children.Add(rescan.At(1));
        root.Children.Add(head);

        // Alerte espace faible (venue du nettoyage automatique)
        _warn.SetResourceReference(Border.BackgroundProperty, "WarnBg");
        _warn.Margin = new Thickness(0, 18, 0, 0);
        root.Children.Add(_warn);

        // Carte score
        var hero = new Grid();
        Ui.Grid2(hero, "auto", "*");
        var ringBox = new Grid { Width = 168, Height = 168, Margin = new Thickness(0, 0, 32, 0), VerticalAlignment = VerticalAlignment.Center };
        ringBox.Children.Add(_ring);
        var sc = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        sc.Children.Add(_score);
        var sur = Ui.Text("sur 100", "Small"); sur.HorizontalAlignment = HorizontalAlignment.Center; sc.Children.Add(sur);
        ringBox.Children.Add(sc);
        hero.Children.Add(ringBox);
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _badge.Style = (Style)Application.Current.Resources["Pill"];
        right.Children.Add(_badge);
        _headline.Margin = new Thickness(0, 12, 0, 0); right.Children.Add(_headline);
        _issues.Margin = new Thickness(0, 8, 0, 0); _issues.MaxWidth = 640; _issues.HorizontalAlignment = HorizontalAlignment.Left; right.Children.Add(_issues);
        var btns = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        var analyse = Ui.Btn("Lancer l'analyse intelligente", "Primary", (_, _) => MainWindow.Instance.Go("clean", "rescan"), "");
        analyse.Margin = new Thickness(0, 0, 12, 0);
        btns.Children.Add(analyse);
        var express = Ui.Btn("Nettoyage express (sans risque)", "Ghost", async (_, _) => await Express());
        express.Height = 44;
        btns.Children.Add(express);
        right.Children.Add(btns);
        hero.Children.Add(right.At(1));
        var heroCard = Ui.Card(hero, new Thickness(32, 28, 32, 28));
        heroCard.Margin = new Thickness(0, 22, 0, 0);
        root.Children.Add(heroCard);

        // Cartes résumé
        _cards.Margin = new Thickness(0, 16, -16, 0);
        root.Children.Add(_cards);

        // Disques + recommandations
        var two = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        Ui.Grid2(two, "*", "16", "*");
        var dc = new StackPanel(); dc.Children.Add(Ui.Text("Disques", "H3")); _drives.Margin = new Thickness(0, 14, 0, 0); dc.Children.Add(_drives);
        two.Children.Add(Ui.Card(dc).At(0));
        var rc = new StackPanel(); rc.Children.Add(Ui.Text("Recommandé pour toi", "H3")); _recos.Margin = new Thickness(0, 12, 0, 0); rc.Children.Add(_recos);
        two.Children.Add(Ui.Card(rc).At(2));
        root.Children.Add(two);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AppState.Changed += Refresh;
    }

    bool _started;
    public void OnShow(object arg)
    {
        AppState.RefreshDrives();
        Refresh();
        if (_started) return;
        _started = true;
        _ = AppState.EnsureScan();
        Task.Run(() =>
        {
            SystemInfo.AddHealth(AppState.Drives);
            AppState.HealthLoaded = true;
            var n = StartupScan.CountEnabled();
            AppState.StartupCount = n >= 0 ? n : SystemInfo.StartupCount();
            AppState.Notify();
        });
    }

    void Refresh()
    {
        var s = Settings.Current;
        var name = Environment.UserName;
        var h = DateTime.Now.Hour;
        _greeting.Text = (h < 18 && h >= 5 ? "Bonjour " : "Bonsoir ") + name;
        var auto = s.AutoClean ? $" · nettoyage automatique le {DayName(s.AutoDay)} à 20 h" : "";
        _sub.Text = (s.LastClean == default ? "Aucun nettoyage effectué pour l'instant" : $"Dernier nettoyage {Fmt.Ago(s.LastClean)} ({Fmt.Size(s.LastCleanBytes)} libérés)") + auto;

        if (!string.IsNullOrEmpty(s.LowSpaceWarning))
        {
            _warn.Visibility = Visibility.Visible;
            var t = Ui.Text("⚠  " + s.LowSpaceWarning + " (détecté lors du dernier nettoyage automatique)", "Body"); t.SetResourceReference(TextBlock.ForegroundProperty, "WarnFg");
            _warn.Child = t;
        }
        else _warn.Visibility = Visibility.Collapsed;

        var session = AppState.Session;
        var scanned = session.Scanned;
        var rep = Health.Compute(AppState.Drives, scanned ? session : null, Math.Max(0, AppState.StartupCount));
        _ring.Value = rep.Score;
        _score.Text = rep.Score.ToString();
        var (bg, fg) = rep.Score >= 65 ? ("SafeBg", "SafeFg") : rep.Score >= 45 ? ("WarnBg", "WarnFg") : ("DangerBg", "DangerFg");
        var bt = new TextBlock { Text = rep.Label + (scanned ? "" : " · analyse en cours…"), FontSize = 12, FontWeight = FontWeights.SemiBold };
        bt.SetResourceReference(TextBlock.ForegroundProperty, fg);
        _badge.Child = bt; _badge.SetResourceReference(Border.BackgroundProperty, bg);
        _headline.Text = rep.Issues.Count == 0 ? "Ton PC est en pleine forme." :
            rep.Score >= 65 ? $"Ton PC va bien. {rep.Issues.Count} point{(rep.Issues.Count > 1 ? "s" : "")} peu{(rep.Issues.Count > 1 ? "vent" : "t")} encore l'améliorer."
            : $"{rep.Issues.Count} point{(rep.Issues.Count > 1 ? "s" : "")} à améliorer.";
        _issues.Text = rep.Issues.Count == 0 ? "L'analyse vérifie l'espace disque, les fichiers inutiles, les programmes au démarrage et la santé de tes disques. Rien n'est supprimé sans ton accord."
            : string.Join(" · ", rep.Issues) + ". Rien n'est supprimé sans ton accord.";

        // Cartes
        _cards.Children.Clear();
        var sys = AppState.Drives.FirstOrDefault(d => d.IsSystem);
        _cards.Children.Add(SummaryCard("Espace récupérable", scanned ? Fmt.Size(session.Total) : "…",
            scanned ? $"dont {Fmt.Size(session.TotalSafe)} sans aucun risque" : "Analyse en cours", "Voir le détail", () => MainWindow.Instance.Go("clean")));
        if (sys != null)
            _cards.Children.Add(SummaryCard($"Disque {sys.Letter} (système)", Fmt.Size(sys.Free) + " libres",
                $"{Fmt.Pct(sys.FreePct)} du disque · conseillé : 20 % ou plus", "Voir ce qui prend de la place", () => MainWindow.Instance.Go("storage")));
        var ssd = AppState.Drives.Where(d => d.HealthPct.HasValue).OrderBy(d => d.HealthPct).FirstOrDefault();
        _cards.Children.Add(SummaryCard("Santé des disques", !AppState.HealthLoaded ? "…" : ssd != null ? ssd.HealthPct + " %" : "OK",
            !AppState.HealthLoaded ? "Lecture en cours" : ssd != null ? $"{ssd.Model} ({ssd.Letter}){(ssd.TempC.HasValue ? $" · {ssd.TempC} °C" : "")}" : "Aucun problème signalé par Windows", null, null));
        _cards.Children.Add(SummaryCard("Au démarrage de Windows", AppState.StartupCount < 0 ? "…" : $"{AppState.StartupCount} programmes",
            AppState.StartupCount > 8 ? "C'est beaucoup : chacun ralentit l'ouverture de session" : "Raisonnable", "Gérer", () => MainWindow.Instance.Go("startup")));

        // Disques
        _drives.Children.Clear();
        foreach (var d in AppState.Drives)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            g.RowDefinitions.Add(new RowDefinition()); g.RowDefinitions.Add(new RowDefinition());
            Ui.Grid2(g, "*", "auto");
            var l = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
            l.Inlines.Add(new System.Windows.Documents.Run(d.Letter + "  ") { FontWeight = FontWeights.SemiBold });
            var meta = new System.Windows.Documents.Run(string.Join(" · ", new[] { d.Role, d.Media }.Where(x => !string.IsNullOrEmpty(x))));
            meta.SetResourceReference(TextElement_Foreground, "Muted");
            l.Inlines.Add(meta);
            g.Children.Add(l);
            g.Children.Add(Ui.Text($"{Fmt.Size(d.Free)} libres / {Fmt.Size(d.Size)}", "Small", 13).At(1));
            var bar = Ui.Bar(d.UsedPct, d.FreePct < 15 ? Ui.Res("WarnFg") : d.IsSystem ? null : Ui.Hex("#5B9DFF"));
            bar.Margin = new Thickness(0, 8, 0, 0);
            g.Children.Add(bar.At(0, 1, 2));
            _drives.Children.Add(g);
        }

        // Recommandations
        _recos.Children.Clear();
        var recos = new List<(string Icon, string Bg, string Fg, string Title, string Sub, string Cta, Action Go)>();
        if (scanned)
        {
            var top = session.Rules.Where(r => r.Risk == Risk.Safe && r.Size > 500L << 20).OrderByDescending(r => r.Size).FirstOrDefault();
            if (top != null) recos.Add(("", "SafeBg", "SafeFg", $"{top.Name} : {Fmt.Size(top.Size)}", "Sans risque, se recrée tout seul", "Nettoyer", () => MainWindow.Instance.Go("clean", top.Id)));
        }
        foreach (var d in AppState.Drives.Where(d => d.FreePct < 15))
            recos.Add(("", "WarnBg", "WarnFg", $"Le disque {d.Letter} est presque plein", $"{Fmt.Pct(d.FreePct)} libre : regarde ce qui prend de la place", "Voir", () => MainWindow.Instance.Go("storage", d.Root)));
        if (!s.AutoClean)
            recos.Add(("", "InfoBg", "InfoFg", "Planifier un nettoyage chaque semaine", "Uniquement les éléments sans risque, en arrière-plan", "Activer", () => MainWindow.Instance.Go("history", "auto")));
        if (Quarantine.TotalSize() > 0)
            recos.Add(("", "InfoBg", "InfoFg", $"{Fmt.Size(Quarantine.TotalSize())} en quarantaine", "Restaurables jusqu'au " + Fmt.Date(Quarantine.NextPurge()), "Voir", () => MainWindow.Instance.Go("history")));
        if (recos.Count == 0) _recos.Children.Add(Ui.Text("Rien à signaler pour l'instant.", "Body"));
        foreach (var r in recos.Take(4)) _recos.Children.Add(Reco(r.Icon, r.Bg, r.Fg, r.Title, r.Sub, r.Cta, r.Go));
    }

    static readonly DependencyProperty TextElement_Foreground = System.Windows.Documents.TextElement.ForegroundProperty;

    static string DayName(string d) => d switch { "MON" => "lundi", "TUE" => "mardi", "WED" => "mercredi", "THU" => "jeudi", "FRI" => "vendredi", "SAT" => "samedi", _ => "dimanche" };

    static FrameworkElement SummaryCard(string label, string value, string sub, string cta, Action go)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(label, "Small", 13));
        var v = Ui.Text(value, "Big"); v.Margin = new Thickness(0, 8, 0, 6); v.TextTrimming = TextTrimming.CharacterEllipsis; v.TextWrapping = TextWrapping.NoWrap; sp.Children.Add(v);
        sp.Children.Add(Ui.Text(sub, "Small", 13));
        if (cta != null)
        {
            var l = new Button { Style = (Style)Application.Current.Resources["LinkBtn"], Content = cta + "  →", Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            l.Click += (_, _) => go();
            sp.Children.Add(l);
        }
        var c = Ui.Card(sp, new Thickness(20));
        if (go != null) { c.Cursor = Cursors.Hand; c.MouseLeftButtonUp += (_, _) => go(); }
        return c;
    }

    static FrameworkElement Reco(string glyph, string bg, string fg, string title, string sub, string cta, Action go)
    {
        var g = new Grid();
        Ui.Grid2(g, "auto", "*", "auto");
        var ic = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(9), Margin = new Thickness(0, 0, 14, 0) };
        ic.SetResourceReference(Border.BackgroundProperty, bg);
        var t = Ui.Icon(glyph, 16); t.HorizontalAlignment = HorizontalAlignment.Center; t.SetResourceReference(TextBlock.ForegroundProperty, fg);
        ic.Child = t;
        g.Children.Add(ic);
        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var tt = Ui.Text(title, "H3", 14); tt.TextTrimming = TextTrimming.CharacterEllipsis; sp.Children.Add(tt);
        sp.Children.Add(Ui.Text(sub, "Small", 13));
        g.Children.Add(sp.At(1));
        var b = Ui.Btn(cta, "Ghost", (_, _) => go()); b.Height = 34; b.Margin = new Thickness(12, 0, 0, 0);
        g.Children.Add(b.At(2));
        var row = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10), Child = g };
        row.SetResourceReference(Border.BackgroundProperty, "Surface2");
        return row;
    }

    async Task Express()
    {
        var mw = MainWindow.Instance;
        if (!AppState.Session.Scanned)
        {
            mw.ShowBusy("Analyse en cours…");
            mw.BusyStatus("Recherche des fichiers inutiles");
            await AppState.EnsureScan();
            mw.HideBusy();
        }
        var sel = AppState.Session.Rules.Where(r => r.Risk == Risk.Safe && r.Default && r.Size > 0 && !r.Irreversible).ToList();
        var total = sel.Sum(r => r.Size);
        if (sel.Count == 0) { Dialog.Info("Rien à nettoyer", "Aucun fichier inutile sans risque n'a été trouvé. Ton PC est déjà propre !"); return; }
        if (!Dialog.Confirm("Nettoyage express", $"{sel.Count} éléments sans risque vont être nettoyés, pour {Fmt.Size(total)} récupérés.\n\nUniquement des caches et fichiers temporaires qui se recréent tout seuls.", "Nettoyer " + Fmt.Size(total))) return;
        mw.ShowBusy("Nettoyage en cours…");
        var progress = new Progress<string>(mw.BusyStatus);
        var ev = await AppState.Session.CleanAsync(sel, false, progress);
        mw.HideBusy();
        AppState.LastResult = ev;
        AppState.RefreshDrives();
        mw.Go("history", ev);
    }
}

/// <summary>Grille à 4 colonnes égales (2 si la fenêtre est étroite).</summary>
public class UniformGrid4 : System.Windows.Controls.Primitives.UniformGrid
{
    public UniformGrid4() { Columns = 4; SizeChanged += (_, e) => Columns = e.NewSize.Width < 900 ? 2 : 4; }
    protected override Size MeasureOverride(Size s)
    {
        foreach (UIElement c in Children) if (c is FrameworkElement f) f.Margin = new Thickness(0, 0, 16, 16);
        return base.MeasureOverride(s);
    }
}
