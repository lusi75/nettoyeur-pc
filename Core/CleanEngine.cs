using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NettoyeurPC.Core;

public enum Risk { Safe, Check, Advanced }

public enum TargetKind { Content, File, Dir }

/// <summary>Un emplacement à nettoyer : contenu d'un dossier, fichier(s) précis, ou dossier entier.</summary>
public record Target(string Path, TargetKind Kind = TargetKind.Content, int Days = 0, string[] Filters = null, bool Recurse = true)
{
    public string[] Patterns => Filters ?? new[] { "*" };
    public bool IsPlain => Days == 0 && Patterns.Length == 1 && Patterns[0] == "*" && Recurse;
}

public class CleanContext
{
    public string Batch;
    public HistoryEvent Event;
    public Action<string> Status = _ => { };
    public CancellationToken Token;
}

public class Rule
{
    public string Id, Category, Name, What, After, Where;
    public Risk Risk;
    public bool Default;
    public bool UseQuarantine;      // les fichiers partent en quarantaine au lieu d'être supprimés
    public bool Irreversible;       // ne peut pas être annulé depuis l'Historique
    public bool Slow;               // opération longue (DISM, compression...)
    public Func<IEnumerable<Target>> Targets = () => Array.Empty<Target>();
    public Func<Rule, long> CustomScan;              // -2 = taille inconnue avant l'opération
    public Func<Rule, CleanContext, long> CustomApply;
    public object State;

    public long Size = -1;
    public bool Checked;

    public string RiskLabel => Risk switch { Risk.Safe => "Sans risque", Risk.Check => "À vérifier", _ => "Avancé" };
}

public static class CleanEngine
{
    static readonly EnumerationOptions Rec = new() { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MatchType = MatchType.Win32 };
    static readonly EnumerationOptions Flat = new() { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint, MatchType = MatchType.Win32 };

    public static IEnumerable<FileInfo> Files(Target t)
    {
        var limit = DateTime.Now.AddDays(-t.Days);
        if (t.Kind == TargetKind.File)
        {
            var dir = System.IO.Path.GetDirectoryName(t.Path); var pat = System.IO.Path.GetFileName(t.Path);
            if (dir == null || !Directory.Exists(dir)) yield break;
            IEnumerable<FileInfo> fs;
            try { fs = new DirectoryInfo(dir).EnumerateFiles(pat, Flat).ToList(); } catch { yield break; }
            foreach (var f in fs) if (t.Days == 0 || f.LastWriteTime < limit) yield return f;
            yield break;
        }
        if (!Directory.Exists(t.Path)) yield break;
        foreach (var pat in t.Patterns)
        {
            IEnumerable<FileInfo> fs;
            try { fs = new DirectoryInfo(t.Path).EnumerateFiles(pat, t.Recurse ? Rec : Flat); } catch { continue; }
            using var en = fs.GetEnumerator();
            while (true)
            {
                FileInfo f;
                try { if (!en.MoveNext()) break; f = en.Current; } catch { break; }
                if (t.Days == 0 || f.LastWriteTime < limit) yield return f;
            }
        }
    }

    public static long Measure(Target t)
    {
        long s = 0;
        foreach (var f in Files(t)) { try { s += f.Length; } catch { } }
        return s;
    }

    public static long Measure(Rule r)
    {
        try
        {
            if (r.CustomScan != null) return r.CustomScan(r);
            long s = 0;
            foreach (var t in r.Targets()) s += Measure(t);
            return s;
        }
        catch (Exception ex) { Log.Warn($"Analyse « {r.Name} » : {ex.Message}"); return 0; }
    }

    public static long Clear(Target t, CleanContext ctx, bool quarantine)
    {
        long freed = 0; int n = 0;
        foreach (var f in Files(t).ToList())
        {
            if (ctx.Token.IsCancellationRequested) break;
            long len = 0;
            try { len = f.Length; } catch { }
            if (quarantine)
            {
                var q = Quarantine.Put(ctx.Batch, f.FullName);
                if (q != null) { ctx.Event.Items.Add(q); freed += q.Size; }
            }
            else
            {
                try
                {
                    if (f.IsReadOnly) f.IsReadOnly = false;
                    f.Delete(); freed += len;
                }
                catch { }
            }
            if (++n % 500 == 0) ctx.Status($"{Fmt.Num(n)} fichiers traités");
        }
        if (t.Kind != TargetKind.File && t.IsPlain && Directory.Exists(t.Path))
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(t.Path, "*", Rec).OrderByDescending(x => x.Length))
                    try { Directory.Delete(d, false); } catch { }
                if (t.Kind == TargetKind.Dir) try { Directory.Delete(t.Path, false); } catch { }
            }
            catch { }
        }
        return freed;
    }

    public static long Apply(Rule r, CleanContext ctx)
    {
        try
        {
            if (r.CustomApply != null) return r.CustomApply(r, ctx);
            long s = 0;
            foreach (var t in r.Targets()) s += Clear(t, ctx, r.UseQuarantine);
            return s;
        }
        catch (Exception ex) { Log.Error($"Nettoyage « {r.Name} »", ex); return 0; }
    }

    public static long FreeOn(string root) { try { return new DriveInfo(root).AvailableFreeSpace; } catch { return 0; } }
}

