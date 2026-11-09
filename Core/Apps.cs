using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NettoyeurPC.Core;

/// <summary>Un programme installé (clé Uninstall du registre).</summary>
public class AppInfo
{
    public string Key { get; set; }            // HKLM\...\Uninstall\xxx
    public string KeyName { get; set; }
    public string Name { get; set; }
    public string Publisher { get; set; }
    public string Version { get; set; }
    public DateTime Installed { get; set; }
    public long DeclaredSize { get; set; }
    public long RealSize { get; set; } = -1;   // taille mesurée du dossier (-1 = pas encore)
    public string InstallLocation { get; set; }
    public string DisplayIcon { get; set; }
    public string Uninstall { get; set; }
    public string QuietUninstall { get; set; }
    public string AppDir { get; set; }
    public bool IsJunction { get; set; }       // dossier déjà déplacé (lien)
    public string JunctionTarget { get; set; }

    public long Size => RealSize > 0 ? RealSize : DeclaredSize;
    public string Drive => string.IsNullOrEmpty(AppDir) ? "?" : (IsJunction && !string.IsNullOrEmpty(JunctionTarget) ? JunctionTarget : AppDir).Substring(0, 2).ToUpperInvariant();
    public bool IsSteam => (Uninstall ?? "").Contains("steam", StringComparison.OrdinalIgnoreCase) && (Uninstall ?? "").Contains("uninstall", StringComparison.OrdinalIgnoreCase) && (Uninstall ?? "").Contains("steam://", StringComparison.OrdinalIgnoreCase);
}

