using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

/// <summary>Treemap « simple » : quelques gros blocs étiquetés.</summary>
public class BlockTreemap : FrameworkElement
{
    public class Block { public string Label; public long Size; public Brush Fill; public Brush Fg; public object Tag; public TRect R; }
    public List<Block> Blocks { get; private set; } = new();
    public object SelectedTag;
    public event Action<object> BlockClicked;
    public event Action<object> BlockDoubleClicked;

    public void SetBlocks(List<Block> blocks) { Blocks = blocks; Layout(); InvalidateVisual(); }

    protected override void OnRenderSizeChanged(SizeChangedInfo i) { base.OnRenderSizeChanged(i); Layout(); InvalidateVisual(); }

    void Layout()
    {
        if (ActualWidth < 10 || ActualHeight < 10) return;
        Treemap.Squarify(Blocks, b => b.Size, new TRect(0, 0, ActualWidth, ActualHeight), (b, r) => b.R = r);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var b in Blocks)
        {
            if (b.R.W < 1 || b.R.H < 1) continue;
            var rect = new Rect(b.R.X + 3, b.R.Y + 3, Math.Max(0, b.R.W - 6), Math.Max(0, b.R.H - 6));
            bool sel = Equals(b.Tag, SelectedTag);
            dc.DrawRoundedRectangle(b.Fill, sel ? new Pen(Ui.Res("Accent"), 2.5) : null, rect, 10, 10);
            if (rect.Width < 60 || rect.Height < 40) continue;
            var face = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var faceBig = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var t1 = new FormattedText(b.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 13, b.Fg, dpi) { MaxTextWidth = Math.Max(1, rect.Width - 24), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            dc.DrawText(t1, new Point(rect.X + 12, rect.Y + 10));
            if (rect.Height >= 64)
            {
                var t2 = new FormattedText(Fmt.Size(b.Size), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, faceBig, rect.Height > 110 && rect.Width > 140 ? 22 : 16, b.Fg, dpi) { MaxTextWidth = Math.Max(1, rect.Width - 24), MaxLineCount = 1 };
                dc.DrawText(t2, new Point(rect.X + 12, rect.Bottom - t2.Height - 10));
            }
        }
    }

    Block Hit(Point p) => Blocks.FirstOrDefault(b => b.R.Contains(p.X, p.Y));

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var b = Hit(e.GetPosition(this)); if (b == null) return;
        SelectedTag = b.Tag; InvalidateVisual(); BlockClicked?.Invoke(b.Tag);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { var b = Hit(e.GetPosition(this)); if (b != null) BlockDoubleClicked?.Invoke(b.Tag); }
    }
    protected override void OnMouseMove(MouseEventArgs e) => Cursor = Hit(e.GetPosition(this)) != null ? Cursors.Hand : Cursors.Arrow;
}

/// <summary>Treemap détaillée façon WinDirStat : un rectangle par fichier, couleur selon le type.</summary>
public class FileTreemap : FrameworkElement
{
    struct Tile { public Rect R; public int Ext; public DirNode Dir; public FileEntry File; }
    sealed class Others { public long Size; public int Ext; }

    StorageScan _scan;
    readonly List<Tile> _tiles = new();
    readonly Dictionary<DirNode, Rect> _dirRects = new();
    Brush[] _brushes = Array.Empty<Brush>();
    Brush[] _dim = Array.Empty<Brush>();
    public Dictionary<int, Color> ExtColors = new();
    public int HighlightExt = -1;
    public DirNode SelectedDir;
    public FileEntry SelectedFile;
    public event Action<DirNode, FileEntry> TileClicked;
    public event Action<string> HoverChanged;

    public static readonly string[] Palette = { "#5B9DFF", "#FF7A6B", "#3DD6B5", "#F5C46A", "#B48CFF", "#F59E42", "#7EE8CF", "#E06AA8", "#8FB4FF", "#D9C27A", "#6F86B8", "#C3A3FF" };

    public void SetScan(StorageScan scan)
    {
        _scan = scan;
        ExtColors.Clear();
        int i = 0;
        foreach (var e in scan.Extensions.Take(Palette.Length)) ExtColors[e.Index] = (Color)ColorConverter.ConvertFromString(Palette[i++]);
        _brushes = new Brush[scan.Extensions.Count];
        _dim = new Brush[scan.Extensions.Count];
        var other = (Color)ColorConverter.ConvertFromString("#5B6475");
        for (int k = 0; k < scan.Extensions.Count; k++)
        {
            var c = ExtColors.TryGetValue(k, out var cc) ? cc : other;
            _brushes[k] = Cushion(c);
            var d = new SolidColorBrush(Color.FromArgb(255, (byte)(c.R / 5 + 14), (byte)(c.G / 5 + 16), (byte)(c.B / 5 + 20))); d.Freeze(); _dim[k] = d;
        }
        Relayout();
    }

