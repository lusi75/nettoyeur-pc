using System.Text.Json;

namespace NettoyeurPC.Core;

public class QItem
{
    public string Original { get; set; }
    public string Stored { get; set; }
    public long Size { get; set; }
}

/// <summary>Une action de l'appli, gardée dans l'historique et annulable si possible.</summary>
public class HistoryEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime Date { get; set; } = DateTime.Now;
    public string Title { get; set; }
    public string Sub { get; set; }
    public long Bytes { get; set; }
    public string Batch { get; set; }                       // dossier de quarantaine
    public List<QItem> Items { get; set; } = new();         // fichiers mis en quarantaine
    public List<string> UndoCommands { get; set; } = new(); // "exe|arguments"
    public bool Undone { get; set; }
    public bool Expired { get; set; }

    public bool CanUndo => !Undone && !Expired && (Items.Count > 0 || UndoCommands.Count > 0);
}

public static class History
{
    static readonly object Lock = new();
    static string FilePath => Path.Combine(AppPaths.Data, "historique.json");
    static List<HistoryEvent> _events;

    public static List<HistoryEvent> Events
    {
        get
        {
            lock (Lock)
            {
                if (_events != null) return _events;
                try { _events = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<HistoryEvent>>(File.ReadAllText(FilePath)) : null; }
                catch (Exception ex) { Log.Error("Lecture de l'historique", ex); }
                return _events ??= new List<HistoryEvent>();
            }
        }
    }

    public static void Add(HistoryEvent e)
    {
        lock (Lock) { Events.Insert(0, e); if (_events.Count > 300) _events.RemoveRange(300, _events.Count - 300); }
        Save();
        Log.Info($"Historique : {e.Title} — {e.Sub}");
    }

    public static void Save()
    {
        lock (Lock)
        {
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(Events, new JsonSerializerOptions { WriteIndented = true })); }
            catch (Exception ex) { Log.Error("Écriture de l'historique", ex); }
        }
    }

    /// <summary>Annule une action : remet les fichiers de quarantaine à leur place et/ou relance les commandes inverses.</summary>
    public static (int Ok, int Failed) Undo(HistoryEvent e)
    {
        int ok = 0, ko = 0;
        foreach (var it in e.Items)
        {
            try
            {
                if (!File.Exists(it.Stored) && !Directory.Exists(it.Stored)) { ko++; continue; }
                var target = it.Original;
                if (File.Exists(target) || Directory.Exists(target))
                    target = Path.Combine(Path.GetDirectoryName(target) ?? "", Path.GetFileNameWithoutExtension(target) + " (restauré)" + Path.GetExtension(target));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                Quarantine.MoveAny(it.Stored, target);
                ok++;
            }
            catch (Exception ex) { ko++; Log.Error($"Restauration de {it.Original}", ex); }
        }
        foreach (var c in e.UndoCommands)
        {
            var parts = c.Split('|', 2);
            var r = Shell.Run(parts[0], parts.Length > 1 ? parts[1] : "");
            if (r.Code == 0) ok++; else { ko++; Log.Warn($"Annulation : {c} → code {r.Code} {r.Output}"); }
        }
        if (!string.IsNullOrEmpty(e.Batch)) { try { if (Directory.Exists(e.Batch)) Directory.Delete(e.Batch, true); } catch { } }
        e.Undone = true;
        Save();
        Log.Info($"Annulation de « {e.Title} » : {ok} ok, {ko} échec(s)");
        return (ok, ko);
    }
}

/// <summary>
/// Quarantaine : les fichiers « à vérifier » ne sont pas supprimés tout de suite mais déplacés
/// sur le disque qui a le plus de place (autre que celui d'origine si possible), puis purgés après N jours.
/// </summary>
public static class Quarantine
{
    public const string FolderName = "NettoyeurPC_Quarantaine";

