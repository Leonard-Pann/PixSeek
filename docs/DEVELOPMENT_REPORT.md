# Rapport de développement — 19 octobre 2026

## Résultat livré

Le squelette Avalonia existant a été transformé en prototype desktop PixSeek, compilé et lancé sous Windows 11 x64. Le livrable autonome est `artifacts/publish/win-x64/PixSeek.exe`, avec les outils natifs, le runtime .NET et le modèle ONNX préembarqué. Le dossier complet représente environ 2,33 Gio. Il n'utilise ni Python, ni serveur, ni FFmpeg/ImageMagick du PATH.

Le périmètre est fonctionnel sur les parcours testés, mais ne couvre pas chaque option du cahier des charges. Les fonctionnalités absentes ne sont pas simulées ; les restrictions sont détaillées ci-dessous et dans KNOWN_LIMITATIONS.

## Implémenté et testé

- SQLite local, WAL, migration 1 idempotente, dossiers/médias, index cosine sqlite-vec, dimensions de vecteurs et reconstruction après changement de modèle.
- Scan récursif, miniatures disque, erreurs isolées, source indisponible conservée, média modifié réanalysé, cache/embeddings inchangés réutilisés après réouverture.
- Deux encodeurs ONNX exécutés sur CPU et tokenizer exact, recherche en français sur un corpus public. Les requêtes chat/café/astronaute retrouvent les bonnes images en tête.
- Vidéo réelle de deux scènes : extraction de plusieurs images, embeddings, persistance des timestamps et scène astronaute retrouvée dans la seconde partie.
- Interface Avalonia française, photothèque par défaut, recherche en haut, filtres automatiques de date/type/dossier, ajout de dossier, thèmes et paramètres. Galerie paginée/virtualisée avec sélection des médias uniquement. Rendu Headless Skia inspecté ; test de recherche réelle via les ViewModels.
- Compression superposée dans la fenêtre principale, entrée dossier ou sélection contextuelle, sortie personnalisable, modes Qualité/Équilibré/Meilleure compression et réglages avancés avec codec/curseur séparés photo et vidéo. Conversion réelle depuis ce panneau testée.
- Rafraîchissements de galerie sans remise à zéro des lignes/cartes ni rechargement des miniatures inchangées ; sélection préservée. Indexation avec préparation indéterminée, total fixe et pondération des étapes vidéo. Ce pourcentage mesure le travail planifié et ne constitue pas une estimation linéaire du temps restant.
- JPEG → JPEG XL réversible, JPEG reconstruit vérifié par SHA-256 ; PNG → JPEG XL sans perte de pixels, signé après décodage ; JPEG XL perceptuel ; alpha et profil ICC ; AVIF avec alpha.
- H.264 avec AAC → AV1/MKV, audio conservé et décodage intégral de la sortie ; source inchangée.
- Rejet des fichiers plus gros, collisions de noms, encodage en erreur, annulation d'un processus, pause coopérative et reprise d'un job interrompu sans écrasement.
- Bootstrap des binaires Windows avec hashes, publication self-contained, diagnostic de la publication avec inférence ONNX depuis les poids embarqués ; lancement de la fenêtre native sur la machine de développement.

## Implémenté avec validation limitée

Sélecteur de dossiers natif, actions système, zoom/pan et raccourcis de la visionneuse : code et rendu implémentés, mais pas de campagne d'interactions manuelles exhaustive. La visionneuse limite son aperçu à 3 200 pixels.

Le téléchargement UI du modèle utilise un pipeline HTTP réel, progression, annulation et SHA-256 ; les mêmes URLs et hashes ont été téléchargés/vérifiés par le script et les poids ont été exécutés. Le clic sur le téléchargement complet de 1,48 Go dans la fenêtre native n'a pas été automatisé.

Le lancement d'une vidéo ouvre FFplay embarqué au timestamp. Le binaire est présent et exécutable ; une lecture audiovisuelle interactive complète n'a pas été évaluée dans cette session.

## Partiellement implémenté

Lecture vidéo **externe**, sans intégration LibVLC. Filtrage des résultats après récupération du top 240. Métadonnées partielles et dates de modification. Jobs persistés par média/fichier et reprise à la relance, pause entre fichiers. Cache borné en mémoire par la réalisation des cartes, quota disque sans administration complète. Réglages avancés limités aux combinaisons effectivement prises en charge. UI française avec localisation anglaise future à extraire.

Les scripts Linux/macOS et workflows CI sont écrits, incluant regroupement des dépendances, contrôles et bundle macOS. Ils n'ont pas été exécutés ici ; les toolchains apt/Homebrew ne sont pas entièrement verrouillées. Aucun résultat de CI distante n'est revendiqué.

## Non implémenté

Lecteur natif intégré, GPU, détection de plans vidéo, gestion complète EXIF/GPS, identité/hash de déplacement, déduplication, surveillance continue, formats animés/HDR garantis, sortie vidéo MP4 configurable, réglages audio/résolution/framerate/concurrence supplémentaires, installateur signé, notarisation Apple et plateformes mobiles. Aucune suppression automatique des originaux.

