using System.Text.Json;
using Microsoft.Win32;

namespace NettoyeurPC.Core;

public class DriveSummary
{
    public string Letter;      // "C:"
    public string Root;        // "C:\"
    public string Label;
    public long Size, Free;
    public string Media = "";  // SSD NVMe, SSD SATA, HDD
    public string Model = "";
    public int? HealthPct;     // 100 - usure
    public int? TempC;
    public string HealthStatus = "";
    public bool IsSystem;
    public double UsedPct => Size > 0 ? 100.0 * (Size - Free) / Size : 0;
    public double FreePct => Size > 0 ? 100.0 * Free / Size : 0;
    public string Role => IsSystem ? "Système" : (Label?.Length > 0 ? Label : "Données");
}

public static class SystemInfo
{
    public static List<DriveSummary> Drives()
    {
        var list = new List<DriveSummary>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                list.Add(new DriveSummary
                {
                    Letter = d.Name.Substring(0, 2).ToUpperInvariant(), Root = d.Name, Label = d.VolumeLabel, Size = d.TotalSize, Free = d.AvailableFreeSpace,
                    IsSystem = d.Name.Equals(AppPaths.SystemDrive, StringComparison.OrdinalIgnoreCase),
                });
            }
            catch { }
        }
        return list.OrderBy(x => x.IsSystem ? 0 : 1).ThenBy(x => x.Letter).ToList();
    }

    /// <summary>Complète les disques avec le modèle, le type et la santé (usure, température) via PowerShell Storage.</summary>
    public static void AddHealth(List<DriveSummary> drives)
    {
        try
        {
            var json = Shell.PowerShell(@"
$parts = @(Get-Partition -EA SilentlyContinue | ? DriveLetter)
$out = foreach ($pd in @(Get-PhysicalDisk -EA SilentlyContinue)) {
  $r = $pd | Get-StorageReliabilityCounter -EA SilentlyContinue
  $letters = ($parts | ? { $_.DiskNumber -eq [int]$pd.DeviceId } | % { [string]$_.DriveLetter }) -join ''
  [pscustomobject]@{ L=$letters; N=$pd.FriendlyName; M=[string]$pd.MediaType; B=[string]$pd.BusType; H=[string]$pd.HealthStatus; W=$r.Wear; T=$r.Temperature }
}
ConvertTo-Json @($out) -Compress", 60000);
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json.Trim());
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var letters = e.GetProperty("L").GetString() ?? "";
                foreach (var d in drives.Where(x => letters.Contains(x.Letter[0], StringComparison.OrdinalIgnoreCase)))
                {
                    d.Model = e.GetProperty("N").GetString() ?? "";
                    var media = e.GetProperty("M").GetString() ?? ""; var bus = e.GetProperty("B").GetString() ?? "";
                    d.Media = media == "SSD" ? (bus == "NVMe" ? "SSD NVMe" : "SSD " + bus) : media == "HDD" ? "Disque dur" : (bus == "NVMe" ? "SSD NVMe" : bus);
                    d.HealthStatus = e.GetProperty("H").GetString() ?? "";
                    if (e.TryGetProperty("W", out var w) && w.ValueKind == JsonValueKind.Number) d.HealthPct = Math.Clamp(100 - w.GetInt32(), 0, 100);
                    if (e.TryGetProperty("T", out var t) && t.ValueKind == JsonValueKind.Number && t.GetInt32() > 0) d.TempC = t.GetInt32();
                }
            }
        }
        catch (Exception ex) { Log.Warn("Santé des disques : " + ex.Message); }
    }

    /// <summary>Nombre de programmes lancés au démarrage (registre + dossiers Démarrage, hors désactivés).</summary>
    public static int StartupCount()
    {
        int n = 0;
        void CountRun(RegistryKey root, string run, string approved)
        {
            try
            {
                using var k = root.OpenSubKey(run); if (k == null) return;
                using var a = root.OpenSubKey(approved);
                foreach (var v in k.GetValueNames())
                {
                    if (string.IsNullOrEmpty(v)) continue;
                    if (a?.GetValue(v) is byte[] b && b.Length > 0 && b[0] % 2 == 1) continue;
                    n++;
                }
            }
            catch { }
        }
        const string ap = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
        CountRun(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", ap + "Run");
        CountRun(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", ap + "Run");
        CountRun(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", ap + "Run32");
        foreach (var f in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Startup), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) })
            try { n += Directory.EnumerateFiles(f).Count(x => !x.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)); } catch { }
        return n;
    }

    public static int RestorePointCount()
    {
        var o = Shell.PowerShell("@(Get-CimInstance Win32_ShadowCopy -EA SilentlyContinue).Count").Trim();
        return int.TryParse(o, out var n) ? n : 0;
    }
}

public class HealthReport
{
    public int Score;
    public string Label;
    public List<string> Issues = new();
}

