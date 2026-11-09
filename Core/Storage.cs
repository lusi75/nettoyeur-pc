namespace NettoyeurPC.Core;

public sealed class FileEntry
{
    public string Name;
    public long Size;
    public int Ext;          // index dans StorageScan.Extensions
    public DirNode Parent;
    public string FullPath => System.IO.Path.Combine(Parent.FullPath, Name);
}

public sealed class DirNode
{
    public string Name;
    public string FullPath;
    public DirNode Parent;
    public List<DirNode> Dirs = new();
    public List<FileEntry> Files = new();
    public long Size;          // taille totale du sous-arbre
    public long FileCount;     // fichiers dans le sous-arbre
    public long DirCount;      // dossiers dans le sous-arbre
    public DateTime Modified;
    public int TopExt = -1;    // type du plus gros fichier du sous-arbre (couleur des blocs trop petits)
    public long TopExtSize;
    public bool Denied;
}

public class ExtStat { public string Ext; public long Size; public long Count; public int Index; }

/// <summary>Analyse complète d'un disque : arborescence, tailles, types de fichiers.</summary>
public class StorageScan
{
    public DirNode Root;
    public List<ExtStat> Extensions = new();
    readonly Dictionary<string, int> _extIndex = new(StringComparer.OrdinalIgnoreCase);
    readonly object _extLock = new();
    long _files, _dirs;
    public long ScannedFiles => Interlocked.Read(ref _files);
    public TimeSpan Duration;

    static readonly EnumerationOptions Opts = new() { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, RecurseSubdirectories = false, ReturnSpecialDirectories = false };

    public static async Task<StorageScan> RunAsync(string root, IProgress<string> progress, CancellationToken ct)
    {
        var s = new StorageScan();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Run(() =>
        {
            s.Root = new DirNode { Name = root, FullPath = root };
            using var timer = new Timer(_ => progress?.Report($"Analyse de {root}… {Fmt.Num(s.ScannedFiles)} fichiers"), null, 300, 300);
            s.ScanDir(s.Root, ct, 0);
            s.Finish(s.Root);
        }, ct);
        s.Duration = sw.Elapsed;
        s.Extensions = s.Extensions.OrderByDescending(e => e.Size).ToList();
        Log.Info($"Analyse de {root} : {Fmt.Num(s._files)} fichiers, {Fmt.Num(s._dirs)} dossiers, {Fmt.Size(s.Root.Size)} en {sw.Elapsed.TotalSeconds:0.0} s");
        return s;
    }

    int ExtOf(string name)
    {
        var dot = name.LastIndexOf('.');
        var ext = dot > 0 && dot >= name.Length - 9 ? name.Substring(dot).ToLowerInvariant() : "(sans)";
        lock (_extLock)
        {
            if (!_extIndex.TryGetValue(ext, out var i)) { i = Extensions.Count; _extIndex[ext] = i; Extensions.Add(new ExtStat { Ext = ext, Index = i }); }
            return i;
        }
    }

    void ScanDir(DirNode node, CancellationToken ct, int depth)
    {
        if (ct.IsCancellationRequested) return;
        DirectoryInfo di;
        try { di = new DirectoryInfo(node.FullPath); node.Modified = di.LastWriteTime; } catch { node.Denied = true; return; }
        var subs = new List<DirNode>();
        try
        {
            foreach (var fsi in di.EnumerateFileSystemInfos("*", Opts))
            {
                if (fsi is FileInfo f)
                {
                    long len; try { len = f.Length; } catch { continue; }
                    var fe = new FileEntry { Name = f.Name, Size = len, Ext = ExtOf(f.Name), Parent = node };
                    node.Files.Add(fe);
                    Interlocked.Increment(ref _files);
                }
                else if (fsi is DirectoryInfo d)
                {
                    subs.Add(new DirNode { Name = d.Name, FullPath = d.FullName, Parent = node });
                    Interlocked.Increment(ref _dirs);
                }
            }
        }
        catch { node.Denied = true; }
        node.Dirs = subs;
        if (depth < 2 && subs.Count > 1)
            Parallel.ForEach(subs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, s => ScanDir(s, ct, depth + 1));
        else
            foreach (var s in subs) ScanDir(s, ct, depth + 1);
    }

    void Finish(DirNode n)
    {
        long size = 0, files = n.Files.Count, dirs = n.Dirs.Count;
        int topExt = -1; long topSize = 0;
        foreach (var f in n.Files)
        {
            size += f.Size;
            var es = Extensions[f.Ext]; es.Size += f.Size; es.Count++;
            if (f.Size > topSize) { topSize = f.Size; topExt = f.Ext; }
        }
        foreach (var d in n.Dirs)
        {
            Finish(d);
            size += d.Size; files += d.FileCount; dirs += d.DirCount;
            if (d.TopExtSize > topSize) { topSize = d.TopExtSize; topExt = d.TopExt; }
        }
        n.Size = size; n.FileCount = files; n.DirCount = dirs; n.TopExt = topExt; n.TopExtSize = topSize;
        n.Dirs.Sort((a, b) => b.Size.CompareTo(a.Size));
        n.Files.Sort((a, b) => b.Size.CompareTo(a.Size));
    }

