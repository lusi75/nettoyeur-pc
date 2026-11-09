using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

/// <summary>Journal de l'iPhone en direct (comme la console d'Apple Configurator), avec le classement des processus qui produisent le plus d'erreurs.</summary>
public class SyslogWindow : Window
{
    public class Line
    {
        public string Time { get; set; }
        public string Proc { get; set; }
        public string Level { get; set; }
        public string Msg { get; set; }
        public Brush Color { get; set; }
        public bool IsError => Level is "Error" or "Fault" or "Critical" or "Alert" or "Emergency";
    }

    static readonly Regex Rx = new(@"^(\w{3}\s+\d+\s[\d:]+)\s+\S+\s+([^\[\s]+?)(?:\([^)]*\))?\[(\d+)\]\s*<(\w+)>:\s?(.*)$", RegexOptions.Compiled);
    const int MaxLines = 4000;

    readonly string _exe, _args;
    readonly Func<string, Line> _parse;
    static readonly Regex LogcatRx = new(@"^(\d\d-\d\d\s[\d:.]+)\s+\d+\s+\d+\s+([VDIWEF])\s+(.*?)\s*:\s(.*)$", RegexOptions.Compiled);

    public static Line ParseIos(string raw)
    {
        var m = Rx.Match(raw);
        return m.Success ? new Line { Time = m.Groups[1].Value, Proc = m.Groups[2].Value, Level = m.Groups[4].Value, Msg = m.Groups[5].Value } : null;
    }

    public static Line ParseLogcat(string raw)
    {
        var m = LogcatRx.Match(raw);
        if (!m.Success) return null;
        var lvl = m.Groups[2].Value switch { "E" => "Error", "F" => "Fault", "W" => "Warning", "I" => "Info", "D" => "Debug", _ => "Verbose" };
        return new Line { Time = m.Groups[1].Value, Proc = m.Groups[3].Value, Level = lvl, Msg = m.Groups[4].Value };
    }

