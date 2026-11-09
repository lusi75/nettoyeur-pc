using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

namespace NettoyeurPC.Core;

public enum StartupKind { Approved, Task, Service }

/// <summary>Un programme lancé au démarrage de Windows (clé Run, dossier Démarrage, tâche planifiée ou service).</summary>
public class StartupEntry
{
    public string Name { get; set; }
    public string Source { get; set; }          // libellé du groupe
    public StartupKind Kind { get; set; }
    public bool Enabled { get; set; }
    public string Command { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Exe { get; set; } = "";        // exécutable résolu (pour « Ouvrir l'emplacement »)
    public RegistryHive Hive { get; set; }
    public string ApprovedKey { get; set; }      // sous-clé StartupApproved (sans la ruche)
    public string Id { get; set; }               // nom de valeur / fichier / tâche / service
    public string TaskPath { get; set; }
    public bool DelayedAuto { get; set; }
    public int Impact { get; set; }              // 0 faible, 1 moyen, 2 élevé
    public string ImpactLabel => Impact switch { 2 => "Impact élevé", 1 => "Impact moyen", _ => "Impact faible" };
}

public static class StartupScan
{
    public const string SrcRun = "Programmes (registre)";
    public const string SrcFolder = "Dossier Démarrage";
    public const string SrcTask = "Tâches planifiées à l'ouverture de session";
    public const string SrcService = "Services lancés automatiquement";
    public static readonly string[] SourceOrder = { SrcRun, SrcFolder, SrcTask, SrcService };

    const string ApprovedBase = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    static string TrackFile => Path.Combine(AppPaths.Data, "demarrage_services.json");

    // ---------------------------------------------------------------- Lecture
    public static List<StartupEntry> Scan()
    {
        var res = new List<StartupEntry>();
        try { ReadRun(res); } catch (Exception ex) { Log.Error("Démarrage : clés Run", ex); }
        try { ReadFolders(res); } catch (Exception ex) { Log.Error("Démarrage : dossiers", ex); }
        try { ReadTasksAndServices(res); } catch (Exception ex) { Log.Error("Démarrage : tâches et services", ex); }
        foreach (var e in res)
        {
            if (string.IsNullOrEmpty(e.Exe)) e.Exe = ExeFromCommand(e.Command);
            if (string.IsNullOrEmpty(e.Publisher) && File.Exists(e.Exe))
                try { e.Publisher = FileVersionInfo.GetVersionInfo(e.Exe).CompanyName?.Trim() ?? ""; } catch { }
            e.Impact = EstimateImpact(e);
        }
        return res.OrderBy(e => Array.IndexOf(SourceOrder, e.Source)).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static int CountEnabled()
    {
        try { return Scan().Count(e => e.Enabled); }
        catch (Exception ex) { Log.Error("Démarrage : comptage", ex); return -1; }
    }

    static void ReadRun(List<StartupEntry> res)
    {
        var runs = new (RegistryHive Hive, string Key, string Approved, string Label)[]
        {
            (RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "utilisateur"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", "Run", "tous les utilisateurs"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "tous les utilisateurs, 32 bits"),
        };
        foreach (var r in runs)
        {
            using var root = RegistryKey.OpenBaseKey(r.Hive, RegistryView.Registry64);
            using var k = root.OpenSubKey(r.Key);
            if (k == null) continue;
            foreach (var n in k.GetValueNames())
            {
                if (string.IsNullOrEmpty(n)) continue;
                var ak = ApprovedBase + r.Approved;
                res.Add(new StartupEntry
                {
                    Name = n, Source = SrcRun, Kind = StartupKind.Approved, Command = k.GetValue(n)?.ToString() ?? "",
                    Detail = "Registre (" + r.Label + ")", Hive = r.Hive, ApprovedKey = ak, Id = n, Enabled = ApprovedState(r.Hive, ak, n),
                });
            }
        }
    }

    static void ReadFolders(List<StartupEntry> res)
    {
        var folders = new (string Path, RegistryHive Hive, string Label)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "utilisateur"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "tous les utilisateurs"),
        };
        foreach (var f in folders)
        {
            if (string.IsNullOrEmpty(f.Path) || !Directory.Exists(f.Path)) continue;
            foreach (var file in Directory.EnumerateFiles(f.Path))
            {
                var fn = Path.GetFileName(file);
                if (fn.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var ak = ApprovedBase + "StartupFolder";
                var (target, args) = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file) : (file, "");
                res.Add(new StartupEntry
                {
                    Name = Path.GetFileNameWithoutExtension(file), Source = SrcFolder, Kind = StartupKind.Approved,
                    Command = string.IsNullOrEmpty(target) ? file : (target + " " + args).Trim(), Exe = File.Exists(target) ? target : file,
                    Detail = "Dossier Démarrage (" + f.Label + ")", Hive = f.Hive, ApprovedKey = ak, Id = fn, Enabled = ApprovedState(f.Hive, ak, fn),
                });
            }
        }
    }

