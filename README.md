# Nettoyeur PC

Une application Windows pour **libérer de la place et garder un PC rapide**, pensée pour être claire et sûre : rien n'est supprimé sans que tu l'aies vu, coché et confirmé, et la plupart des actions s'annulent en un clic.

Elle s'occupe aussi de ton **téléphone** (iPhone ou Android) branché en USB.

> Version actuelle : **2.3.1** · Windows 10 et 11 (64 bits) · interface en français

---

## Sommaire

- [Installation](#installation)
- [Fonctionnalités](#fonctionnalités)
- [Sécurité : ce que l'appli fait et ne fait pas](#sécurité--ce-que-lappli-fait-et-ne-fait-pas)
- [Téléphone : ce qu'il faut préparer](#téléphone--ce-quil-faut-préparer)
- [Questions fréquentes](#questions-fréquentes)
- [Technique : langages, outils et architecture](#technique--langages-outils-et-architecture)
- [Compiler soi-même](#compiler-soi-même)
- [Crédits et licences](#crédits-et-licences)

---

## Installation

1. Télécharge **`NettoyeurPC.exe`** dans la section **Releases** de ce dépôt (colonne de droite).
2. Double-clique dessus : il n'y a rien à installer, tout est dans ce seul fichier (environ 65 Mo).
3. **« Windows a protégé votre ordinateur »** s'affiche : c'est normal pour une appli maison, non signée par un éditeur payant. Clique sur **Informations complémentaires** puis **Exécuter quand même**.
4. L'appli demande les **droits administrateur** : ils sont nécessaires pour nettoyer les fichiers système, désinstaller des programmes et gérer le démarrage.

**Ne plus avoir la demande à chaque lancement :** Paramètres → **Installer et créer les raccourcis**. L'appli se copie dans `Program Files` et crée des raccourcis Bureau et menu Démarrer qui l'ouvrent directement, sans confirmation.

---

## Fonctionnalités

### Accueil
- **Score de santé** du PC : espace libre, état des disques (SSD/HDD, usure, température), nombre de programmes au démarrage.
- **Nettoyage express** : un clic pour tout ce qui est sans risque.
- Vue des disques et **recommandations** personnalisées.

### Nettoyage
- **49 types de fichiers inutiles**, classés par catégorie : Windows, carte graphique, navigateurs, applications, outils de développement, fichiers personnels, système (avancé).
- Chaque élément indique **ce que c'est**, **ce qui se passe après** et **où il se trouve**, avec un niveau de risque : *Sûr*, *À vérifier*, *Avancé*.
- Exemples : fichiers temporaires, cache de Windows Update, anciennes installations de Windows, caches des pilotes NVIDIA/AMD et des shaders, caches de Chrome, Edge, Firefox et Brave, caches de Discord, Spotify, Teams, Steam, caches npm, pip, NuGet, Gradle, dumps de plantage, corbeille.
- Option de **point de restauration** Windows avant le nettoyage.

### Stockage
- **Mode simple** : une carte claire de ce qui occupe chaque disque, les dossiers à regarder et les **plus gros fichiers**, avec trois actions : *Ouvrir l'emplacement*, *Déplacer* (vers un autre disque, en gardant l'arborescence) et *Supprimer*.
- **Mode avancé**, façon WinDirStat : arborescence complète, statistiques par type de fichier et carte en tuiles de tous les fichiers du disque.

### Applications
- Tous les programmes installés, avec leur **vraie taille mesurée**, leur icône, leur date d'installation et le disque où ils se trouvent. Filtre par disque, recherche et tri.
- **Désinstallation au premier plan** : l'assistant du programme s'ouvre devant toi et l'appli attend la fin, même quand il se relance tout seul. Pour les programmes MSI, c'est toujours le désinstalleur qui s'ouvre, jamais l'installeur.
- **Déplacer un programme vers un autre disque, sans le réinstaller** : le dossier est copié, vérifié, puis remplacé par un lien. Windows et le programme le trouvent toujours au même endroit. Annulable depuis l'Historique.

### Démarrage
- Tout ce qui se lance avec Windows : clés « Run » du registre, dossiers Démarrage, tâches planifiées à l'ouverture de session et services automatiques.
- Un interrupteur par élément pour l'activer ou le désactiver, comme le Gestionnaire des tâches. **Rien n'est supprimé**, et tout se rétablit depuis l'Historique.

### Registre
- 9 vérifications : programmes désinstallés encore listés, entrées de démarrage cassées, chemins d'applications, DLL partagées, historiques, règles de pare-feu, menus clic droit, services et tâches pointant vers des fichiers disparus.
- Protections contre les faux positifs : un dossier simplement inaccessible n'est jamais considéré comme disparu.
- **Sauvegarde `.reg` automatique** avant chaque correction et restauration en un clic.

### Téléphone
Un écran de choix **iPhone / Android**, puis :

| | iPhone | Android |
|---|---|---|
| Stockage utilisé et libre | ✅ | ✅ |
| Taille de chaque appli et de ses données | ✅ | ✅ (avec le cache) |
| Journal du téléphone en direct, avec le classement des applis qui produisent le plus d'erreurs | ✅ | ✅ |
| Rapports de plantage récupérés puis effacés | ✅ | — |
| Vider les caches de toutes les applis | — (interdit par iOS) | ✅ |
| Photos et vidéos en double (identiques à l'octet près) | via les outils d'Apple, guidé | ✅ copie sur le PC puis suppression |
| Contacts en double | via les outils d'Apple, guidé | ✅ analyse |
| Corbeille de la galerie, mises à jour déjà téléchargées | guidé | ✅ |
| Gros fichiers et dossiers qui grossissent (WhatsApp, Telegram…) | — | ✅ |
| Désinstaller une appli depuis le PC | ✅ | ✅ (applis installées par toi uniquement) |
| Sauvegarde complète sur le PC (sur le disque de ton choix) | ✅ chiffrée si tu veux | ✅ copie des photos |
| Remise à zéro des « Données système » (sauvegarde, effacement, restauration) | ✅ guidé | — |

Il existe aussi une appli compagnon **[Nettoyeur iPhone](https://github.com/lusi75/nettoyeur-iphone)**, qui tourne directement sur le téléphone : doublons, photos similaires, rafales, captures d'écran, grosses vidéos, contacts en double.

### Historique
- Toutes les actions de l'appli, avec la place libérée et un bouton **Annuler** quand c'est possible.
- Les fichiers « à vérifier » passent par une **quarantaine** de 7 jours (réglable) avant d'être supprimés pour de bon.

### Mode jeu
Interrupteur en bas du menu. Il passe l'alimentation en « Performances élevées », met le nettoyage automatique en pause et ferme les applis que tu choisis. **Tout revient comme avant** à la désactivation, et les applis fermées sont relancées.

### Nettoyage automatique
Une fois par semaine, le jour de ton choix, uniquement avec les règles sûres, sans rien afficher.

---

## Sécurité : ce que l'appli fait et ne fait pas

- ✅ Rien n'est supprimé sans **sélection et confirmation**.
- ✅ Les éléments à risque passent par la **quarantaine** et sont restaurables.
- ✅ Démarrage, registre, déplacements d'applis : **tout s'annule** depuis l'Historique.
- ✅ Le registre est **sauvegardé** avant chaque correction.
- ✅ Un **journal** détaillé est tenu dans `%LOCALAPPDATA%\NettoyeurPC\logs`.
- ❌ Aucune donnée n'est envoyée sur Internet. L'appli ne contient ni publicité, ni traceur, ni compte.
- ❌ Elle ne touche jamais aux fichiers de Windows indispensables, aux pilotes, aux antivirus ni aux anti-triche.
- ❌ Sur téléphone, elle ne modifie jamais le système : ni partition système, ni applis système.

---

## Téléphone : ce qu'il faut préparer

**iPhone**
1. Installer **iTunes depuis le site d'Apple**, pas la version du Microsoft Store : il fournit le pilote USB et le service « Apple Mobile Device ».
   Téléchargement direct (Windows 64 bits) : **https://www.apple.com/itunes/download/win64**
2. Brancher l'iPhone, le **déverrouiller** et appuyer sur **Se fier**.
3. Si Windows ne le reconnaît toujours pas (dans le Gestionnaire de périphériques, il apparaît comme « Périphérique USB MTP »), il manque le pilote USB d'Apple :
   - **Le plus simple, depuis l'appli :** écran Téléphone → iPhone → **Installer le pilote USB d'Apple**. L'appli télécharge le pilote officiel sur le Catalogue Microsoft Update, l'installe et relance le service Apple. Débranche et rebranche ensuite l'iPhone ; redémarre le PC si besoin.
   - **À la main :**
     1. Télécharge le pilote, PC classiques (x64) : [Apple Mobile Device USB 538, AMD64](https://catalog.s.download.windowsupdate.com/d/msdownload/update/driver/drvs/2023/10/5fb262ea-d52d-46a7-9361-f3260ba57a1a_3e8075a4dded0a795131f82285e2f1a06525ebc2.cab). Toutes les versions sont dans le [Catalogue Microsoft Update](https://www.catalog.update.microsoft.com/Search.aspx?q=Apple%2C%20Inc.%20USBDevice) : prends la ligne **AMD64**, pas ARM64, sauf sur un PC à processeur ARM.
     2. Ouvre le fichier `.cab` (double-clic) et copie son contenu dans un dossier.
     3. Gestionnaire de périphériques → **Contrôleurs de bus USB** → clic droit sur le **Périphérique USB composite** qui disparaît quand tu débranches l'iPhone → **Mettre à jour le pilote** → **Parcourir mon poste de travail** → **Choisir parmi une liste** → **Disque fourni** → choisis `AppleUsb.inf` → **Apple Mobile Device USB Composite Device**.
     4. Redémarre le PC.

**Android**
1. Paramètres → À propos du téléphone → appuyer **7 fois sur « Numéro de version »**.
2. Paramètres → Options pour les développeurs → activer **Débogage USB**.
3. Brancher le téléphone et accepter **« Autoriser le débogage USB »**.

---

## Questions fréquentes

**L'antivirus se méfie de l'appli ?** C'est un faux positif classique pour un exécutable non signé qui demande les droits administrateur et touche au système. Le code est entièrement lisible dans ce dépôt.

**Où sont mes réglages et mon historique ?** Dans `%LOCALAPPDATA%\NettoyeurPC` (`reglages.json`, `historique.json`, journaux, sauvegardes du registre).

**Je veux récupérer un fichier supprimé.** Historique → **Annuler**, ou le dossier `NettoyeurPC_Quarantaine` à la racine du disque le plus libre, pendant le délai de quarantaine.

**Désinstaller l'appli ?** Supprimer `NettoyeurPC.exe`. Si tu as utilisé « Installer et créer les raccourcis », supprime aussi `C:\Program Files\NettoyeurPC`, les raccourcis et les tâches planifiées `NettoyeurPC_Ouvrir` et `NettoyeurPC_Automatique`.

---

## Technique : langages, outils et architecture

### Langages et frameworks
| | |
|---|---|
| **C# 13 / .NET 9** | Tout le code de l'appli |
| **WPF + XAML** | Interface : thème sombre, couleur d'accent personnalisable, pages construites en code |
| **PowerShell** | Appelé ponctuellement : santé des disques, tâches planifiées, pare-feu |
| **Swift / SwiftUI** | Appli compagnon iPhone (dépôt séparé) |

### Composants Windows utilisés
- **API du registre** (`Microsoft.Win32.Registry`) : programmes installés, démarrage, analyse du registre.
- **WMI** (`System.Management`) : suivi des processus de désinstallation, services, processus en cours.
- **Win32** : `SHFileOperation` (corbeille), `ExtractIconEx` (icônes), `SetForegroundWindow` (désinstalleurs au premier plan), `DwmSetWindowAttribute` (barre de titre sombre).
- Outils intégrés à Windows : `schtasks` (tâches planifiées), `powercfg` (mode jeu), `reg.exe` (sauvegarde du registre), `mklink /J` (déplacement d'applis), `robocopy` (retour d'une appli déplacée), `sc` et `net` (services), `Get-PhysicalDisk` (santé des disques).
- **Points de restauration** Windows (`Checkpoint-Computer`).

### Outils pour les téléphones
- **[libimobiledevice](https://libimobiledevice.org)** (open source) pour l'iPhone : `ideviceinfo`, `idevicecrashreport`, `idevicebackup2`, `idevicesyslog`, `ideviceinstaller`. La liste des applis avec leurs tailles passe par des appels directs à la bibliothèque (P/Invoke), parce que les outils en ligne de commande ne demandent pas ces tailles à l'iPhone.
- **ADB** (Android SDK Platform-Tools de Google) pour Android : `pm`, `dumpsys diskstats`, `md5sum`, `find`, `logcat`, `content query`.
- Les deux sont **embarqués** dans l'exécutable et extraits dans `%LOCALAPPDATA%\NettoyeurPC` au premier usage.

### Algorithmes
- Carte du stockage : **treemap « squarified »** (Bruls, Huizing, van Wijk) avec tuiles ombrées par fichier, à la façon de WinDirStat.
- Analyse des disques **parallélisée** sur les premiers niveaux de l'arborescence.
- Doublons Android : regroupement par taille, puis **empreinte MD5** calculée sur le téléphone.

### Organisation du code
```
Core/            logique, sans interface
  CleanEngine.cs   règles de nettoyage et moteur
  Storage.cs       analyse des disques et treemap
  Apps.cs          programmes installés, désinstallation, déplacement
  StartupScan.cs   démarrage
  RegistryScan.cs  analyse et réparation du registre
  History.cs       historique, annulation, quarantaine
  GameMode.cs      mode jeu
  IPhone*.cs       iPhone (libimobiledevice)
  Android.cs       Android (ADB)
  SystemInfo.cs    disques, santé, nettoyage automatique
  Util.cs          réglages, journal, formats, appels système
UI/              une page par écran (Accueil, Nettoyage, Stockage, Applications,
                 Démarrage, Registre, Téléphone, Historique, Paramètres) + thème
```

---

## Compiler soi-même

Prérequis : **SDK .NET 9**. La compilation marche aussi sous Linux ou macOS grâce à `EnableWindowsTargeting`.

```bash
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

Les outils téléphone (`Assets/outils-iphone.zip` et `Assets/outils-android.zip`) ne sont pas stockés dans le dépôt. La compilation automatique (`.github/workflows/build.yml`) les prépare elle-même : libimobiledevice via MSYS2, Platform-Tools depuis le site de Google. Elle compile ensuite l'exécutable et le publie dans **Releases** à chaque nouvelle version.

---

## Crédits et licences

- Le robot Android est reproduit à partir d'un travail créé et partagé par Google, utilisé selon les conditions de la licence [Creative Commons Attribution 3.0](https://creativecommons.org/licenses/by/3.0/).
- [libimobiledevice](https://github.com/libimobiledevice/libimobiledevice) : LGPL 2.1.
- Android SDK Platform-Tools : © Google, selon les conditions de licence du SDK Android.
- iPhone, iOS et iTunes sont des marques d'Apple Inc. ; Android est une marque de Google LLC. Ce projet n'est affilié ni à Apple ni à Google.

Fourni tel quel, sans garantie. Fais une sauvegarde de tes données importantes avant tout nettoyage en profondeur.
