using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

/// <summary>Écran « Démarrage » : ce qui se lance avec Windows, activable / désactivable (réversible).</summary>
public class StartupPage : UserControl, IPage
{
    readonly StackPanel _root = new() { Margin = new Thickness(40, 30, 40, 40), MaxWidth = 1400 };
    readonly WrapPanel _pills = new() { Margin = new Thickness(0, 16, 0, 0) };
    readonly StackPanel _list = new() { Margin = new Thickness(0, 20, 0, 0) };
    readonly TextBox _filter = new() { Width = 260, Height = 36, VerticalContentAlignment = VerticalAlignment.Center };
    readonly Button _refresh;
    List<StartupEntry> _entries = new();
    bool _loading, _loaded;

    public StartupPage()
    {
        var head = new Grid();
        Ui.Grid2(head, "*", "auto");
        var hs = new StackPanel();
        hs.Children.Add(Ui.Text("Démarrage", "H1"));
        hs.Children.Add(Ui.Text("Ce qui se lance tout seul avec Windows. Désactiver une entrée ne supprime rien : vous pouvez la réactiver ici ou l'annuler depuis l'Historique.", "Small", 14)
            .With(t => { t.Margin = new Thickness(0, 6, 0, 0); t.TextWrapping = TextWrapping.Wrap; t.MaxWidth = 760; t.HorizontalAlignment = HorizontalAlignment.Left; }));
        head.Children.Add(hs);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom };
        _filter.ToolTip = "Filtrer par nom, éditeur ou commande";
        System.Windows.Automation.AutomationProperties.SetName(_filter, "Filtrer");
        _filter.TextChanged += (_, _) => Build();
        tools.Children.Add(_filter);
        _refresh = Ui.Btn("Actualiser", "Ghost", (_, _) => Load(), "");
        _refresh.Margin = new Thickness(10, 0, 0, 0);
        tools.Children.Add(_refresh);
        head.Children.Add(tools.At(1));
        _root.Children.Add(head);
        _root.Children.Add(_pills);
        _root.Children.Add(_list);
        Content = new ScrollViewer { Content = _root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public void OnShow(object arg)
    {
        if (!_loaded || arg as string == "refresh") Load();
    }

    void Load()
    {
        if (_loading) return;
        _loading = true; _refresh.IsEnabled = false;
        _pills.Children.Clear();
        _list.Children.Clear();
        _list.Children.Add(Ui.Card(Ui.Text("Lecture des programmes au démarrage (registre, dossiers, tâches planifiées, services)…", "Body")));
        Task.Run(StartupScan.Scan).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            _loading = false; _loaded = true; _refresh.IsEnabled = true;
            if (t.IsFaulted) { Log.Error("Démarrage : lecture", t.Exception); _entries = new(); }
            else _entries = t.Result;
            AppState.StartupCount = _entries.Count(e => e.Enabled);
            AppState.Notify();
            Build();
        }));
    }

    void BuildPills()
    {
        _pills.Children.Clear();
        int on = _entries.Count(e => e.Enabled), off = _entries.Count - on, heavy = _entries.Count(e => e.Enabled && e.Impact == 2);
        _pills.Children.Add(Ui.Pill($"{on} actif{(on > 1 ? "s" : "")}", "SafeBg", "SafeFg").With(p => p.Margin = new Thickness(0, 0, 8, 0)));
        _pills.Children.Add(Ui.Pill($"{off} désactivé{(off > 1 ? "s" : "")}", "Surface3", "Text2").With(p => p.Margin = new Thickness(0, 0, 8, 0)));
        _pills.Children.Add(heavy > 0
            ? Ui.Pill($"{heavy} à fort impact sur le démarrage", "WarnBg", "WarnFg")
            : Ui.Pill("Aucun programme lourd actif", "InfoBg", "InfoFg"));
    }

    void Build()
    {
        if (_loading) return;
        BuildPills();
        _list.Children.Clear();
        var q = _filter.Text?.Trim() ?? "";
        var shown = _entries.Where(e => q.Length == 0
            || (e.Name ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || (e.Publisher ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || (e.Command ?? "").Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();

        if (shown.Count == 0)
        {
            _list.Children.Add(Ui.Card(Ui.Text(_entries.Count == 0 ? "Aucun programme tiers ne se lance au démarrage." : "Aucun résultat pour ce filtre.", "Body")));
            return;
        }

        foreach (var src in StartupScan.SourceOrder)
        {
            var items = shown.Where(e => e.Source == src).ToList();
            if (items.Count == 0) continue;
            var sp = new StackPanel();
            var gh = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            gh.Children.Add(Ui.Icon(SourceGlyph(src), 16, Ui.Res("Accent")).With(i => i.Margin = new Thickness(0, 0, 10, 0)));
            gh.Children.Add(Ui.Text(src, "H3"));
            gh.Children.Add(Ui.Text($"  ·  {items.Count(e => e.Enabled)} / {items.Count} actifs", "Small", 13).With(t => t.VerticalAlignment = VerticalAlignment.Center));
            sp.Children.Add(gh);
            if (src == StartupScan.SrcService)
                sp.Children.Add(Ui.Text("Un service désactivé passe en « manuel » : il démarre encore si un programme en a besoin.", "Small", 12).With(t => t.Margin = new Thickness(26, 0, 0, 8)));
            for (int i = 0; i < items.Count; i++)
                sp.Children.Add(Row(items[i], i < items.Count - 1));
            _list.Children.Add(Ui.Card(sp, new Thickness(22, 18, 22, 10)).With(c => c.Margin = new Thickness(0, 0, 0, 16)));
        }
    }

    static string SourceGlyph(string src) =>
        src == StartupScan.SrcRun ? "" : src == StartupScan.SrcFolder ? "" : src == StartupScan.SrcTask ? "" : "";

    UIElement Row(StartupEntry e, bool line)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4), MinHeight = 48 };
        Ui.Grid2(g, "36", "*", "130", "auto", "70");

        var ic = new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center };
        ic.SetResourceReference(Border.BackgroundProperty, "Surface3");
        ic.Child = Ui.Icon(e.Kind == StartupKind.Service ? "" : e.Kind == StartupKind.Task ? "" : "", 14, Ui.Res("Text2")).With(i => i.HorizontalAlignment = HorizontalAlignment.Center);
        g.Children.Add(ic.At(0));

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 0) };
        var name = Ui.Text(e.Name, "Body", 14, e.Enabled ? Ui.Res("Text") : Ui.Res("Muted"), FontWeights.SemiBold);
        name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap;
        info.Children.Add(name);
        var subTxt = string.IsNullOrWhiteSpace(e.Publisher) ? e.Command : e.Publisher + "  ·  " + e.Command;
        var sub = Ui.Text(subTxt, "Small", 12);
        sub.TextTrimming = TextTrimming.CharacterEllipsis; sub.TextWrapping = TextWrapping.NoWrap; sub.Margin = new Thickness(0, 2, 0, 0);
        info.Children.Add(sub);
        info.ToolTip = $"{e.Name}\n{e.Detail}" + (string.IsNullOrWhiteSpace(e.Publisher) ? "" : $"\nÉditeur : {e.Publisher}") + $"\n\n{e.Command}";
        info.Background = Brushes.Transparent;
        g.Children.Add(info.At(1));

        var impact = e.Impact switch { 2 => Ui.Pill(e.ImpactLabel, "WarnBg", "WarnFg"), 1 => Ui.Pill(e.ImpactLabel, "InfoBg", "InfoFg"), _ => Ui.Pill(e.ImpactLabel, "Surface3", "Text2") };
        impact.VerticalAlignment = VerticalAlignment.Center; impact.HorizontalAlignment = HorizontalAlignment.Left;
        impact.ToolTip = "Estimation d'après le programme et sa taille : plus l'impact est élevé, plus il ralentit l'ouverture de session.";
        g.Children.Add(impact.At(2));

        var loc = Ui.IconBtn("", "Ouvrir l'emplacement", (_, _) =>
        {
            if (File.Exists(e.Exe) || Directory.Exists(e.Exe)) Shell.OpenInExplorer(e.Exe);
            else Dialog.Info("Emplacement introuvable", "Le fichier de ce programme n'existe plus :\n" + e.Exe + "\n\nL'entrée de démarrage est peut-être orpheline : l'écran Registre peut la repérer.");
        });
        loc.Margin = new Thickness(0, 0, 14, 0); loc.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(loc.At(3));

        var sw = new ToggleButton { Style = (Style)Application.Current.Resources["Switch"], IsChecked = e.Enabled, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        sw.ToolTip = e.Enabled ? "Actif au démarrage — cliquer pour désactiver" : "Désactivé — cliquer pour réactiver";
        System.Windows.Automation.AutomationProperties.SetName(sw, "Lancer " + e.Name + " au démarrage");
        bool busy = false;
        sw.Click += async (_, _) =>
        {
            if (busy) return;
            var want = sw.IsChecked == true;
            if (!want && e.Kind == StartupKind.Service &&
                !Dialog.Confirm("Passer le service en manuel ?", $"« {e.Name} » ne démarrera plus automatiquement. Il pourra toujours être lancé par le programme qui en a besoin.\n\nC'est réversible à tout moment.", "Passer en manuel"))
            { sw.IsChecked = true; return; }
            busy = true; sw.IsEnabled = false;
            var ok = await Task.Run(() => StartupScan.SetEnabled(e, want));
            busy = false; sw.IsEnabled = true;
            if (!ok)
            {
                sw.IsChecked = !want;
                Dialog.Info("Modification impossible", $"« {e.Name} » n'a pas pu être {(want ? "réactivé" : "désactivé")}. Le détail est dans le journal.");
                return;
            }
            name.Foreground = want ? Ui.Res("Text") : Ui.Res("Muted");
            sw.ToolTip = want ? "Actif au démarrage — cliquer pour désactiver" : "Désactivé — cliquer pour réactiver";
            AppState.StartupCount = _entries.Count(x => x.Enabled);
            AppState.Notify();
            BuildPills();
        };
        g.Children.Add(sw.At(4));

        if (!line) return g;
        var wrap = new StackPanel();
        wrap.Children.Add(g);
        var sep = new Border { Height = 1, Margin = new Thickness(42, 4, 0, 0) };
        sep.SetResourceReference(Border.BackgroundProperty, "Line");
        wrap.Children.Add(sep);
        return wrap;
    }
}
