using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

/// <summary>Écran Téléphone : choix iPhone / Android, puis l'écran correspondant.</summary>
public class PhonePage : UserControl, IPage
{
    readonly IPhonePage _ios = new();
    readonly AndroidPage _android = new();
    readonly ContentControl _host = new();
    readonly ContentControl _sub = new();
    readonly Button _change, _refresh;
    readonly TextBlock _chooseSub = Ui.Text("Quel téléphone veux-tu nettoyer ?", "Small", 14);
    string _kind;          // null = écran de choix
    bool _iosLoaded, _androidLoaded;

    public PhonePage()
    {
        var root = new DockPanel { Margin = new Thickness(40, 30, 40, 24) };
        var head = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        Ui.Grid2(head, "*", "auto", "auto");
        var hs = new StackPanel();
        hs.Children.Add(Ui.Text("Téléphone", "H1"));
        hs.Children.Add(_sub.With(t => t.Margin = new Thickness(0, 6, 0, 0)));
        head.Children.Add(hs);
        _change = Ui.Btn("Changer de téléphone", "Ghost", (_, _) => Choose(), "");
        _change.Height = 40; _change.Margin = new Thickness(0, 0, 12, 0); _change.VerticalAlignment = VerticalAlignment.Bottom;
        head.Children.Add(_change.At(1));
        _refresh = Ui.IconBtn("", "Rechercher le téléphone", (_, _) => { if (_kind == "iphone") _ios.Load(); else _android.Load(); });
        _refresh.Width = 40; _refresh.Height = 40; _refresh.VerticalAlignment = VerticalAlignment.Bottom;
        head.Children.Add(_refresh.At(2));
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(new ScrollViewer { Content = _host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
        Choose();
    }

    public void OnShow(object arg) { }

    // ------------------------------------------------------------------ Écran de choix
    void Choose()
    {
        _kind = null;
        _sub.Content = _chooseSub;
        _change.Visibility = _refresh.Visibility = Visibility.Collapsed;
        var last = Settings.Current.PhoneKind;
        var g = new Grid { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        Ui.Grid2(g, "auto", "auto");
        g.Children.Add(ChoiceCard("iphone", "iPhone", "iOS · câble USB et iTunes", IPhoneArt(),
            new[] { "Journal en direct et rapports de plantage", "Taille réelle de chaque appli", "Sauvegarde complète sur le PC", "Remise à zéro des données système" }, last == "iphone"));
        g.Children.Add(ChoiceCard("android", "Android", "Samsung, Pixel, Xiaomi… · câble USB", AndroidArt(),
            new[] { "Vidage des caches de toutes les applis", "Photos et vidéos en double", "Gros fichiers et corbeille", "Copie des photos sur le PC" }, last == "android").With(c => c.Margin = new Thickness(28, 0, 0, 0)).At(1));
        _host.Content = g;
    }

    Border ChoiceCard(string kind, string title, string sub, UIElement art, string[] features, bool last)
    {
        var sp = new StackPanel { Width = 330 };
        var stage = new Border { Height = 420, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 0, 22), Child = art, ClipToBounds = true };
        stage.Background = new RadialGradientBrush(Color.FromArgb(70, 61, 214, 181), Colors.Transparent) { GradientOrigin = new Point(0.5, 0.55), Center = new Point(0.5, 0.55), RadiusX = 0.6, RadiusY = 0.55 };
        sp.Children.Add(stage);
        var t = new StackPanel { Orientation = Orientation.Horizontal };
        t.Children.Add((kind == "android" ? AndroidRobot(34) : IosBadge(34)).With(b => { b.Margin = new Thickness(0, 0, 12, 0); b.VerticalAlignment = VerticalAlignment.Center; }));
        t.Children.Add(Ui.Text(title, "H2").With(x => x.VerticalAlignment = VerticalAlignment.Center));
        if (last) t.Children.Add(Ui.Pill("dernier choix", "InfoBg", "InfoFg").With(p => { p.Margin = new Thickness(12, 0, 0, 0); p.VerticalAlignment = VerticalAlignment.Center; }));
        sp.Children.Add(t);
        sp.Children.Add(Ui.Text(sub, "Small", 13).With(x => x.Margin = new Thickness(0, 4, 0, 16)));
        foreach (var f in features)
        {
            var r = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            r.Children.Add(Ui.Icon("", 13, Ui.Res("Accent")).With(i => { i.Margin = new Thickness(0, 2, 10, 0); i.VerticalAlignment = VerticalAlignment.Top; }));
            r.Children.Add(Ui.Text(f, "Body", 14));
            sp.Children.Add(r);
        }
        var btn = Ui.Btn($"Choisir {title}", "Primary", (_, _) => Open(kind), "");
        btn.Height = 42; btn.Margin = new Thickness(0, 14, 0, 0); btn.HorizontalAlignment = HorizontalAlignment.Stretch;
        sp.Children.Add(btn);

        var card = new Border { CornerRadius = new CornerRadius(18), Padding = new Thickness(26), BorderThickness = new Thickness(1.5), Child = sp, Cursor = Cursors.Hand };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, last ? "Accent" : "Line");
        card.MouseEnter += (_, _) => { card.SetResourceReference(Border.BorderBrushProperty, "Accent"); card.RenderTransform = new TranslateTransform(0, -3); };
        card.MouseLeave += (_, _) => { card.SetResourceReference(Border.BorderBrushProperty, last ? "Accent" : "Line"); card.RenderTransform = null; };
        card.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not Button) Open(kind); };
        return card;
    }

    void Open(string kind)
    {
        _kind = kind;
        Settings.Current.PhoneKind = kind; Settings.Current.Save();
        _change.Visibility = _refresh.Visibility = Visibility.Visible;
        if (kind == "iphone")
        {
            _host.Content = _ios; _sub.Content = _ios.Sub;
            if (!_iosLoaded) { _iosLoaded = true; _ios.Load(); }
        }
        else
        {
            _host.Content = _android; _sub.Content = _android.Sub;
            if (!_androidLoaded) { _androidLoaded = true; _android.Load(); }
        }
    }

    // ------------------------------------------------------------------ Badges
    /// <summary>Tête du robot Android. Le robot Android est reproduit ou modifié à partir d'un travail créé et partagé par Google,
    /// utilisé selon les conditions de la licence Creative Commons Attribution 3.0.</summary>
    public static FrameworkElement AndroidRobot(double size)
    {
        var green = Ui.Hex("#3DDC84");
        var c = new Canvas { Width = 100, Height = 62 };
        // tête : demi-disque
        c.Children.Add(new System.Windows.Shapes.Path { Fill = green, Data = Geometry.Parse("M 6,60 A 44,44 0 0 1 94,60 Z") });
        // antennes
        c.Children.Add(new Line { X1 = 26, Y1 = 22, X2 = 15, Y2 = 4, Stroke = green, StrokeThickness = 5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        c.Children.Add(new Line { X1 = 74, Y1 = 22, X2 = 85, Y2 = 4, Stroke = green, StrokeThickness = 5, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        // yeux
        var eye1 = new Ellipse { Width = 9, Height = 9, Fill = Ui.Hex("#171A21") }; Canvas.SetLeft(eye1, 27); Canvas.SetTop(eye1, 36);
        var eye2 = new Ellipse { Width = 9, Height = 9, Fill = Ui.Hex("#171A21") }; Canvas.SetLeft(eye2, 64); Canvas.SetTop(eye2, 36);
        c.Children.Add(eye1); c.Children.Add(eye2);
        return new Viewbox { Width = size, Height = size * 0.62, Child = c, ToolTip = "Le robot Android est reproduit à partir d'un travail créé et partagé par Google (licence CC BY 3.0)." };
    }

    static FrameworkElement IosBadge(double size) => new Border
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.24),
        Background = Wallpaper(("#5E5CE6", 0), ("#BF5AF2", 0.55), ("#FF6482", 1)),
        Child = new TextBlock { Text = "iOS", Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = size * 0.36, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    // ------------------------------------------------------------------ Illustrations des téléphones
    static readonly DropShadowEffect Shadow = new() { BlurRadius = 40, ShadowDepth = 14, Direction = 270, Opacity = 0.55, Color = Colors.Black };

    static Brush Wallpaper(params (string Hex, double Off)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        foreach (var (h, o) in stops) b.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(h), o));
        b.Freeze(); return b;
    }

    static UIElement StatusBar(string time, Brush fg, double side = 22, double top = 12)
    {
        var g = new Grid { Margin = new Thickness(side, top, side, 0), VerticalAlignment = VerticalAlignment.Top };
        Ui.Grid2(g, "*", "auto");
        g.Children.Add(new TextBlock { Text = time, Foreground = fg, FontSize = 11, FontWeight = FontWeights.SemiBold });
        var r = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 4; i++) r.Children.Add(new Rectangle { Width = 3, Height = 4 + i * 2, Fill = fg, Margin = new Thickness(0, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Bottom, RadiusX = 1, RadiusY = 1 });
        r.Children.Add(new Border { Width = 20, Height = 9, CornerRadius = new CornerRadius(2.5), BorderBrush = fg, BorderThickness = new Thickness(1), Margin = new Thickness(5, 0, 0, 0), Child = new Rectangle { Fill = fg, Margin = new Thickness(1.2), HorizontalAlignment = HorizontalAlignment.Left, Width = 13 } });
        g.Children.Add(r.At(1));
        return g;
    }

    static UIElement Clock(string t, string d) 
    {
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 62, 0, 0) };
        s.Children.Add(new TextBlock { Text = d, Foreground = Brushes.White, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.85 });
        s.Children.Add(new TextBlock { Text = t, Foreground = Brushes.White, FontSize = 52, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, -4, 0, 0) });
        return s;
    }

    /// <summary>Smartphone à bords très arrondis, cadre titane et îlot noir en haut de l'écran.</summary>
    static UIElement IPhoneArt()
    {
        var screen = new Grid();
        screen.Children.Add(new Border { CornerRadius = new CornerRadius(35), Background = Wallpaper(("#1B1F5E", 0), ("#6B3FA0", 0.45), ("#E0607E", 0.8), ("#F5A15A", 1)) });
        screen.Children.Add(new Border { Width = 50, Height = 17, CornerRadius = new CornerRadius(8.5), Background = Brushes.Black, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 10, 0, 0) });
        screen.Children.Add(StatusBar("9:41", Brushes.White, 15, 13));
        screen.Children.Add(Clock("9:41", "lundi 9 novembre"));
        // dock
        var dock = new Border { CornerRadius = new CornerRadius(18), Height = 46, Margin = new Thickness(10, 0, 10, 10), VerticalAlignment = VerticalAlignment.Bottom, Background = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)) };
        var icons = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Rows = 1, Margin = new Thickness(8, 0, 8, 0) };
        foreach (var c in new[] { "#34C759", "#0A84FF", "#FF9F0A", "#FF375F" })
            icons.Children.Add(new Border { Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = Ui.Hex(c), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        dock.Child = icons;
        screen.Children.Add(dock);

        var body = new Border
        {
            Width = 200, Height = 400, CornerRadius = new CornerRadius(44), Padding = new Thickness(8),
            Background = Brushes.Black, BorderThickness = new Thickness(3.5),
            BorderBrush = Wallpaper(("#C9C5BD", 0), ("#8E8A83", 0.5), ("#D8D4CC", 1)),
            Child = screen, Effect = Shadow,
        };
        var g = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new RotateTransform(-6), RenderTransformOrigin = new Point(0.5, 0.5) };
        // boutons latéraux
        g.Children.Add(new Rectangle { Width = 3, Height = 22, Fill = Ui.Hex("#9A968F"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-2, 82, 0, 0), RadiusX = 1, RadiusY = 1 });
        g.Children.Add(new Rectangle { Width = 3, Height = 36, Fill = Ui.Hex("#9A968F"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-2, 122, 0, 0), RadiusX = 1, RadiusY = 1 });
        g.Children.Add(new Rectangle { Width = 3, Height = 36, Fill = Ui.Hex("#9A968F"), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(-2, 172, 0, 0), RadiusX = 1, RadiusY = 1 });
        g.Children.Add(new Rectangle { Width = 3, Height = 54, Fill = Ui.Hex("#9A968F"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 130, -2, 0), RadiusX = 1, RadiusY = 1 });
        g.Children.Add(body);
        return g;
    }

    /// <summary>Smartphone Android haut de gamme : bords plus carrés, écran plat sans bordure, caméra « poinçon ».</summary>
    static UIElement AndroidArt()
    {
        var screen = new Grid();
        screen.Children.Add(new Border { CornerRadius = new CornerRadius(14), Background = Wallpaper(("#03251F", 0), ("#0B6E5F", 0.4), ("#3DD6B5", 0.75), ("#B9F6E6", 1)) });
        screen.Children.Add(new Ellipse { Width = 11, Height = 11, Fill = Brushes.Black, Stroke = Ui.Hex("#222"), StrokeThickness = 1, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 11, 0, 0) });
        screen.Children.Add(StatusBar("09:41", Brushes.White, 20, 12));
        screen.Children.Add(Clock("09:41", "Lun. 9 novembre"));
        // icônes rondes et barre de navigation
        var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = 4, Rows = 1, Margin = new Thickness(14, 0, 14, 30), VerticalAlignment = VerticalAlignment.Bottom, Height = 40 };
        foreach (var c in new[] { "#4285F4", "#EA4335", "#FBBC05", "#34A853" })
            row.Children.Add(new Ellipse { Width = 30, Height = 30, Fill = Ui.Hex(c), HorizontalAlignment = HorizontalAlignment.Center });
        screen.Children.Add(row);
        screen.Children.Add(AndroidRobot(58).With(r => { r.VerticalAlignment = VerticalAlignment.Bottom; r.HorizontalAlignment = HorizontalAlignment.Center; r.Margin = new Thickness(0, 0, 0, 92); }));
        screen.Children.Add(new Rectangle { Width = 50, Height = 3.5, RadiusX = 2, RadiusY = 2, Fill = Brushes.White, Opacity = 0.8, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 10) });

        var body = new Border
        {
            Width = 200, Height = 404, CornerRadius = new CornerRadius(20), Padding = new Thickness(4),
            Background = Brushes.Black, BorderThickness = new Thickness(2.5),
            BorderBrush = Wallpaper(("#5B5E66", 0), ("#2C2E33", 0.5), ("#6C6F77", 1)),
            Child = screen, Effect = Shadow,
        };
        var g = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, RenderTransform = new RotateTransform(6), RenderTransformOrigin = new Point(0.5, 0.5) };
        g.Children.Add(new Rectangle { Width = 3, Height = 50, Fill = Ui.Hex("#55585F"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 80, -2, 0), RadiusX = 1, RadiusY = 1 });
        g.Children.Add(new Rectangle { Width = 3, Height = 26, Fill = Ui.Hex("#55585F"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 142, -2, 0), RadiusX = 1, RadiusY = 1 });
        g.Children.Add(body);
        return g;
    }
}
