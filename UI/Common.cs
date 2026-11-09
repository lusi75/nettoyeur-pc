using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

/// <summary>État partagé entre les écrans.</summary>
public static class AppState
{
    public static CleanSession Session = new();
    public static Task ScanTask;
    public static List<DriveSummary> Drives = SystemInfo.Drives();
    public static bool HealthLoaded;
    public static int StartupCount = -1;
    public static readonly Dictionary<string, StorageScan> Scans = new(StringComparer.OrdinalIgnoreCase);
    public static HistoryEvent LastResult;
    public static event Action Changed;
    public static void Notify() => Application.Current.Dispatcher.BeginInvoke(() => Changed?.Invoke());

    public static Task EnsureScan(bool force = false)
    {
        if (force || ScanTask == null || (ScanTask.IsCompleted && !Session.Scanned))
        {
            if (force) Session = new CleanSession();
            var s = Session;
            ScanTask = s.ScanAsync(null).ContinueWith(_ => Notify());
        }
        return ScanTask;
    }

    public static void RefreshDrives()
    {
        var old = Drives;
        Drives = SystemInfo.Drives();
        foreach (var d in Drives)
        {
            var o = old.FirstOrDefault(x => x.Letter == d.Letter);
            if (o != null) { d.Model = o.Model; d.Media = o.Media; d.HealthPct = o.HealthPct; d.TempC = o.TempC; d.HealthStatus = o.HealthStatus; }
        }
    }
}

public interface IPage { void OnShow(object arg); }

public static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    public static Brush Hex(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }

    public static TextBlock Icon(string glyph, double size = 16, Brush fg = null)
    {
        var t = new TextBlock { Text = glyph, FontFamily = (FontFamily)Application.Current.Resources["Icons"], FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        if (fg != null) t.Foreground = fg;
        return t;
    }

    public static TextBlock Text(string s, string style = "Body", double? size = null, Brush fg = null, FontWeight? weight = null)
    {
        var t = new TextBlock { Text = s, Style = (Style)Application.Current.Resources[style] };
        if (size.HasValue) t.FontSize = size.Value;
        if (fg != null) t.Foreground = fg;
        if (weight.HasValue) t.FontWeight = weight.Value;
        return t;
    }

    public static Border Pill(string text, Risk risk) => risk switch
    {
        Risk.Safe => Pill(text, "SafeBg", "SafeFg"),
        Risk.Check => Pill(text, "WarnBg", "WarnFg"),
        _ => Pill(text, "DangerBg", "DangerFg"),
    };

    public static Border Pill(string text, string bgKey, string fgKey)
    {
        var b = new Border { Style = (Style)Application.Current.Resources["Pill"] };
        b.SetResourceReference(Border.BackgroundProperty, bgKey);
        var t = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold };
        t.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
        b.Child = t;
        return b;
    }

    public static Button Btn(string text, string style = "Ghost", RoutedEventHandler click = null, string glyph = null)
    {
        var b = new Button { Style = (Style)Application.Current.Resources[style] };
        if (glyph != null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(Icon(glyph, 14));
            if (!string.IsNullOrEmpty(text)) sp.Children.Add(new TextBlock { Text = text, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            b.Content = sp;
        }
        else b.Content = text;
        if (click != null) b.Click += click;
        return b;
    }

    public static Button IconBtn(string glyph, string tip, RoutedEventHandler click, bool danger = false)
    {
        var b = new Button { Style = (Style)Application.Current.Resources[danger ? "Danger" : "IconBtn"], Content = Icon(glyph, 14), ToolTip = tip };
        if (danger) { b.Width = 34; b.Height = 34; b.Padding = new Thickness(0); }
        System.Windows.Automation.AutomationProperties.SetName(b, tip);
        b.Click += click;
        return b;
    }

    public static Border Card(UIElement child, Thickness? padding = null)
    {
        var c = new Border { Style = (Style)Application.Current.Resources["Card"], Child = child };
        if (padding.HasValue) c.Padding = padding.Value;
        return c;
    }

    public static ProgressBar Bar(double pct, Brush fg = null, double h = 8)
    {
        var p = new ProgressBar { Style = (Style)Application.Current.Resources["Bar"], Minimum = 0, Maximum = 100, Value = Math.Clamp(pct, 0, 100), Height = h };
        if (fg != null) p.Foreground = fg;
        return p;
    }

    public static void Grid2(Grid g, params string[] cols)
    {
        foreach (var c in cols)
            g.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = c == "*" ? new GridLength(1, GridUnitType.Star) : c == "auto" ? GridLength.Auto
                    : c.EndsWith('*') ? new GridLength(double.Parse(c.TrimEnd('*'), CultureInfo.InvariantCulture), GridUnitType.Star) : new GridLength(double.Parse(c, CultureInfo.InvariantCulture))
            });
    }

    public static T At<T>(this T e, int col, int row = 0, int colSpan = 1) where T : UIElement
    {
        Grid.SetColumn(e, col); Grid.SetRow(e, row); if (colSpan > 1) Grid.SetColumnSpan(e, colSpan); return e;
    }

    // ---- Barre de titre sombre (Windows 10 20H1+ / Windows 11)
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    public static void DarkTitleBar(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).EnsureHandle();
            int on = 1; DwmSetWindowAttribute(h, 20, ref on, 4);
            int color = 0x001C1613; DwmSetWindowAttribute(h, 35, ref color, 4);   // fond de la barre de titre (BGR)
        }
        catch { }
    }
}

