# PixSeek

Application desktop multilingue de photothèque, recherche sémantique locale et optimisation de photos/vidéos. Développée directement à partir du projet **PixSeek Avalonia 12.1.1 / .NET 10** existant. Les projets métier, l'interface et l'exécutable portent le nom **PixSeek**.

## Démarrer sous Windows 11 x64

Le SDK .NET 10 est requis pour développer. Le bootstrap utilise aussi les DLL redistribuables MSVC 14.51.36231 de Visual Studio 2026, déjà disponibles sur la machine de développement (voir DEPENDENCIES.md). Aucun Python, serveur, Docker, outil multimédia du PATH ni installation séparée du runtime C++ n'est requis par la publication fournie, qui embarque ces DLL.

```powershell
pwsh -NoProfile -File scripts/bootstrap-windows.ps1
dotnet restore
dotnet build
dotnet run --project PixSeek.Desktop
```

En développement, dans **Settings → AI model**, télécharger le couple XLM-RoBERTa + ViT-B/32 si nécessaire (environ 1,48 Go). La publication Windows le préinstalle par défaut. Ajouter ensuite un dossier. Sans les poids, le scan et les miniatures fonctionnent ; la recherche affiche une erreur explicite. Le téléchargement utilise des URL de révision fixes et vérifie chaque SHA-256 avant validation. Après installation, l'inférence fonctionne hors ligne.

Pour préinstaller les poids sans passer par l'interface :

```powershell
pwsh -File scripts/install-model.ps1 -Destination "$env:LOCALAPPDATA/PixSeek/models"
```

Une publication autonome **avec modèle embarqué**, utilisable hors ligne dès le premier lancement, est également préparée dans `artifacts/publish/win-x64/PixSeek.exe` (environ 2,43 Gio). Elle doit être déplacée avec **tout son dossier**, pas uniquement son EXE.

## Fonctionnalités réellement implémentées

- Dossiers référencés sans déplacement des originaux ; scan récursif, sous-dossiers, réconciliation, erreurs par fichier.
- Base SQLite locale WAL, migration versionnée, extension native sqlite-vec chargée explicitement.
- Galerie à défilement continu, chargée par lots SQL de 240 médias ; virtualisation des lignes ; miniatures disque ; décodage à la demande de trois miniatures à la fois, libérées hors écran.
- Photothèque au démarrage, recherche en tête, filtres automatiques par type/dossier, avec un choix explicite Tous les dossiers, sélection individuelle des médias et ajout de dossier en haut à droite.
- Miniatures générées en parallèle, indépendamment du worker ONNX ; traitement et lectures SQL hors du thread UI.
- Rafraîchissement incrémental pendant l'indexation : les cartes existantes, sélections et miniatures restent en place. Préparation indéterminée puis progression pondérée sur un total fixe, incluant les frames vidéo.
- Filtres type/dossier sur toute la base avant pagination et classement vectoriel ; réparation automatique des anciennes colonnes de filtrage.
- Recherche hybride : ONNX CPU multilingue + KNN cosine sqlite-vec ; priorité aux noms exacts, expressions complètes et ressemblances très fortes (seuil 0,84). Les ressemblances faibles ne modifient pas le classement sémantique.
- FFprobe, extraction de frames vidéo réparties sur toute la durée, limite et intervalle réglables ; meilleur timestamp par vidéo.
- Visionneuse photo/vidéo dans la même fenêtre : décodage Skia direct, orientation EXIF, cache de trois images, préchargement, zoom sous le curseur, déplacement et flèches clavier. Vidéo LibVLC intégrée avec format original/rotation/SAR, seek, relance après la fin, volume et sourdine, y compris au timestamp de recherche. Fond noir immédiat pendant le chargement.
- Optimisation d'un dossier ou d'une sélection : JPEG XL, JPEG réversible si vérifié, PNG sans perte de pixels, AVIF perceptuel, AV1/MKV avec copie des pistes audio/sous-titres compatibles.
- Panneau de compression superposé : entrée pour un dossier ou sélection depuis le clic droit, sortie personnalisable, modes Qualité/Équilibré/Meilleure compression, codecs et curseurs de qualité photo/vidéo dans les paramètres avancés.
- Progression FFmpeg, pause entre fichiers, annulation, résultats et jobs persistés dans SQLite.
- Sorties temporaires, décodage de validation, aucune suppression des sources, aucun écrasement, rejet par défaut des fichiers plus volumineux.
- Thèmes système/sombre/clair, cinq langues JSON (anglais par défaut, français, italien, allemand, espagnol), sauvegarde automatique, vidage des caches et version. Diagnostics techniques disponibles uniquement via CLI/logs.
- Réconciliation de bibliothèque au lancement et chaque minute, avec FileSystemWatcher pour réagir rapidement aux créations, modifications et suppressions. Les vecteurs des fichiers supprimés sont retirés.

