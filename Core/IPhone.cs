using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace NettoyeurPC.Core;

public class IApp
{
    public string Id, Name, Version;
    public long App, Data;
}

public class IDeviceInfo
{
    public string Udid, Name, Model, Ios;
    public long Total, Free, Used;
    public bool Encrypted;
    public string Error;
}

/// <summary>Dialogue avec l'iPhone en USB grâce à libimobiledevice (outils open source embarqués dans l'appli).
/// S'appuie sur le service « Apple Mobile Device » installé avec iTunes.</summary>
public static class IPhone
{
    const string ToolsVersion = "1";
    public static string ToolsDir => Path.Combine(AppPaths.Data, "outils-iphone");
    public static string BackupRootName = "Sauvegardes iPhone";

    /// <summary>Extrait les outils embarqués la première fois.</summary>
    public static string EnsureTools()
    {
        var dir = ToolsDir;
        var stamp = Path.Combine(dir, "version.txt");
        if (File.Exists(stamp) && File.ReadAllText(stamp).Trim() == ToolsVersion && File.Exists(Path.Combine(dir, "ideviceinfo.exe"))) return null;
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("NettoyeurPC.outils-iphone.zip");
            if (s == null) return "Les outils iPhone ne sont pas inclus dans cette version de l'appli.";
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(dir);
            using var z = new ZipArchive(s);
            z.ExtractToDirectory(dir, true);
            File.WriteAllText(stamp, ToolsVersion);
            return null;
        }
        catch (Exception ex) { Log.Error("Extraction des outils iPhone", ex); return "Impossible de préparer les outils iPhone : " + ex.Message; }
    }

    public static (int Code, string Output) Tool(string exe, string args, int timeoutMs = 60000, Action<string> onLine = null, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(Path.Combine(ToolsDir, exe + ".exe"), args)
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
                Line(ct.IsCancellationRequested ? "Annulé." : "Délai dépassé.");
                return (-1, sb.ToString());
            }
        }
        p.WaitForExit();
        return (p.ExitCode, sb.ToString());
    }

    static Dictionary<string, string> Kv(string output)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in output.Split('\n'))
        {
            var i = l.IndexOf(": ", StringComparison.Ordinal);
            if (i > 0) d[l[..i].Trim()] = l[(i + 2)..].Trim();
        }
        return d;
    }

    static string Friendly(string output)
    {
        var o = output ?? "";
        if (o.Contains("No device found", StringComparison.OrdinalIgnoreCase)) return "Aucun iPhone détecté. Branche-le en USB et déverrouille-le.";
        if (Regex.IsMatch(o, "not paired|PairingDialogResponsePending|Please accept the trust dialog|InvalidHostID|PASSWORD_PROTECTED", RegexOptions.IgnoreCase))
            return "Déverrouille l'iPhone et appuie sur « Se fier » quand il le demande, puis réessaie.";
        if (o.Contains("usbmuxd", StringComparison.OrdinalIgnoreCase) || o.Contains("Could not connect", StringComparison.OrdinalIgnoreCase))
            return "Le service « Apple Mobile Device » ne répond pas. Il est installé avec iTunes : vérifie qu'iTunes est installé (version du site d'Apple).";
        return null;
    }

    public static List<string> Devices()
    {
        var r = Tool("idevice_id", "-l", 15000);
        return r.Output.Split('\n').Select(l => l.Trim()).Where(l => Regex.IsMatch(l, "^[0-9A-Fa-f-]{20,}$")).Distinct().ToList();
    }

    public static IDeviceInfo Info(string udid)
    {
        var info = new IDeviceInfo { Udid = udid };
        var r = Tool("ideviceinfo", $"-u {udid}", 30000);
        if (r.Code != 0)
        {
            // Première connexion : on demande l'appairage (fait apparaître « Se fier » sur l'iPhone)
            Tool("idevicepair", $"-u {udid} pair", 30000);
            r = Tool("ideviceinfo", $"-u {udid}", 30000);
            if (r.Code != 0) { info.Error = Friendly(r.Output) ?? r.Output.Trim(); return info; }
        }
        var d = Kv(r.Output);
        info.Name = d.GetValueOrDefault("DeviceName", "iPhone");
        info.Model = d.GetValueOrDefault("ProductType", "");
        info.Ios = d.GetValueOrDefault("ProductVersion", "");
        var du = Kv(Tool("ideviceinfo", $"-u {udid} -q com.apple.disk_usage", 30000).Output);
        long L(string k) => du.TryGetValue(k, out var v) && long.TryParse(v, out var n) ? n : 0;
        info.Total = L("TotalDiskCapacity");
        var dataCap = L("TotalDataCapacity");
        info.Free = L("TotalDataAvailable");
        if (info.Free == 0) info.Free = L("AmountDataAvailable");
        // Comme l'iPhone : « utilisé » = capacité totale − espace libre (iOS lui-même compris)
        info.Used = Math.Max(0, info.Total - info.Free);
        var bk = Kv(Tool("ideviceinfo", $"-u {udid} -q com.apple.mobile.backup", 30000).Output);
        info.Encrypted = bk.TryGetValue("WillEncrypt", out var we) && we.Equals("true", StringComparison.OrdinalIgnoreCase);
        return info;
    }

    // ------------------------------------------------------------------ Applis
    public static (List<IApp> Apps, string Error) Apps(string udid)
    {
        string xml = null;
        try { xml = IPhoneNative.BrowseAppsXml(udid); }
        catch (Exception ex) { Log.Warn("Liste des applis (accès direct) : " + ex.Message); }
        if (xml == null)
        {
            // Secours : l'outil en ligne de commande (sans les tailles)
            var r = Tool("ideviceinstaller", $"-u {udid} -l -o list_user -o xml", 3 * 60000);
            var start = r.Output.IndexOf("<?xml", StringComparison.Ordinal);
            if (start < 0) return (new(), Friendly(r.Output) ?? "Impossible de lister les applis : " + r.Output.Trim());
            xml = r.Output[start..];
        }
        var res = new List<IApp>();
        try
        {
            var doc = System.Xml.Linq.XDocument.Parse(xml);
            foreach (var dict in doc.Root!.Element("array")!.Elements("dict"))
            {
                var kv = new Dictionary<string, string>();
                var els = dict.Elements().ToList();
                for (int i = 0; i + 1 < els.Count; i++)
                    if (els[i].Name == "key") { kv[els[i].Value] = els[i + 1].Value; i++; }
                long N(string k) => kv.TryGetValue(k, out var v) && long.TryParse(v, out var n) ? n : 0;
                res.Add(new IApp
                {
                    Id = kv.GetValueOrDefault("CFBundleIdentifier", "?"),
                    Name = kv.GetValueOrDefault("CFBundleDisplayName") ?? kv.GetValueOrDefault("CFBundleName") ?? kv.GetValueOrDefault("CFBundleIdentifier", "?"),
                    Version = kv.GetValueOrDefault("CFBundleShortVersionString", ""),
                    App = N("StaticDiskUsage"),
                    Data = N("DynamicDiskUsage"),
                });
            }
        }
        catch (Exception ex) { Log.Error("Liste des applis iPhone", ex); return (new(), "Réponse de l'iPhone illisible : " + ex.Message); }
        Log.Info($"iPhone : {res.Count} applis, {Fmt.Size(res.Sum(a => a.App))} + {Fmt.Size(res.Sum(a => a.Data))} de données");
        if (res.Count > 0 && res.All(a => a.App == 0 && a.Data == 0))
            return (res, "L'iPhone n'a pas communiqué la taille des applis. La liste reste utilisable, et le détail des tailles est dans Réglages → Général → Stockage iPhone.");
        return (res, null);
    }

    public static string Uninstall(string udid, IApp a)
    {
        var r = Tool("ideviceinstaller", $"-u {udid} -U {a.Id}", 3 * 60000);
        var ok = r.Code == 0 && !r.Output.Contains("ERROR", StringComparison.OrdinalIgnoreCase);
        Log.Info($"iPhone : désinstallation de {a.Name} ({a.Id}) : {(ok ? "OK" : r.Output.Trim())}");
        if (ok) History.Add(new HistoryEvent { Title = $"iPhone : {a.Name} désinstallé", Sub = $"{Fmt.Size(a.App + a.Data)} libérés · à réinstaller depuis l'App Store" , Bytes = a.App + a.Data });
        return ok ? null : (Friendly(r.Output) ?? r.Output.Trim());
    }

    // ------------------------------------------------------------------ Rapports de plantage / diagnostic
    public static string ReportsArchive => Path.Combine(AppPaths.Data, "iPhone", "Rapports");

    /// <summary>Copie les rapports sans les effacer, pour mesurer ce qu'il y a.</summary>
    public static (int Count, long Size, List<(string Name, int Count, long Size)> Top, string Error) ScanReports(string udid, Action<string> status, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "npc_rapports_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            int n = 0;
            var r = Tool("idevicecrashreport", $"-u {udid} -k -e \"{tmp}\"", 15 * 60000, l => { if (l.StartsWith("Copy:", StringComparison.OrdinalIgnoreCase) || l.Contains("copy", StringComparison.OrdinalIgnoreCase)) { n++; if (n % 20 == 0) status?.Invoke($"Lecture des rapports… {n}"); } }, ct);
            var files = new DirectoryInfo(tmp).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            if (r.Code != 0 && files.Count == 0) return (0, 0, new(), Friendly(r.Output) ?? r.Output.Trim());
            var top = files.GroupBy(f => Regex.Replace(Path.GetFileNameWithoutExtension(f.Name), @"[-_.]?\d{4}-\d{2}-\d{2}.*$", ""))
                .Select(g => (g.Key, g.Count(), g.Sum(f => f.Length))).OrderByDescending(x => x.Item3).Take(8).ToList();
            return (files.Count, files.Sum(f => f.Length), top, null);
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>Récupère les rapports sur le PC puis les efface de l'iPhone.</summary>
    public static (int Count, long Size, string Folder, string Error) CleanReports(string udid, Action<string> status, CancellationToken ct)
    {
        var dst = Path.Combine(ReportsArchive, DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
        Directory.CreateDirectory(dst);
        int n = 0;
        var r = Tool("idevicecrashreport", $"-u {udid} -e \"{dst}\"", 20 * 60000, l => { n++; if (n % 20 == 0) status?.Invoke($"Récupération et effacement… {n}"); }, ct);
        var files = new DirectoryInfo(dst).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
        if (r.Code != 0 && files.Count == 0) return (0, 0, dst, Friendly(r.Output) ?? r.Output.Trim());
        var size = files.Sum(f => f.Length);
        Log.Info($"iPhone : {files.Count} rapports de plantage récupérés et effacés ({Fmt.Size(size)}) → {dst}");
        History.Add(new HistoryEvent { Title = "iPhone : rapports de plantage effacés", Sub = $"{files.Count} fichiers · copie gardée dans {dst}", Bytes = size });
        return (files.Count, size, dst, null);
    }

    // ------------------------------------------------------------------ Sauvegarde complète
    public static string BackupRoot(string driveRoot) => Path.Combine(driveRoot, BackupRootName);

    public static string MobileSyncBackup => Path.Combine(AppPaths.Roaming, "Apple Computer", "MobileSync", "Backup");

    public static List<(string Udid, string Folder, DateTime Date, long Size, string Name)> ExistingBackups()
    {
        var res = new List<(string, string, DateTime, long, string)>();
        foreach (var d in DriveInfo.GetDrives().Where(d => { try { return d.IsReady && d.DriveType == DriveType.Fixed; } catch { return false; } }))
        {
            var root = BackupRoot(d.RootDirectory.FullName);
            if (!Directory.Exists(root)) continue;
            foreach (var b in Directory.GetDirectories(root))
            {
                var status = Path.Combine(b, "Status.plist");
                var manifest = Path.Combine(b, "Manifest.db");
                if (!File.Exists(manifest)) continue;
                string name = "";
                try { var m = Regex.Match(File.ReadAllText(Path.Combine(b, "Info.plist")), @"<key>Device Name</key>\s*<string>([^<]*)</string>"); if (m.Success) name = m.Groups[1].Value; } catch { }
                res.Add((Path.GetFileName(b), b, File.GetLastWriteTime(File.Exists(status) ? status : manifest), Quarantine.DirSize(b), name));
            }
        }
        return res.OrderByDescending(x => x.Item3).ToList();
    }

    public static string EnableEncryption(string udid, string password)
    {
        var r = Tool("idevicebackup2", $"-u {udid} encryption on \"{password.Replace("\"", "")}\"", 5 * 60000);
        return r.Code == 0 ? null : (Friendly(r.Output) ?? "Le chiffrement n'a pas pu être activé : " + r.Output.Trim());
    }

    static readonly Regex Pct = new(@"(\d{1,3})%", RegexOptions.Compiled);

    public static (bool Ok, string Message) Backup(string udid, string root, Action<string, double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(root);
        Log.Info($"Sauvegarde iPhone {udid} → {root}");
        string last = "";
        var r = Tool("idevicebackup2", $"-u {udid} backup --full \"{root}\"", 6 * 3600 * 1000, l =>
        {
            var t = l.Trim();
            if (t.Length == 0) return;
            var m = Pct.Match(t);
            double p = m.Success ? Math.Min(100, int.Parse(m.Groups[1].Value)) / 100.0 : -1;
            if (t.Contains("Receiving files", StringComparison.OrdinalIgnoreCase) || m.Success || t.StartsWith("Backup", StringComparison.OrdinalIgnoreCase))
            {
                last = t;
                progress?.Invoke(t.Length > 90 ? t[..90] : t, p);
            }
        }, ct);
        var dir = Path.Combine(root, udid);
        var ok = r.Code == 0 && File.Exists(Path.Combine(dir, "Manifest.db"));
        if (!ok)
        {
            var f = Friendly(r.Output);
            var msg = f ?? (r.Output.Contains("Find My", StringComparison.OrdinalIgnoreCase) ? "Désactive « Localiser mon iPhone » puis recommence." : "La sauvegarde a échoué. Détail dans le journal.");
            Log.Warn("Sauvegarde iPhone : " + r.Output.Trim().Split('\n').TakeLast(15).Aggregate("", (a, b) => a + "\n   " + b));
            return (false, msg);
        }
        var size = Quarantine.DirSize(dir);
        Log.Info($"Sauvegarde iPhone terminée : {Fmt.Size(size)}");
        History.Add(new HistoryEvent { Title = "Sauvegarde complète de l'iPhone", Sub = $"{Fmt.Size(size)} dans {dir}" });
        return (true, $"Sauvegarde terminée : {Fmt.Size(size)} dans {dir}.");
    }

    /// <summary>Fait pointer le dossier de sauvegardes d'iTunes vers le disque choisi, pour qu'iTunes propose la restauration.</summary>
    public static string LinkItunesBackups(string root)
    {
        try
        {
            var ms = MobileSyncBackup;
            var di = new DirectoryInfo(ms);
            if (di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (string.Equals(di.LinkTarget?.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return null;
                Directory.Delete(ms); // ancien lien uniquement
            }
            else if (di.Exists)
            {
                // Sauvegardes iTunes existantes : on les déplace vers le nouveau dossier pour ne rien perdre
                foreach (var sub in Directory.GetDirectories(ms))
                {
                    var dst = Path.Combine(root, Path.GetFileName(sub));
                    if (!Directory.Exists(dst)) Quarantine.MoveAny(sub, dst);
                }
                if (!Directory.EnumerateFileSystemEntries(ms).Any()) Directory.Delete(ms);
                else return "Le dossier de sauvegardes d'iTunes n'est pas vide : lien non créé.";
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ms)!);
            var r = Shell.Run("cmd.exe", $"/c mklink /J \"{ms}\" \"{root}\"");
            if (r.Code != 0) return "Lien vers iTunes non créé : " + r.Output;
            Log.Info($"Sauvegardes iTunes redirigées : {ms} → {root}");
            return null;
        }
        catch (Exception ex) { Log.Error("Lien des sauvegardes iTunes", ex); return ex.Message; }
    }
}