/// <summary>Fenêtre de confirmation sombre, cohérente avec l'appli.</summary>
public class Dialog : Window
{
    public bool Ok;

    public static bool Confirm(string title, string message, string okText = "Continuer", string cancelText = "Annuler", bool danger = false, UIElement extra = null)
    {
        var d = new Dialog(title, message, okText, cancelText, danger, extra);
        d.ShowDialog();
        return d.Ok;
    }

    public static void Info(string title, string message) => new Dialog(title, message, "OK", null, false, null).ShowDialog();

    Dialog(string title, string message, string okText, string cancelText, bool danger, UIElement extra)
    {
        Owner = Application.Current.MainWindow;
        WindowStartupLocation = Owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height; Width = 480; ShowInTaskbar = false; Title = title;
        FontFamily = (FontFamily)Application.Current.Resources["Ui"];

        var sp = new StackPanel();
        sp.Children.Add(Ui.Text(title, "H2"));
        var msg = Ui.Text(message, "Body"); msg.Margin = new Thickness(0, 10, 0, 0); sp.Children.Add(msg);
        if (extra != null) { if (extra is FrameworkElement fe) fe.Margin = new Thickness(0, 14, 0, 0); sp.Children.Add(extra); }
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        if (cancelText != null) { var c = Ui.Btn(cancelText, "Ghost", (_, _) => { Ok = false; Close(); }); c.Height = 40; c.Margin = new Thickness(0, 0, 10, 0); c.IsCancel = true; btns.Children.Add(c); }
        var ok = Ui.Btn(okText, "Primary", (_, _) => { Ok = true; Close(); }); ok.IsDefault = true; ok.Height = 40;
        if (danger) ok.SetResourceReference(BackgroundProperty, "DangerFg");
        btns.Children.Add(ok);
        sp.Children.Add(btns);

        var card = new Border { CornerRadius = new CornerRadius(14), Padding = new Thickness(26), BorderThickness = new Thickness(1), Child = sp, Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.5 } };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Line2");
        Content = card;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { Ok = false; Close(); } };
    }
}

/// <summary>Anneau de score (0–100).</summary>
public class Ring : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(Ring), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get; set; } = 14;

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight), r = (size - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(Ui.Res("Track"), Thickness), c, r, r);
        var v = Math.Clamp(Value, 0, 100) / 100.0;
        if (v <= 0) return;
        if (v >= 0.999) { dc.DrawEllipse(null, new Pen(Ui.Res("Accent"), Thickness), c, r, r); return; }
        double a = v * 2 * Math.PI;
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X + r * Math.Sin(a), c.Y - r * Math.Cos(a));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, new Pen(Ui.Res("Accent"), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, g);
    }
}

/// <summary>Barre empilée (répartition par catégorie).</summary>
public class StackBar : FrameworkElement
{
    public List<(double Value, Brush Brush)> Parts = new();
    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        var clip = new RectangleGeometry(rect, ActualHeight / 2, ActualHeight / 2);
        dc.PushClip(clip);
        dc.DrawRectangle(Ui.Res("Track"), null, rect);
        double total = Parts.Sum(p => p.Value), x = 0;
        if (total > 0)
            foreach (var p in Parts)
            {
                var w = ActualWidth * p.Value / total;
                if (w > 0.5) dc.DrawRectangle(p.Brush, null, new Rect(x, 0, Math.Max(0, w - 2), ActualHeight));
                x += w;
            }
        dc.Pop();
    }
}