    static Brush Cushion(Color c)
    {
        Color Light(double f) => Color.FromArgb(255, (byte)Math.Min(255, c.R * f), (byte)Math.Min(255, c.G * f), (byte)Math.Min(255, c.B * f));
        var b = new RadialGradientBrush { GradientOrigin = new Point(0.3, 0.25), Center = new Point(0.4, 0.35), RadiusX = 0.9, RadiusY = 0.9 };
        b.GradientStops.Add(new GradientStop(Light(1.18), 0));
        b.GradientStops.Add(new GradientStop(c, 0.55));
        b.GradientStops.Add(new GradientStop(Light(0.55), 1));
        b.Freeze();
        return b;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo i) { base.OnRenderSizeChanged(i); Relayout(); }

    public void Relayout()
    {
        _tiles.Clear(); _dirRects.Clear();
        if (_scan?.Root == null || ActualWidth < 20 || ActualHeight < 20) { InvalidateVisual(); return; }
        LayoutDir(_scan.Root, new TRect(0, 0, ActualWidth, ActualHeight));
        InvalidateVisual();
    }

    void LayoutDir(DirNode n, TRect r)
    {
        _dirRects[n] = new Rect(r.X, r.Y, r.W, r.H);
        if (n.Size <= 0) return;
        double area = r.W * r.H;
        if (area < 36) { _tiles.Add(new Tile { R = new Rect(r.X, r.Y, r.W, r.H), Ext = Math.Max(0, n.TopExt), Dir = n }); return; }
        double minSize = 3.0 * n.Size / area;   // en dessous de ~3 px², regroupé
        var items = new List<object>();
        long rest = 0; int restExt = -1; long restTop = 0;
        foreach (var d in n.Dirs) { if (d.Size >= minSize) items.Add(d); else if (d.Size > 0) { rest += d.Size; if (d.TopExtSize > restTop) { restTop = d.TopExtSize; restExt = d.TopExt; } } }
        foreach (var f in n.Files) { if (f.Size >= minSize) items.Add(f); else if (f.Size > 0) { rest += f.Size; if (f.Size > restTop) { restTop = f.Size; restExt = f.Ext; } } }
        if (rest > 0) items.Add(new Others { Size = rest, Ext = Math.Max(0, restExt) });
        Treemap.Squarify(items, o => o switch { DirNode d => d.Size, FileEntry f => f.Size, Others x => x.Size, _ => 0 }, r, (o, rr) =>
        {
            switch (o)
            {
                case DirNode d: LayoutDir(d, rr); break;
                case FileEntry f: _tiles.Add(new Tile { R = new Rect(rr.X, rr.Y, rr.W, rr.H), Ext = f.Ext, Dir = n, File = f }); break;
                case Others x: _tiles.Add(new Tile { R = new Rect(rr.X, rr.Y, rr.W, rr.H), Ext = x.Ext, Dir = n }); break;
            }
        });
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle((Brush)Application.Current.Resources["Bg"], null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_tiles.Count == 0) return;
        foreach (var t in _tiles)
        {
            var r = t.R;
            if (r.Width > 3 && r.Height > 3) r = new Rect(r.X + 0.5, r.Y + 0.5, r.Width - 1, r.Height - 1);
            var b = HighlightExt >= 0 && t.Ext != HighlightExt ? _dim[t.Ext] : _brushes[t.Ext];
            dc.DrawRectangle(b, null, r);
        }
        if (SelectedFile != null)
        {
            var t = _tiles.FirstOrDefault(x => x.File == SelectedFile);
            if (t.File != null) dc.DrawRectangle(null, new Pen(Brushes.White, 2), t.R);
        }
        else if (SelectedDir != null && SelectedDir != _scan.Root && _dirRects.TryGetValue(SelectedDir, out var dr))
        {
            dc.PushOpacity(0.35);
            var shade = Brushes.Black;
            dc.DrawRectangle(shade, null, new Rect(0, 0, ActualWidth, dr.Top));
            dc.DrawRectangle(shade, null, new Rect(0, dr.Bottom, ActualWidth, Math.Max(0, ActualHeight - dr.Bottom)));
            dc.DrawRectangle(shade, null, new Rect(0, dr.Top, dr.Left, dr.Height));
            dc.DrawRectangle(shade, null, new Rect(dr.Right, dr.Top, Math.Max(0, ActualWidth - dr.Right), dr.Height));
            dc.Pop();
            dc.DrawRectangle(null, new Pen(Brushes.White, 2), dr);
        }
    }

    Tile? Hit(Point p)
    {
        for (int i = 0; i < _tiles.Count; i++) if (_tiles[i].R.Contains(p)) return _tiles[i];
        return null;
    }

    Tile? _lastHover;
    protected override void OnMouseMove(MouseEventArgs e)
    {
        var t = Hit(e.GetPosition(this));
        if (t.HasValue && _lastHover.HasValue && t.Value.File == _lastHover.Value.File && t.Value.Dir == _lastHover.Value.Dir) return;
        _lastHover = t;
        if (t is Tile tt)
            HoverChanged?.Invoke(tt.File != null ? $"{tt.File.FullPath}  ·  {Fmt.Size(tt.File.Size)}" : $"{tt.Dir.FullPath}  ·  petits fichiers regroupés");
        else HoverChanged?.Invoke(null);
    }
    protected override void OnMouseLeave(MouseEventArgs e) { _lastHover = null; HoverChanged?.Invoke(null); }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (Hit(e.GetPosition(this)) is Tile t) TileClicked?.Invoke(t.Dir, t.File);
    }
}
