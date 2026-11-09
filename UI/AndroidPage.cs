using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NettoyeurPC.Core;

namespace NettoyeurPC.UI;

public class AndroidPage : UserControl
{
    readonly StackPanel _body = new();
    public readonly TextBlock Sub = Ui.Text("Branche ton téléphone Android en USB.", "Small", 14);
    ADevice _dev;
    bool _loading;

    public AndroidPage() { Content = _body; }

    public void Load()
    {
        if (_loading) return;
        LoadAsync();
    }

    async void LoadAsync()
    {
        _loading = true;
        _body.Children.Clear();
        _body.Children.Add(Ui.Text("Recherche du téléphone…", "Body"));
        try
        {
            var err = await Task.Run(Android.EnsureTools);
            if (err != null) { Message(err); return; }
            var devs = await Task.Run(Android.Devices);
            var ready = devs.FirstOrDefault(d => d.State == "device");
            if (ready == null)
            {
                Sub.Text = devs.Count == 0 ? "Aucun téléphone Android détecté." : "Téléphone détecté, en attente d'autorisation.";
                if (devs.Any(d => d.State == "unauthorized"))
                    Message("Sur le téléphone, une fenêtre « Autoriser le débogage USB ? » est affichée : coche « Toujours autoriser depuis cet ordinateur » puis appuie sur Autoriser. Clique ensuite sur Actualiser.");
                else NoDevice();
                return;
            }
            await Task.Run(() => Android.Fill(ready));
            _dev = ready;
            Sub.Text = $"{_dev.Brand} {_dev.Model} · Android {_dev.Android} · {Fmt.SizeDec(_dev.Free)} libres";
            Build();
        }
        catch (Exception ex) { Log.Error("Écran Android", ex); Message("Erreur : " + ex.Message); }
        finally { _loading = false; }
    }

    static string Setup() =>
        "Pour que le PC puisse analyser le téléphone, il faut activer le « débogage USB » (une seule fois) :\n\n" +
        "1.  Paramètres → À propos du téléphone → Informations sur le logiciel → touche 7 fois « Numéro de version ». Le téléphone confirme que les options pour les développeurs sont activées.\n" +
        "2.  Paramètres → Options pour les développeurs → active « Débogage USB ».\n" +
        "3.  Branche le téléphone en USB, déverrouille-le et accepte « Autoriser le débogage USB » (coche « Toujours autoriser »).\n" +
        "4.  Clique sur Actualiser.\n\n" +
        "Tu pourras désactiver le débogage USB une fois terminé. Si Windows ne voit pas le téléphone, choisis « Transfert de fichiers » dans la notification USB.";

