using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NettoyeurPC.Core;

public class ADevice
{
    public string Serial, State, Model, Brand, Android;
    public long Total, Used, Free;
}

public class AApp { public string Id, Name; public long App, Data, Cache; public bool User; }
public class AFile { public string Path; public long Size; public DateTime Date; }
public class AFolder { public string Key, Label, Path, Hint; public long Size; public bool SafeToEmpty; }
public class AContactGroup { public List<(string Name, string Phone)> Items = new(); }

/// <summary>Dialogue avec un téléphone Android par ADB (outil officiel de Google, embarqué dans l'appli).
/// Il faut activer le « débogage USB » dans les options pour les développeurs du téléphone.</summary>
public static class Android
{
    const string ToolsVersion = "1";
    public static string ToolsDir => Path.Combine(AppPaths.Data, "outils-android");
    static string AdbExe => Path.Combine(ToolsDir, "adb.exe");

    public static string EnsureTools()
    {
        var dir = ToolsDir;
        var stamp = Path.Combine(dir, "version.txt");
        if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == ToolsVersion && File.Exists(AdbExe)) return null;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NettoyeurPC.outils-android.zip");
            if (s == null) return "Les outils Android ne sont pas inclus dans cette version de l'appli.";
            // adb peut tourner encore (serveur) : on l'arrête avant de remplacer
            if (File.Exists(AdbExe)) try { Run("kill-server", 5000); } catch { }
            Directory.CreateDirectory(dir);
            using var z = new ZipArchive(s);
            z.ExtractToDirectory(dir, true);
            File.WriteAllText(stamp, ToolsVersion);
            return null;
        }
        catch (Exception ex) { Log.Error("Extraction des outils Android", ex); return "Impossible de préparer les outils Android : " + ex.Message; }
    }

    public static (int Code, string Output) Run(string args, int timeoutMs = 60000, Action<string> onLine = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(AdbExe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = ToolsDir,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        var sb = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        void Line(string l) { if (l == null) return; lock (sb) sb.AppendLine(l); onLine?.Invoke(l); }
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        var sw = Stopwatch.StartNew();
        while (!p.WaitForExit(200))
        {
            if (ct.IsCancellationRequested || sw.ElapsedMilliseconds > timeoutMs)
            {
                try { p.Kill(true); } catch { }
                return (-1, sb.ToString());
            }
        }
        p.WaitForExit();
        return (p.ExitCode, sb.ToString());
    }

    /// <summary>Commande shell sur le téléphone. N'utiliser que des apostrophes dans la commande.</summary>
    public static string Sh(string serial, string cmd, int timeoutMs = 120000, CancellationToken ct = default)
        => Run($"-s {serial} shell \"{cmd.Replace("\"", "'")}\"", timeoutMs, null, ct).Output;

    static string Q(string path) => "'" + path.Replace("'", "'\\''") + "'";

    public static List<ADevice> Devices()
    {
        var res = new List<ADevice>();
        var o = Run("devices -l", 20000).Output;
        foreach (var l in o.Split('\n').Skip(1))
        {
            var m = Regex.Match(l.Trim(), @"^(\S+)\s+(device|unauthorized|offline|no permissions)\b(.*)$");
            if (!m.Success) continue;
            var d = new ADevice { Serial = m.Groups[1].Value, State = m.Groups[2].Value };
            var mm = Regex.Match(m.Groups[3].Value, @"model:(\S+)");
            if (mm.Success) d.Model = mm.Groups[1].Value.Replace('_', ' ');
            res.Add(d);
        }
        return res;
    }

    public static void Fill(ADevice d)
    {
        var props = Sh(d.Serial, "getprop ro.product.brand; getprop ro.product.model; getprop ro.build.version.release").Split('\n').Select(x => x.Trim()).ToList();
        if (props.Count >= 3) { d.Brand = props[0]; d.Model = props[1]; d.Android = props[2]; }
        var df = Sh(d.Serial, "df -k /data").Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0 && char.IsLetterOrDigit(x[0]) && !x.StartsWith("Filesystem"));
        if (df != null)
        {
            var f = Regex.Split(df, @"\s+");
            if (f.Length >= 4 && long.TryParse(f[1], out var t) && long.TryParse(f[2], out var u) && long.TryParse(f[3], out var a))
            { d.Total = t * 1024; d.Used = u * 1024; d.Free = a * 1024; }
        }
    }

    public static long FreeBytes(string serial)
    {
        var df = Sh(serial, "df -k /data").Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.Length > 0 && !x.StartsWith("Filesystem"));
        var f = df == null ? Array.Empty<string>() : Regex.Split(df, @"\s+");
        return f.Length >= 4 && long.TryParse(f[3], out var a) ? a * 1024 : 0;
    }

    // ------------------------------------------------------------------ Applis et caches
    public static (List<AApp> Apps, string Note) Apps(string serial)
    {
        var user = new HashSet<string>(Sh(serial, "pm list packages -3").Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("package:")).Select(l => l[8..]));
        var ds = Sh(serial, "dumpsys diskstats", 180000);
        List<string> Arr(string label)
        {
            var m = Regex.Match(ds, Regex.Escape(label) + @":\s*(\[.*?\])", RegexOptions.Singleline);
            if (!m.Success) return new();
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value);
                return doc.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()).ToList();
            }
            catch { return new(); }
        }
        var names = Arr("Package Names"); var app = Arr("App Sizes"); var data = Arr("App Data Sizes"); var cache = Arr("Cache Sizes");
        var res = new List<AApp>();
        for (int i = 0; i < names.Count; i++)
        {
            long V(List<string> l) => i < l.Count && long.TryParse(l[i], out var v) ? v : 0;
            res.Add(new AApp { Id = names[i], Name = Pretty(names[i]), App = V(app), Data = V(data), Cache = V(cache), User = user.Contains(names[i]) });
        }
        if (res.Count == 0) // diskstats indisponible : au moins la liste
            res = user.Select(u => new AApp { Id = u, Name = Pretty(u), User = true }).ToList();
        var note = res.All(a => a.App == 0 && a.Data == 0) ? "Ce téléphone ne communique pas la taille des applis : le détail est dans Paramètres → Stockage." :
                   "Tailles calculées par Android lors de sa dernière mesure (en général une fois par jour).";
        return (res, note);
    }

    static string Pretty(string id)
    {
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["com.whatsapp"] = "WhatsApp", ["com.instagram.android"] = "Instagram", ["com.snapchat.android"] = "Snapchat",
            ["com.zhiliaoapp.musically"] = "TikTok", ["com.facebook.katana"] = "Facebook", ["com.facebook.orca"] = "Messenger",
            ["com.google.android.youtube"] = "YouTube", ["com.spotify.music"] = "Spotify", ["com.netflix.mediaclient"] = "Netflix",
            ["org.telegram.messenger"] = "Telegram", ["com.android.chrome"] = "Chrome", ["com.google.android.apps.photos"] = "Google Photos",
            ["com.google.android.gm"] = "Gmail", ["com.twitter.android"] = "X (Twitter)", ["com.discord"] = "Discord",
            ["com.google.android.apps.maps"] = "Google Maps", ["com.samsung.android.messaging"] = "Messages Samsung",
        };
        if (known.TryGetValue(id, out var n)) return n;
        var parts = id.Split('.').Where(p => p is not ("com" or "android" or "app" or "apps" or "org" or "net" or "mobile")).ToList();
        var last = parts.Count > 0 ? parts[^1] : id;
        return char.ToUpper(last[0]) + last[1..];
    }

    /// <summary>Demande à Android de vider les caches de toutes les applis (comme quand la place manque). Sans danger.</summary>
    public static long TrimCaches(string serial)
    {
        var before = FreeBytes(serial);
        Sh(serial, "pm trim-caches 9999G", 300000);
        Thread.Sleep(1500);
        var freed = Math.Max(0, FreeBytes(serial) - before);
        Log.Info($"Android : caches vidés, {Fmt.Size(freed)} libérés");
        History.Add(new HistoryEvent { Title = "Android : caches des applis vidés", Sub = $"{Fmt.SizeDec(freed)} libérés sur le téléphone", Bytes = freed });
        return freed;
    }

    public static string Uninstall(string serial, AApp a)
    {
        if (!a.User) return "Les applis système ne sont pas désinstallées par sécurité.";
        var o = Run($"-s {serial} uninstall {a.Id}", 120000).Output;
        var ok = o.Contains("Success", StringComparison.OrdinalIgnoreCase);
        Log.Info($"Android : désinstallation de {a.Id} : {o.Trim()}");
        if (ok) History.Add(new HistoryEvent { Title = $"Android : {a.Name} désinstallé", Sub = $"{Fmt.SizeDec(a.App + a.Data + a.Cache)} libérés · à réinstaller depuis le Play Store", Bytes = a.App + a.Data + a.Cache });
        return ok ? null : o.Trim();
    }

    // ------------------------------------------------------------------ Dossiers qui grossissent
    static readonly (string Key, string Label, string Path, string Hint, bool Safe)[] Folders =
    {
        ("dcim", "Appareil photo", "/sdcard/DCIM", "Tes photos et vidéos. Copie-les sur le PC avant de faire de la place.", false),
        ("shots", "Captures d'écran", "/sdcard/Pictures/Screenshots", "Souvent inutiles après coup.", false),
        ("shots2", "Captures d'écran (DCIM)", "/sdcard/DCIM/Screenshots", "Souvent inutiles après coup.", false),
        ("dl", "Téléchargements", "/sdcard/Download", "Fichiers téléchargés : installeurs, PDF, vidéos…", false),
        ("wa", "WhatsApp : médias reçus", "/sdcard/Android/media/com.whatsapp/WhatsApp/Media", "Photos, vidéos et notes vocales reçues. À trier dans WhatsApp → Paramètres → Stockage et données → Gérer le stockage.", false),
        ("wa_old", "WhatsApp : médias (ancien dossier)", "/sdcard/WhatsApp/Media", "Ancien emplacement des médias WhatsApp.", false),
        ("tg", "Telegram", "/sdcard/Android/media/org.telegram.messenger", "Fichiers Telegram. Telegram → Paramètres → Données et stockage → Utilisation du stockage.", false),
        ("movies", "Vidéos", "/sdcard/Movies", "", false),
        ("thumbs", "Miniatures de la galerie", "/sdcard/DCIM/.thumbnails", "Recréées automatiquement par la galerie : sans danger à vider.", true),
    };

    public static List<AFolder> FolderSizes(string serial)
    {
        var cmd = string.Join("; ", Folders.Select(f => $"du -sk {Q(f.Path)} 2>/dev/null"));
        var o = Sh(serial, cmd, 300000);
        var sizes = new Dictionary<string, long>();
        foreach (var l in o.Split('\n'))
        {
            var m = Regex.Match(l.Trim(), @"^(\d+)\s+(.+)$");
            if (m.Success) sizes[m.Groups[2].Value.Trim()] = long.Parse(m.Groups[1].Value) * 1024;
        }
        return Folders.Where(f => sizes.ContainsKey(f.Path) && sizes[f.Path] > 1_000_000)
            .Select(f => new AFolder { Key = f.Key, Label = f.Label, Path = f.Path, Hint = f.Hint, Size = sizes[f.Path], SafeToEmpty = f.Safe })
            .OrderByDescending(f => f.Size).ToList();
    }

    public static void EmptyFolder(string serial, AFolder f)
    {
        if (!f.SafeToEmpty) return;
        Sh(serial, $"rm -rf {Q(f.Path)}/*", 300000);
        History.Add(new HistoryEvent { Title = $"Android : {f.Label} vidé", Sub = $"{Fmt.SizeDec(f.Size)} libérés", Bytes = f.Size });
    }

    // ------------------------------------------------------------------ Corbeille, mises à jour téléchargées
    /// <summary>Fichiers mis à la corbeille (Android 11+ : « .trashed-… »), supprimés automatiquement au bout de 30 jours.</summary>
    public static List<AFile> Trashed(string serial) => FindFiles(serial, "/sdcard", "-name '.trashed-*'");

    /// <summary>Paquets de mise à jour déjà téléchargés et laissés sur le stockage. Les supprimer est sans danger :
    /// ils ne servent qu'à l'installation et le téléphone les retélécharge si besoin. On ne touche jamais à la partition système.</summary>
    public static List<AFile> UpdatePackages(string serial)
    {
        var res = new List<AFile>();
        foreach (var dir in new[] { "/sdcard/downloaded_rom", "/sdcard/.Ota", "/sdcard/ota", "/sdcard/fota", "/sdcard/.fota", "/sdcard/FOTA" })
            res.AddRange(FindFiles(serial, dir, "-size +20M"));
        res.AddRange(FindFiles(serial, "/sdcard/Download", "\\( -iname 'ota*.zip' -o -iname '*_ota*.zip' -o -iname '*-ota*.zip' \\) -size +100M"));
        return res;
    }

    public static List<AFile> BigFiles(string serial, long minMb = 100)
        => FindFiles(serial, "/sdcard", $"-size +{minMb}M").Where(f => !f.Path.Contains("/Android/data/") && !f.Path.Contains("/Android/obb/")).OrderByDescending(f => f.Size).Take(80).ToList();

    static List<AFile> FindFiles(string serial, string root, string predicate, int timeoutMs = 300000)
    {
        var o = Sh(serial, $"find {Q(root)} -type f {predicate} -exec stat -c '%s|%Y|%n' {{}} + 2>/dev/null", timeoutMs);
        var res = new List<AFile>();
        foreach (var l in o.Split('\n'))
        {
            var p = l.TrimEnd('\r').Split('|', 3);
            if (p.Length == 3 && long.TryParse(p[0], out var s) && long.TryParse(p[1], out var t))
                res.Add(new AFile { Size = s, Date = DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime, Path = p[2] });
        }
        return res;
    }

    public static int DeleteFiles(string serial, IEnumerable<AFile> files, string what)
    {
        var list = files.ToList();
        int n = 0;
        foreach (var chunk in list.Chunk(40))
        {
            Sh(serial, "rm -f " + string.Join(" ", chunk.Select(f => Q(f.Path))), 120000);
            n += chunk.Length;
        }
        Rescan(serial, list.Select(f => f.Path));
        var size = list.Sum(f => f.Size);
        Log.Info($"Android : {what} supprimés ({n} fichiers, {Fmt.Size(size)})");
        History.Add(new HistoryEvent { Title = $"Android : {what} supprimés", Sub = $"{n} fichier(s) · {Fmt.SizeDec(size)} libérés", Bytes = size });
        return n;
    }

    static void Rescan(string serial, IEnumerable<string> paths)
    {
        // Prévient la galerie que des fichiers ont disparu (sinon elle peut afficher des vignettes vides un moment)
        foreach (var dir in paths.Select(p => p[..Math.Max(0, p.LastIndexOf('/'))]).Distinct().Take(20))
            Sh(serial, $"am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d {Q("file://" + dir)} >/dev/null 2>&1", 20000);
    }

    // ------------------------------------------------------------------ Doublons de photos et vidéos
    public static List<List<AFile>> Duplicates(string serial, Action<string> status, CancellationToken ct)
    {
        status?.Invoke("Liste des photos et vidéos…");
        var exts = "\\( -iname '*.jpg' -o -iname '*.jpeg' -o -iname '*.png' -o -iname '*.heic' -o -iname '*.webp' -o -iname '*.gif' -o -iname '*.mp4' -o -iname '*.mov' -o -iname '*.3gp' \\)";
        var all = new List<AFile>();
        foreach (var root in new[] { "/sdcard/DCIM", "/sdcard/Pictures", "/sdcard/Download", "/sdcard/Movies", "/sdcard/Android/media/com.whatsapp/WhatsApp/Media", "/sdcard/WhatsApp/Media", "/sdcard/Telegram", "/sdcard/Android/media/org.telegram.messenger" })
        {
            if (ct.IsCancellationRequested) return new();
            all.AddRange(FindFiles(serial, root, exts + " -size +20k -not -name '.trashed-*' -not -path '*/.thumbnails/*'"));
        }
        // Même taille = candidat ; on confirme par empreinte MD5 calculée sur le téléphone
        var bySize = all.GroupBy(f => f.Size).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
        status?.Invoke($"{all.Count:#,0} fichiers · comparaison de {bySize.Count:#,0} candidats…");
        var hash = new Dictionary<string, string>();
        int done = 0;
        foreach (var chunk in bySize.Chunk(30))
        {
            if (ct.IsCancellationRequested) return new();
            var o = Sh(serial, "md5sum " + string.Join(" ", chunk.Select(f => Q(f.Path))) + " 2>/dev/null", 600000, ct);
            foreach (var l in o.Split('\n'))
            {
                var m = Regex.Match(l.TrimEnd('\r'), @"^([0-9a-f]{32})\s+(.+)$");
                if (m.Success) hash[m.Groups[2].Value] = m.Groups[1].Value;
            }
            done += chunk.Length;
            status?.Invoke($"Comparaison… {done:#,0} / {bySize.Count:#,0}");
        }
        var groups = bySize.Where(f => hash.ContainsKey(f.Path)).GroupBy(f => hash[f.Path] + f.Size).Where(g => g.Count() > 1)
            .Select(g => g.OrderBy(f => Rank(f.Path)).ThenBy(f => f.Date).ToList())   // l'original (appareil photo, le plus ancien) en premier
            .OrderByDescending(g => g.Skip(1).Sum(f => f.Size)).ToList();
        Log.Info($"Android : {groups.Count} groupes de doublons, {Fmt.Size(groups.Sum(g => g.Skip(1).Sum(f => f.Size)))} récupérables");
        return groups;
    }

    static int Rank(string p) => p.Contains("/DCIM/Camera", StringComparison.OrdinalIgnoreCase) ? 0 : p.Contains("/DCIM/") ? 1 : p.Contains("/Pictures/") ? 2 : p.Contains("WhatsApp", StringComparison.OrdinalIgnoreCase) ? 4 : 3;

    // ------------------------------------------------------------------ Copie sur le PC (avant de supprimer)
    public static string BackupRoot(string driveRoot, ADevice d) => Path.Combine(driveRoot, "Sauvegardes Android", Safe($"{d.Brand} {d.Model}".Trim()));
    static string Safe(string s) => Regex.Replace(s, @"[\\/:*?""<>|]", "_");

    /// <summary>Copie des fichiers sur le PC en gardant l'arborescence, puis vérifie la taille de chaque copie.</summary>
    public static List<AFile> PullFiles(string serial, IEnumerable<AFile> files, string destRoot, Action<string> status, CancellationToken ct)
    {
        var ok = new List<AFile>();
        var list = files.ToList(); int i = 0;
        foreach (var f in list)
        {
            if (ct.IsCancellationRequested) break;
            i++;
            var rel = f.Path.StartsWith("/sdcard/") ? f.Path[8..] : f.Path.TrimStart('/');
            var dst = Path.Combine(destRoot, rel.Replace('/', '\\'));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            status?.Invoke($"Copie sur le PC {i}/{list.Count} : {Path.GetFileName(f.Path)}");
            Run($"-s {serial} pull \"{f.Path}\" \"{dst}\"", 30 * 60000, null, ct);
            if (File.Exists(dst) && new FileInfo(dst).Length == f.Size) ok.Add(f);
            else Log.Warn($"Android : copie incomplète de {f.Path}");
        }
        return ok;
    }

    /// <summary>Copie tout un dossier (ex. DCIM) sur le PC.</summary>
    public static (bool Ok, long Size, string Folder) PullFolder(string serial, string remote, string destRoot, Action<string> status, CancellationToken ct)
    {
        var dst = Path.Combine(destRoot, $"{remote.Trim('/').Replace('/', '_')}_{DateTime.Now:yyyy-MM-dd}");
        Directory.CreateDirectory(dst);
        var t = Task.Run(() => Run($"-s {serial} pull \"{remote}/.\" \"{dst}\"", 12 * 3600 * 1000, null, ct));
        while (!t.Wait(2000)) status?.Invoke($"Copie en cours : {Fmt.SizeDec(Quarantine.DirSize(dst))}…");
        var size = Quarantine.DirSize(dst);
        var ok = t.Result.Code == 0;
        Log.Info($"Android : copie de {remote} → {dst} : {(ok ? "OK" : t.Result.Output.Trim())} ({Fmt.Size(size)})");
        if (ok) History.Add(new HistoryEvent { Title = $"Android : {remote} copié sur le PC", Sub = $"{Fmt.SizeDec(size)} dans {dst}" });
        return (ok, size, dst);
    }

    // ------------------------------------------------------------------ Contacts en double (analyse)
    public static (List<AContactGroup> Groups, int Total, string Error) ContactDuplicates(string serial)
    {
        var o = Sh(serial, "content query --uri content://com.android.contacts/data/phones --projection contact_id:display_name:data1", 120000);
        if (o.Contains("Exception", StringComparison.OrdinalIgnoreCase) || o.Contains("Permission", StringComparison.OrdinalIgnoreCase))
            return (new(), 0, "Ce téléphone ne laisse pas l'ordinateur lire les contacts.");
        var rows = new List<(string Id, string Name, string Phone)>();
        foreach (var l in o.Split('\n'))
        {
            var m = Regex.Match(l, @"contact_id=(\d+), display_name=(.*?), data1=(.*)$");
            if (m.Success) rows.Add((m.Groups[1].Value, m.Groups[2].Value.Trim(), m.Groups[3].Value.Trim()));
        }
        var contacts = rows.GroupBy(r => r.Id).Select(g => (Id: g.Key, Name: g.First().Name, Phones: g.Select(x => x.Phone).Distinct().ToList())).ToList();
        // Union par nom normalisé ou par numéro (9 derniers chiffres)
        var parent = Enumerable.Range(0, contacts.Count).ToArray();
        int F(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var seen = new Dictionary<string, int>();
        for (int i = 0; i < contacts.Count; i++)
        {
            var keys = new List<string>();
            var n = Regex.Replace(contacts[i].Name.ToLowerInvariant().Normalize(NormalizationForm.FormD), @"[^\p{L}\p{N} ]", "").Trim();
            if (n.Length >= 3) keys.Add("n:" + n);
            foreach (var p in contacts[i].Phones) { var d = new string(p.Where(char.IsDigit).ToArray()); if (d.Length >= 8) keys.Add("p:" + d[^9..]); }
            foreach (var k in keys) { if (seen.TryGetValue(k, out var j)) parent[F(i)] = F(j); else seen[k] = i; }
        }
        var groups = Enumerable.Range(0, contacts.Count).GroupBy(F).Where(g => g.Count() > 1)
            .Select(g => new AContactGroup { Items = g.Select(i => (contacts[i].Name, string.Join(", ", contacts[i].Phones))).ToList() })
            .OrderBy(g => g.Items[0].Name).ToList();
        return (groups, contacts.Count, null);
    }
}
