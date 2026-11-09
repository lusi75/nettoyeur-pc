using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NettoyeurPC.Core;

public enum RegAction { Key, Value, FwRule, Service, Task }

/// <summary>Une entrée orpheline trouvée dans le registre.</summary>
public class RegFinding
{
    public string Cat { get; set; }
    public string Name { get; set; }
    public Risk Risk { get; set; }          // Safe = « Sûr », Check = « Modéré »
    public string Key { get; set; }         // HKLM\..., HKCU\..., HKCR\... (ou chemin de tâche)
    public string Value { get; set; }
    public RegAction Action { get; set; }
    public string Detail { get; set; }
    public string Extra { get; set; }       // clé StartupApproved liée, ou dossier de la tâche
    public bool Checked { get; set; }

    public string Location => !string.IsNullOrEmpty(Value) && (Action == RegAction.Value || Action == RegAction.FwRule) ? $"{Key}  [{Value}]" : Key;
}

/// <summary>
/// Recherche d'entrées orphelines laissées par des logiciels désinstallés (portage fidèle de la V1).
/// Prudent : on ne signale une entrée que si le fichier visé est sur un disque fixe présent, n'existe plus,
/// et que son premier dossier parent existant est lisible (sinon « accès refusé » : on ne peut pas conclure).
/// </summary>
public static class RegistryScan
{
    static readonly string W = AppPaths.Windows;
    public static string BackupDir => Path.Combine(AppPaths.Data, "Sauvegardes registre");

    public static readonly string[] CategoryOrder =
    {
        "Programmes désinstallés", "Démarrage", "Chemins d'applications", "Ouvrir avec", "DLL partagées",
        "Dossiers d'installation", "Polices", "Historique (MuiCache)", "Historique (compatibilité)",
        "Pare-feu", "Menu clic droit", "Services", "Tâches planifiées",
    };