    /// <summary>Les plus gros fichiers, en ignorant éventuellement les dossiers système.</summary>
    public List<FileEntry> BiggestFiles(int count, bool skipSystem)
    {
        var heap = new PriorityQueue<FileEntry, long>();
        void Walk(DirNode n)
        {
            if (skipSystem && IsSystemDir(n.FullPath)) return;
            foreach (var f in n.Files)
            {
                if (skipSystem && (f.Name.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase) || f.Name.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase))) continue;
                if (heap.Count < count) heap.Enqueue(f, f.Size);
                else if (f.Size > heap.Peek().Size) { heap.Dequeue(); heap.Enqueue(f, f.Size); }
            }
            foreach (var d in n.Dirs) Walk(d);
        }
        Walk(Root);
        var list = new List<FileEntry>();
        while (heap.Count > 0) list.Add(heap.Dequeue());
        list.Reverse();
        return list;
    }

    public static bool IsSystemDir(string p)
    {
        var rest = p.Length > 3 ? p.Substring(3) : "";
        return rest.StartsWith("Windows", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("Program Files", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("ProgramData", StringComparison.OrdinalIgnoreCase) || rest.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase) || rest.EndsWith(@"\AppData", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("$", StringComparison.Ordinal) || rest.StartsWith("System Volume Information", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("Recovery", StringComparison.OrdinalIgnoreCase) || rest.StartsWith(Quarantine.FolderName, StringComparison.OrdinalIgnoreCase);
    }

    public static DirNode Find(DirNode root, string path)
    {
        if (root == null) return null;
        if (string.Equals(root.FullPath.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return root;
        foreach (var d in root.Dirs)
            if (path.StartsWith(d.FullPath, StringComparison.OrdinalIgnoreCase) && (path.Length == d.FullPath.Length || path[d.FullPath.Length] == '\\'))
                return Find(d, path);
        return null;
    }

    /// <summary>Retire un dossier ou fichier de l'arbre après suppression/déplacement, et met à jour les tailles des parents.</summary>
    public void Remove(DirNode d)
    {
        var p = d.Parent; if (p == null) return;
        p.Dirs.Remove(d);
        for (var x = p; x != null; x = x.Parent) { x.Size -= d.Size; x.FileCount -= d.FileCount; x.DirCount -= d.DirCount + 1; }
    }
    public void Remove(FileEntry f)
    {
        var p = f.Parent; p.Files.Remove(f);
        for (var x = p; x != null; x = x.Parent) { x.Size -= f.Size; x.FileCount -= 1; }
    }
}

public struct TRect
{
    public double X, Y, W, H;
    public TRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
    public bool Contains(double px, double py) => px >= X && px < X + W && py >= Y && py < Y + H;
}

/// <summary>Algorithme « squarified treemap » (Bruls, Huizing, van Wijk) : rectangles aussi carrés que possible.</summary>
public static class Treemap
{
    public static void Squarify<T>(IList<T> items, Func<T, long> size, TRect r, Action<T, TRect> emit)
    {
        var rest = items.Where(i => size(i) > 0).OrderByDescending(size).ToList();
        double total = rest.Sum(i => (double)size(i));
        double x = r.X, y = r.Y, w = r.W, h = r.H;
        int start = 0;
        while (start < rest.Count && w > 0.01 && h > 0.01 && total > 0)
        {
            double scale = w * h / total, shortSide = Math.Min(w, h);
            int end = start + 1;
            double rowSum = size(rest[start]);
            double Worst(double sum, double mx, double mn)
            {
                double s = sum * scale, a = mx * scale, b = mn * scale;
                return Math.Max(shortSide * shortSide * a / (s * s), s * s / (shortSide * shortSide * b));
            }
            double cur = Worst(rowSum, size(rest[start]), size(rest[start]));
            while (end < rest.Count)
            {
                double ns = rowSum + size(rest[end]);
                double nw = Worst(ns, size(rest[start]), size(rest[end]));
                if (nw > cur) break;
                rowSum = ns; cur = nw; end++;
            }
            double rs = rowSum * scale;
            if (w >= h)
            {
                double cw = rs / h, cy = y;
                for (int i = start; i < end; i++) { double ch = size(rest[i]) * scale / cw; emit(rest[i], new TRect(x, cy, cw, ch)); cy += ch; }
                x += cw; w -= cw;
            }
            else
            {
                double ch = rs / w, cx = x;
                for (int i = start; i < end; i++) { double cw2 = size(rest[i]) * scale / ch; emit(rest[i], new TRect(cx, y, cw2, ch)); cx += cw2; }
                y += ch; h -= ch;
            }
            total -= rowSum; start = end;
        }
    }
}