/// <summary>Catalogue de toutes les règles de nettoyage.</summary>
public static class Rules
{
    static string L => AppPaths.Local;
    static string R => AppPaths.Roaming;
    static string PD => AppPaths.ProgramData;
    static string W => AppPaths.Windows;
    static string UP => AppPaths.Profile;
    static string PF86 => AppPaths.ProgramFilesX86;
    static string Sys => AppPaths.SystemDrive;

    static Target T(string p, TargetKind k = TargetKind.Content, int days = 0, string[] f = null, bool rec = true) => new(p, k, days, f, rec);

    static IEnumerable<Target> Chromium(string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        foreach (var n in new[] { "GrShaderCache", "ShaderCache", "GraphiteDawnCache" }) yield return T(Path.Combine(userData, n));
        IEnumerable<string> profiles;
        try { profiles = Directory.EnumerateDirectories(userData).Where(d => { var n = Path.GetFileName(d); return n == "Default" || n.StartsWith("Profile ") || n == "Guest Profile"; }).ToList(); }
        catch { yield break; }
        foreach (var p in profiles)
            foreach (var n in new[] { "Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache" })
                yield return T(Path.Combine(p, n));
    }

    static IEnumerable<string> SteamLibraries()
    {
        var steam = Path.Combine(PF86, "Steam");
        var vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
        var libs = new List<string>();
        try
        {
            if (File.Exists(vdf))
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                    libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
        }
        catch { }
        if (libs.Count == 0 && Directory.Exists(steam)) libs.Add(steam);
        return libs.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    static long FreeDelta(Action a)
    {
        var before = CleanEngine.FreeOn(Sys);
        a();
        return Math.Max(0, CleanEngine.FreeOn(Sys) - before);
    }

    public static List<Rule> All()
    {
        var list = new List<Rule>();
        void Add(string id, string cat, string name, Risk risk, bool def, string what, string after, Func<IEnumerable<Target>> targets, string where = null)
            => list.Add(new Rule { Id = id, Category = cat, Name = name, Risk = risk, Default = def, What = what, After = after, Targets = targets, Where = where });

        // ------------------------------------------------------------ Windows
        Add("tmp_user", "Windows", "Fichiers temporaires", Risk.Safe, true,
            "Fichiers laissés par les installations et les applis, vieux de plus de 24 h.",
            "Aucun effet. Les fichiers en cours d'utilisation sont ignorés.",
            () => new[] { T(AppPaths.Temp, days: 1) }, "%TEMP%");
        Add("tmp_win", "Windows", "Fichiers temporaires de Windows", Risk.Safe, true,
            "Fichiers temporaires du système, vieux de plus de 24 h.",
            "Aucun effet. Les fichiers en cours d'utilisation sont ignorés.",
            () => new[] { T(Path.Combine(W, "Temp"), days: 1) }, @"C:\Windows\Temp");
        Add("wu", "Windows", "Cache de Windows Update", Risk.Safe, true,
            "Mises à jour déjà installées que Windows garde au cas où.",
            "Aucun effet sur les mises à jour installées. Le service est arrêté le temps du nettoyage.",
            () => new[] { T(Path.Combine(W, @"SoftwareDistribution\Download")) }, @"C:\Windows\SoftwareDistribution\Download");
        list[^1].CustomApply = (r, ctx) =>
        {
            Shell.Run("net.exe", "stop wuauserv /y"); Shell.Run("net.exe", "stop bits /y");
            long f = 0; foreach (var t in r.Targets()) f += CleanEngine.Clear(t, ctx, false);
            Shell.Run("net.exe", "start bits"); Shell.Run("net.exe", "start wuauserv");
            return f;
        };
        Add("do", "Windows", "Cache d'optimisation de distribution", Risk.Safe, true,
            "Morceaux de mises à jour partagés entre ordinateurs.",
            "Aucun effet.",
            () => new[] { T(Path.Combine(W, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache")) });
        list[^1].CustomApply = (r, ctx) => FreeDelta(() => Shell.PowerShell("Delete-DeliveryOptimizationCache -Force -ErrorAction SilentlyContinue"));
        Add("wer", "Windows", "Rapports d'erreurs Windows", Risk.Safe, true,
            "Rapports de plantage déjà envoyés ou jamais lus.",
            "Aucun effet.",
            () => new[] { T(Path.Combine(PD, @"Microsoft\Windows\WER\ReportArchive")), T(Path.Combine(PD, @"Microsoft\Windows\WER\ReportQueue")), T(Path.Combine(PD, @"Microsoft\Windows\WER\Temp")), T(Path.Combine(L, @"Microsoft\Windows\WER")) },
            @"C:\ProgramData\Microsoft\Windows\WER");
        Add("dumps", "Windows", "Fichiers de plantage (dumps)", Risk.Safe, true,
            "Copies de la mémoire enregistrées lors de plantages d'applis ou de Windows.",
            "Aucun effet, sauf si tu dois envoyer un rapport de plantage à un support technique.",
            () => new[] { T(Path.Combine(L, "CrashDumps")), T(Path.Combine(W, "Minidump")), T(Path.Combine(W, "LiveKernelReports")), T(Path.Combine(W, "MEMORY.DMP"), TargetKind.File) });
        Add("logs", "Windows", "Journaux de Windows (plus de 7 jours)", Risk.Safe, true,
            "Journaux d'installation et de maintenance (CBS, DISM, Windows Update).",
            "Aucun effet.",
            () => new[] { T(Path.Combine(W, "Logs"), days: 7, f: new[] { "*.log", "*.cab", "*.etl", "*.old" }), T(Path.Combine(W, "Panther"), days: 7, f: new[] { "*.log" }), T(Path.Combine(W, @"SoftwareDistribution\DataStore\Logs"), days: 7) });
        Add("thumbs", "Windows", "Cache des miniatures", Risk.Safe, true,
            "Aperçus d'images affichés dans l'Explorateur.",
            "Les miniatures se recréent quand tu ouvres un dossier.",
            () => new[] { T(Path.Combine(L, @"Microsoft\Windows\Explorer"), f: new[] { "thumbcache_*.db" }, rec: false) });
        Add("d3d", "Windows", "Cache des shaders DirectX", Risk.Safe, true,
            "Shaders préparés par Windows pour les jeux et les applis.",
            "Se recrée automatiquement.",
            () => new[] { T(Path.Combine(L, "D3DSCache")) });
        Add("recycle", "Windows", "Corbeille (tous les disques)", Risk.Check, true,
            "Fichiers que tu as mis à la corbeille sur tous les disques.",
            "Suppression définitive : vérifie qu'elle ne contient rien d'utile.",
            () => Array.Empty<Target>());
        list[^1].Irreversible = true;
        list[^1].CustomScan = r =>
        {
            long s = 0;
            foreach (var d in DriveInfo.GetDrives())
                try { if (d.DriveType == DriveType.Fixed && d.IsReady) s += Quarantine.DirSize(Path.Combine(d.Name, "$Recycle.Bin")); } catch { }
            return s;
        };
        list[^1].CustomApply = (r, ctx) => { var s = r.Size; Shell.EmptyRecycleBin(); return Math.Max(0, s); };
        Add("winold", "Windows", "Anciennes installations de Windows", Risk.Safe, true,
            "Windows.old et les dossiers laissés par une mise à niveau de Windows.",
            "Tu ne pourras plus revenir à l'ancienne version de Windows.",
            () => new[] { T(Path.Combine(Sys, "Windows.old"), TargetKind.Dir), T(Path.Combine(Sys, "$Windows.~BT"), TargetKind.Dir), T(Path.Combine(Sys, "$Windows.~WS"), TargetKind.Dir) });
        list[^1].Irreversible = true;
        list[^1].CustomApply = (r, ctx) => FreeDelta(() =>
        {
            foreach (var p in new[] { "Windows.old", "$Windows.~BT", "$Windows.~WS" }.Select(n => Path.Combine(Sys, n)).Where(Directory.Exists))
            {
                Shell.Run("takeown.exe", $"/F \"{p}\" /R /A /D O");
                Shell.Run("icacls.exe", $"\"{p}\" /grant *S-1-5-32-544:F /T /C /Q");
                try { Directory.Delete(p, true); } catch (Exception ex) { Log.Warn($"Suppression de {p} : {ex.Message}"); }
            }
        });
        Add("patchcache", "Windows", "Cache des correctifs Windows Installer", Risk.Check, false,
            "Copies de référence des correctifs de logiciels installés. Rarement utilisées.",
            "Les fichiers partent en quarantaine. Un logiciel pourrait en avoir besoin pour se réparer.",
            () => new[] { T(Path.Combine(W, @"Installer\$PatchCache$")) });
        list[^1].UseQuarantine = true;
        Add("evtlogs", "Windows", "Journaux d'événements", Risk.Advanced, false,
            "Historique de l'Observateur d'événements.",
            "Supprime l'historique utile pour diagnostiquer un problème. Déconseillé.",
            () => new[] { T(Path.Combine(W, @"System32\winevt\Logs"), f: new[] { "*.evtx" }) });
        list[^1].Irreversible = true;
        list[^1].CustomApply = (r, ctx) =>
        {
            var before = CleanEngine.Measure(r.Targets().First());
            foreach (var lg in Shell.Run("wevtutil.exe", "el").Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Shell.Run("wevtutil.exe", $"cl \"{lg}\"");
            return Math.Max(0, before - CleanEngine.Measure(r.Targets().First()));
        };

        // ------------------------------------------------------------ Carte graphique
        Add("gpu", "Carte graphique", "Cache de shaders NVIDIA / AMD", Risk.Safe, true,
            "Les jeux préparent des « shaders » pour s'afficher plus vite et les gardent ici. Le dossier grossit à chaque mise à jour de pilote, même pour des jeux désinstallés.",
            "Se reconstruit tout seul. Le premier lancement de chaque jeu peut être un peu plus long.",
            () => new[] { T(Path.Combine(L, @"NVIDIA\DXCache")), T(Path.Combine(L, @"NVIDIA\GLCache")), T(Path.Combine(L, @"NVIDIA\OptixCache")), T(Path.Combine(PD, @"NVIDIA Corporation\NV_Cache")), T(Path.Combine(L, @"AMD\DxCache")), T(Path.Combine(L, @"AMD\DxcCache")), T(Path.Combine(L, @"AMD\GLCache")), T(Path.Combine(L, @"AMD\VkCache")) },
            @"%LOCALAPPDATA%\NVIDIA\DXCache");
        Add("gpuinst", "Carte graphique", "Installeurs de pilotes déjà utilisés", Risk.Safe, true,
            "Copies des pilotes graphiques déjà installés, gardées par l'appli NVIDIA ou AMD.",
            "Aucun effet : les pilotes restent installés.",
            () => new[] { T(Path.Combine(PD, @"NVIDIA Corporation\NVIDIA app\UpdateFramework\ota-artifacts")), T(Path.Combine(PD, @"NVIDIA Corporation\Downloader")), T(Path.Combine(Sys, "NVIDIA"), TargetKind.Dir), T(Path.Combine(Sys, "AMD"), TargetKind.Dir) });
        Add("steamshader", "Carte graphique", "Cache de shaders Steam", Risk.Check, false,
            "Shaders précompilés des jeux Steam, sur toutes tes bibliothèques.",
            "Recréés au lancement des jeux : les premières minutes peuvent être moins fluides.",
            () => SteamLibraries().Select(l => T(Path.Combine(l, @"steamapps\shadercache"))).ToList());

        // ------------------------------------------------------------ Navigateurs
        Add("chrome", "Navigateurs", "Google Chrome – cache", Risk.Safe, true,
            "Images et scripts des sites déjà visités. Ni l'historique, ni les mots de passe, ni les connexions.",
            "Les sites se rechargent un peu plus lentement la première fois. Si Chrome est ouvert, les fichiers utilisés sont ignorés.",
            () => Chromium(Path.Combine(L, @"Google\Chrome\User Data")).ToList(), @"%LOCALAPPDATA%\Google\Chrome\User Data");
        Add("chromeai", "Navigateurs", "Google Chrome – modèle d'IA local", Risk.Safe, true,
            "Modèle Gemini Nano (weights.bin, environ 4 Go) téléchargé par Chrome pour ses fonctions d'IA.",
            "Une règle empêche aussi Chrome de le retélécharger. Les fonctions d'IA locales de Chrome ne seront plus disponibles.",
            () => new[] { T(Path.Combine(L, @"Google\Chrome\User Data\OptGuideOnDeviceModel")) });
        list[^1].CustomApply = (r, ctx) =>
        {
            try { using var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Google\Chrome"); k.SetValue("GenAILocalFoundationalModelSettings", 1, RegistryValueKind.DWord); } catch { }
            long f = 0; foreach (var t in r.Targets()) f += CleanEngine.Clear(t, ctx, false); return f;
        };
        Add("edge", "Navigateurs", "Microsoft Edge – cache", Risk.Safe, true,
            "Cache web d'Edge. Ni l'historique, ni les mots de passe.", "Aucun effet sur tes données.",
            () => Chromium(Path.Combine(L, @"Microsoft\Edge\User Data")).ToList());
        Add("edgeai", "Navigateurs", "Microsoft Edge – modèle d'IA local", Risk.Safe, true,
            "Modèle d'IA téléchargé par Edge.", "Edge peut le retélécharger s'il en a besoin.",
            () => new[] { T(Path.Combine(L, @"Microsoft\Edge\User Data\EdgeLLMOnDeviceModel")), T(Path.Combine(L, @"Microsoft\Edge\User Data\OptGuideOnDeviceModel")) });
        Add("brave", "Navigateurs", "Brave – cache", Risk.Safe, true, "Cache web de Brave.", "Aucun effet sur tes données.",
            () => Chromium(Path.Combine(L, @"BraveSoftware\Brave-Browser\User Data")).ToList());
        Add("vivaldi", "Navigateurs", "Vivaldi – cache", Risk.Safe, true, "Cache web de Vivaldi.", "Aucun effet sur tes données.",
            () => Chromium(Path.Combine(L, @"Vivaldi\User Data")).ToList());
        Add("comet", "Navigateurs", "Comet (Perplexity) – cache", Risk.Safe, true, "Cache web de Comet.", "Aucun effet sur tes données.",
            () => Chromium(Path.Combine(L, @"Perplexity\Comet\User Data")).ToList());
        Add("opera", "Navigateurs", "Opera – cache", Risk.Safe, true, "Cache web d'Opera et Opera GX.", "Aucun effet sur tes données.",
            () => new[] { "Opera Stable", "Opera GX Stable" }.SelectMany(n => new[] { T(Path.Combine(L, "Opera Software", n, "Cache")), T(Path.Combine(R, "Opera Software", n, "Code Cache")), T(Path.Combine(R, "Opera Software", n, "GPUCache")) }).ToList());
        Add("firefox", "Navigateurs", "Firefox – cache", Risk.Safe, true, "Cache web de Firefox. Ni l'historique, ni les mots de passe.", "Aucun effet sur tes données.",
            () =>
            {
                var o = new List<Target>();
                try { foreach (var p in Directory.EnumerateDirectories(Path.Combine(L, @"Mozilla\Firefox\Profiles"))) { o.Add(T(Path.Combine(p, "cache2"))); o.Add(T(Path.Combine(p, "startupCache"))); o.Add(T(Path.Combine(p, "thumbnails"))); } } catch { }
                try { foreach (var p in Directory.EnumerateDirectories(Path.Combine(R, @"Mozilla\Firefox\Profiles"))) o.Add(T(Path.Combine(p, "shader-cache"))); } catch { }
                return o;
            });

        // ------------------------------------------------------------ Applications
        Add("discord", "Applications", "Discord – cache", Risk.Safe, true, "Images et vidéos des discussions déjà vues.", "Les images se rechargent quand tu remontes dans une discussion.",
            () => new[] { T(Path.Combine(R, @"discord\Cache")), T(Path.Combine(R, @"discord\Code Cache")), T(Path.Combine(R, @"discord\GPUCache")), T(Path.Combine(R, @"discordptb\Cache")), T(Path.Combine(R, @"discordcanary\Cache")) });
        Add("spotify", "Applications", "Spotify – cache", Risk.Safe, true, "Musique gardée en cache par Spotify.", "Les titres se rechargent depuis Internet.",
            () => new[] { T(Path.Combine(L, @"Spotify\Storage")), T(Path.Combine(L, @"Spotify\Data")), T(Path.Combine(L, @"Spotify\Browser\Cache")) });
        Add("steam", "Applications", "Steam – cache web et journaux", Risk.Safe, true, "Cache du navigateur intégré de Steam, journaux et dumps.", "Le magasin Steam se recharge un peu plus lentement la première fois.",
            () => new[] { T(Path.Combine(L, @"Steam\htmlcache")), T(Path.Combine(PF86, @"Steam\appcache\httpcache")), T(Path.Combine(PF86, @"Steam\logs")), T(Path.Combine(PF86, @"Steam\dumps")) });
        Add("bnet", "Applications", "Battle.net – cache", Risk.Safe, true, "Cache du launcher Blizzard.", "Le launcher recharge sa page d'accueil.",
            () => new[] { T(Path.Combine(L, @"Battle.net\Cache")), T(Path.Combine(L, @"Battle.net\BrowserCache")), T(Path.Combine(L, @"Blizzard Entertainment\Battle.net\Cache")), T(Path.Combine(L, @"Battle.net\Logs")), T(Path.Combine(PD, @"Battle.net\Agent\Logs")) });
        Add("epic", "Applications", "Epic Games – cache et journaux", Risk.Safe, true, "Cache web du launcher Epic.", "Aucun effet sur tes jeux.",
            () => { var o = new List<Target> { T(Path.Combine(L, @"EpicGamesLauncher\Saved\Logs")) }; try { o.AddRange(Directory.EnumerateDirectories(Path.Combine(L, @"EpicGamesLauncher\Saved"), "webcache*").Select(d => T(d))); } catch { } return o; });
        Add("ea", "Applications", "EA app – cache", Risk.Safe, true, "Cache de l'appli EA et de son anti-triche.", "Aucun effet sur tes jeux.",
            () => new[] { T(Path.Combine(L, @"Electronic Arts\EA Desktop\cache")), T(Path.Combine(L, @"EADesktop\cache")), T(Path.Combine(R, @"EA\AC\Cache")), T(Path.Combine(L, @"Electronic Arts\EA Desktop\Logs")) });
        Add("ubi", "Applications", "Ubisoft Connect – cache et journaux", Risk.Safe, true, "Cache HTTP et journaux du launcher Ubisoft.", "Aucun effet sur tes jeux.",
            () => new[] { T(Path.Combine(PF86, @"Ubisoft\Ubisoft Game Launcher\cache\http")), T(Path.Combine(PF86, @"Ubisoft\Ubisoft Game Launcher\logs")), T(Path.Combine(L, @"Ubisoft Game Launcher\cache")) });
        Add("teams", "Applications", "Microsoft Teams – cache", Risk.Safe, true, "Cache de Teams.", "Teams se recharge un peu plus lentement la première fois.",
            () =>
            {
                var o = new List<Target> { T(Path.Combine(R, @"Microsoft\Teams\Cache")), T(Path.Combine(R, @"Microsoft\Teams\Code Cache")), T(Path.Combine(R, @"Microsoft\Teams\GPUCache")) };
                try { o.AddRange(Directory.EnumerateDirectories(Path.Combine(L, "Packages"), "MSTeams_*").Select(d => T(Path.Combine(d, @"LocalCache\Microsoft\MSTeams\EBWebView\Default\Cache")))); } catch { }
                return o;
            });
        Add("vscode", "Applications", "Visual Studio Code – caches", Risk.Safe, true, "Caches et paquets d'extensions déjà installées.", "Aucun effet sur tes projets ni tes extensions.",
            () => new[] { "Cache", "CachedData", "CachedExtensionVSIXs", "Code Cache", "GPUCache", "logs" }.Select(n => T(Path.Combine(R, "Code", n))).ToList());
        Add("updaters", "Applications", "Installeurs de mises à jour d'applis", Risk.Safe, true, "Installeurs de mises à jour déjà appliquées (dossiers « -updater »).", "Aucun effet.",
            () => { try { return Directory.EnumerateDirectories(L, "*-updater").Select(d => T(d)).ToList(); } catch { return new List<Target>(); } });
        Add("squirrel", "Applications", "Anciens paquets de mise à jour (.nupkg)", Risk.Check, false, "Archives de mises à jour déjà installées (Discord, Postman, Teams...).", "Les fichiers partent en quarantaine. La prochaine mise à jour sera un peu plus longue à télécharger.",
            () => { try { return Directory.EnumerateDirectories(L).Where(d => Directory.Exists(Path.Combine(d, "packages"))).Select(d => T(Path.Combine(d, "packages"), f: new[] { "*.nupkg" }, rec: false)).ToList(); } catch { return new List<Target>(); } });
        list[^1].UseQuarantine = true;
        Add("applogs", "Applications", "Journaux d'applis volumineux", Risk.Safe, true, "Journaux de SteelSeries GG, Logitech G HUB, NVIDIA app...", "Aucun effet.",
            () => new[] { T(Path.Combine(PD, @"SteelSeries\GG\Logs"), f: new[] { "*.txt", "*.log" }), T(Path.Combine(PD, @"LGHUB\logs"), days: 2), T(Path.Combine(L, @"NVIDIA Corporation\NVIDIA app\logs"), days: 2), T(Path.Combine(PD, @"NVIDIA Corporation\NVIDIA app\logs"), days: 2) });

        // ------------------------------------------------------------ Développement
        Add("npm", "Développement", "Cache npm", Risk.Safe, false, "Paquets npm téléchargés.", "Retéléchargés à la prochaine installation.", () => new[] { T(Path.Combine(L, "npm-cache")) });
        Add("pip", "Développement", "Cache pip (Python)", Risk.Safe, false, "Paquets Python téléchargés.", "Retéléchargés à la prochaine installation.", () => new[] { T(Path.Combine(L, @"pip\cache")) });
        Add("nuget", "Développement", "Cache NuGet (.NET)", Risk.Safe, false, "Paquets NuGet téléchargés.", "Retéléchargés à la prochaine compilation.", () => new[] { T(Path.Combine(L, @"NuGet\v3-cache")), T(Path.Combine(L, @"NuGet\http-cache")), T(Path.Combine(L, @"NuGet\plugins-cache")) });
        Add("gradle", "Développement", "Caches Gradle / Maven", Risk.Check, false, "Dépendances Java téléchargées.", "Retéléchargées à la prochaine compilation (peut être long).", () => new[] { T(Path.Combine(UP, @".gradle\caches")), T(Path.Combine(UP, @".gradle\wrapper\dists")), T(Path.Combine(UP, @".m2\repository")) });

        // ------------------------------------------------------------ Fichiers personnels
        Add("oldinst", "Fichiers personnels", "Vieux installeurs dans Téléchargements", Risk.Check, false,
            "Fichiers .exe, .msi et .iso de plus de 30 jours dans Téléchargements.",
            "Les fichiers partent en quarantaine. Pour les garder ailleurs, utilise plutôt Stockage → Déplacer.",
            () => new[] { T(Path.Combine(UP, "Downloads"), days: 30, f: new[] { "*.exe", "*.msi", "*.iso", "*.msix", "*.appx" }, rec: false) });
        list[^1].UseQuarantine = true;

        // ------------------------------------------------------------ Système (avancé)
        var sysCat = "Système (avancé)";
        list.Add(new Rule
        {
            Id = "restorepoints", Category = sysCat, Name = "Anciens points de restauration", Risk = Risk.Check, Default = false, Irreversible = true,
            What = "Garde uniquement le point de restauration le plus récent.", After = "Tu ne pourras plus revenir à une date plus ancienne.", Where = "Protection du système – C:",
            CustomScan = r =>
            {
                var o = Shell.PowerShell("$s=@(Get-CimInstance Win32_ShadowCopy -EA SilentlyContinue).Count; $u=(Get-CimInstance Win32_ShadowStorage -EA SilentlyContinue | Measure-Object UsedSpace -Sum).Sum; \"$s|$u\"").Trim();
                var p = o.Split('|'); int n = int.TryParse(p[0], out var a) ? a : 0; long used = p.Length > 1 && long.TryParse(p[1], out var b) ? b : 0;
                return n <= 1 ? 0 : used * (n - 1) / n;
            },
            CustomApply = (r, ctx) => FreeDelta(() => Shell.PowerShell("$s=@(Get-CimInstance Win32_ShadowCopy | Sort-Object InstallDate); if($s.Count -gt 1){ $s | Select-Object -First ($s.Count-1) | Remove-CimInstance }")),
        });
        list.Add(new Rule
        {
            Id = "hiber", Category = sysCat, Name = "Désactiver l'hibernation", Risk = Risk.Check, Default = false,
            What = "Supprime hiberfil.sys (souvent plusieurs Go). Désactive aussi le « démarrage rapide », souvent source de bugs.", After = "La mise en veille normale reste disponible. Réversible depuis l'Historique.", Where = @"C:\hiberfil.sys",
            CustomScan = r => { try { var f = new FileInfo(Path.Combine(Sys, "hiberfil.sys")); return f.Exists ? f.Length : 0; } catch { return 0; } },
            CustomApply = (r, ctx) => { var s = r.Size; Shell.Run("powercfg.exe", "/h off"); ctx.Event.UndoCommands.Add("powercfg.exe|/h on"); return Math.Max(0, s); },
        });
        list.Add(new Rule
        {
            Id = "reserved", Category = sysCat, Name = "Stockage réservé de Windows", Risk = Risk.Check, Default = false,
            What = "Windows garde environ 7 Go de côté pour ses mises à jour. Avec plus de 20 % d'espace libre, ce n'est pas utile.", After = "Réversible depuis l'Historique.", Where = "Paramètre système (DISM)",
            CustomScan = r => { var o = Shell.Run("dism.exe", "/Online /Get-ReservedStorageState").Output; if (o.Contains("disabled", StringComparison.OrdinalIgnoreCase) || o.Contains("désactiv", StringComparison.OrdinalIgnoreCase)) return 0; return o.Contains("enabled", StringComparison.OrdinalIgnoreCase) || o.Contains("activé", StringComparison.OrdinalIgnoreCase) ? 7L << 30 : 0; },
            CustomApply = (r, ctx) => FreeDelta(() => { Shell.Run("dism.exe", "/Online /Set-ReservedStorageState /State:Disabled /Quiet"); ctx.Event.UndoCommands.Add("dism.exe|/Online /Set-ReservedStorageState /State:Enabled /Quiet"); }),
        });
        list.Add(new Rule
        {
            Id = "winsxs", Category = sysCat, Name = "Nettoyer les composants de Windows (WinSxS)", Risk = Risk.Safe, Default = false, Slow = true, Irreversible = true,
            What = "Supprime les anciennes versions des composants de Windows remplacées par des mises à jour.", After = "Aucun effet visible. Opération longue : 5 à 15 minutes.", Where = @"C:\Windows\WinSxS",
            CustomScan = r => -2,
            CustomApply = (r, ctx) => FreeDelta(() => Shell.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup /Quiet /NoRestart")),
        });
        list.Add(new Rule
        {
            Id = "drivers", Category = sysCat, Name = "Anciennes versions de pilotes", Risk = Risk.Check, Default = false, Irreversible = true,
            Slow = true, What = "Garde la version la plus récente de chaque pilote et supprime les anciennes copies (souvent plusieurs Go d'anciens pilotes graphiques). Les pilotes utilisés ne sont jamais supprimés.", After = "Tu ne pourras plus « revenir au pilote précédent » dans le Gestionnaire de périphériques. Gain mesuré pendant l'opération.", Where = @"C:\Windows\System32\DriverStore",
            CustomScan = r => -2,
            CustomApply = (r, ctx) =>
            {
                ctx.Status("recherche des anciennes versions (1 à 2 minutes)");
                var json = Shell.PowerShell(@"$d=@(Get-WindowsDriver -Online -EA SilentlyContinue); $c=@(); $d | Group-Object { '{0}|{1}|{2}' -f $_.ProviderName,$_.ClassName,(Split-Path $_.OriginalFileName -Leaf) } | ? { $_.Count -gt 1 } | % { $_.Group | Sort-Object @{e={ try{[version]$_.Version}catch{[version]'0.0'} }},Date -Descending | Select-Object -Skip 1 | % { $c += [pscustomobject]@{ Driver=$_.Driver; Dir=(Split-Path $_.OriginalFileName -Parent); Name=('{0} {1}' -f $_.ProviderName,$_.Version) } } }; ConvertTo-Json @($c) -Compress", 300000);
                var cands = new List<(string Driver, string Dir)>();
                try
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "[]" : json.Trim());
                    foreach (var e in doc.RootElement.EnumerateArray()) cands.Add((e.GetProperty("Driver").GetString(), e.GetProperty("Dir").GetString()));
                }
                catch { }
                return FreeDelta(() =>
                {
                    foreach (var c in cands)
                    {
                        var res = Shell.Run("pnputil.exe", $"/delete-driver {c.Driver}");
                        Log.Info($"Pilote {c.Driver} : {(res.Code == 0 ? "supprimé" : "conservé (utilisé)")}");
                    }
                });
            },
        });
        list.Add(new Rule
        {
            Id = "compactos", Category = sysCat, Name = "Compresser Windows (CompactOS)", Risk = Risk.Check, Default = false, Slow = true,
            What = "Compresse les fichiers de Windows : 2 à 4 Go gagnés, impact négligeable sur un processeur récent.", After = "Opération longue (10 à 20 minutes). Réversible depuis l'Historique.", Where = @"C:\Windows",
            CustomScan = r => -2,
            CustomApply = (r, ctx) => FreeDelta(() => { Shell.Run("compact.exe", "/CompactOS:always"); ctx.Event.UndoCommands.Add("compact.exe|/CompactOS:never"); }),
        });
        list.Add(new Rule
        {
            Id = "pagefile", Category = sysCat, Name = "Réduire le fichier d'échange à 8 Go", Risk = Risk.Check, Default = false,
            What = "Le fichier d'échange est la mémoire de secours de Windows. Avec 16 Go de RAM ou plus, 4 à 8 Go suffisent.", After = "Prend effet au prochain redémarrage. Réversible depuis l'Historique.", Where = @"C:\pagefile.sys",
            CustomScan = r =>
            {
                try
                {
                    var f = new FileInfo(Path.Combine(Sys, "pagefile.sys"));
                    if (!f.Exists) return 0;
                    var o = Shell.PowerShell("$cs=Get-CimInstance Win32_ComputerSystem; $p=Get-CimInstance Win32_PageFileSetting | ? { $_.Name -like 'C:*' }; \"$($cs.AutomaticManagedPagefile)|$($p.InitialSize)|$($p.MaximumSize)\"").Trim();
                    r.State = o;
                    var p = o.Split('|');
                    bool auto = p[0].Equals("True", StringComparison.OrdinalIgnoreCase);
                    int max = p.Length > 2 && int.TryParse(p[2], out var m) ? m : 0;
                    if (!auto && max > 0 && max <= 8192) return 0;
                    return Math.Max(0, f.Length - (8L << 30));
                }
                catch { return 0; }
            },
            CustomApply = (r, ctx) =>
            {
                var prev = (r.State as string ?? "True||").Split('|');
                Shell.PowerShell("$cs=Get-CimInstance Win32_ComputerSystem; if($cs.AutomaticManagedPagefile){ Set-CimInstance -InputObject $cs -Property @{AutomaticManagedPagefile=$false} }; $p=Get-CimInstance Win32_PageFileSetting | ? { $_.Name -like 'C:*' }; if($p){ Set-CimInstance -InputObject $p -Property @{InitialSize=[uint32]4096;MaximumSize=[uint32]8192} } else { New-CimInstance -ClassName Win32_PageFileSetting -Property @{Name='C:\\pagefile.sys';InitialSize=[uint32]4096;MaximumSize=[uint32]8192} | Out-Null }");
                var undo = prev[0].Equals("True", StringComparison.OrdinalIgnoreCase)
                    ? "$cs=Get-CimInstance Win32_ComputerSystem; Set-CimInstance -InputObject $cs -Property @{AutomaticManagedPagefile=$true}"
                    : $"$p=Get-CimInstance Win32_PageFileSetting | ? {{ $_.Name -like 'C:*' }}; Set-CimInstance -InputObject $p -Property @{{InitialSize=[uint32]{(prev.Length > 1 && prev[1] != "" ? prev[1] : "0")};MaximumSize=[uint32]{(prev.Length > 2 && prev[2] != "" ? prev[2] : "0")}}}";
                ctx.Event.UndoCommands.Add("powershell.exe|-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(undo)));
                return 0;
            },
        });

        foreach (var r in list) r.Checked = r.Default;
        return list;
    }

    public static readonly string[] CategoryOrder = { "Carte graphique", "Windows", "Navigateurs", "Applications", "Développement", "Fichiers personnels", "Système (avancé)" };
    public static readonly Dictionary<string, string> CategoryColor = new()
    {
        ["Carte graphique"] = "#3DD6B5", ["Windows"] = "#5B9DFF", ["Navigateurs"] = "#B48CFF", ["Applications"] = "#F5A524",
        ["Développement"] = "#E06AA8", ["Fichiers personnels"] = "#7EC8FF", ["Système (avancé)"] = "#8A93A6",
    };
}

/// <summary>Analyse et nettoyage complets, partagés entre l'interface et le mode automatique.</summary>
public class CleanSession
{
    public List<Rule> Rules { get; } = CoreRules();
    public bool Scanned { get; private set; }
    public DateTime ScanDate { get; private set; }
    public TimeSpan ScanDuration { get; private set; }

    static List<Rule> CoreRules() => NettoyeurPC.Core.Rules.All();

    public async Task ScanAsync(IProgress<string> progress, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int done = 0;
        await Task.Run(() =>
        {
            Parallel.ForEach(Rules, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, r =>
            {
                r.Size = CleanEngine.Measure(r);
                var n = Interlocked.Increment(ref done);
                progress?.Report($"Analyse… {n}/{Rules.Count} · {r.Name}");
            });
        }, ct);
        ScanDuration = sw.Elapsed; ScanDate = DateTime.Now; Scanned = true;
        Log.Info($"Analyse : {Fmt.Size(Rules.Where(r => r.Size > 0).Sum(r => r.Size))} récupérables en {sw.Elapsed.TotalSeconds:0.0} s");
    }

    public long Total => Rules.Where(r => r.Size > 0).Sum(r => r.Size);
    public long TotalSafe => Rules.Where(r => r.Size > 0 && r.Risk == Risk.Safe && r.Default).Sum(r => r.Size);

    public static void CreateRestorePoint()
    {
        try
        {
            using (var k = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore"))
                k.SetValue("SystemRestorePointCreationFrequency", 0, RegistryValueKind.DWord);
            Shell.PowerShell("Enable-ComputerRestore -Drive 'C:\\' -EA SilentlyContinue; Checkpoint-Computer -Description 'Nettoyeur PC' -RestorePointType MODIFY_SETTINGS -EA SilentlyContinue", 300000);
            Log.Info("Point de restauration demandé");
        }
        catch (Exception ex) { Log.Error("Point de restauration", ex); }
    }

    /// <summary>Nettoie les règles cochées. Renvoie l'événement d'historique créé.</summary>
    public async Task<HistoryEvent> CleanAsync(IEnumerable<Rule> selected, bool restorePoint, IProgress<string> progress, CancellationToken ct = default)
    {
        var rules = selected.ToList();
        var ev = new HistoryEvent { Title = $"Nettoyage : {rules.Count} élément{(rules.Count > 1 ? "s" : "")}" };
        await Task.Run(() =>
        {
            if (restorePoint) { progress?.Report("Création d'un point de restauration…"); CreateRestorePoint(); }
            var ctx = new CleanContext { Event = ev, Token = ct };
            long total = 0; int i = 0;
            foreach (var r in rules)
            {
                if (ct.IsCancellationRequested) break;
                i++;
                ctx.Status = s => progress?.Report($"{i}/{rules.Count} · {r.Name} · {s}");
                progress?.Report($"{i}/{rules.Count} · {r.Name}{(r.Slow ? " (opération longue)" : "")}");
                if (r.UseQuarantine && ctx.Batch == null) { ctx.Batch = Quarantine.NewBatch(AppPaths.SystemDrive); ev.Batch = ctx.Batch; }
                var freed = CleanEngine.Apply(r, ctx);
                total += freed;
                Log.Info($"Nettoyé « {r.Name} » : {Fmt.Size(freed)}");
                r.Size = CleanEngine.Measure(r);
            }
            ev.Bytes = total;
            var q = ev.Items.Sum(x => x.Size);
            ev.Sub = q > 0 ? $"{Fmt.Size(total)} libérés, dont {Fmt.Size(q)} en quarantaine" : $"{Fmt.Size(total)} libérés";
            if (ev.Batch != null && ev.Items.Count == 0) { try { Directory.Delete(ev.Batch, true); } catch { } ev.Batch = null; }
        }, ct);
        History.Add(ev);
        Settings.Current.LastClean = DateTime.Now; Settings.Current.LastCleanBytes = ev.Bytes; Settings.Current.Save();
        return ev;
    }
}
