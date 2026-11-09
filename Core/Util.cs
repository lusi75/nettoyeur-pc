using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace NettoyeurPC.Core;

public static class Fmt
{
    static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Taille en unités décimales (1 Go = 1 000 000 000 octets), comme l'affiche l'iPhone.</summary>
    public static string SizeDec(long bytes)
    {
        if (bytes < 0) return "?";
        double b = bytes;
        if (b >= 1e12) return (b / 1e12).ToString("0.00", Fr) + " To";
        if (b >= 1e9) return (b / 1e9).ToString("0.00", Fr) + " Go";
        if (b >= 1e6) return (b / 1e6).ToString(b >= 1e8 ? "0" : "0.0", Fr) + " Mo";
        if (b >= 1e3) return (b / 1e3).ToString("0", Fr) + " Ko";
        return bytes.ToString(Fr) + " o";
    }

    public static string Size(long bytes)
    {
        if (bytes < 0) return "?";
        double b = bytes;
        if (b >= 1L << 40) return (b / (1L << 40)).ToString("0.00", Fr) + " To";
        if (b >= 1L << 30) return (b / (1L << 30)).ToString(b >= 100L * (1L << 30) ? "0" : "0.0", Fr) + " Go";
        if (b >= 1L << 20) return (b / (1L << 20)).ToString(b >= 100L * (1L << 20) ? "0" : "0.0", Fr) + " Mo";
        if (b >= 1L << 10) return (b / (1L << 10)).ToString("0", Fr) + " Ko";
        return bytes.ToString(Fr) + " o";
    }

    public static string Num(long n) => n.ToString("#,0", Fr);
    public static string Pct(double p) => p.ToString(p < 10 ? "0.0" : "0", Fr) + " %";
    public static string Date(DateTime d) => d == default ? "" : d.ToString("dd/MM/yyyy", Fr);
    public static string DateTime(DateTime d) => d.ToString("dd/MM/yyyy HH:mm", Fr);
    public static string Time(DateTime d) => d.ToString("HH:mm", Fr);
    public static string DayLong(DateTime d) => d.ToString("dddd d MMMM", Fr);

    public static string Ago(DateTime d)
    {
        if (d == default) return "jamais";
        var s = System.DateTime.Now - d;
        if (s.TotalMinutes < 2) return "à l'instant";
        if (s.TotalHours < 1) return $"il y a {(int)s.TotalMinutes} min";
        if (s.TotalDays < 1) return $"il y a {(int)s.TotalHours} h";
        if (s.TotalDays < 2) return "hier";
        return $"il y a {(int)s.TotalDays} jours";
    }
}

public static class Log
{
    static readonly object Lock = new();
    public static string Dir => Path.Combine(AppPaths.Data, "logs");
    public static string File => Path.Combine(Dir, $"journal_{System.DateTime.Now:yyyy-MM}.txt");

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception ex = null) => Write("ERREUR", ex == null ? msg : msg + " : " + ex);

    static void Write(string level, string msg)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Dir);
                System.IO.File.AppendAllText(File, $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {msg}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }
}

public static class AppPaths
{
    public static string Data
    {
        get
        {
            var d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NettoyeurPC");
            Directory.CreateDirectory(d);
            return d;
        }
    }
    public static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    public static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    public static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    public static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    public static string Temp => Path.GetTempPath().TrimEnd('\\');
    public static string SystemDrive => Path.GetPathRoot(Windows) ?? "C:\\";
    public static string ExePath => Environment.ProcessPath ?? "";
    public static string InstalledExe => Path.Combine(ProgramFiles, "NettoyeurPC", "NettoyeurPC.exe");
}