    /// <summary>Journal de l'iPhone.</summary>
    public SyslogWindow(string udid, string deviceName) : this(deviceName, Path.Combine(IPhone.ToolsDir, "idevicesyslog.exe"), $"-u {udid} --no-colors", ParseIos, true) { }
    readonly ObservableCollection<Line> _view = new();
    readonly List<Line> _all = new();
    readonly ConcurrentQueueLite _incoming = new();
    readonly Dictionary<string, (int Total, int Errors)> _stats = new(StringComparer.OrdinalIgnoreCase);
    readonly ListBox _list = new();
    readonly StackPanel _top = new();
    readonly TextBox _filter = new() { Height = 34, Width = 260, VerticalContentAlignment = VerticalAlignment.Center };
    readonly ToggleButton _errorsOnly = new() { Style = (Style)Application.Current.Resources["Switch"], VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _status = Ui.Text("", "Small", 12);
    readonly Button _startStop;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    string _procFilter;
    readonly bool _ios;
    Process _proc;
    StreamWriter _file;
    string _filePath;
    bool _autoScroll = true;
    long _count, _errors;

    class ConcurrentQueueLite
    {
        readonly Queue<string> _q = new();
        public void Add(string s) { lock (_q) { _q.Enqueue(s); if (_q.Count > 20000) _q.Dequeue(); } }
        public List<string> Drain() { lock (_q) { var l = _q.ToList(); _q.Clear(); return l; } }
    }

    public SyslogWindow(string deviceName, string exe, string args, Func<string, Line> parse, bool ios)
    {
        _exe = exe; _args = args; _parse = parse; _ios = ios;
        Title = $"Journal en direct · {deviceName}";
        Width = 1250; Height = 760; MinWidth = 900; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Bg");
        SetResourceReference(ForegroundProperty, "Text");
        FontFamily = (FontFamily)Application.Current.Resources["Ui"];
        SourceInitialized += (_, _) => Ui.DarkTitleBar(this);

        var root = new DockPanel { Margin = new Thickness(20) };

        // Barre d'outils
        var bar = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        Ui.Grid2(bar, "auto", "auto", "auto", "*", "auto", "auto");
        _startStop = Ui.Btn("Démarrer", "Primary", (_, _) => { if (_proc == null) Start(); else Stop(); }, "");
        _startStop.Height = 36;
        bar.Children.Add(_startStop);
        var fg = new Grid { Margin = new Thickness(12, 0, 0, 0) };
        fg.Children.Add(_filter);
        var ph = Ui.Text("Filtrer (texte, appli…)", "Small", 13); ph.IsHitTestVisible = false; ph.Margin = new Thickness(10, 0, 0, 0); ph.VerticalAlignment = VerticalAlignment.Center;
        fg.Children.Add(ph);
        _filter.TextChanged += (_, _) => { ph.Visibility = _filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Refilter(); };
        bar.Children.Add(fg.At(1));
        var eo = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        eo.Children.Add(_errorsOnly);
        eo.Children.Add(Ui.Text("Erreurs seulement", "Body", 13).With(t => { t.Margin = new Thickness(8, 0, 0, 0); t.VerticalAlignment = VerticalAlignment.Center; }));
        _errorsOnly.Checked += (_, _) => Refilter(); _errorsOnly.Unchecked += (_, _) => Refilter();
        bar.Children.Add(eo.At(2));
        bar.Children.Add(_status.With(t => { t.VerticalAlignment = VerticalAlignment.Center; t.HorizontalAlignment = HorizontalAlignment.Right; t.Margin = new Thickness(0, 0, 12, 0); }).At(3));
        var clear = Ui.Btn("Vider", "Ghost", (_, _) => { _all.Clear(); _view.Clear(); _stats.Clear(); _count = _errors = 0; BuildTop(); }, "");
        clear.Height = 36; clear.Margin = new Thickness(0, 0, 8, 0);
        bar.Children.Add(clear.At(4));
        var save = Ui.Btn("Enregistrer", "Ghost", (_, _) => SaveCopy(), "");
        save.Height = 36;
        bar.Children.Add(save.At(5));
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        // Classement des sources d'erreurs
        var side = new StackPanel();
        side.Children.Add(Ui.Text("Sources d'erreurs", "H3"));
        side.Children.Add(Ui.Text("Clique pour ne voir qu'elle", "Small", 12).With(t => t.Margin = new Thickness(0, 2, 0, 10)));
        side.Children.Add(_top);
        var sideCard = Ui.Card(new ScrollViewer { Content = side, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, new Thickness(16));
        sideCard.Width = 300; sideCard.Margin = new Thickness(12, 0, 0, 0);
        DockPanel.SetDock(sideCard, Dock.Right);
        root.Children.Add(sideCard);

        // Liste virtualisée
        _list.ItemsSource = _view;
        _list.SetResourceReference(Control.BackgroundProperty, "Surface");
        _list.BorderThickness = new Thickness(0);
        _list.FontFamily = (FontFamily)Application.Current.Resources["Mono"];
        _list.FontSize = 12;
        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.ItemTemplate = RowTemplate();
        _list.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.ExtentHeightChange == 0) _autoScroll = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 2;
        }));
        var listCard = Ui.Card(_list, new Thickness(6));
        root.Children.Add(listCard);
        Content = root;

        _timer.Tick += (_, _) => Flush();
        Loaded += (_, _) => Start();
        Closed += (_, _) => Stop();
    }

    static DataTemplate RowTemplate()
    {
        var g = new FrameworkElementFactory(typeof(Grid));
        void Col(GridLength w) { var c = new FrameworkElementFactory(typeof(ColumnDefinition)); c.SetValue(ColumnDefinition.WidthProperty, w); g.AppendChild(c); }
        Col(new GridLength(118)); Col(new GridLength(170)); Col(new GridLength(1, GridUnitType.Star));
        FrameworkElementFactory T(string path, int col, bool wrap = false)
        {
            var t = new FrameworkElementFactory(typeof(TextBlock));
            t.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(path));
            t.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("Color"));
            t.SetValue(Grid.ColumnProperty, col);
            t.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 10, 0));
            if (wrap) t.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            else t.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            return t;
        }
        g.AppendChild(T("Time", 0)); g.AppendChild(T("Proc", 1)); g.AppendChild(T("Msg", 2, true));
        return new DataTemplate { VisualTree = g };
    }

    void Start()
    {
        var err = _ios ? IPhone.EnsureTools() : Android.EnsureTools();
        if (err != null) { _status.Text = err; return; }
        try
        {
            var dir = Path.Combine(AppPaths.Data, _ios ? "iPhone" : "Android", "Journaux");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, $"journal_{DateTime.Now:yyyy-MM-dd_HHmmss}.txt");
            _file = new StreamWriter(_filePath, false, new UTF8Encoding(false)) { AutoFlush = false };
            var psi = new ProcessStartInfo(_exe, _args)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(_exe)!,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _proc.OutputDataReceived += (_, e) => { if (e.Data != null) _incoming.Add(e.Data); };
            _proc.ErrorDataReceived += (_, e) => { if (e.Data != null) _incoming.Add(e.Data); };
            _proc.Exited += (_, _) => Dispatcher.BeginInvoke(() => { if (_proc != null) { Flush(); Stop(); _status.Text = "Connexion au téléphone perdue."; } });
            _proc.Start(); _proc.BeginOutputReadLine(); _proc.BeginErrorReadLine();
            _timer.Start();
            _startStop.Content = "Arrêter";
            Log.Info("Journal du téléphone en direct démarré → " + _filePath);
        }
        catch (Exception ex) { Log.Error("Journal iPhone", ex); _status.Text = "Impossible de lancer le journal : " + ex.Message; _proc = null; }
    }

    void Stop()
    {
        _timer.Stop();
        var p = _proc; _proc = null;
        try { if (p != null && !p.HasExited) p.Kill(true); } catch { }
        try { _file?.Flush(); _file?.Dispose(); } catch { }
        _file = null;
        _startStop.Content = "Reprendre";
    }

    void Flush()
    {
        var lines = _incoming.Drain();
        if (lines.Count == 0) return;
        var f = _filter.Text.Trim();
        foreach (var raw in lines)
        {
            try { _file?.WriteLine(raw); } catch { }
            var l = _parse(raw);
            if (l != null) { }
            else if (_all.Count > 0 && raw.StartsWith(" ")) { _all[^1].Msg += "\n" + raw.Trim(); continue; }
            else l = new Line { Time = "", Proc = "", Level = "", Msg = raw };
            l.Color = l.IsError ? Ui.Res("DangerFg") : l.Level == "Warning" ? Ui.Res("WarnFg") : l.Level is "Debug" or "Info" ? Ui.Res("Muted") : Ui.Res("Text2");
            _count++;
            if (l.Proc.Length > 0)
            {
                _stats.TryGetValue(l.Proc, out var s);
                _stats[l.Proc] = (s.Total + 1, s.Errors + (l.IsError ? 1 : 0));
            }
            if (l.IsError) _errors++;
            _all.Add(l);
            if (Matches(l, f)) _view.Add(l);
        }
        if (_all.Count > MaxLines) _all.RemoveRange(0, _all.Count - MaxLines);
        while (_view.Count > MaxLines) _view.RemoveAt(0);
        try { _file?.Flush(); } catch { }
        if (_autoScroll && _view.Count > 0) _list.ScrollIntoView(_view[^1]);
        _status.Text = $"{_count:#,0} lignes · {_errors:#,0} erreurs";
        BuildTop();
    }

    bool Matches(Line l, string f)
    {
        if (_errorsOnly.IsChecked == true && !l.IsError) return false;
        if (_procFilter != null && !l.Proc.Equals(_procFilter, StringComparison.OrdinalIgnoreCase)) return false;
        return f.Length == 0 || l.Msg.Contains(f, StringComparison.OrdinalIgnoreCase) || l.Proc.Contains(f, StringComparison.OrdinalIgnoreCase);
    }

    void Refilter()
    {
        var f = _filter.Text.Trim();
        _view.Clear();
        foreach (var l in _all) if (Matches(l, f)) _view.Add(l);
        if (_view.Count > 0) _list.ScrollIntoView(_view[^1]);
    }

    DateTime _lastTop;
    void BuildTop()
    {
        if ((DateTime.Now - _lastTop).TotalMilliseconds < 1000 && _stats.Count > 0) return;
        _lastTop = DateTime.Now;
        _top.Children.Clear();
        if (_procFilter != null)
        {
            var all = Ui.Btn($"✕  {_procFilter} (tout afficher)", "Ghost", (_, _) => { _procFilter = null; Refilter(); BuildTop(); });
            all.Margin = new Thickness(0, 0, 0, 10); all.HorizontalAlignment = HorizontalAlignment.Left;
            _top.Children.Add(all);
        }
        var top = _stats.Where(s => s.Value.Errors > 0).OrderByDescending(s => s.Value.Errors).Take(15).ToList();
        if (top.Count == 0) { _top.Children.Add(Ui.Text(_count == 0 ? "En attente des premières lignes… Déverrouille le téléphone et utilise-le normalement." : "Aucune erreur pour l'instant.", "Small", 12).With(t => t.TextWrapping = TextWrapping.Wrap)); return; }
        var max = top[0].Value.Errors;
        foreach (var (name, v) in top.Select(s => (s.Key, s.Value)))
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent, ToolTip = $"{name} : {v.Errors} erreurs sur {v.Total} lignes" };
            Ui.Grid2(g, "*", "auto");
            g.Children.Add(Ui.Text(name, "Body", 13).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis));
            g.Children.Add(Ui.Text(v.Errors.ToString("#,0"), "Body", 13, Ui.Res("DangerFg"), FontWeights.SemiBold).At(1));
            var b = Ui.Bar(100.0 * v.Errors / max, Ui.Res("DangerFg"), 4);
            b.Margin = new Thickness(0, 20, 0, 0);
            g.Children.Add(b.At(0, 0, 2));
            var n = name;
            g.MouseLeftButtonUp += (_, _) => { _procFilter = n; Refilter(); _lastTop = default; BuildTop(); };
            _top.Children.Add(g);
        }
    }

    void SaveCopy()
    {
        try { _file?.Flush(); } catch { }
        if (_filePath != null && File.Exists(_filePath)) Shell.OpenInExplorer(_filePath);
    }
}