public static class Apps
{
    static readonly (RegistryHive Hive, RegistryView View, string Label)[] Roots =
    {
        (RegistryHive.LocalMachine, RegistryView.Registry64, "HKLM"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, "HKLM32"),
        (RegistryHive.CurrentUser, RegistryView.Registry64, "HKCU"),
    };
    const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<AppInfo> List()
    {
        var res = new Dictionary<string, AppInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (hive, view, label) in Roots)
        {
            try
            {
                using var bk = RegistryKey.OpenBaseKey(hive, view);
                using var uk = bk.OpenSubKey(UninstallPath);
                if (uk == null) continue;
                foreach (var name in uk.GetSubKeyNames())
                {
                    try
                    {
                        using var k = uk.OpenSubKey(name);
                        if (k == null) continue;
                        var dn = k.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(dn)) continue;
                        if (k.GetValue("SystemComponent") is int sc && sc == 1) continue;
                        if (k.GetValue("ParentKeyName") != null) continue;
                        var un = k.GetValue("UninstallString") as string;
                        var qun = k.GetValue("QuietUninstallString") as string;
                        if (string.IsNullOrWhiteSpace(un) && string.IsNullOrWhiteSpace(qun)) continue;
                        if (res.ContainsKey(dn.Trim())) continue;
                        var a = new AppInfo
                        {
                            Key = $"{(hive == RegistryHive.LocalMachine ? "HKLM" : "HKCU")}\\{(view == RegistryView.Registry32 && hive == RegistryHive.LocalMachine ? @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" : UninstallPath)}\\{name}",
                            KeyName = name,
                            Name = dn.Trim(),
                            Publisher = (k.GetValue("Publisher") as string)?.Trim() ?? "",
                            Version = (k.GetValue("DisplayVersion") as string)?.Trim() ?? "",
                            InstallLocation = k.GetValue("InstallLocation") as string,
                            DisplayIcon = k.GetValue("DisplayIcon") as string,
                            Uninstall = un, QuietUninstall = qun,
                        };
                        var es = k.GetValue("EstimatedSize");
                        if (es is int i && i > 0) a.DeclaredSize = (long)i * 1024;
                        if (k.GetValue("InstallDate") is string d && d.Length == 8 && DateTime.TryParseExact(d, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var dt)) a.Installed = dt;
                        a.AppDir = AppDir(a);
                        if (a.AppDir != null)
                        {
                            try
                            {
                                var di = new DirectoryInfo(a.AppDir);
                                if (di.Exists && di.Attributes.HasFlag(FileAttributes.ReparsePoint)) { a.IsJunction = true; a.JunctionTarget = di.LinkTarget; }
                                else if (!di.Exists) a.AppDir = null;
                            }
                            catch { }
                        }
                        if (a.Installed == default && a.AppDir != null) try { a.Installed = Directory.GetCreationTime(a.AppDir); } catch { }
                        res[a.Name] = a;
                    }
                    catch { }
                }
            }
            catch (Exception ex) { Log.Error("Lecture des programmes " + label, ex); }
        }
        return res.Values.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    static readonly Regex Junk = new(@"\\(Package Cache|Installer|InstallShield Installation Information|Downloaded Installations)\\", RegexOptions.IgnoreCase);

    /// <summary>Dossier réel du programme : emplacement d'installation, sinon icône, sinon désinstalleur (hors caches d'installation).</summary>
    public static string AppDir(AppInfo p)
    {
        var cands = new List<string>();
        if (!string.IsNullOrWhiteSpace(p.InstallLocation)) cands.Add(p.InstallLocation.Trim().Trim('"'));
        if (!string.IsNullOrWhiteSpace(p.DisplayIcon)) cands.Add(Regex.Replace(p.DisplayIcon, @",\s*-?\d+$", "").Trim().Trim('"'));
        foreach (var u in new[] { p.Uninstall, p.QuietUninstall })
            if (!string.IsNullOrWhiteSpace(u) && !u.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) cands.Add(SplitCommand(u).Exe);
        foreach (var c0 in cands)
        {
            var c = Environment.ExpandEnvironmentVariables(c0).TrimEnd('\\');
            if (!Regex.IsMatch(c, @"^[A-Za-z]:\\")) continue;
            if (Junk.IsMatch(c + "\\") || c.EndsWith("steam.exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (Regex.IsMatch(c, @"\.\w{2,4}$") && !Directory.Exists(c)) c = Path.GetDirectoryName(c);
            if (string.IsNullOrEmpty(c) || c.Length <= 3) continue;
            return c;
        }
        return null;
    }

    public static (string Exe, string Args) SplitCommand(string cmd)
    {
        var c = Environment.ExpandEnvironmentVariables(cmd ?? "").Trim();
        var m = Regex.Match(c, "^\"([^\"]+)\"\\s*(.*)$");
        if (m.Success) return (m.Groups[1].Value, m.Groups[2].Value);
        m = Regex.Match(c, @"^(.+?\.(exe|com|bat|cmd))(?=$|\s)\s*(.*)$", RegexOptions.IgnoreCase);
        if (m.Success) return (m.Groups[1].Value, m.Groups[3].Value);
        var parts = c.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return (parts.Length > 0 ? parts[0] : "", parts.Length > 1 ? parts[1] : "");
    }

    /// <summary>Dossier trop général pour être mesuré ou déplacé (Program Files lui-même, Common Files, racine…).</summary>
    public static bool IsSharedDir(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return true;
        if (Shell.IsProtected(dir)) return true;
        var n = Path.GetFileName(dir.TrimEnd('\\')).ToLowerInvariant();
        if (n is "common files" or "windowsapps" or "microsoft" or "steamapps" or "common" or "bin" or "programs") return true;
        if (dir.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase)) return true;
        return Path.GetDirectoryName(dir.TrimEnd('\\')) == null;
    }

    /// <summary>Mesure la taille réelle des dossiers d'installation, en arrière-plan.</summary>
    public static void MeasureAll(IEnumerable<AppInfo> apps, Action<AppInfo> done, CancellationToken ct)
    {
        Parallel.ForEach(apps.Where(a => a.AppDir != null && !IsSharedDir(a.AppDir)), new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct }, a =>
        {
            var dir = a.IsJunction && !string.IsNullOrEmpty(a.JunctionTarget) ? a.JunctionTarget : a.AppDir;
            a.RealSize = Quarantine.DirSize(dir);
            done(a);
        });
    }

    // ------------------------------------------------------------------ Désinstallation au premier plan
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);

    static readonly Regex GuidRx = new(@"\{[0-9A-Fa-f\-]{36}\}");

    public static (string Exe, string Args) UninstallCommand(AppInfo p)
    {
        string guid = null;
        var un = p.Uninstall ?? "";
        if (GuidRx.IsMatch(p.KeyName ?? "") && GuidRx.Match(p.KeyName).Value == p.KeyName && un.Contains("msiexec", StringComparison.OrdinalIgnoreCase)) guid = p.KeyName;
        else if (un.Contains("msiexec", StringComparison.OrdinalIgnoreCase) && GuidRx.IsMatch(un)) guid = GuidRx.Match(un).Value;
        // msiexec /I ouvre l'installeur (mode « modifier ») : on force /X pour désinstaller
        if (guid != null) return ("msiexec.exe", $"/x {guid} /norestart");
        return SplitCommand(string.IsNullOrWhiteSpace(p.Uninstall) ? p.QuietUninstall : p.Uninstall);
    }

    /// <summary>Lance le désinstalleur visible, au premier plan, suit tous ses sous-processus et attend la fin.
    /// Renvoie true si terminé normalement, false si l'attente a été interrompue.</summary>
    public static bool RunUninstaller(AppInfo p, Action<string> status, CancellationToken ct)
    {
        var (exe, args) = UninstallCommand(p);
        Log.Info($"Désinstallation de {p.Name} : {exe} {args}");
        AllowSetForegroundWindow(-1);
        var t0 = DateTime.Now.AddSeconds(-1);
        Process pr;
        try
        {
            ProcessStartInfo psi;
            if (exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{exe}\" {args}\"");
            else psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = true; psi.WindowStyle = ProcessWindowStyle.Normal;
            pr = Process.Start(psi);
        }
        catch (Exception ex) { Log.Error("Lancement du désinstalleur de " + p.Name, ex); status?.Invoke("Impossible de lancer le désinstalleur : " + ex.Message); Thread.Sleep(2500); return true; }
        if (pr == null) { Thread.Sleep(3000); return true; }   // ex. lien steam:// → Steam gère la confirmation
        if (p.IsSteam) status?.Invoke("La confirmation s'affiche dans Steam.");

        var tracked = new HashSet<int> { pr.Id };
        var focused = new HashSet<int>();
        DateTime? deadSince = null;
        int tick = 0;
        while (!ct.IsCancellationRequested)
        {
            Thread.Sleep(250); tick++;
            if (tick % 2 == 0)
            {
                // Beaucoup de désinstalleurs se recopient dans %TEMP% et se relancent : on suit les enfants
                try
                {
                    using var q = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, CreationDate FROM Win32_Process");
                    foreach (ManagementObject mo in q.Get())
                    {
                        using (mo)
                        {
                            int id = Convert.ToInt32(mo["ProcessId"]), parent = Convert.ToInt32(mo["ParentProcessId"]);
                            if (!tracked.Contains(parent) || tracked.Contains(id)) continue;
                            var cd = mo["CreationDate"] as string;
                            if (cd != null && ManagementDateTimeConverter.ToDateTime(cd) < t0) continue;
                            tracked.Add(id);
                        }
                    }
                }
                catch { }
                foreach (var id in tracked.ToList())
                {
                    try
                    {
                        using var gp = Process.GetProcessById(id);
                        var h = gp.MainWindowHandle;
                        if (h != IntPtr.Zero && focused.Add(id))
                        {
                            if (IsIconic(h)) ShowWindow(h, 9);
                            BringWindowToTop(h); SetForegroundWindow(h);
                            Log.Info($"   fenêtre mise au premier plan : {gp.ProcessName} « {gp.MainWindowTitle} »");
                        }
                    }
                    catch { }
                }
            }
            bool alive = false;
            foreach (var id in tracked) { try { using var gp = Process.GetProcessById(id); if (!gp.HasExited) { alive = true; break; } } catch { } }
            if (alive) deadSince = null;
            else if (deadSince == null) deadSince = DateTime.Now;
            else if ((DateTime.Now - deadSince.Value).TotalSeconds >= 3) break;   // 3 s sans aucun processus : terminé
        }
        if (ct.IsCancellationRequested) { Log.Info("   attente interrompue par l'utilisateur."); return false; }
        try { Log.Info($"   terminé (code {pr.ExitCode})"); } catch { }
        return true;
    }

    public static bool StillInstalled(AppInfo p)
    {
        try
        {
            var parts = p.Key.Split('\\', 2);
            using var bk = RegistryKey.OpenBaseKey(parts[0] == "HKCU" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = bk.OpenSubKey(parts[1]);
            return k != null;
        }
        catch { return true; }
    }

    // ------------------------------------------------------------------ Déplacement vers un autre disque (lien de dossier)
    public static string CheckMovable(AppInfo a)
    {
        if (a.AppDir == null) return "L'emplacement de ce programme est inconnu.";
        if (a.IsSteam) return "Pour un jeu Steam, utilise plutôt Steam : clic droit sur le jeu → Propriétés → Fichiers installés → Déplacer le dossier d'installation.";
        if (a.IsJunction) return $"Ce programme est déjà déplacé vers {a.JunctionTarget}.";
        if (IsSharedDir(a.AppDir)) return "Ce dossier est partagé par plusieurs programmes ou protégé par Windows : on n'y touche pas.";
        if (a.AppDir.StartsWith(AppPaths.Windows, StringComparison.OrdinalIgnoreCase)) return "Les composants de Windows ne se déplacent pas.";
        var n = a.Name.ToLowerInvariant();
        if (Regex.IsMatch(n, @"\b(driver|pilote|redistributable|runtime|\.net|visual c\+\+|directx|chipset|nvidia|amd software|realtek|intel|antivirus|defender|security|vpn|anti-cheat|easyanticheat|battleye|vanguard)\b"))
            return "Pilotes, runtimes, antivirus, VPN et anti-triche doivent rester sur le disque système.";
        try
        {
            var dirInfo = new DriveInfo(a.AppDir[..1]);
            if (dirInfo.DriveType != DriveType.Fixed) return "Le programme n'est pas sur un disque interne.";
        }
        catch { }
        return null;
    }

    /// <summary>Services Windows dont l'exécutable est dans ce dossier.</summary>
    public static List<(string Name, string Display, bool Running)> ServicesIn(string dir)
    {
        var res = new List<(string, string, bool)>();
        try
        {
            using var q = new ManagementObjectSearcher("SELECT Name, DisplayName, PathName, State FROM Win32_Service");
            foreach (ManagementObject mo in q.Get())
                using (mo)
                {
                    var path = mo["PathName"] as string;
                    if (string.IsNullOrEmpty(path)) continue;
                    var exe = SplitCommand(path).Exe;
                    if (exe.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) res.Add((mo["Name"] as string, mo["DisplayName"] as string, (mo["State"] as string) == "Running"));
                }
        }
        catch { }
        return res;
    }

    /// <summary>Processus lancés depuis ce dossier (il faut les fermer avant de déplacer).</summary>
    public static List<(int Id, string Name)> ProcessesIn(string dir)
    {
        var res = new List<(int, string)>();
        try
        {
            using var q = new ManagementObjectSearcher("SELECT ProcessId, Name, ExecutablePath FROM Win32_Process");
            foreach (ManagementObject mo in q.Get())
                using (mo)
                {
                    var p = mo["ExecutablePath"] as string;
                    if (p != null && p.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) res.Add((Convert.ToInt32(mo["ProcessId"]), mo["Name"] as string));
                }
        }
        catch { }
        return res;
    }

    public static string DefaultTarget(AppInfo a, string driveRoot)
    {
        // E:\Applications\Program Files\Editeur\Appli : on garde l'arborescence d'origine pour s'y retrouver
        var rel = a.AppDir.Substring(3);
        return Path.Combine(driveRoot, Settings.Current.AppsMoveFolder, rel);
    }

    /// <summary>Copie le dossier sur l'autre disque, vérifie, remplace l'original par un lien (jonction) puis supprime l'original.
    /// Le programme continue de fonctionner sans réinstallation.</summary>
    public static (bool Ok, string Message) Move(AppInfo a, string target, Action<string> status, CancellationToken ct)
    {
        var src = a.AppDir.TrimEnd('\\');
        var old = src + ".npc_ancien";
        Log.Info($"Déplacement de {a.Name} : {src} → {target}");
        try
        {
            if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) return (false, $"Le dossier de destination existe déjà et n'est pas vide :\n{target}");
            if (Directory.Exists(old)) return (false, $"Un ancien déplacement inachevé existe encore :\n{old}");
            var files = new DirectoryInfo(src).EnumerateFiles("*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint }).ToList();
            long total = files.Sum(f => f.Length);
            var drive = new DriveInfo(target[..1]);
            if (drive.AvailableFreeSpace < total + 1_000_000_000L) return (false, $"Pas assez de place sur {target[..2]} ({Fmt.Size(drive.AvailableFreeSpace)} libres pour {Fmt.Size(total)}).");

            // 1. Copie
            long done = 0; int n = 0;
            Directory.CreateDirectory(target);
            foreach (var d in Directory.EnumerateDirectories(src, "*", new System.IO.EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(src, d)));
            foreach (var f in files)
            {
                if (ct.IsCancellationRequested) { TryDelete(target); return (false, "Déplacement annulé : rien n'a été modifié."); }
                var dst = Path.Combine(target, Path.GetRelativePath(src, f.FullName));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                f.CopyTo(dst, false);
                try { File.SetAttributes(dst, f.Attributes); } catch { }
                done += f.Length; n++;
                if (n % 25 == 0) status?.Invoke($"Copie : {Fmt.Size(done)} sur {Fmt.Size(total)} ({n:#,0} / {files.Count:#,0} fichiers)");
            }
            // 2. Vérification
            status?.Invoke("Vérification de la copie…");
            var copied = new DirectoryInfo(target).EnumerateFiles("*", new System.IO.EnumerationOptions { RecurseSubdirectories = true }).ToList();
            if (copied.Count != files.Count || copied.Sum(f => f.Length) != total)
            {
                TryDelete(target);
                return (false, "La copie ne correspond pas à l'original : rien n'a été modifié.");
            }
            // 3. Bascule : l'original est renommé (échoue si un fichier est encore ouvert), puis remplacé par un lien
            status?.Invoke("Création du lien…");
            try { Directory.Move(src, old); }
            catch (Exception ex)
            {
                TryDelete(target);
                return (false, "Un fichier du programme est encore utilisé (programme ou service ouvert). Ferme-le puis recommence.\n\n" + ex.Message);
            }
            var r = Shell.Run("cmd.exe", $"/c mklink /J \"{src}\" \"{target}\"");
            if (r.Code != 0 || !Directory.Exists(src))
            {
                try { if (Directory.Exists(src)) Directory.Delete(src); Directory.Move(old, src); } catch (Exception ex) { Log.Error("Retour arrière du déplacement", ex); }
                TryDelete(target);
                return (false, "Le lien n'a pas pu être créé : tout a été remis comme avant.\n" + r.Output);
            }
            // 4. Suppression de l'ancien dossier
            status?.Invoke("Libération de l'espace…");
            try { Directory.Delete(old, true); }
            catch (Exception ex) { Log.Warn($"Ancien dossier non supprimé entièrement ({old}) : {ex.Message}"); }

            var undo = $"if (Test-Path -LiteralPath '{Ps(src)}') {{ cmd /c rmdir \"{src}\" }}; robocopy '{Ps(target)}' '{Ps(src)}' /E /MOVE /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NP /NJH /NJS | Out-Null; if ($LASTEXITCODE -lt 8) {{ exit 0 }} else {{ exit 1 }}";
            History.Add(new HistoryEvent
            {
                Title = $"{a.Name} déplacé vers {target[..2]}",
                Sub = $"{Fmt.Size(total)} libérés sur {src[..2]} · {src} → {target}",
                Bytes = total,
                UndoCommands = { "powershell.exe|-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(undo)) },
            });
            return (true, $"{Fmt.Size(total)} libérés sur {src[..2]}. {a.Name} fonctionne comme avant : Windows le voit toujours à son emplacement d'origine.");
        }
        catch (Exception ex)
        {
            Log.Error("Déplacement de " + a.Name, ex);
            if (!Directory.Exists(src) && Directory.Exists(old)) try { Directory.Move(old, src); } catch { }
            if (Directory.Exists(src) && Directory.Exists(target) && !new DirectoryInfo(src).Attributes.HasFlag(FileAttributes.ReparsePoint)) TryDelete(target);
            return (false, "Erreur : " + ex.Message + "\nL'original est intact.");
        }
    }

    static string Ps(string s) => s.Replace("'", "''");
    static void TryDelete(string dir) { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (Exception ex) { Log.Warn("Nettoyage de la copie : " + ex.Message); } }
}