    public static string RootFor(string sourcePath)
    {
        var srcRoot = Path.GetPathRoot(sourcePath) ?? AppPaths.SystemDrive;
        var pref = Settings.Current.QuarantineDrive;
        DriveInfo best = null;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                if (!string.IsNullOrEmpty(pref) && d.Name.StartsWith(pref, StringComparison.OrdinalIgnoreCase)) { best = d; break; }
                if (string.Equals(d.Name, srcRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (best == null || d.AvailableFreeSpace > best.AvailableFreeSpace) best = d;
            }
            catch { }
        }
        var root = Path.Combine(best?.Name ?? srcRoot, FolderName);
        Directory.CreateDirectory(root);
        try
        {
            var readme = Path.Combine(root, "LISEZ-MOI.txt");
            if (!File.Exists(readme))
                File.WriteAllText(readme, "Dossier de quarantaine de Nettoyeur PC.\r\nLes fichiers sont restaurables depuis l'onglet Historique, puis supprimés automatiquement après quelques jours.\r\n");
        }
        catch { }
        return root;
    }

    public static string NewBatch(string sourceHint)
    {
        var dir = Path.Combine(RootFor(sourceHint), DateTime.Now.ToString("yyyy-MM-dd_HHmmss"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Déplace un fichier dans le lot de quarantaine en gardant son chemin d'origine. Renvoie null si impossible (fichier utilisé...).</summary>
    public static QItem Put(string batch, string file)
    {
        try
        {
            var fi = new FileInfo(file);
            if (!fi.Exists) return null;
            var root = Path.GetPathRoot(file)!;
            var stored = Path.Combine(batch, root.TrimEnd('\\', ':'), file.Substring(root.Length));
            Directory.CreateDirectory(Path.GetDirectoryName(stored)!);
            long len = fi.Length;
            MoveAny(file, stored);
            return new QItem { Original = file, Stored = stored, Size = len };
        }
        catch (Exception ex) { Log.Warn($"Quarantaine impossible pour {file} : {ex.Message}"); return null; }
    }

    /// <summary>Déplace un fichier ou un dossier, y compris d'un disque à l'autre (copie puis suppression).</summary>
    public static void MoveAny(string src, string dst)
    {
        if (File.Exists(src))
        {
            if (SameVolume(src, dst)) { File.Move(src, dst); return; }
            File.Copy(src, dst, false);
            try { File.Delete(src); }
            catch { try { File.Delete(dst); } catch { } throw; }
            return;
        }
        if (!Directory.Exists(src)) throw new FileNotFoundException(src);
        if (SameVolume(src, dst) && !Directory.Exists(dst)) { Directory.Move(src, dst); return; }
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.EnumerateFiles(src)) MoveAny(f, Path.Combine(dst, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(src)) MoveAny(d, Path.Combine(dst, Path.GetFileName(d)));
        try { Directory.Delete(src, false); } catch { }
    }

    static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Supprime les lots plus vieux que N jours. Renvoie les octets libérés.</summary>
    public static long Purge(int days)
    {
        long freed = 0;
        foreach (var e in History.Events.Where(e => !string.IsNullOrEmpty(e.Batch) && !e.Undone && !e.Expired && e.Date < DateTime.Now.AddDays(-days)).ToList())
        {
            try
            {
                if (Directory.Exists(e.Batch)) { freed += DirSize(e.Batch); Directory.Delete(e.Batch, true); }
                e.Expired = true;
                Log.Info($"Quarantaine purgée : {e.Batch}");
            }
            catch (Exception ex) { Log.Warn($"Purge de {e.Batch} : {ex.Message}"); }
        }
        History.Save();
        return freed;
    }

    public static long TotalSize()
    {
        long s = 0;
        foreach (var e in History.Events.Where(e => !string.IsNullOrEmpty(e.Batch) && !e.Undone && !e.Expired))
            if (Directory.Exists(e.Batch)) s += e.Items.Sum(i => File.Exists(i.Stored) ? i.Size : 0);
        return s;
    }

    public static DateTime NextPurge()
    {
        var first = History.Events.Where(e => !string.IsNullOrEmpty(e.Batch) && !e.Undone && !e.Expired).Select(e => e.Date).DefaultIfEmpty().Min();
        return first == default ? default : first.AddDays(Settings.Current.QuarantineDays);
    }

    public static long DirSize(string dir)
    {
        long s = 0;
        try
        {
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                s += f.Length;
        }
        catch { }
        return s;
    }
}
