using System.Diagnostics;
using System.Text.RegularExpressions;

namespace NettoyeurPC.Core;

/// <summary>
/// Mode jeu : plan d'alimentation « Performances élevées », nettoyage automatique en pause,
/// fermeture des applis choisies. Tout est remis comme avant à la désactivation.
/// </summary>
public static class GameMode
{
    const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";
    const string ManagedName = "Nettoyeur PC - Mode jeu";
    static readonly Regex GuidRx = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");

    public static bool IsOn => Settings.Current.GameModeOn;

    public static string ActiveScheme()
    {
        var m = GuidRx.Match(Shell.Run("powercfg.exe", "/getactivescheme").Output);
        return m.Success ? m.Value.ToLowerInvariant() : "";
    }

    /// <summary>Trouve (ou crée) un plan hautes performances. Renvoie son GUID.</summary>
    static string PerformanceScheme()
    {
        var list = Shell.Run("powercfg.exe", "/list").Output;
        foreach (var line in list.Split('\n'))
        {
            var m = GuidRx.Match(line);
            if (!m.Success) continue;
            var g = m.Value.ToLowerInvariant();
            if (g == Ultimate || line.Contains(ManagedName)) return g;
        }
        foreach (var line in list.Split('\n')) { var m = GuidRx.Match(line); if (m.Success && m.Value.ToLowerInvariant() == HighPerf) return HighPerf; }
        // Plan masqué sur certains PC : on crée une copie du plan « Performances optimales » (ou « élevées »)
        foreach (var src in new[] { Ultimate, HighPerf })
        {
            var r = Shell.Run("powercfg.exe", "-duplicatescheme " + src);
            var m = GuidRx.Matches(r.Output).Select(x => x.Value.ToLowerInvariant()).FirstOrDefault(x => x != src);
            if (r.Code == 0 && m != null) { Shell.Run("powercfg.exe", $"-changename {m} \"{ManagedName}\""); return m; }
        }
        return null;
    }

    public static string SchemeName(string guid)
    {
        foreach (var line in Shell.Run("powercfg.exe", "/list").Output.Split('\n'))
            if (line.Contains(guid, StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(line, @"\((.+)\)");
                if (m.Success) return m.Groups[1].Value;
            }
        return guid;
    }

    /// <summary>Applis ouvertes avec une fenêtre (candidates à la fermeture).</summary>
    public static List<(string Name, string Title, string Exe)> RunningApps()
    {
        var self = Process.GetCurrentProcess().Id;
        var res = new List<(string, string, string)>();
        string[] never = { "explorer", "dwm", "steam", "steamwebhelper", "discord", "obs64", "obs", "battle.net", "epicgameslauncher", "nvcontainer", "nvidia share", "taskmgr", "sonar", "steelseriesgg", "steelseriesengine", "textinputhost", "applicationframehost", "systemsettings", "shellexperiencehost", "searchhost", "startmenuexperiencehost", "lockapp" };
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.MainWindowHandle == IntPtr.Zero || string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                var n = p.ProcessName;
                if (never.Contains(n.ToLowerInvariant())) continue;
                string exe = null; try { exe = p.MainModule?.FileName; } catch { }
                if (exe != null && exe.StartsWith(AppPaths.Windows, StringComparison.OrdinalIgnoreCase)) continue;
                if (res.Any(r => r.Item1.Equals(n, StringComparison.OrdinalIgnoreCase))) continue;
                res.Add((n, p.MainWindowTitle, exe));
            }
            catch { }
            finally { p.Dispose(); }
        }
        return res.OrderBy(r => r.Item1, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string Enable(List<string> closeApps, Action<string> status)
    {
        var s = Settings.Current;
        var report = new List<string>();
        // 1. Alimentation
        status?.Invoke("Plan d'alimentation…");
        var prev = ActiveScheme();
        var perf = PerformanceScheme();
        if (perf != null && perf != prev)
        {
            if (Shell.Run("powercfg.exe", "/setactive " + perf).Code == 0) { s.GameModePrevScheme = prev; report.Add("Alimentation : " + SchemeName(perf)); }
        }
        else if (perf == prev) { s.GameModePrevScheme = ""; report.Add("Alimentation : déjà en performances maximales"); }
        // 2. Nettoyage automatique en pause
        s.GameModeAutoWasOn = false;
        if (s.AutoClean && Shell.Run("schtasks.exe", "/change /tn NettoyeurPC_Automatique /disable").Code == 0) { s.GameModeAutoWasOn = true; report.Add("Nettoyage automatique en pause"); }
        // 3. Fermeture des applis choisies (proprement, puis de force après 5 s)
        s.GameModeApps = closeApps ?? new();
        s.GameModeClosed = new();
        int closed = 0;
        foreach (var name in s.GameModeApps)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    status?.Invoke("Fermeture de " + name + "…");
                    string exe = null; try { exe = p.MainModule?.FileName; } catch { }
                    if (p.MainWindowHandle != IntPtr.Zero) p.CloseMainWindow();
                    if (!p.WaitForExit(5000)) p.Kill(true);
                    if (exe != null && !s.GameModeClosed.Contains(exe, StringComparer.OrdinalIgnoreCase)) s.GameModeClosed.Add(exe);
                    closed++;
                }
                catch (Exception ex) { Log.Warn($"Mode jeu : fermeture de {name} impossible : {ex.Message}"); }
                finally { p.Dispose(); }
            }
        }
        if (closed > 0) report.Add($"{s.GameModeClosed.Count} appli(s) fermée(s), relancées à la sortie du mode jeu");
        s.GameModeOn = true;
        s.Save();
        Log.Info("Mode jeu activé : " + string.Join(" ; ", report));
        return string.Join("\n", report);
    }

    public static string Disable(Action<string> status)
    {
        var s = Settings.Current;
        var report = new List<string>();
        if (!string.IsNullOrEmpty(s.GameModePrevScheme))
        {
            status?.Invoke("Plan d'alimentation…");
            if (Shell.Run("powercfg.exe", "/setactive " + s.GameModePrevScheme).Code == 0) report.Add("Alimentation : " + SchemeName(s.GameModePrevScheme));
        }
        if (s.GameModeAutoWasOn && s.AutoClean) { Shell.Run("schtasks.exe", "/change /tn NettoyeurPC_Automatique /enable"); report.Add("Nettoyage automatique réactivé"); }
        int relaunched = 0;
        foreach (var exe in s.GameModeClosed)
        {
            try
            {
                if (!File.Exists(exe) || Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)).Length > 0) continue;
                status?.Invoke("Relance de " + Path.GetFileNameWithoutExtension(exe) + "…");
                Launcher.StartAsUser(exe);
                relaunched++;
            }
            catch (Exception ex) { Log.Warn($"Mode jeu : relance de {exe} impossible : {ex.Message}"); }
        }
        if (relaunched > 0) report.Add($"{relaunched} appli(s) relancée(s)");
        s.GameModeOn = false; s.GameModePrevScheme = ""; s.GameModeAutoWasOn = false; s.GameModeClosed = new();
        s.Save();
        Log.Info("Mode jeu désactivé : " + string.Join(" ; ", report));
        return report.Count == 0 ? "Tout est revenu comme avant." : string.Join("\n", report);
    }
}

/// <summary>Lance un programme sans les droits administrateur de l'appli (via l'Explorateur).</summary>
public static class Launcher
{
    public static void StartAsUser(string exe)
    {
        // explorer.exe lance le programme avec le jeton de l'utilisateur, pas celui (élevé) de l'appli
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{exe}\"") { UseShellExecute = true });
    }
}