    void NoDevice()
    {
        _body.Children.Clear();
        var sp = new StackPanel();
        sp.Children.Add(Wrap(Setup(), "Body"));
        sp.Children.Add(Ui.Text("Toujours rien ? Il manque peut-être le pilote USB de la marque", "H3").With(t => t.Margin = new Thickness(0, 18, 0, 6)));
        sp.Children.Add(Wrap("Windows 10 et 11 l'installent souvent tout seuls. Sinon, télécharge-le chez le fabricant, installe-le puis rebranche le téléphone :", "Small", 13));
        var links = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var (label, url) in new[]
        {
            ("Samsung", "https://developer.samsung.com/galaxy/others/android-usb-driver-for-windows"),
            ("Google Pixel", "https://developer.android.com/studio/run/win-usb"),
            ("Autres marques (Xiaomi, Motorola…)", "https://developer.android.com/studio/run/oem-usb"),
        })
        {
            var b = Ui.Btn(label, "Ghost", (_, _) => Shell.OpenUrlOrFile(url), "\uE896");
            b.Height = 36; b.Margin = new Thickness(0, 0, 10, 10);
            links.Children.Add(b);
        }
        sp.Children.Add(links);
        sp.Children.Add(Wrap("OnePlus, Oppo et marques absentes : rubrique « Support » ou « Téléchargements » du site du fabricant.", "Small", 12));
        _body.Children.Add(Ui.Card(sp, new Thickness(22)));
    }

    void Message(string text)
    {
        _body.Children.Clear();
        var t = Ui.Text(text, "Body"); t.TextWrapping = TextWrapping.Wrap;
        _body.Children.Add(Ui.Card(t, new Thickness(22)));
    }

    static TextBlock Wrap(string s, string style = "Body", double? size = null) { var t = Ui.Text(s, style, size); t.TextWrapping = TextWrapping.Wrap; return t; }
    Border Card(UIElement c) => Ui.Card(c, new Thickness(22)).With(b => b.Margin = new Thickness(0, 0, 0, 16));

    static StackPanel Section(string glyph, string title, string text)
    {
        var sp = new StackPanel();
        var h = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        h.Children.Add(Ui.Icon(glyph, 18, Ui.Res("Accent")).With(i => i.Margin = new Thickness(0, 0, 10, 0)));
        h.Children.Add(Ui.Text(title, "H3"));
        sp.Children.Add(h);
        if (text != null) sp.Children.Add(Wrap(text, "Small", 13).With(t => t.Margin = new Thickness(0, 0, 0, 14)));
        return sp;
    }

    static Button Btn(string text, string style, string glyph, RoutedEventHandler click)
    {
        var b = Ui.Btn(text, style, click, glyph);
        b.Height = 38; b.Margin = new Thickness(0, 0, 10, 0); b.HorizontalAlignment = HorizontalAlignment.Left;
        return b;
    }

    static StackPanel Row(params UIElement[] els) { var p = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var e in els) p.Children.Add(e); return p; }

    string BackupDest()
    {
        var d = DriveInfo.GetDrives().Where(x => { try { return x.IsReady && x.DriveType == DriveType.Fixed; } catch { return false; } })
            .OrderBy(x => x.Name.StartsWith(AppPaths.SystemDrive[..1], StringComparison.OrdinalIgnoreCase)).ThenByDescending(x => x.AvailableFreeSpace).First();
        return Android.BackupRoot(d.RootDirectory.FullName, _dev);
    }

    void Refresh() { Android.Fill(_dev); Dispatcher.Invoke(() => Sub.Text = $"{_dev.Brand} {_dev.Model} · Android {_dev.Android} · {Fmt.SizeDec(_dev.Free)} libres"); }

    void Build()
    {
        _body.Children.Clear();
        // Stockage
        var sp = new StackPanel();
        var row = new Grid(); Ui.Grid2(row, "*", "auto");
        row.Children.Add(Ui.Text($"{Fmt.SizeDec(_dev.Used)} utilisés", "H2"));
        row.Children.Add(Ui.Text($"{Fmt.SizeDec(_dev.Free)} libres sur {Fmt.SizeDec(_dev.Total)}", "Body").With(t => t.VerticalAlignment = VerticalAlignment.Bottom).At(1));
        sp.Children.Add(row);
        var pct = _dev.Total > 0 ? 100.0 * _dev.Used / _dev.Total : 0;
        sp.Children.Add(Ui.Bar(pct, pct > 90 ? Ui.Res("DangerFg") : pct > 80 ? Ui.Res("WarnFg") : null, 10).With(b => b.Margin = new Thickness(0, 10, 0, 6)));
        sp.Children.Add(Wrap("Espace de stockage des données (le système Android lui-même n'est pas compté ici).", "Small", 12));
        _body.Children.Add(Card(sp));

        _body.Children.Add(CachesCard());
        _body.Children.Add(QuickWinsCard());
        _body.Children.Add(DuplicatesCard());
        _body.Children.Add(FoldersCard());
        _body.Children.Add(BigFilesCard());
        _body.Children.Add(AppsCard());
        _body.Children.Add(ContactsCard());
        _body.Children.Add(BackupCard());
        _body.Children.Add(LogCard());
    }

    // ------------------------------------------------------------------ Caches
    UIElement CachesCard()
    {
        var sp = Section("", "Vider les caches de toutes les applis", "Android demande à chaque appli de vider ses fichiers temporaires, exactement comme quand la place vient à manquer. Sans danger : aucun compte, message ou photo n'est touché. Les applis seront un peu plus lentes au premier lancement.");
        var res = Ui.Text("", "Body"); res.Margin = new Thickness(0, 10, 0, 0);
        sp.Children.Add(Btn("Vider les caches", "Primary", "", async (_, _) =>
        {
            MainWindow.Instance.ShowBusy("Vidage des caches du téléphone");
            long freed;
            try { freed = await Task.Run(() => { var f = Android.TrimCaches(_dev.Serial); Refresh(); return f; }); } finally { MainWindow.Instance.HideBusy(); }
            res.Text = freed > 0 ? $"{Fmt.SizeDec(freed)} libérés." : "Les caches étaient déjà presque vides.";
            AppState.Notify();
        }));
        sp.Children.Add(res);
        return Card(sp);
    }

    // ------------------------------------------------------------------ Corbeille, mises à jour, miniatures
    UIElement QuickWinsCard()
    {
        var sp = Section("", "Gains rapides et sans danger", "Corbeille de la galerie, paquets de mise à jour déjà téléchargés et laissés sur le stockage. On ne touche jamais au système : seuls des fichiers devenus inutiles sont proposés.");
        var list = new StackPanel();
        sp.Children.Add(Btn("Rechercher", "Primary", "", async (_, _) =>
        {
            MainWindow.Instance.ShowBusy("Recherche sur le téléphone");
            List<AFile> trash, ota;
            try { (trash, ota) = await Task.Run(() => (Android.Trashed(_dev.Serial), Android.UpdatePackages(_dev.Serial))); } finally { MainWindow.Instance.HideBusy(); }
            list.Children.Clear();
            void Item(string title, string text, List<AFile> files, string what)
            {
                var g = new Grid { Margin = new Thickness(0, 12, 0, 0) }; Ui.Grid2(g, "*", "auto");
                var t = new StackPanel();
                t.Children.Add(Ui.Text($"{title} · {files.Count} fichier(s) · {Fmt.SizeDec(files.Sum(f => f.Size))}", "Body", 14, null, FontWeights.SemiBold));
                t.Children.Add(Wrap(text, "Small", 12));
                g.Children.Add(t);
                var b = Btn("Supprimer", "Danger", "", null);
                b.VerticalAlignment = VerticalAlignment.Center;
                b.Click += async (_, _) =>
                {
                    if (!Dialog.Confirm($"Supprimer : {title} ?", $"{files.Count} fichier(s), {Fmt.SizeDec(files.Sum(f => f.Size))}. Suppression définitive sur le téléphone.", "Supprimer", danger: true)) return;
                    MainWindow.Instance.ShowBusy("Suppression");
                    try { await Task.Run(() => { Android.DeleteFiles(_dev.Serial, files, what); Refresh(); }); } finally { MainWindow.Instance.HideBusy(); }
                    g.IsEnabled = false; g.Opacity = 0.4;
                    AppState.Notify();
                };
                g.Children.Add(b.At(1));
                list.Children.Add(g);
            }
            if (trash.Count > 0) Item("Corbeille de la galerie", "Photos et vidéos que tu as déjà supprimées : Android les garde 30 jours avant de les effacer pour de bon.", trash, "fichiers de la corbeille");
            if (ota.Count > 0) Item("Mises à jour déjà téléchargées", "Paquets d'installation laissés sur le stockage. Sans danger : ils ne servent qu'à installer une mise à jour, et le téléphone les retélécharge si besoin. " + string.Join(", ", ota.Select(f => Path.GetFileName(f.Path)).Take(3)), ota, "paquets de mise à jour");
            if (trash.Count == 0 && ota.Count == 0) list.Children.Add(Ui.Text("Rien à signaler : pas de corbeille ni de mise à jour oubliée.", "Body").With(t => t.Margin = new Thickness(0, 12, 0, 0)));
            list.Children.Add(Wrap("Mise à jour téléchargée mais non installée dans la partition système : elle n'est pas accessible depuis le PC. Installe-la (Paramètres → Mise à jour logicielle) : c'est la seule façon sûre de récupérer cette place.", "Small", 12).With(t => t.Margin = new Thickness(0, 14, 0, 0)));
        }));
        sp.Children.Add(list);
        return Card(sp);
    }

    // ------------------------------------------------------------------ Doublons de photos
    UIElement DuplicatesCard()
    {
        var sp = Section("", "Photos et vidéos en double", "Fichiers strictement identiques (même contenu, vérifié par empreinte), par exemple une photo de l'appareil photo renvoyée par WhatsApp. Les doublons sont d'abord copiés sur le PC, puis supprimés du téléphone : rien n'est perdu.");
        var res = new StackPanel();
        sp.Children.Add(Btn("Rechercher les doublons", "Primary", "", async (_, _) =>
        {
            var ct = MainWindow.Instance.ShowBusy("Recherche des doublons", true);
            List<List<AFile>> groups;
            try { groups = await Task.Run(() => Android.Duplicates(_dev.Serial, MainWindow.Instance.BusyStatus, ct)); } finally { MainWindow.Instance.HideBusy(); }
            res.Children.Clear();
            if (groups.Count == 0) { res.Children.Add(Ui.Text("Aucun doublon trouvé.", "Body").With(t => t.Margin = new Thickness(0, 12, 0, 0))); return; }
            var extra = groups.SelectMany(g => g.Skip(1)).ToList();
            res.Children.Add(Ui.Text($"{groups.Count} groupes · {extra.Count} copies en trop · {Fmt.SizeDec(extra.Sum(f => f.Size))}", "H3").With(t => t.Margin = new Thickness(0, 14, 0, 8)));
            foreach (var g in groups.Take(25))
            {
                var p = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                p.Children.Add(Ui.Text($"✓ gardé : {g[0].Path}", "Small", 12, Ui.Res("SafeFg")).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis));
                foreach (var f in g.Skip(1)) p.Children.Add(Ui.Text($"✕ {f.Path} · {Fmt.SizeDec(f.Size)}", "Small", 12).With(t => t.TextTrimming = TextTrimming.CharacterEllipsis));
                res.Children.Add(p);
            }
            if (groups.Count > 25) res.Children.Add(Ui.Text($"… et {groups.Count - 25} autres groupes.", "Small", 12));
            var dest = Path.Combine(BackupDest(), $"Doublons_{DateTime.Now:yyyy-MM-dd}");
            var b = Btn($"Copier les {extra.Count} doublons sur le PC puis les supprimer du téléphone", "Danger", "", null);
            b.Margin = new Thickness(0, 10, 0, 0);
            b.Click += async (_, _) =>
            {
                if (!Dialog.Confirm("Retirer les doublons du téléphone ?", $"{extra.Count} fichiers ({Fmt.SizeDec(extra.Sum(f => f.Size))}) seront copiés dans :\n{dest}\n\npuis supprimés du téléphone. L'exemplaire marqué « gardé » reste sur le téléphone.", "Continuer")) return;
                var ct2 = MainWindow.Instance.ShowBusy("Copie des doublons sur le PC", true);
                int n = 0;
                try
                {
                    n = await Task.Run(() =>
                    {
                        var copied = Android.PullFiles(_dev.Serial, extra, dest, MainWindow.Instance.BusyStatus, ct2);
                        if (copied.Count > 0) Android.DeleteFiles(_dev.Serial, copied, "doublons de photos");
                        Refresh();
                        return copied.Count;
                    });
                }
                finally { MainWindow.Instance.HideBusy(); }
                Dialog.Info("C'est fait", $"{n} doublon(s) retiré(s) du téléphone. Copie sur le PC : {dest}" + (n < extra.Count ? $"\n\n{extra.Count - n} fichier(s) n'ont pas pu être copiés : ils sont restés sur le téléphone." : ""));
                res.Children.Clear();
                AppState.Notify();
            };
            res.Children.Add(b);
        }));
        sp.Children.Add(res);
        return Card(sp);
    }

    // ------------------------------------------------------------------ Dossiers
    UIElement FoldersCard()
    {
        var sp = Section("", "Ce qui prend de la place", "Les dossiers qui grossissent le plus : photos, captures, téléchargements, médias WhatsApp et Telegram.");
        var res = new StackPanel();
        sp.Children.Add(Btn("Mesurer", "Primary", "", async (_, _) =>
        {
            MainWindow.Instance.ShowBusy("Mesure des dossiers du téléphone");
            List<AFolder> fs;
            try { fs = await Task.Run(() => Android.FolderSizes(_dev.Serial)); } finally { MainWindow.Instance.HideBusy(); }
            res.Children.Clear();
            var max = fs.Count > 0 ? fs.Max(f => f.Size) : 1;
            foreach (var f in fs)
            {
                var g = new Grid { Margin = new Thickness(0, 12, 0, 0) }; Ui.Grid2(g, "*", "110", "auto");
                var t = new StackPanel();
                t.Children.Add(Ui.Text(f.Label, "Body", 14, null, FontWeights.SemiBold));
                if (f.Hint.Length > 0) t.Children.Add(Wrap(f.Hint, "Small", 12));
                t.Children.Add(Ui.Bar(100.0 * f.Size / max, null, 4).With(b => b.Margin = new Thickness(0, 6, 20, 0)));
                g.Children.Add(t);
                g.Children.Add(Ui.Text(Fmt.SizeDec(f.Size), "Body", 14).With(x => { x.HorizontalAlignment = HorizontalAlignment.Right; x.VerticalAlignment = VerticalAlignment.Center; }).At(1));
                if (f.SafeToEmpty)
                {
                    var b = Btn("Vider", "Ghost", "", null);
                    b.Margin = new Thickness(14, 0, 0, 0); b.VerticalAlignment = VerticalAlignment.Center;
                    var ff = f;
                    b.Click += async (_, _) =>
                    {
                        MainWindow.Instance.ShowBusy("Vidage");
                        try { await Task.Run(() => { Android.EmptyFolder(_dev.Serial, ff); Refresh(); }); } finally { MainWindow.Instance.HideBusy(); }
                        g.Opacity = 0.4; g.IsEnabled = false;
                    };
                    g.Children.Add(b.At(2));
                }
                res.Children.Add(g);
            }
        }));
        sp.Children.Add(res);
        return Card(sp);
    }

    // ------------------------------------------------------------------ Gros fichiers
    UIElement BigFilesCard()
    {
        var sp = Section("", "Gros fichiers", "Les fichiers de plus de 100 Mo (vidéos, archives, installeurs…). « Déplacer sur le PC » copie le fichier sur ton disque le plus libre puis le retire du téléphone.");
        var res = new StackPanel();
        sp.Children.Add(Btn("Rechercher", "Primary", "", async (_, _) =>
        {
            MainWindow.Instance.ShowBusy("Recherche des gros fichiers");
            List<AFile> files;
            try { files = await Task.Run(() => Android.BigFiles(_dev.Serial)); } finally { MainWindow.Instance.HideBusy(); }
            res.Children.Clear();
            if (files.Count == 0) { res.Children.Add(Ui.Text("Aucun fichier de plus de 100 Mo.", "Body").With(t => t.Margin = new Thickness(0, 12, 0, 0))); return; }
            var dest = Path.Combine(BackupDest(), "Fichiers déplacés");
            foreach (var f in files)
            {
                var g = new Grid { Margin = new Thickness(0, 10, 0, 0) }; Ui.Grid2(g, "*", "100", "110", "auto");
                g.Children.Add(Ui.Text(f.Path, "Body", 13).With(t => { t.TextTrimming = TextTrimming.CharacterEllipsis; t.ToolTip = f.Path; t.VerticalAlignment = VerticalAlignment.Center; }));
                g.Children.Add(Ui.Text(Fmt.Date(f.Date), "Small", 12).With(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; }).At(1));
                g.Children.Add(Ui.Text(Fmt.SizeDec(f.Size), "Body", 13).With(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; }).At(2));
                var b = Ui.IconBtn("", "Déplacer sur le PC", null);
                b.Margin = new Thickness(12, 0, 0, 0);
                var ff = f;
                b.Click += async (_, _) =>
                {
                    var ct = MainWindow.Instance.ShowBusy("Déplacement sur le PC", true);
                    int n;
                    try
                    {
                        n = await Task.Run(() =>
                        {
                            var ok = Android.PullFiles(_dev.Serial, new[] { ff }, dest, MainWindow.Instance.BusyStatus, ct);
                            if (ok.Count > 0) Android.DeleteFiles(_dev.Serial, ok, "fichier déplacé sur le PC");
                            Refresh();
                            return ok.Count;
                        });
                    }
                    finally { MainWindow.Instance.HideBusy(); }
                    if (n == 1) { g.Opacity = 0.4; g.IsEnabled = false; }
                    else Dialog.Info("Copie impossible", "Le fichier n'a pas pu être copié : il est resté sur le téléphone.");
                };
                g.Children.Add(b.At(3));
                res.Children.Add(g);
            }
            res.Children.Add(Wrap("Les fichiers déplacés vont dans : " + dest, "Small", 12).With(t => t.Margin = new Thickness(0, 12, 0, 0)));
        }));
        sp.Children.Add(res);
        return Card(sp);
    }

    // ------------------------------------------------------------------ Applis
    UIElement AppsCard()
    {
        var sp = Section("", "Applis et leurs données", "Taille de chaque appli, de ses données et de son cache, d'après Android.");
        var list = new StackPanel();
        sp.Children.Add(Btn("Lister les applis", "Primary", "", async (_, _) =>
        {
            MainWindow.Instance.ShowBusy("Lecture des applis");
            (List<AApp> Apps, string Note) r;
            try { r = await Task.Run(() => Android.Apps(_dev.Serial)); } finally { MainWindow.Instance.HideBusy(); }
            list.Children.Clear();
            list.Children.Add(Wrap(r.Note, "Small", 12).With(t => t.Margin = new Thickness(0, 12, 0, 8)));
            var head = new Grid(); Ui.Grid2(head, "*", "95", "95", "95", "95", "50");
            int c = 0;
            foreach (var h in new[] { "Appli", "Appli", "Données", "Cache", "Total" })
                head.Children.Add(Ui.Text(h, "Small", 12).With(t => { if (c > 0) t.HorizontalAlignment = HorizontalAlignment.Right; }).At(c++));
            list.Children.Add(head);
            foreach (var a in r.Apps.OrderByDescending(a => a.App + a.Data + a.Cache).Take(60))
            {
                var g = new Grid { MinHeight = 38 }; Ui.Grid2(g, "*", "95", "95", "95", "95", "50");
                var n = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                n.Children.Add(Ui.Text(a.Name, "Body", 14));
                n.Children.Add(Ui.Text(a.Id + (a.User ? "" : " · système"), "Small", 11));
                g.Children.Add(n);
                long[] vals = { a.App, a.Data, a.Cache, a.App + a.Data + a.Cache };
                for (int i = 0; i < 4; i++)
                {
                    var heavy = i == 2 && a.Cache > 300_000_000;
                    g.Children.Add(Ui.Text(Fmt.SizeDec(vals[i]), "Body", 13, heavy ? Ui.Res("WarnFg") : null, i == 3 ? FontWeights.SemiBold : null)
                        .With(t => { t.HorizontalAlignment = HorizontalAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; }).At(i + 1));
                }
                if (a.User)
                {
                    var aa = a;
                    var del = Ui.IconBtn("", "Désinstaller du téléphone", async (_, _) =>
                    {
                        if (!Dialog.Confirm($"Désinstaller {aa.Name} ?", "L'appli et ses données seront supprimées du téléphone. Tu pourras la réinstaller depuis le Play Store.", "Désinstaller", danger: true)) return;
                        MainWindow.Instance.ShowBusy($"Désinstallation de {aa.Name}");
                        string e;
                        try { e = await Task.Run(() => Android.Uninstall(_dev.Serial, aa)); } finally { MainWindow.Instance.HideBusy(); }
                        if (e != null) Dialog.Info("Désinstallation impossible", e); else { g.Opacity = 0.35; g.IsEnabled = false; }
                    });
                    del.HorizontalAlignment = HorizontalAlignment.Right;
                    g.Children.Add(del.At(5));
                }
                var b = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Child = g, Padding = new Thickness(0, 4, 0, 4) };
                b.SetResourceReference(Border.BorderBrushProperty, "Line");
                list.Children.Add(b);
            }
        }));
        sp.Children.Add(list);
        return Card(sp);
    }

    // ------------------------------------------------------------------ Contacts
    UIElement ContactsCard()
    {
        var sp = Section("", "Contacts en double", "Contacts qui ont le même nom ou le même numéro. L'analyse se fait ici ; la fusion se fait dans l'appli Contacts, qui garde l'historique de tes comptes.");
        var res = new StackPanel();
        sp.Children.Add(Btn("Rechercher", "Primary", "", async (_, _) =>
        {
            MainWindow.Instance.ShowBusy("Lecture des contacts");
            (List<AContactGroup> Groups, int Total, string Error) r;
            try { r = await Task.Run(() => Android.ContactDuplicates(_dev.Serial)); } finally { MainWindow.Instance.HideBusy(); }
            res.Children.Clear();
            if (r.Error != null) { res.Children.Add(Wrap(r.Error, "Small", 13)); }
            else if (r.Groups.Count == 0) res.Children.Add(Ui.Text($"Aucun doublon parmi {r.Total} contacts.", "Body").With(t => t.Margin = new Thickness(0, 12, 0, 0)));
            else
            {
                res.Children.Add(Ui.Text($"{r.Groups.Count} groupe(s) sur {r.Total} contacts", "H3").With(t => t.Margin = new Thickness(0, 14, 0, 8)));
                foreach (var g in r.Groups.Take(40))
                    res.Children.Add(Wrap("•  " + string.Join("   ↔   ", g.Items.Select(i => $"{i.Name} ({i.Phone})")), "Small", 12).With(t => t.Margin = new Thickness(0, 0, 0, 4)));
            }
            res.Children.Add(Wrap("Pour fusionner : Google Contacts → « Fusionner et corriger » (contacts.google.com sur le PC, ou l'appli Contacts). Sur Samsung : Contacts → menu → Gérer les contacts → Fusionner les contacts.", "Small", 12).With(t => t.Margin = new Thickness(0, 12, 0, 0)));
        }));
        sp.Children.Add(res);
        return Card(sp);
    }

    // ------------------------------------------------------------------ Copie des photos
    UIElement BackupCard()
    {
        var sp = Section("", "Copier les photos et vidéos sur le PC", "Copie tout le dossier Appareil photo (DCIM) sur ton disque le plus libre. Rien n'est supprimé du téléphone : c'est une sécurité avant de faire du tri.");
        var res = Ui.Text("", "Body"); res.Margin = new Thickness(0, 10, 0, 0); res.TextWrapping = TextWrapping.Wrap;
        sp.Children.Add(Btn("Copier les photos", "Primary", "", async (_, _) =>
        {
            var dest = BackupDest();
            var ct = MainWindow.Instance.ShowBusy("Copie des photos sur le PC", true);
            (bool Ok, long Size, string Folder) r;
            try { r = await Task.Run(() => Android.PullFolder(_dev.Serial, "/sdcard/DCIM", dest, MainWindow.Instance.BusyStatus, ct)); } finally { MainWindow.Instance.HideBusy(); }
            res.Text = r.Ok ? $"{Fmt.SizeDec(r.Size)} copiés dans {r.Folder}." : $"Copie interrompue ({Fmt.SizeDec(r.Size)} copiés dans {r.Folder}).";
        }));
        sp.Children.Add(res);
        return Card(sp);
    }

    UIElement LogCard()
    {
        var sp = Section("", "Journal en direct", "Tout ce que le téléphone écrit dans son journal (logcat), en temps réel. Les erreurs sont en rouge et le classement à droite montre quelles applis en produisent le plus.");
        sp.Children.Add(Btn("Ouvrir le journal en direct", "Primary", "", (_, _) =>
            new SyslogWindow($"{_dev.Brand} {_dev.Model}", Path.Combine(Android.ToolsDir, "adb.exe"), $"-s {_dev.Serial} logcat -v threadtime", SyslogWindow.ParseLogcat, false) { Owner = Window.GetWindow(this) }.Show()));
        return Card(sp);
    }
}