## Organisation des fichiers

`PixSeek` et `PixSeek.Desktop` conservent l'UI et le lanceur existants. Les couches Core, Infrastructure, Media et AI sont sous `src/`. Les tests xUnit 3/Headless et le corpus public sont sous `tests/`. Les manifestes sont sous `tools/`, les binaires par RID sous `runtimes/`, les scripts de bootstrap/build/publication sous `scripts/`. Les documents demandés, notices, licences et SBOM sont présents. Les packages transitifs sont verrouillés dans les `packages.lock.json`.

Les anciens projets mobiles/browser sont conservés mais retirés de la solution desktop, conformément au périmètre.

## Modèle sélectionné

XLM-RoBERTa Base / ViT-B-32 LAION-5B, export ONNX Immich à la révision `111f6ddca817bb6cd8343c3e3dc5ce39d9ba54b0`. Il offre un espace texte/image partagé multilingue, des fichiers ONNX directement disponibles et une licence MIT déclarée pour les poids. Le tokenizer XLM-RoBERTa est chargé par les bindings Hugging Face, pas reconstitué par découpage des mots.

Entrées réellement chargées : image float32 `[1,3,224,224]`, texte int32 `[1,77]`. Sorties de 512 dimensions normalisées L2. Prétraitement et contrôles documentés dans AI_MODELS.

## Versions et codecs

SDK .NET 10.0.402 ; runtime 10.0.13 ; Avalonia 12.1.1 ; ONNX Runtime 1.31.0 ; sqlite-vec 0.1.6 ; Tokenizers.HuggingFace 3.23.1 (Apache-2.0).

Les dépendances Microsoft C++ CRT/OpenMP **14.51.36231** sont également embarquées à côté de l'application et des outils. Leur origine Visual Studio redistribuable et leurs SHA-256 sont épinglés ; le script de staging vérifie les onze DLL. Le build de développement doit disposer de cette source redistribuable licenciée, contrairement au poste utilisateur de la publication. Voir DEPENDENCIES.

FFmpeg/FFprobe/FFplay **8.1.2-full_build Gyan**, GPLv3. Encodeur AV1 sélectionné : **libsvtav1**, fallback libaom-av1 détectable. ImageMagick **7.1.2-34 Q16 x64** avec JXL, AVIF et HEIC en lecture. Délégué JXL ImageMagick : libjxl 0.12.0 ; outils dédiés cjxl/djxl : **0.11.1**. Toutes les commandes s'appuient sur les capacités réellement interrogées.

## Builds et tests

`dotnet restore` réussi. `dotnet build` Debug réussi avec **0 erreur et 0 avertissement**. Publication Release self-contained Windows x64 réussie sous le nom **PixSeek.exe**. Suite complète après cette révision : **23 tests réussis, 0 échec, 0 ignoré**. Les tests utilisent les vrais outils et modèles. Un test supplémentaire couvre les variables de chemins vides, corrigées après le contrôle de démarrage publié.

Le diagnostic de la publication retourne sqlite-vec v0.1.6, JXL/AVIF disponibles, libsvtav1, ONNX CPU 512 dimensions et norme 1,000000. La fenêtre native a été lancée et son titre contrôlé. Aucune VM Windows vierge, aucun système macOS/Linux et aucun job GitHub Actions n'ont été exécutés.

## Lancer et publier

Windows : lancer le fichier publié avec tout son dossier, ou depuis la racine du dépôt :

```powershell
dotnet restore
dotnet build
dotnet run --project PixSeek.Desktop
```

Pour le mode développeur, télécharger le modèle dans Paramètres ou exécuter `scripts/install-model.ps1 -Destination "$env:LOCALAPPDATA/PixSeek/models"`. La publication fournie possède déjà les poids.

Publication Windows : `pwsh -File scripts/publish-windows.ps1 -IncludeModel`.

Sur Linux x64 : préparer les outils de build décrits dans CROSS_PLATFORM, puis `bash scripts/stage-unix.sh linux-x64` et `bash scripts/publish-linux.sh`.

Sur Mac : `brew install ffmpeg imagemagick jpeg-xl dylibbundler`, puis `bash scripts/stage-unix.sh osx-arm64` et `bash scripts/publish-macos.sh osx-arm64` ; remplacer le RID par `osx-x64` sur Intel. Les paquets Unix restent expérimentaux tant qu'ils n'ont pas été exécutés sur place.

## Correctifs recommandés

Priorité à une validation Windows vierge et à un corpus varié EXIF/ICC/vidéos longues ; intégrer ensuite LibVLC, pousser les filtres avant KNN, enrichir les métadonnées et la gestion de cache, puis exécuter et verrouiller les builds Unix. Finaliser les sources correspondantes et obligations GPL des binaires avant distribution publique. Le prototype conserve délibérément les originaux : l'économie indiquée compare une source avec son équivalent optimisé, elle n'est pas une suppression effective d'espace sur le disque tant que les deux exemplaires restent présents.