Les limites précises sont dans [docs/KNOWN_LIMITATIONS.md](docs/KNOWN_LIMITATIONS.md). Il s'agit d'un prototype utilisable, avec lecture vidéo intégrée validée sous Windows et packaging Unix expérimental.

## Modèle et dépendances

Modèle : [XLM-RoBERTa Base / ViT-B-32 LAION-5B exporté par Immich](https://huggingface.co/immich-app/XLM-Roberta-Base-ViT-B-32__laion5b_s13b_b90k), révision `111f6ddca817bb6cd8343c3e3dc5ce39d9ba54b0`, 512 dimensions. Deux graphes ONNX, tokenizer Unigram XLM-R exact. SigLIP2 ViT-B/16 (768 dimensions) est téléchargeable et activable à la place, avec réindexation automatique. Détails et licence : [AI_MODELS](docs/AI_MODELS.md).

Windows embarque FFmpeg/FFprobe/FFplay **8.1.2 Gyan GPLv3**, ImageMagick **7.1.2-34 Q16**, cjxl/djxl **0.11.1**, sqlite-vec **0.1.6**, ONNX Runtime **1.31.0**. Le délégué JXL de cette version d'ImageMagick utilise libjxl **0.12.0**. Les archives, sources et SHA-256 sont dans `tools/binaries/win-x64.lock.json`. La voie JPEG réversible utilise cjxl/djxl et compare le SHA-256 du JPEG reconstruit.

## Tests

```powershell
pwsh -File scripts/install-model.ps1  # XLM-R dans artifacts/models
pwsh -File scripts/install-model.ps1 -Model siglip2 # modèle alternatif testé
dotnet test --logger trx
```

La suite exécute les vrais encodeurs et ONNX. Les tests d'intégration échouent explicitement si les binaires ou modèles manquent. Elle génère ses médias de compression et utilise trois images publiques documentées dans `tests/fixtures`. Les images de rendu Headless sont écrites dans `artifacts/pixseek-*.png`. Les tests de virtualisation utilisent des lignes synthétiques identifiées comme telles ; les tests sémantiques et d'indexation utilisent de vrais médias.

## Publication

```powershell
pwsh -File scripts/publish-windows.ps1
# Le modèle XLM-R est inclus par défaut ; option explicite :
pwsh -File scripts/publish-windows.ps1 -IncludeModel
```

La publication est self-contained, sans AOT ni trimming. `models/weights` est utilisé s'il contient le modèle embarqué complet. Autrement, les poids sont stockés dans les données utilisateur.

Sous Linux/macOS, préparer les dépendances **sur la plateforme cible**, puis :

```bash
bash scripts/stage-unix.sh linux-x64
bash scripts/publish-linux.sh
# Sur Mac ARM64 / Intel :
bash scripts/stage-unix.sh osx-arm64
bash scripts/publish-macos.sh osx-arm64
# remplacer osx-arm64 par osx-x64 sur Mac Intel
```

Voir [CROSS_PLATFORM](docs/CROSS_PLATFORM.md) pour les prérequis de build, les bibliothèques regroupées, les limites et la signature. Aucun test macOS/Linux n'a été exécuté sur la machine Windows de développement. Le workflow `.github/workflows/build.yml` prépare les jobs sur runners natifs ; il n'a pas été exécuté depuis cette session.

## Organisation

```text
PixSeek/                    UI Avalonia, styles, contrôles et ViewModels
PixSeek.Desktop/            Point d'entrée desktop et diagnostics CLI
src/PixSeek.Core/            Contrats, modèles, formats, contrôle des jobs
src/PixSeek.Infrastructure/  SQLite, fichiers, paramètres, chemins, logs
src/PixSeek.Media/           Processus, miniatures, métadonnées, compression
src/PixSeek.AI/              ONNX, modèle, tokenizer et pipeline d'indexation
tests/PixSeek.Tests/         Tests xUnit 3, Avalonia Headless et intégration
tests/fixtures/             Images publiques de recherche
tools/                     Manifestes et sommes de contrôle
runtimes/<RID>/native/      Outils et bibliothèques natifs
scripts/                   Bootstrap, build, publication
licenses/                  Licences tierces et SBOM ImageMagick
docs/                      Architecture, validations et limites
```

Les anciens projets Android/iOS/Browser sont conservés sur disque mais retirés de la solution desktop. Ils ne sont ni développés ni validés et ne doivent pas référencer les exécutables desktop pour une future version mobile.

Les données Windows sont dans `%LOCALAPPDATA%/PixSeek`, macOS dans `~/Library/Application Support/PixSeek`, Linux dans `$XDG_DATA_HOME/PixSeek` ou `~/.local/share/PixSeek`. Pour les tests et installations portables, `PIXSEEK_DATA_DIR` et `PIXSEEK_MODEL_DIR` permettent de changer ces emplacements.

Si une ancienne installation Lumina existe déjà, son dossier de données et sa base sont réutilisés pour conserver la bibliothèque, les embeddings et les chemins du cache. Les anciens dossiers de sortie restent exclus des scans.

Diagnostic sans interface :

```powershell
./artifacts/publish/win-x64/PixSeek.exe --diagnostics ./artifacts/diagnostics.json
```

Les sources restent sous la licence du dépôt. Respecter les [notices tierces](THIRD_PARTY_NOTICES.md), en particulier les obligations GPL des binaires FFmpeg, avant une distribution publique.

## Révision 0.2.0

La fenêtre ouvre directement la photothèque, sans menu latéral ni filtres de dates. Le bouton des paramètres est à droite de l'ajout de dossier. Le menu contextuel propose Lire, Compresser et Ouvrir l'emplacement du fichier. La visionneuse reste dans la fenêtre principale ; Échap ou la croix revient à la galerie.

Les modèles disponibles et leur installation sont gérés dans AI model / Modèle d'IA. Un seul modèle peut être actif. Pour supprimer le modèle actif, activer d'abord l'autre couple installé. Le changement reconstruit l'index ; les photos et vidéos d'origine sont conservées.

Les traductions sont dans les cinq fichiers JSON de PixSeek/Localization. Les nouveaux profils démarrent en anglais ; une langue déjà enregistrée reste conservée. Les caches d'aperçus et miniatures peuvent être vidés ; les images de scènes référencées par l'index sont conservées et les miniatures nécessaires sont régénérées sans recalculer les embeddings inchangés.

LibVLCSharp 3.10.2 et VideoLAN.LibVLC.Windows 3.0.23.1 sont fournis via NuGet. Aucun VLC externe n'est requis sous Windows. Voir docs/UX_UPDATE_2026-10-20.md pour les validations de cette révision.

Correctifs de lecture, de filtres et de performances : [révision 0.2.1](docs/BUGFIXES_0.2.1.md).

Version 0.3.0 : [nouveautés et validation](docs/RELEASE_0.3.0.md), [recherche de personnes et algorithme](docs/PEOPLE.md). Les portraits de référence et les visages reconnus restent locaux. Les rapports de compression restent dans SQLite ; aucun nouveau JSON ne pollue la sortie.