/// <summary>Réglages persistants (JSON dans %LOCALAPPDATA%\NettoyeurPC).</summary>
public class Settings
{
    public string Accent { get; set; } = "#3DD6B5";
    public int QuarantineDays { get; set; } = 7;
    public string QuarantineDrive { get; set; } = "";   // vide = automatique
    public bool AutoClean { get; set; }
    public string AutoDay { get; set; } = "SUN";
    public bool AutoIncludeBrowsers { get; set; }
    public bool AutoPurgeQuarantine { get; set; } = true;
    public bool WarnLowSpace { get; set; } = true;
    public bool RestorePointBeforeClean { get; set; } = true;
    public DateTime LastClean { get; set; }
    public long LastCleanBytes { get; set; }
    public string LowSpaceWarning { get; set; } = "";
    // Mode jeu
    public bool GameModeOn { get; set; }
    public string GameModePrevScheme { get; set; } = "";
    public bool GameModeAutoWasOn { get; set; }
    public List<string> GameModeApps { get; set; } = new();     // noms de processus à fermer
    public List<string> GameModeClosed { get; set; } = new();   // exécutables fermés, relancés à la sortie
    // Applications
    public string AppsMoveFolder { get; set; } = "Applications";
    public string PhoneKind { get; set; } = "iphone";

    static string FilePath => Path.Combine(AppPaths.Data, "reglages.json");
    static Settings _current;
    public static Settings Current => _current ??= Load();

    static Settings Load()
    {
        try { if (System.IO.File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(FilePath)) ?? new Settings(); }
        catch (Exception ex) { Log.Error("Lecture des réglages", ex); }
        return new Settings();
    }

    public void Save()
    {
        try { System.IO.File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log.Error("Écriture des réglages", ex); }
    }
}

public static class Shell
{
    /// <summary>Lance un programme sans fenêtre et attend sa fin. Renvoie (code, sortie).</summary>
    public static (int Code, string Output) Run(string exe, string args, int timeoutMs = 30 * 60 * 1000)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            var sb = new StringBuilder();
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "délai dépassé"); }
            p.WaitForExit();
            return (p.ExitCode, sb.ToString());
        }
        catch (Exception ex) { Log.Error($"Lancement de {exe} {args}", ex); return (-1, ex.Message); }
    }

    public static string PowerShell(string script, int timeoutMs = 120000)
    {
        var enc = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script));
        return Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc, timeoutMs).Output;
    }

    public static void OpenInExplorer(string path)
    {
        try
        {
            if (System.IO.File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
        }
        catch (Exception ex) { Log.Error("Ouverture de l'Explorateur", ex); }
    }

    public static void OpenUrlOrFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    // ---- Corbeille (SHFileOperation avec annulation possible)
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags;
        public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string lpszProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    const uint FO_DELETE = 3; const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

    public static bool SendToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT { wFunc = FO_DELETE, pFrom = path + "\0\0", fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);
    public static void EmptyRecycleBin() { try { SHEmptyRecycleBin(IntPtr.Zero, null, 0x1 | 0x2 | 0x4); } catch { } }

    /// <summary>Chemins qu'on refuse de supprimer ou déplacer depuis l'appli (sécurité).</summary>
    public static bool IsProtected(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        var p = Path.GetFullPath(path).TrimEnd('\\');
        if (p.Length <= 3) return true;   // racine d'un disque
        string[] exact = { AppPaths.Windows, AppPaths.ProgramFiles, AppPaths.ProgramFilesX86, AppPaths.ProgramData, AppPaths.Profile,
            Path.Combine(AppPaths.SystemDrive, "Users"), Path.Combine(AppPaths.Profile, "AppData"), AppPaths.Local, AppPaths.Roaming,
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Environment.GetFolderPath(Environment.SpecialFolder.Desktop) };
        foreach (var e in exact) if (!string.IsNullOrEmpty(e) && string.Equals(p, e.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
        if (p.StartsWith(AppPaths.Windows + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        var name = Path.GetFileName(p).ToLowerInvariant();
        if (name is "pagefile.sys" or "hiberfil.sys" or "swapfile.sys" or "$recycle.bin" or "system volume information") return true;
        return false;
    }
}
