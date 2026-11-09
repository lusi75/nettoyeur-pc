using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

public class IPhonePage : UserControl
{
    readonly StackPanel _body = new();
    readonly TextBlock _sub = Ui.Text("Branche ton iPhone en USB et déverrouille-le.", "Small", 14);
    IDeviceInfo _dev;
    bool _loading;

    public IPhonePage() { Content = _body; }

    public TextBlock Sub => _sub;
    public bool Loaded_ => _dev != null;
    public void Load() { if (!_loading) LoadAsync(); }

    async void LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        _body.Children.Clear();
        _body.Children.Add(Ui.Text("Recherche de l'iPhone…", "Body"));
        try
        {
            var err = await Task.Run(IPhone.EnsureTools);
            if (err != null) { Message(err); return; }
            var ids = await Task.Run(IPhone.Devices);
            if (ids.Count == 0)
            {
                _sub.Text = "Aucun iPhone détecté.";
                Message("Branche ton iPhone en USB, déverrouille-le et appuie sur « Se fier » s'il le demande, puis clique sur Actualiser.\n\nSi rien ne change : iTunes (version du site d'Apple) doit être installé, il fournit le service qui fait le lien avec l'iPhone.");
                return;
            }
            _dev = await Task.Run(() => IPhone.Info(ids[0]));
            if (_dev.Error != null) { _sub.Text = "iPhone détecté mais pas encore autorisé."; Message(_dev.Error); _dev = null; return; }
            _sub.Text = $"{_dev.Name} · iOS {_dev.Ios} · {Fmt.SizeDec(_dev.Free)} libres sur {Fmt.SizeDec(_dev.Total)}";
            Build();
        }
        catch (Exception ex) { Log.Error("Écran iPhone", ex); Message("Erreur : " + ex.Message); }
        finally { _loading = false; }
    }

    void Message(string text)
    {
        _body.Children.Clear();
        var t = Ui.Text(text, "Body"); t.TextWrapping = TextWrapping.Wrap;
        _body.Children.Add(Ui.Card(t));
    }

    static TextBlock Wrap(string s, string style = "Body", double? size = null) { var t = Ui.Text(s, style, size); t.TextWrapping = TextWrapping.Wrap; return t; }

    static StackPanel Section(string glyph, string title, string text)
    {
        var sp = new StackPanel();
        var h = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        h.Children.Add(Ui.Icon(glyph, 18, Ui.Res("Accent")).With(i => i.Margin = new Thickness(0, 0, 10, 0)));
        h.Children.Add(Ui.Text(title, "H3"));
        sp.Children.Add(h);
        sp.Children.Add(Wrap(text, "Small", 13).With(t => t.Margin = new Thickness(0, 0, 0, 14)));
        return sp;
    }

    void Build()
    {
        _body.Children.Clear();
        // Stockage
        var used = _dev.Used;
        var sp = new StackPanel();
        var row = new Grid(); Ui.Grid2(row, "*", "auto");
        row.Children.Add(Ui.Text($"{Fmt.SizeDec(used)} utilisés", "H2"));
        row.Children.Add(Ui.Text($"{Fmt.SizeDec(_dev.Free)} libres", "Body").With(t => t.VerticalAlignment = VerticalAlignment.Bottom).At(1));
        sp.Children.Add(row);
        var pct = _dev.Total > 0 ? 100.0 * used / _dev.Total : 0;
        sp.Children.Add(Ui.Bar(pct, pct > 90 ? Ui.Res("DangerFg") : pct > 80 ? Ui.Res("WarnFg") : null, 10).With(b => b.Margin = new Thickness(0, 10, 0, 6)));
        sp.Children.Add(Wrap($"{_dev.Name} ({_dev.Model}) · iOS {_dev.Ios}. Le détail par catégorie (dont « Données système ») est dans Réglages → Général → Stockage iPhone.", "Small", 12));
        _body.Children.Add(Ui.Card(sp, new Thickness(22)).With(c => c.Margin = new Thickness(0, 0, 0, 16)));

        _body.Children.Add(QuickWinsCard());
        _body.Children.Add(LogCard());
        _body.Children.Add(AppsCard());
        _body.Children.Add(ReportsCard());
        _body.Children.Add(BackupCard());
        _body.Children.Add(ResetCard());
    }

    Border Card(UIElement c) => Ui.Card(c, new Thickness(22)).With(b => b.Margin = new Thickness(0, 0, 0, 16));

    // ------------------------------------------------------------------ Gains à faire sur l'iPhone (outils d'Apple, sans danger)
    UIElement QuickWinsCard()
    {
        var sp = Section("\uE945", "Doublons, contacts et mise à jour : à faire sur l'iPhone", "Apple ne laisse pas un ordinateur supprimer des photos, des contacts ou une mise à jour : ce serait risqué pour la photothèque et le système. Mais l'iPhone a ses propres outils, sûrs, pour chacun :");
        var items = new[]
        {
            ("Photos en double", "Photos → Albums → Utilitaires → Doublons → « Fusionner ». iOS garde la meilleure version et rassemble les informations. Ton appli Nettoyeur sur l'iPhone trouve aussi les photos similaires, les rafales et les grosses vidéos."),
            ("Contacts en double", "Contacts → bandeau « Doublons trouvés » en haut de la liste → Fusionner. Ton appli Nettoyeur sur l'iPhone le fait aussi (écran Contacts en double)."),
            ("Mise à jour iOS déjà téléchargée", "Réglages → Général → Stockage iPhone → cherche « iOS » suivi d'un numéro de version dans la liste des applis → Supprimer la mise à jour. Sans danger : il s'agit seulement du fichier d'installation, iOS le retéléchargera si besoin. Ne touche pas à la ligne « iOS » du haut, qui est le système lui-même (elle ne se supprime pas)."),
            ("Supprimés récemment", "Photos → Albums → Utilitaires → Supprimés récemment → Sélectionner → Tout supprimer. Les photos supprimées y occupent la place pendant 30 jours."),
            ("Pièces jointes de Messages", "Réglages → Général → Stockage iPhone → Messages → « Passer en revue les grosses pièces jointes »."),
        };
        foreach (var (t, d) in items)
        {
            var p = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            p.Children.Add(Ui.Text(t, "Body", 14, null, FontWeights.SemiBold));
            p.Children.Add(Wrap(d, "Small", 12));
            sp.Children.Add(p);
        }
        return Card(sp);
    }

    // ------------------------------------------------------------------ Journal en direct
    UIElement LogCard()
    {
        var sp = Section("\uE9D9", "Journal en direct", "Tout ce que l'iPhone écrit dans son journal, en temps réel, comme la console d'Apple Configurator. Les erreurs sont en rouge, et le classement à droite montre quelles applis ou quels services en produisent le plus. Tout est aussi enregistré dans un fichier.");
        var btn = Ui.Btn("Ouvrir le journal en direct", "Primary", (_, _) => new SyslogWindow(_dev.Udid, _dev.Name) { Owner = Window.GetWindow(this) }.Show(), "\uE7B3");
        btn.HorizontalAlignment = HorizontalAlignment.Left; btn.Height = 38;
        sp.Children.Add(btn);
        return Card(sp);
    }

    // ------------------------------------------------------------------ Applis et leurs données
    UIElement AppsCard()
    {
        var sp = Section("", "Applis et leurs données", "Pour chaque appli : sa taille et celle de ses « documents et données ». Quand les données pèsent bien plus que l'appli, ce sont surtout des caches : désinstalle-la puis réinstalle-la depuis l'App Store pour repartir propre (connecte-toi d'abord pour ne rien perdre).");
        var list = new StackPanel();
        var btn = Ui.Btn("Lister les applis", "Primary", null, "");
        btn.HorizontalAlignment = HorizontalAlignment.Left; btn.Height = 38;
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false; list.Children.Clear(); list.Children.Add(Ui.Text("Lecture des applis…", "Small", 13));
            var (apps, err) = await Task.Run(() => IPhone.Apps(_dev.Udid));
            btn.IsEnabled = true; list.Children.Clear();
            if (err != null) list.Children.Add(Wrap(err, "Small", 13));
            if (apps.Count == 0) return;
            btn.Content = "Actualiser";
            var total = apps.Sum(a => a.Data);
            list.Children.Add(Wrap("Les tailles sont celles que l'iPhone communique pour chaque appli. Elles peuvent être inférieures à Réglages → Stockage iPhone, qui ajoute les dossiers partagés de certaines applis (par exemple les photos et vidéos reçues sur WhatsApp).", "Small", 12).With(t => t.Margin = new Thickness(0, 12, 0, 0)));
            list.Children.Add(Ui.Text($"{apps.Count} applis · {Fmt.SizeDec(apps.Sum(a => a.App))} d'applis + {Fmt.SizeDec(total)} de données", "Small", 13).With(t => t.Margin = new Thickness(0, 12, 0, 8)));
            var head = new Grid { Margin = new Thickness(0, 0, 0, 4) }; Ui.Grid2(head, "*", "100", "150", "100", "60");
            head.Children.Add(Ui.Text("Appli", "Small", 12));
            head.Children.Add(Ui.Text("Appli", "Small", 12).With(t => t.HorizontalAlignment = HorizontalAlignment.Right).At(1));
            head.Children.Add(Ui.Text("Documents et données", "Small", 12).With(t => t.HorizontalAlignment = HorizontalAlignment.Right).At(2));
            head.Children.Add(Ui.Text("Total", "Small", 12).With(t => t.HorizontalAlignment = HorizontalAlignment.Right).At(3));
            list.Children.Add(head);
            foreach (var a in apps.OrderByDescending(a => a.App + a.Data).Take(60))
            {
                var g = new Grid { MinHeight = 40 }; Ui.Grid2(g, "*", "100", "150", "100", "60");
                var n = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                n.Children.Add(Ui.Text(a.Name, "Body", 14).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis));
                n.Children.Add(Ui.Text(a.Id, "Small", 11));
                g.Children.Add(n);
                g.Children.Add(Ui.Text(Fmt.SizeDec(a.App), "Body", 13).With(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; }).At(1));
                var heavy = a.Data > 500L * 1024 * 1024 && a.Data > a.App * 2;
                g.Children.Add(Ui.Text(Fmt.SizeDec(a.Data), "Body", 13, heavy ? Ui.Res("WarnFg") : null, heavy ? FontWeights.SemiBold : null).With(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; }).At(2));
                g.Children.Add(Ui.Text(Fmt.SizeDec(a.App + a.Data), "Body", 13, null, FontWeights.SemiBold).With(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; }).At(3));
                var del = Ui.IconBtn("", "Désinstaller de l'iPhone", async (_, _) =>
                {
                    if (!Dialog.Confirm($"Désinstaller {a.Name} de l'iPhone ?", $"L'appli et ses {Fmt.SizeDec(a.Data)} de données seront supprimées du téléphone. Réinstalle-la ensuite depuis l'App Store (ses comptes en ligne se retrouvent en te reconnectant ; les données uniquement locales sont perdues).", "Désinstaller", danger: true)) return;
                    MainWindow.Instance.ShowBusy($"Désinstallation de {a.Name}");
                    string e2;
                    try { e2 = await Task.Run(() => IPhone.Uninstall(_dev.Udid, a)); } finally { MainWindow.Instance.HideBusy(); }
                    if (e2 != null) Dialog.Info("Désinstallation impossible", e2);
                    else { g.IsEnabled = false; g.Opacity = 0.35; }
                });
                del.HorizontalAlignment = HorizontalAlignment.Right;
                g.Children.Add(del.At(4));
                var b = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = g, Padding = new Thickness(0, 4, 0, 4) };
                b.SetResourceReference(Border.BorderBrushProperty, "Line");
                list.Children.Add(b);
            }
        };
        sp.Children.Add(btn);
        sp.Children.Add(list);
        return Card(sp);
    }

    // ------------------------------------------------------------------ Rapports de plantage
    UIElement ReportsCard()
    {
        var sp = Section("", "Rapports de plantage et de diagnostic", "iOS garde des journaux de chaque plantage, gel ou consommation anormale. Ils comptent dans les « Données système ». On les récupère sur le PC (copie gardée) puis on les efface de l'iPhone.");
        var res = new StackPanel();
        var scan = Ui.Btn("Analyser", "Primary", null, "");
        var clean = Ui.Btn("Effacer de l'iPhone", "Danger", null, "");
        clean.Visibility = Visibility.Collapsed;
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var b in new[] { scan, clean }) { b.Height = 38; b.Margin = new Thickness(0, 0, 10, 0); bar.Children.Add(b); }
        scan.Click += async (_, _) =>
        {
            var ct = MainWindow.Instance.ShowBusy("Lecture des rapports de l'iPhone", true);
            (int Count, long Size, List<(string Name, int Count, long Size)> Top, string Error) r;
            try { r = await Task.Run(() => IPhone.ScanReports(_dev.Udid, s => MainWindow.Instance.BusyStatus(s), ct)); } finally { MainWindow.Instance.HideBusy(); }
            res.Children.Clear();
            if (r.Error != null) { res.Children.Add(Wrap(r.Error, "Small", 13)); return; }
            if (r.Count == 0) { res.Children.Add(Ui.Text("Aucun rapport : rien à nettoyer ici.", "Body").With(t => t.Margin = new Thickness(0, 12, 0, 0))); return; }
            res.Children.Add(Ui.Text($"{r.Count} rapports · {Fmt.SizeDec(r.Size)}", "H3").With(t => t.Margin = new Thickness(0, 14, 0, 6)));
            foreach (var t in r.Top)
                res.Children.Add(Ui.Text($"•  {t.Name} — {t.Count} rapport(s), {Fmt.SizeDec(t.Size)}", "Small", 13));
            clean.Visibility = Visibility.Visible;
        };
        clean.Click += async (_, _) =>
        {
            if (!Dialog.Confirm("Effacer les rapports de l'iPhone ?", $"Ils sont d'abord copiés sur le PC dans :\n{IPhone.ReportsArchive}\n\npuis supprimés du téléphone.", "Effacer")) return;
            var ct = MainWindow.Instance.ShowBusy("Récupération et effacement des rapports", true);
            (int Count, long Size, string Folder, string Error) r;
            try { r = await Task.Run(() => IPhone.CleanReports(_dev.Udid, s => MainWindow.Instance.BusyStatus(s), ct)); } finally { MainWindow.Instance.HideBusy(); }
            res.Children.Clear(); clean.Visibility = Visibility.Collapsed;
            res.Children.Add(Wrap(r.Error ?? $"{r.Count} rapports effacés de l'iPhone ({Fmt.SizeDec(r.Size)}). Copie gardée dans {r.Folder}.", "Body").With(t => t.Margin = new Thickness(0, 12, 0, 0)));
            AppState.Notify();
        };
        sp.Children.Add(bar); sp.Children.Add(res);
        return Card(sp);
    }

    // ------------------------------------------------------------------ Sauvegarde
    UIElement BackupCard()
    {
        var sp = Section("", "Sauvegarde complète sur le PC", "Copie tout l'iPhone (photos, messages, réglages, données d'applis) sur un disque du PC, au format d'iTunes. Indispensable avant une restauration. Les applis elles-mêmes ne sont pas copiées : l'iPhone les retélécharge.");
        var drives = DriveInfo.GetDrives().Where(d => { try { return d.IsReady && d.DriveType == DriveType.Fixed; } catch { return false; } })
            .OrderBy(d => d.Name.StartsWith(AppPaths.SystemDrive[..1], StringComparison.OrdinalIgnoreCase)).ThenByDescending(d => d.AvailableFreeSpace).ToList();
        var need = _dev.Used + 2_000_000_000L;
        string root = null;
        var chips = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var d in drives)
        {
            var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.Name[..2] : $"{d.Name[..2]} {d.VolumeLabel}";
            var rb = new RadioButton { Style = (Style)Application.Current.Resources["Chip"], Content = $"{label} · {Fmt.SizeDec(d.AvailableFreeSpace)} libres", GroupName = "bkdrive", Margin = new Thickness(0, 0, 8, 8), IsEnabled = d.AvailableFreeSpace > need };
            var r0 = IPhone.BackupRoot(d.RootDirectory.FullName);
            rb.Checked += (_, _) => root = r0;
            chips.Children.Add(rb);
        }
        var first = chips.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsEnabled);
        if (first != null) first.IsChecked = true;
        sp.Children.Add(Ui.Text($"Où ? (il faut environ {Fmt.SizeDec(need)})", "Small", 13).With(t => t.Margin = new Thickness(0, 0, 0, 8)));
        sp.Children.Add(chips);

        // Chiffrement
        var enc = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        PasswordBox p1 = null, p2 = null;
        if (_dev.Encrypted)
            enc.Children.Add(Wrap("🔒 Les sauvegardes de cet iPhone sont déjà chiffrées : il faudra son mot de passe pour restaurer. Mot de passe oublié ? Réglages → Général → Transférer ou réinitialiser → Réinitialiser → Réinitialiser tous les réglages, puis recommence.", "Small", 12));
        else
        {
            enc.Children.Add(Wrap("Recommandé : chiffrer la sauvegarde. Sans chiffrement, les mots de passe, les données Santé et les Wi-Fi ne sont pas sauvegardés. Choisis un mot de passe et garde-le précieusement : sans lui, la sauvegarde est inutilisable.", "Small", 12).With(t => t.Margin = new Thickness(0, 0, 0, 8)));
            var pw = new StackPanel { Orientation = Orientation.Horizontal };
            p1 = Pwd("Mot de passe"); p2 = Pwd("Confirmation");
            pw.Children.Add(p1); pw.Children.Add(p2);
            enc.Children.Add(pw);
        }
        sp.Children.Add(enc);

        var go = Ui.Btn("Sauvegarder l'iPhone", "Primary", null, "");
        go.HorizontalAlignment = HorizontalAlignment.Left; go.Height = 40;
        go.IsEnabled = first != null;
        if (first == null) sp.Children.Add(Wrap("Aucun disque n'a assez de place libre.", "Small", 13));
        var result = new StackPanel();
        go.Click += async (_, _) =>
        {
            if (root == null) return;
            if (p1 != null && p1.Password.Length > 0)
            {
                if (p1.Password != p2.Password) { Dialog.Info("Mots de passe différents", "Les deux mots de passe ne correspondent pas."); return; }
                if (p1.Password.Length < 4) { Dialog.Info("Mot de passe trop court", "Choisis au moins 4 caractères."); return; }
            }
            else if (p1 != null && !Dialog.Confirm("Sauvegarder sans chiffrement ?", "Les mots de passe, les données Santé et les Wi-Fi ne seront pas inclus.", "Continuer sans chiffrer")) return;

            var ct = MainWindow.Instance.ShowBusy("Sauvegarde de l'iPhone", true);
            (bool Ok, string Message) r = (false, "");
            try
            {
                if (p1 != null && p1.Password.Length > 0)
                {
                    MainWindow.Instance.BusyStatus("Activation du chiffrement : l'iPhone peut demander son code.");
                    var pwd = p1.Password;
                    var e = await Task.Run(() => IPhone.EnableEncryption(_dev.Udid, pwd));
                    if (e != null) { r = (false, e); return; }
                    _dev.Encrypted = true;
                }
                MainWindow.Instance.BusyStatus("Préparation… laisse l'iPhone branché et déverrouillé. Ça peut prendre de 10 minutes à plus d'une heure.");
                r = await Task.Run(() => IPhone.Backup(_dev.Udid, root, (s, p) => MainWindow.Instance.BusyStatus(p >= 0 ? $"{p:P0} · {s}" : s), ct));
            }
            finally
            {
                MainWindow.Instance.HideBusy();
                if (r.Message != "") Dialog.Info(r.Ok ? "Sauvegarde terminée" : "Sauvegarde non terminée", r.Message);
                RefreshBackups(result);
            }
        };
        sp.Children.Add(go);
        sp.Children.Add(result);
        RefreshBackups(result);
        return Card(sp);
    }

    static PasswordBox Pwd(string tip)
    {
        var p = new PasswordBox { Width = 200, Height = 34, Margin = new Thickness(0, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0, 8, 0), ToolTip = tip, BorderThickness = new Thickness(1) };
        p.SetResourceReference(Control.BackgroundProperty, "Surface2");
        p.SetResourceReference(Control.ForegroundProperty, "Text");
        p.SetResourceReference(Control.BorderBrushProperty, "Line2");
        p.SetResourceReference(PasswordBox.CaretBrushProperty, "Text");
        System.Windows.Automation.AutomationProperties.SetName(p, tip);
        return p;
    }

    async void RefreshBackups(StackPanel host)
    {
        var list = await Task.Run(IPhone.ExistingBackups);
        host.Children.Clear();
        if (list.Count == 0) return;
        host.Children.Add(Ui.Text("Sauvegardes sur ce PC", "Small", 13).With(t => t.Margin = new Thickness(0, 16, 0, 6)));
        foreach (var b in list)
        {
            var g = new Grid(); Ui.Grid2(g, "*", "auto");
            var mine = _dev != null && b.Udid.Equals(_dev.Udid, StringComparison.OrdinalIgnoreCase);
            g.Children.Add(Ui.Text($"{(string.IsNullOrEmpty(b.Name) ? "iPhone" : b.Name)}{(mine ? " (cet iPhone)" : "")} · {Fmt.Date(b.Date)} · {Fmt.SizeDec(b.Size)}", "Body", 13).With(t => t.VerticalAlignment = VerticalAlignment.Center));
            var open = Ui.IconBtn("", "Ouvrir l'emplacement", (_, _) => Shell.OpenInExplorer(b.Folder));
            g.Children.Add(open.At(1));
            host.Children.Add(g);
        }
    }

    // ------------------------------------------------------------------ Remise à zéro des données système
    UIElement ResetCard()
    {
        var sp = Section("", "Remettre les « Données système » à zéro", "La seule méthode vraiment efficace quand les données système ont gonflé : sauvegarder, effacer l'iPhone, puis restaurer la sauvegarde. Tu retrouves tout (photos, messages, applis, réglages) sans les fichiers accumulés. Compte 1 à 2 heures.");
        var steps = new[]
        {
            "1.  Fais une sauvegarde complète ci-dessus, chiffrée de préférence, et vérifie qu'elle apparaît dans la liste.",
            "2.  Clique sur « Préparer iTunes » ci-dessous : iTunes verra la sauvegarde même si elle est sur un autre disque que C:.",
            "3.  Sur l'iPhone : Réglages → [ton nom] → Localiser → Localiser mon iPhone → désactiver. Puis Réglages → Général → Transférer ou réinitialiser → Effacer contenu et réglages.",
            "4.  Quand l'iPhone redémarre sur l'écran de configuration, branche-le et ouvre iTunes : choisis « Restaurer à partir de cette sauvegarde » et la plus récente.",
            "5.  Laisse l'iPhone branché jusqu'au bout, puis sur le Wi-Fi le temps qu'il retélécharge les applis.",
        };
        foreach (var s in steps) sp.Children.Add(Wrap(s, "Body", 13).With(t => t.Margin = new Thickness(0, 0, 0, 6)));
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        var prep = Ui.Btn("Préparer iTunes", "Ghost", (_, _) =>
        {
            var b = IPhone.ExistingBackups().FirstOrDefault(x => _dev != null && x.Udid.Equals(_dev.Udid, StringComparison.OrdinalIgnoreCase));
            if (b.Folder == null) { Dialog.Info("Pas encore de sauvegarde", "Fais d'abord une sauvegarde complète de cet iPhone."); return; }
            var root = Path.GetDirectoryName(b.Folder)!;
            var e = IPhone.LinkItunesBackups(root);
            Dialog.Info(e == null ? "iTunes est prêt" : "Problème", e ?? $"iTunes lira désormais ses sauvegardes dans :\n{root}\n\nSauvegarde de cet iPhone : {Fmt.Date(b.Date)}, {Fmt.SizeDec(b.Size)}.");
        }, "");
        var itunes = Ui.Btn("Ouvrir iTunes", "Ghost", (_, _) =>
        {
            var exe = Path.Combine(AppPaths.ProgramFiles, "iTunes", "iTunes.exe");
            if (File.Exists(exe)) Launcher.StartAsUser(exe); else Dialog.Info("iTunes introuvable", "Installe iTunes depuis le site d'Apple.");
        }, "");
        foreach (var b in new[] { prep, itunes }) { b.Height = 38; b.Margin = new Thickness(0, 0, 10, 0); bar.Children.Add(b); }
        sp.Children.Add(bar);
        return Card(sp);
    }
}