    static bool ApprovedState(RegistryHive hive, string key, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var k = root.OpenSubKey(key);
            if (k?.GetValue(name) is byte[] b && b.Length > 0) return b[0] % 2 == 0;
        }
        catch { }
        return true;
    }

    static void ReadTasksAndServices(List<StartupEntry> res)
    {
        var tracked = TrackedServices();
        const string ps = @"
$r = New-Object System.Collections.ArrayList
try {
 Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object {
   $_.TaskPath -notlike '\Microsoft\*' -and ($_.Triggers | Where-Object { $_.CimClass.CimClassName -in 'MSFT_TaskLogonTrigger','MSFT_TaskBootTrigger' })
 } | ForEach-Object {
   [void]$r.Add([pscustomobject]@{ K='T'; N=[string]$_.TaskName; P=[string]$_.TaskPath; E=($_.State -ne 'Disabled'); A=[string]$_.Author;
     C=((@($_.Actions) | ForEach-Object { ('' + $_.Execute + ' ' + $_.Arguments).Trim() }) -join ' ; ') })
 }
} catch {}
try {
 Get-CimInstance Win32_Service -ErrorAction SilentlyContinue | Where-Object {
   $_.PathName -and ($_.StartMode -eq 'Auto' -or $_.StartMode -eq 'Manual') -and $_.PathName -notmatch '\\Windows\\' -and $_.PathName -notmatch 'Windows Defender|Microsoft'
 } | ForEach-Object {
   [void]$r.Add([pscustomobject]@{ K='S'; N=[string]$_.Name; D=[string]$_.DisplayName; M=[string]$_.StartMode; Dl=[bool]$_.DelayedAutoStart; St=[string]$_.State; C=[string]$_.PathName })
 }
} catch {}
ConvertTo-Json -InputObject @($r) -Depth 3 -Compress";
        var json = Shell.PowerShell(ps, 90000)?.Trim();
        if (string.IsNullOrEmpty(json)) return;
        var start = json.IndexOf('[');
        if (start < 0) return;
        using var doc = JsonDocument.Parse(json[start..]);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string S(string p) => el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool B(string p) => el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;
            if (S("K") == "T")
            {
                res.Add(new StartupEntry
                {
                    Name = S("N"), Source = SrcTask, Kind = StartupKind.Task, Enabled = B("E"), Command = Environment.ExpandEnvironmentVariables(S("C").Trim('"', ' ')),
                    Publisher = S("A"), Detail = "Tâche planifiée " + S("P"), Id = S("N"), TaskPath = S("P"),
                });
            }
            else
            {
                var mode = S("M"); var name = S("N");
                bool auto = mode == "Auto";
                // Les services en « manuel » ne sont montrés que si c'est l'appli qui les y a mis (sinon la liste serait énorme).
                if (!auto && !tracked.Contains(name)) continue;
                res.Add(new StartupEntry
                {
                    Name = string.IsNullOrEmpty(S("D")) ? name : S("D"), Source = SrcService, Kind = StartupKind.Service, Enabled = auto,
                    Command = S("C"), Detail = "Service " + name + " · " + (S("St") == "Running" ? "en cours" : "arrêté") + (auto ? "" : " · manuel"),
                    Id = name, DelayedAuto = B("Dl"),
                });
            }
        }
    }

    // ---------------------------------------------------------------- Modification
    /// <summary>Active ou désactive une entrée (réversible, rien n'est supprimé) et l'inscrit dans l'historique.</summary>
    public static bool SetEnabled(StartupEntry e, bool enable)
    {
        string undo;
        try
        {
            switch (e.Kind)
            {
                case StartupKind.Approved:
                {
                    using var root = RegistryKey.OpenBaseKey(e.Hive, RegistryView.Registry64);
                    using var k = root.CreateSubKey(e.ApprovedKey, true);
                    var b = new byte[12];
                    b[0] = (byte)(enable ? 2 : 3);
                    if (!enable) BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(b, 4);
                    k.SetValue(e.Id, b, RegistryValueKind.Binary);
                    var hive = e.Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
                    undo = $"reg.exe|add \"{hive}\\{e.ApprovedKey}\" /v \"{e.Id}\" /t REG_BINARY /d {(enable ? "03" : "02")}0000000000000000000000 /f";
                    break;
                }
                case StartupKind.Task:
                {
                    var tn = (e.TaskPath ?? "\\") + e.Id;
                    var r = Shell.Run("schtasks.exe", $"/Change /TN \"{tn}\" {(enable ? "/ENABLE" : "/DISABLE")}", 60000);
                    if (r.Code != 0) { Log.Warn($"Démarrage : schtasks {tn} → {r.Code} {r.Output}"); return false; }
                    undo = $"schtasks.exe|/Change /TN \"{tn}\" {(enable ? "/DISABLE" : "/ENABLE")}";
                    break;
                }
                default:
                {
                    var autoMode = e.DelayedAuto ? "delayed-auto" : "auto";
                    var r = Shell.Run("sc.exe", $"config \"{e.Id}\" start= {(enable ? autoMode : "demand")}", 60000);
                    if (r.Code != 0) { Log.Warn($"Démarrage : sc config {e.Id} → {r.Code} {r.Output}"); return false; }
                    undo = $"sc.exe|config \"{e.Id}\" start= {(enable ? "demand" : autoMode)}";
                    var t = TrackedServices();
                    t.Add(e.Id);   // on garde la trace pour pouvoir la réafficher même une fois en « manuel »
                    SaveTracked(t);
                    break;
                }
            }
        }
        catch (Exception ex) { Log.Error($"Démarrage : {(enable ? "activation" : "désactivation")} de {e.Name}", ex); return false; }

        e.Enabled = enable;
        History.Add(new HistoryEvent
        {
            Title = (enable ? "Réactivé au démarrage : " : "Retiré du démarrage : ") + e.Name,
            Sub = e.Kind == StartupKind.Service ? (enable ? "Service remis en automatique" : "Service passé en manuel") : e.Detail,
            UndoCommands = { undo },
        });
        return true;
    }

    static HashSet<string> TrackedServices()
    {
        try { if (File.Exists(TrackFile)) return new HashSet<string>(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(TrackFile)) ?? new(), StringComparer.OrdinalIgnoreCase); }
        catch { }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
    static void SaveTracked(HashSet<string> s)
    {
        try { File.WriteAllText(TrackFile, JsonSerializer.Serialize(s.ToList())); } catch (Exception ex) { Log.Error("Démarrage : suivi des services", ex); }
    }

    // ---------------------------------------------------------------- Outils
    public static string ExeFromCommand(string cmd)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return "";
        cmd = Environment.ExpandEnvironmentVariables(cmd.Trim());
        string p;
        if (cmd.StartsWith('"')) { var end = cmd.IndexOf('"', 1); p = end > 0 ? cmd[1..end] : cmd.Trim('"'); }
        else
        {
            var i = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            p = i > 0 ? cmd[..(i + 4)] : cmd.Split(' ')[0];
        }
        if (p.StartsWith(@"\??\")) p = p[4..];
        if (!Path.IsPathRooted(p))
        {
            var sys = Path.Combine(Environment.SystemDirectory, p);
            if (File.Exists(sys)) return sys;
            if (File.Exists(sys + ".exe")) return sys + ".exe";
        }
        return p;
    }

    static int EstimateImpact(StartupEntry e)
    {
        long size = 0;
        try { if (File.Exists(e.Exe)) size = new FileInfo(e.Exe).Length; } catch { }
        var c = (e.Command + " " + e.Name).ToLowerInvariant();
        string[] heavy = { "steam", "discord", "epic", "battle.net", "teams", "onedrive", "dropbox", "adobe", "creative cloud", "spotify", "icue", "armoury", "razer", "synapse", "ubisoft", "ea desktop", "origin", "msedge", "chrome", "opera" };
        if (heavy.Any(c.Contains) || size > 60L * 1024 * 1024) return 2;
        if (e.Kind == StartupKind.Service || size > 8L * 1024 * 1024) return 1;
        return 0;
    }

    /// <summary>Lit la cible d'un raccourci .lnk via le shell COM (sans dépendance).</summary>
    static (string Target, string Args) ResolveShortcut(string lnk)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return ("", "");
            dynamic sh = Activator.CreateInstance(t);
            dynamic sc = sh.CreateShortcut(lnk);
            string target = sc.TargetPath;
            string args = sc.Arguments;
            return (target ?? "", args ?? "");
        }
        catch { return ("", ""); }
    }
}