    // ------------------------------------------------------------------ accès registre
    public static RegistryKey Open(string k, bool writable = false)
    {
        var parts = k.Split('\\', 2);
        RegistryHive? hive = parts[0].ToUpperInvariant() switch
        {
            "HKLM" => RegistryHive.LocalMachine,
            "HKCU" => RegistryHive.CurrentUser,
            "HKCR" => RegistryHive.ClassesRoot,
            _ => null,
        };
        if (hive == null) return null;
        try
        {
            var b = RegistryKey.OpenBaseKey(hive.Value, RegistryView.Registry64);
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[1])) return b;
            return b.OpenSubKey(parts[1], writable);
        }
        catch { return null; }
    }

    static object Val(RegistryKey k, string name) { try { return k.GetValue(name); } catch { return null; } }
    static string SVal(RegistryKey k, string name) => Val(k, name)?.ToString();
    static string[] SubKeys(RegistryKey k) { try { return k.GetSubKeyNames(); } catch { return Array.Empty<string>(); } }
    static string[] ValueNames(RegistryKey k) { try { return k.GetValueNames(); } catch { return Array.Empty<string>(); } }
    static RegistryKey Sub(RegistryKey k, string name) { try { return k.OpenSubKey(name); } catch { return null; } }

    // ------------------------------------------------------------------ chemins
    static bool FileOrDir(string p) { try { return File.Exists(p) || Directory.Exists(p); } catch { return false; } }

    static string ResolveExePath(string p)
    {
        p = (p ?? "").Trim().Trim('"');
        if (p.Length == 0) return null;
        try { if (Path.IsPathRooted(p)) return p; } catch { return null; }
        foreach (var d in new[] { Path.Combine(W, "System32"), W, Path.Combine(W, "SysWOW64") })
        {
            string c;
            try { c = Path.Combine(d, p); } catch { return null; }
            if (FileOrDir(c)) return c;
            if (!p.Contains('.') && File.Exists(c + ".exe")) return c + ".exe";
        }
        return null; // introuvable dans le PATH système : on ne sait pas, donc on ne signale pas
    }

    static readonly Regex ReQuoted = new(@"^""([^""]+)""\s*(.*)$", RegexOptions.Compiled);
    static readonly Regex ReExt = new(@"^(.+?\.(exe|com|bat|cmd|dll|cpl|scr|msc|vbs|js|ps1|lnk|sys|ocx))(?=$|[\s,])\s*(.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
        { "msiexec", "cmd", "powershell", "pwsh", "wscript", "cscript", "explorer", "mshta", "conhost", "svchost", "dllhost", "wsl", "bash", "start" };

    public static string CmdTarget(string cmd, int depth = 0)
    {
        if (string.IsNullOrWhiteSpace(cmd) || depth > 3) return null;
        var c = Environment.ExpandEnvironmentVariables(cmd).Trim();
        if (c.Length == 0) return null;
        string exe, rest;
        Match m;
        if ((m = ReQuoted.Match(c)).Success) { exe = m.Groups[1].Value; rest = m.Groups[2].Value; }
        else if ((m = ReExt.Match(c)).Success) { exe = m.Groups[1].Value; rest = m.Groups[3].Value; }
        else { var parts = Regex.Split(c, @"\s+", RegexOptions.None); exe = parts[0]; rest = parts.Length > 1 ? string.Join(" ", parts.Skip(1)) : ""; }
        if (exe.StartsWith(@"\??\")) exe = exe.Substring(4);
        if (exe.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) exe = W + exe.Substring(@"\SystemRoot".Length);
        if (Regex.IsMatch(exe, @"^(System32|SysWOW64)\\", RegexOptions.IgnoreCase)) exe = Path.Combine(W, exe);
        if (exe.StartsWith(@"\\")) return null;
        string leaf;
        try { leaf = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant(); } catch { return null; }
        if (leaf is "rundll32" or "regsvr32")
        {
            var inner = Regex.Replace(rest, @"^(/\w+\s+)+", "").Split(',')[0];
            return CmdTarget(inner, depth + 1);
        }
        if (Hosts.Contains(leaf)) return null;
        return ResolveExePath(exe);
    }

    static HashSet<string> _fixedRoots;
    static readonly Regex ReBadChars = new(@"[*?<>|""]", RegexOptions.Compiled);

    /// <summary>Vrai seulement si on est sûr que le chemin n'existe plus.</summary>
    public static bool IsMissing(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (ReBadChars.IsMatch(path)) return false;
        if (_fixedRoots == null)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in DriveInfo.GetDrives()) { try { if (d.DriveType == DriveType.Fixed && d.IsReady) set.Add(d.RootDirectory.FullName); } catch { } }
            _fixedRoots = set;
        }
        string r0;
        try { r0 = Path.GetPathRoot(path); } catch { return false; }
        if (string.IsNullOrEmpty(r0) || !_fixedRoots.Contains(r0)) return false; // disque absent, USB ou réseau
        if (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return false;
        if (FileOrDir(path)) return false;
        if (path.Contains(@"\System32\", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var alt in new[] { Regex.Replace(path, @"\\System32\\", @"\SysWOW64\", RegexOptions.IgnoreCase), Regex.Replace(path, @"\\System32\\", @"\Sysnative\", RegexOptions.IgnoreCase) })
                if (FileOrDir(alt)) return false;
        }
        // Accès refusé ? On remonte au premier dossier existant : s'il n'est pas lisible, on ne conclut pas.
        var anc = path;
        while (true)
        {
            try { anc = Path.GetDirectoryName(anc); } catch { return false; }
            if (string.IsNullOrEmpty(anc)) return false;
            if (Directory.Exists(anc)) break;
        }
        try { using var en = Directory.EnumerateFileSystemEntries(anc).GetEnumerator(); en.MoveNext(); }
        catch { return false; }
        return true;
    }

    public static string PackedGuid(string g)
    {
        g = g.Trim('{', '}').Replace("-", "");
        if (g.Length != 32) return null;
        static string Rev(string s) { var a = s.ToCharArray(); Array.Reverse(a); return new string(a); }
        var sb = new StringBuilder(Rev(g[..8]) + Rev(g.Substring(8, 4)) + Rev(g.Substring(12, 4)));
        for (int i = 16; i < 32; i += 2) { sb.Append(g[i + 1]); sb.Append(g[i]); }
        return sb.ToString().ToUpperInvariant();
    }

    static bool Exists(string key) { using var o = Open(key); return o != null; }

    public static bool MsiRegistered(string guid)
    {
        var p = PackedGuid(guid);
        if (p == null) return true;
        foreach (var k in new[] { $@"HKLM\SOFTWARE\Classes\Installer\Products\{p}", $@"HKCU\Software\Microsoft\Installer\Products\{p}", $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData\S-1-5-18\Products\{p}" })
            if (Exists(k)) return true;
        using var ud = Open(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData");
        if (ud != null)
            foreach (var sid in SubKeys(ud)) { using var o = Sub(ud, $@"{sid}\Products\{p}"); if (o != null) return true; }
        return false;
    }

    static string Leaf(string p) { try { return Path.GetFileName(p.TrimEnd('\\')); } catch { return p; } }

    // ------------------------------------------------------------------ analyse
    public static List<RegFinding> Find(Action<string> step, CancellationToken ct)
    {
        _fixedRoots = null;
        var res = new List<RegFinding>();
        void Add(string cat, string name, Risk risk, string key, string value, RegAction a, string detail, string extra = null)
            => res.Add(new RegFinding { Cat = cat, Name = name, Risk = risk, Key = key, Value = value, Action = a, Detail = detail, Extra = extra, Checked = risk == Risk.Safe });
        void Step(string s) { ct.ThrowIfCancellationRequested(); step?.Invoke(s); }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string last = null;
        void Timed(string label) { if (last != null) Log.Info($"   Registre — {last} : {watch.Elapsed.TotalSeconds:0.0} s (total {res.Count})"); last = label; watch.Restart(); Step(label); }

        // 1. Programmes désinstallés encore listés
        Timed("Programmes désinstallés");
        foreach (var root in new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall" })
        {
            using var rk = Open(root); if (rk == null) continue;
            foreach (var sub in SubKeys(rk))
            {
                string dn, us, il;
                using (var k = Sub(rk, sub)) { if (k == null) continue; dn = SVal(k, "DisplayName"); us = SVal(k, "UninstallString"); il = SVal(k, "InstallLocation"); }
                if (string.IsNullOrEmpty(dn) || string.IsNullOrEmpty(us)) continue;
                string guid = null;
                if (Regex.IsMatch(sub, @"^\{[0-9A-Fa-f-]{36}\}$")) guid = sub;
                else { var mm = Regex.Match(us, @"msiexec.*(\{[0-9A-Fa-f-]{36}\})", RegexOptions.IgnoreCase); if (mm.Success) guid = mm.Groups[1].Value; }
                if (us.Contains("msiexec", StringComparison.OrdinalIgnoreCase) && guid != null)
                {
                    if (!MsiRegistered(guid)) Add("Programmes désinstallés", dn, Risk.Safe, $@"{root}\{sub}", null, RegAction.Key, $"Programme MSI qui n'est plus installé, mais toujours listé ({guid}).");
                    continue;
                }
                var t = CmdTarget(us);
                if (IsMissing(t))
                {
                    if (!string.IsNullOrEmpty(il) && IsMissing(il.Trim('"')))
                        Add("Programmes désinstallés", dn, Risk.Safe, $@"{root}\{sub}", null, RegAction.Key, $"Le désinstalleur et le dossier du programme n'existent plus : {t}");
                    else if (string.IsNullOrEmpty(il))
                        Add("Programmes désinstallés", dn, Risk.Check, $@"{root}\{sub}", null, RegAction.Key, $"Le désinstalleur n'existe plus ({t}). Le programme semble supprimé, mais vérifie qu'il n'est plus sur le PC.");
                }
            }
        }

        // 2. Démarrage vers un programme supprimé
        Timed("Démarrage");
        var runMap = new (string Run, string Approved)[]
        {
            (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
            (@"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce", null),
            (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"),
            (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce", null),
            (@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32"),
            (@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce", null),
        };
        foreach (var (run, appr) in runMap)
        {
            using var rk = Open(run); if (rk == null) continue;
            foreach (var n in ValueNames(rk))
            {
                if (string.IsNullOrEmpty(n)) continue;
                var t = CmdTarget(SVal(rk, n));
                if (IsMissing(t)) Add("Démarrage", n, Risk.Safe, run, n, RegAction.Value, $"Lance un programme qui n'existe plus : {t}", appr);
            }
        }
        foreach (var (appr, run) in new[]
        {
            (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"),
            (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        })
        {
            using var ak = Open(appr); if (ak == null) continue;
            string[] names;
            using (var rk = Open(run)) names = rk != null ? ValueNames(rk) : Array.Empty<string>();
            foreach (var n in ValueNames(ak))
                if (!string.IsNullOrEmpty(n) && !names.Contains(n, StringComparer.OrdinalIgnoreCase))
                    Add("Démarrage", n, Risk.Safe, appr, n, RegAction.Value, "Réglage de démarrage d'un programme qui n'est plus au démarrage.");
        }
        foreach (var (appr, dir) in new[]
        {
            (@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", Environment.GetFolderPath(Environment.SpecialFolder.Startup)),
            (@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder", Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)),
        })
        {
            using var ak = Open(appr); if (ak == null || string.IsNullOrEmpty(dir)) continue;
            foreach (var n in ValueNames(ak))
                if (!string.IsNullOrEmpty(n) && !FileOrDir(Path.Combine(dir, n)))
                    Add("Démarrage", n, Risk.Safe, appr, n, RegAction.Value, "Réglage d'un raccourci de démarrage qui n'existe plus.");
        }

        // 3. App Paths et « Ouvrir avec »
        Timed("Chemins d'applications");
        foreach (var root in new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths", @"HKCU\Software\Microsoft\Windows\CurrentVersion\App Paths" })
        {
            using var rk = Open(root); if (rk == null) continue;
            foreach (var sub in SubKeys(rk))
            {
                string v; using (var k = Sub(rk, sub)) { if (k == null) continue; v = SVal(k, ""); }
                var t = CmdTarget(v);
                if (IsMissing(t)) Add("Chemins d'applications", sub, Risk.Safe, $@"{root}\{sub}", null, RegAction.Key, $"Pointe vers un programme supprimé : {t}");
            }
        }
        foreach (var root in new[] { @"HKCU\Software\Classes\Applications", @"HKLM\SOFTWARE\Classes\Applications" })
        {
            using var rk = Open(root); if (rk == null) continue;
            foreach (var sub in SubKeys(rk))
            {
                string v; using (var k = Sub(rk, $@"{sub}\shell\open\command")) { if (k == null) continue; v = SVal(k, ""); }
                var t = CmdTarget(v);
                if (IsMissing(t)) Add("Ouvrir avec", sub, Risk.Safe, $@"{root}\{sub}", null, RegAction.Key, $"Le menu « Ouvrir avec » propose un programme supprimé : {t}");
            }
        }

        // 4. DLL partagées, dossiers d'installation, polices
        Timed("DLL partagées, dossiers, polices");
        foreach (var root in new[] { @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs" })
        {
            using var rk = Open(root); if (rk == null) continue;
            foreach (var n in ValueNames(rk))
                if (!string.IsNullOrEmpty(n) && IsMissing(Environment.ExpandEnvironmentVariables(n)))
                    Add("DLL partagées", Leaf(n), Risk.Safe, root, n, RegAction.Value, $"Fichier partagé supprimé : {n}");
        }
        {
            const string root = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\Folders";
            using var rk = Open(root);
            if (rk != null)
                foreach (var n in ValueNames(rk))
                    if (!string.IsNullOrEmpty(n) && IsMissing(n.TrimEnd('\\')))
                        Add("Dossiers d'installation", n, Risk.Safe, root, n, RegAction.Value, $"Dossier d'installation supprimé : {n}");
        }
        foreach (var (root, dir) in new[] { (@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts", Path.Combine(W, "Fonts")), (@"HKCU\Software\Microsoft\Windows NT\CurrentVersion\Fonts", Path.Combine(AppPaths.Local, @"Microsoft\Windows\Fonts")) })
        {
            using var rk = Open(root); if (rk == null) continue;
            foreach (var n in ValueNames(rk))
            {
                var v = SVal(rk, n); if (string.IsNullOrEmpty(v)) continue;
                string full; try { full = Path.IsPathRooted(v) ? v : Path.Combine(dir, v); } catch { continue; }
                if (IsMissing(full)) Add("Polices", n, Risk.Safe, root, n, RegAction.Value, $"Police déclarée mais fichier absent : {full}");
            }
        }

        // 5. MuiCache et compatibilité
        Timed("Historiques");
        {
            const string root = @"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
            using var rk = Open(root);
            if (rk != null)
                foreach (var n in ValueNames(rk))
                {
                    var mm = Regex.Match(n ?? "", @"^(.+\.(exe|dll))\.(FriendlyAppName|ApplicationCompany)$", RegexOptions.IgnoreCase);
                    if (mm.Success && IsMissing(mm.Groups[1].Value))
                        Add("Historique (MuiCache)", Leaf(mm.Groups[1].Value), Risk.Safe, root, n, RegAction.Value, $"Nom mémorisé d'un programme supprimé : {mm.Groups[1].Value}");
                }
        }
        foreach (var root in new[] { @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store", @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers" })
        {
            using var rk = Open(root); if (rk == null) continue;
            foreach (var n in ValueNames(rk))
                if (!string.IsNullOrEmpty(n) && IsMissing(n))
                    Add("Historique (compatibilité)", Leaf(n), Risk.Safe, root, n, RegAction.Value, $"Réglage de compatibilité d'un programme supprimé : {n}");
        }

        // 6. Pare-feu
        Timed("Pare-feu");
        {
            const string fw = @"HKLM\SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";
            using var rk = Open(fw);
            if (rk != null)
                foreach (var n in ValueNames(rk))
                {
                    var d = SVal(rk, n) ?? "";
                    var mm = Regex.Match(d, @"\|App=([^|]+)\|");
                    if (!mm.Success) continue;
                    var app = Environment.ExpandEnvironmentVariables(mm.Groups[1].Value);
                    if (!app.Equals("System", StringComparison.OrdinalIgnoreCase) && IsMissing(app))
                    {
                        var nm = Regex.Match(d, @"\|Name=([^|]+)\|");
                        Add("Pare-feu", nm.Success ? nm.Groups[1].Value : n, Risk.Safe, fw, n, RegAction.FwRule, $"Règle de pare-feu pour un programme supprimé : {app}");
                    }
                }
        }

        // 7. Menu clic droit
        Timed("Menu clic droit");
        foreach (var b in new[] { "*", "AllFilesystemObjects", "Directory", @"Directory\Background", "Folder", "Drive", "DesktopBackground" })
        {
            var root = $@"HKCR\{b}\shellex\ContextMenuHandlers";
            using var rk = Open(root); if (rk == null) continue;
            foreach (var sub in SubKeys(rk))
            {
                string clsid; using (var k = Sub(rk, sub)) { if (k == null) continue; clsid = SVal(k, "") ?? ""; }
                if (Regex.IsMatch(sub, @"^\{[0-9A-Fa-f-]{36}\}$")) clsid = sub;
                if (!Regex.IsMatch(clsid, @"^\{[0-9A-Fa-f-]{36}\}$")) continue;
                string dll = null; bool exists = false;
                foreach (var ckPath in new[] { $@"HKCR\CLSID\{clsid}", $@"HKLM\SOFTWARE\WOW6432Node\Classes\CLSID\{clsid}" })
                {
                    using var ck = Open(ckPath); if (ck == null) continue;
                    exists = true;
                    using (var ip = Sub(ck, "InprocServer32")) if (ip != null) dll = SVal(ip, "");
                    if (!string.IsNullOrEmpty(dll)) break;
                }
                if (!exists) Add("Menu clic droit", sub, Risk.Check, $@"{root}\{sub}", null, RegAction.Key, $"Extension du clic droit sur « {b} » dont le composant ({clsid}) n'existe plus.");
                else if (!string.IsNullOrEmpty(dll) && IsMissing(Environment.ExpandEnvironmentVariables(dll).Trim('"')))
                    Add("Menu clic droit", sub, Risk.Check, $@"{root}\{sub}", null, RegAction.Key, $"Extension du clic droit sur « {b} » dont la DLL a été supprimée : {dll}");
            }
        }

        // 8. Services
        Timed("Services");
        {
            using var rk = Open(@"HKLM\SYSTEM\CurrentControlSet\Services");
            if (rk != null)
                foreach (var sub in SubKeys(rk))
                {
                    ct.ThrowIfCancellationRequested();
                    object type; string img, dn;
                    using (var k = Sub(rk, sub)) { if (k == null) continue; type = Val(k, "Type"); img = SVal(k, "ImagePath"); dn = SVal(k, "DisplayName"); }
                    if (string.IsNullOrEmpty(img) || type is not int ti || ti is not (16 or 32 or 272 or 288)) continue;
                    if (Regex.IsMatch(img, @"\\Windows\\|^%SystemRoot%|^\\SystemRoot|svchost", RegexOptions.IgnoreCase)) continue;
                    var t = CmdTarget(img);
                    if (IsMissing(t))
                    {
                        var nm = !string.IsNullOrEmpty(dn) && !dn.StartsWith("@") ? $"{dn} ({sub})" : sub;
                        Add("Services", nm, Risk.Check, $@"HKLM\SYSTEM\CurrentControlSet\Services\{sub}", sub, RegAction.Service, $"Service dont le programme n'existe plus : {t}");
                    }
                }
        }

        // 9. Tâches planifiées (hors Microsoft)
        Timed("Tâches planifiées");
        try
        {
            var json = Shell.PowerShell(@"$r = @(Get-ScheduledTask -ErrorAction SilentlyContinue | Where-Object { $_.TaskPath -notlike '\Microsoft\*' } | ForEach-Object { [pscustomobject]@{ P = $_.TaskPath; N = $_.TaskName; E = @($_.Actions | ForEach-Object { [string]$_.Execute }) } }); ConvertTo-Json -InputObject $r -Depth 3 -Compress", 90000);
            var i0 = json.IndexOf('[');
            if (i0 >= 0)
            {
                using var doc = JsonDocument.Parse(json[i0..]);
                foreach (var t in doc.RootElement.EnumerateArray())
                {
                    var p = t.TryGetProperty("P", out var pp) ? pp.GetString() : "\\";
                    var n = t.TryGetProperty("N", out var nn) ? nn.GetString() : null;
                    if (n == null || !t.TryGetProperty("E", out var ee)) continue;
                    var exes = ee.ValueKind == JsonValueKind.Array ? ee.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null) : new[] { ee.ValueKind == JsonValueKind.String ? ee.GetString() : null };
                    foreach (var ex in exes)
                    {
                        if (string.IsNullOrWhiteSpace(ex)) continue;
                        var target = CmdTarget("\"" + ex.Trim('"') + "\"");
                        if (IsMissing(target)) { Add("Tâches planifiées", n, Risk.Check, p + n, n, RegAction.Task, $"Tâche qui lance un programme supprimé : {target}", p); break; }
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log.Error("Registre : lecture des tâches planifiées", ex); }
        Timed("Terminé");
        foreach (var g in res.GroupBy(f => f.Cat)) Log.Info($"   {g.Key} : {g.Count()}");
        return res;
    }

    // ------------------------------------------------------------------ sauvegarde / correction
    /// <summary>Exporte toutes les clés touchées dans un seul .reg (UTF-16) + les tâches en XML. Renvoie (fichier .reg, [(tâche, xml)]).</summary>
    public static (string RegFile, List<(string TaskFullName, string Xml)> Tasks) Backup(IEnumerable<RegFinding> findings)
    {
        Directory.CreateDirectory(BackupDir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var file = Path.Combine(BackupDir, stamp + ".reg");
        var list = findings.ToList();
        var keys = list.Where(f => f.Action != RegAction.Task).Select(f => f.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tmp = Path.GetTempFileName();
        var sb = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        foreach (var k in keys)
        {
            var r = Shell.Run("reg.exe", $"export \"{k}\" \"{tmp}\" /y", 120000);
            if (r.Code != 0) { Log.Warn($"Sauvegarde registre : export de {k} impossible ({r.Output?.Trim()})"); continue; }
            try
            {
                var c = File.ReadAllText(tmp, Encoding.Unicode);
                c = Regex.Replace(c, @"^﻿?\s*Windows Registry Editor Version 5\.00\s*", "\r\n");
                sb.Append(c);
            }
            catch (Exception ex) { Log.Error("Lecture de l'export " + k, ex); }
        }
        try { File.Delete(tmp); } catch { }
        File.WriteAllText(file, sb.ToString(), Encoding.Unicode);

        var tasks = new List<(string, string)>();
        foreach (var t in list.Where(f => f.Action == RegAction.Task))
        {
            var full = (t.Extra ?? "\\") + t.Value;
            var safe = Regex.Replace(t.Value, @"[\\/:*?""<>|]", "_");
            var xmlPath = Path.Combine(BackupDir, $"tache_{stamp}_{safe}.xml");
            static string Q(string x) => "'" + (x ?? "").Replace("'", "''") + "'";
            Shell.PowerShell($"Export-ScheduledTask -TaskName {Q(t.Value)} -TaskPath {Q(t.Extra ?? "\\")} -ErrorAction Stop | Set-Content -LiteralPath {Q(xmlPath)} -Encoding Unicode", 60000);
            if (File.Exists(xmlPath)) tasks.Add((full, xmlPath));
            else Log.Warn($"Sauvegarde de la tâche {full} impossible.");
        }
        Log.Info("Sauvegarde du registre : " + file);
        return (file, tasks);
    }

    public static void Repair(RegFinding f)
    {
        switch (f.Action)
        {
            case RegAction.Key:
            {
                var i = f.Key.LastIndexOf('\\');
                using var parent = Open(f.Key[..i], true) ?? throw new Exception("clé parente inaccessible");
                parent.DeleteSubKeyTree(f.Key[(i + 1)..], false);
                break;
            }
            case RegAction.Value:
            {
                using (var k = Open(f.Key, true) ?? throw new Exception("clé inaccessible")) k.DeleteValue(f.Value, false);
                if (!string.IsNullOrEmpty(f.Extra)) { using var ak = Open(f.Extra, true); ak?.DeleteValue(f.Value, false); }
                break;
            }
            case RegAction.FwRule:
            {
                Shell.PowerShell($"Remove-NetFirewallRule -Name '{f.Value.Replace("'", "''")}' -ErrorAction SilentlyContinue", 60000);
                using var k = Open(f.Key, true); k?.DeleteValue(f.Value, false);
                break;
            }
            case RegAction.Service:
            {
                var r = Shell.Run("sc.exe", $"delete \"{f.Value}\"", 60000);
                if (r.Code != 0) throw new Exception($"sc delete a échoué (code {r.Code})");
                break;
            }
            case RegAction.Task:
            {
                var r = Shell.Run("schtasks.exe", $"/delete /tn \"{(f.Extra ?? "\\") + f.Value}\" /f", 60000);
                if (r.Code != 0) throw new Exception($"schtasks /delete a échoué : {r.Output?.Trim()}");
                break;
            }
        }
    }

    public static List<FileInfo> Backups()
    {
        try { return Directory.Exists(BackupDir) ? new DirectoryInfo(BackupDir).GetFiles("*.reg").OrderByDescending(f => f.LastWriteTime).ToList() : new(); }
        catch { return new(); }
    }

    public static bool Restore(string regFile)
    {
        var r = Shell.Run("reg.exe", $"import \"{regFile}\"", 300000);
        if (r.Code != 0) Log.Warn($"Restauration {regFile} : code {r.Code} {r.Output}");
        else Log.Info("Registre restauré depuis " + regFile);
        // Tâches sauvegardées en même temps
        var stamp = Path.GetFileNameWithoutExtension(regFile);
        try
        {
            foreach (var x in Directory.GetFiles(Path.GetDirectoryName(regFile)!, $"tache_{stamp}_*.xml"))
            {
                var name = Path.GetFileNameWithoutExtension(x)[$"tache_{stamp}_".Length..];
                Shell.Run("schtasks.exe", $"/create /tn \"{name}\" /xml \"{x}\" /f", 60000);
            }
        }
        catch { }
        return r.Code == 0;
    }
}