public static class Health
{
    public static HealthReport Compute(List<DriveSummary> drives, CleanSession session, int startupCount)
    {
        var r = new HealthReport { Score = 100 };
        var sys = drives.FirstOrDefault(d => d.IsSystem);
        if (sys != null)
        {
            var f = sys.FreePct;
            if (f < 10) { r.Score -= 35; r.Issues.Add($"Le disque {sys.Letter} est presque plein ({Fmt.Pct(f)} libre)"); }
            else if (f < 15) { r.Score -= 25; r.Issues.Add($"Le disque {sys.Letter} manque de place ({Fmt.Pct(f)} libre)"); }
            else if (f < 20) { r.Score -= 12; r.Issues.Add($"Le disque {sys.Letter} pourrait avoir plus de place ({Fmt.Pct(f)} libre)"); }
        }
        foreach (var d in drives.Where(d => d.HealthPct is < 80 || (d.HealthStatus != "" && d.HealthStatus != "Healthy")))
        { r.Score -= 20; r.Issues.Add($"Le disque {d.Letter} montre des signes d'usure"); }
        if (session?.Scanned == true)
        {
            var safe = session.TotalSafe;
            if (safe > 10L << 30) { r.Score -= 15; r.Issues.Add($"{Fmt.Size(safe)} de fichiers inutiles"); }
            else if (safe > 3L << 30) { r.Score -= 9; r.Issues.Add($"{Fmt.Size(safe)} de fichiers inutiles"); }
            else if (safe > 500L << 20) { r.Score -= 4; r.Issues.Add($"{Fmt.Size(safe)} de fichiers inutiles"); }
        }
        if (startupCount > 12) { r.Score -= 8; r.Issues.Add($"{startupCount} programmes au démarrage"); }
        else if (startupCount > 8) { r.Score -= 4; r.Issues.Add($"{startupCount} programmes au démarrage"); }
        var last = Settings.Current.LastClean;
        if (last == default || (DateTime.Now - last).TotalDays > 30) { r.Score -= 5; r.Issues.Add("Pas de nettoyage depuis plus d'un mois"); }
        r.Score = Math.Clamp(r.Score, 0, 100);
        r.Label = r.Score >= 85 ? "Très bon état" : r.Score >= 65 ? "Bon état" : r.Score >= 45 ? "À entretenir" : "Attention";
        return r;
    }
}

/// <summary>Nettoyage automatique hebdomadaire via le Planificateur de tâches de Windows.</summary>
public static class AutoClean
{
    public const string TaskName = "NettoyeurPC_Automatique";

    /// <summary>Copie l'appli dans Program Files (emplacement stable) et crée la tâche planifiée.</summary>
    public static bool Enable(string day)
    {
        try
        {
            var exe = InstallCopy();
            var r = Shell.Run("schtasks.exe", $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --auto\" /SC WEEKLY /D {day} /ST 20:00 /RL HIGHEST");
            Log.Info($"Tâche planifiée ({day}) : code {r.Code} {r.Output.Trim()}");
            return r.Code == 0;
        }
        catch (Exception ex) { Log.Error("Activation du nettoyage automatique", ex); return false; }
    }

    public const string LaunchTask = "NettoyeurPC_Ouvrir";

    /// <summary>
    /// Crée une tâche « exécuter avec les privilèges les plus élevés » + des raccourcis (Bureau, menu Démarrer)
    /// qui la lancent : l'appli s'ouvre en administrateur sans la fenêtre de confirmation à chaque fois.
    /// </summary>
    public static void CreateLaunchShortcuts(string exe)
    {
        var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var menu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        var ps = $@"
$exe = '{exe.Replace("'", "''")}'
$a = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
$p = New-ScheduledTaskPrincipal -UserId ""$env:USERDOMAIN\$env:USERNAME"" -LogonType Interactive -RunLevel Highest
$s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName '{LaunchTask}' -Action $a -Principal $p -Settings $s -Force | Out-Null
$w = New-Object -ComObject WScript.Shell
foreach ($d in @('{desk.Replace("'", "''")}', '{menu.Replace("'", "''")}')) {{
  $l = $w.CreateShortcut((Join-Path $d 'Nettoyeur PC.lnk'))
  $l.TargetPath = ""$env:windir\System32\schtasks.exe""
  $l.Arguments = '/run /tn ""{LaunchTask}""'
  $l.WindowStyle = 7
  $l.IconLocation = ""$exe,0""
  $l.Save()
}}";
        var o = Shell.PowerShell(ps);
        Log.Info("Raccourcis sans confirmation créés " + o.Trim());
    }

    public static void Disable() { var r = Shell.Run("schtasks.exe", $"/Delete /F /TN \"{TaskName}\""); Log.Info($"Tâche planifiée supprimée : code {r.Code}"); }

    public static string InstallCopy()
    {
        var src = AppPaths.ExePath; var dst = AppPaths.InstalledExe;
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) { CreateLaunchShortcuts(dst); return dst; }
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(src, dst, true);
        CreateLaunchShortcuts(dst);
        return dst;
    }

    /// <summary>Exécuté par la tâche planifiée : nettoyage sans risque, sans fenêtre.</summary>
    public static async Task RunHeadless()
    {
        Log.Info("=== Nettoyage automatique ===");
        var s = Settings.Current;
        try
        {
            if (s.AutoPurgeQuarantine) Quarantine.Purge(s.QuarantineDays);
            var session = new CleanSession();
            foreach (var r in session.Rules)
                r.Checked = r.Risk == Risk.Safe && r.Default && r.CustomApply == null && !r.UseQuarantine && (s.AutoIncludeBrowsers || r.Category != "Navigateurs");
            await session.ScanAsync(null);
            var sel = session.Rules.Where(r => r.Checked && r.Size > 0).ToList();
            if (sel.Count > 0)
            {
                var ev = await session.CleanAsync(sel, false, null);
                ev.Title = "Nettoyage automatique"; History.Save();
            }
            if (s.WarnLowSpace)
            {
                var low = SystemInfo.Drives().Where(d => d.FreePct < 15).Select(d => $"{d.Letter} ({Fmt.Pct(d.FreePct)} libre)").ToList();
                s.LowSpaceWarning = low.Count > 0 ? "Espace faible sur " + string.Join(", ", low) : "";
                s.Save();
            }
        }
        catch (Exception ex) { Log.Error("Nettoyage automatique", ex); }
    }
}
