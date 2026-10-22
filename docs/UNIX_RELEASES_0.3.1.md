# Releases Unix de PixSeek 0.3.1

## Artefacts préparés

- PixSeek-0.3.1-linux-x64.tar.gz : dossier portable, lanceur PixSeek.
- PixSeek-0.3.1-osx-arm64.tar.gz : PixSeek.app pour Apple Silicon.
- PixSeek-0.3.1-osx-x64.tar.gz : PixSeek.app pour Intel.

Les archives sont dans artifacts/releases, accompagnées de leur SHA-256. Elles contiennent .NET, le moteur ONNX CPU, SQLite/sqlite-vec, tokenizer, FFmpeg/FFprobe, ImageMagick, cjxl/djxl, LibVLC, les cinq traductions, les licences et les modèles XLM-R/ViT-B32 et YuNet/SFace. Aucun Python, FFmpeg, ImageMagick, VLC ou .NET séparé n'est demandé pour ces versions.

Les bibliothèques graphiques usuelles de l'OS restent nécessaires. Cible Linux : x64 avec glibc 2.36 ou plus récente et environnement desktop X11/XWayland, ICU, fontconfig, libICE/libSM/libX11. Debian 12/13, Ubuntu 24.04 et distributions récentes comparables sont les cibles de test prévues. Alpine/musl n'est pas couvert. La pile graphique du système est prioritaire sur les bibliothèques de repli pour éviter de remplacer GTK/GL.

macOS : 15 ou ultérieur, Apple Silicon ou Intel selon l'archive. Les [versions supportées par .NET 10](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) évoluent avec le support Apple.

## Résultats réellement vérifiés depuis Windows

Les trois commandes dotnet publish -c Release -r <RID> --self-contained true ont été exécutées avec succès. Les paquets complets ont été inspectés : format ELF/Mach-O, architecture, présence des dépendances natives requises, SHA-256 des modèles, absence de binaires Windows et de symboles PDB/DBG/MDB.

Les tests du packager vérifient les permissions exécutables Unix, la déduplication par liens physiques et le rejet des chemins sortant du dossier d'extraction. Les scripts PowerShell et shell sont analysés avant livraison. Le workflow passe actionlint 1.7.12. La suite Windows Release passe : 44 tests réussis, 0 échec, 0 ignoré (2 min 27 s), rapport tests/PixSeek.Tests/TestResults/unix-packaging-regression.trx.

Cela reste une compilation croisée : aucun exécutable Linux ou macOS n'a été lancé sur cette machine Windows. L'interface graphique, les sélecteurs natifs, l'audio, la vidéo et les chemins réels devront être essayés sur ces OS. Les rapports artifacts/<RID>-release-validation.json distinguent explicitement la compilation du lancement natif.

Les bundles Mac générés sur Windows ne sont ni signés avec Developer ID ni notarisés par Apple. Une diffusion publique Mac doit prévoir ces étapes. La CI native réalise une signature ad hoc, pas une notarisation.

## Utiliser sous Linux

Dans un terminal ouvert dans le dossier du téléchargement :

    sha256sum -c PixSeek-0.3.1-linux-x64.tar.gz.sha256
    tar -xzf PixSeek-0.3.1-linux-x64.tar.gz
    cd PixSeek-0.3.1-linux-x64
    ./PixSeek

Les permissions d'exécution sont présentes dans le tar. Extraire sur un système de fichiers permettant l'exécution, sans déplacer les sous-dossiers. Le lanceur trouve ses fichiers relativement à son propre emplacement, y compris dans un chemin contenant des espaces. Les données sont écrites dans XDG_DATA_HOME/PixSeek ou ~/.local/share/PixSeek, pas dans le dossier d'installation.

Diagnostic à joindre à un éventuel retour de test :

    ./PixSeek --diagnostics "$HOME/pixseek-diagnostics.json"

Les logs sont dans le sous-dossier logs des données utilisateur.

## Utiliser sous macOS

Extraire l'archive de la bonne architecture pour obtenir PixSeek.app. Le bundle contient Contents/MacOS et Contents/Resources/PixSeek.icns. Ses dépendances sont relatives à l'application ; la photothèque et la base sont dans ~/Library/Application Support/PixSeek. La disponibilité réelle sur Mac et Gatekeeper reste à valider sur Mac.

Les modèles par défaut sont préinstallés. Les paramètres actuels téléchargent les modèles optionnels dans le dossier models du logiciel : conserver ce dossier accessible en écriture si l'on veut ajouter/supprimer SigLIP2. Cette restriction existante est conservée et documentée au README.

## Reproduire les builds

SDK .NET 10 et PowerShell 7 sont requis uniquement sur le poste de compilation.

Depuis Windows :

    pwsh -File scripts/prepare-packaging-tools.ps1
    pwsh -File scripts/publish-unix.ps1 -Rid linux-x64
    pwsh -File scripts/publish-unix.ps1 -Rid osx-arm64
    pwsh -File scripts/publish-unix.ps1 -Rid osx-x64

Depuis le système cible, installer 7zz comme outil de build (7zip sous Ubuntu, sevenzip via Homebrew), puis :

    bash scripts/publish-linux.sh
    bash scripts/publish-macos.sh osx-arm64
    bash scripts/publish-macos.sh osx-x64

Les versions, URLs et SHA-256 sont figés dans tools/binaries/<RID>.lock.json. Le bootstrap télécharge des archives, vérifie les hashes et prépare les dépendances sans les installer sur le poste. Les outils 7-Zip et micromamba ont servi à la préparation ; ils ne sont pas distribués ni nécessaires à l'usage de PixSeek. Les fichiers de verrouillage permettent la reproduction sans refaire la résolution micromamba.

La version Release désactive DebugType et DebugSymbols globalement. Une cible MSBuild filtre aussi les symboles provenant de packages ; le script de publication refuse les artefacts restants. Les fichiers .deps.json, .runtimeconfig.json et configurations ImageMagick sont conservés car nécessaires.

Les copies identiques des bibliothèques sont dédupliquées dans le tar avec des liens physiques. Les exécutables/lanceurs reçoivent le mode 0755, les données 0644. Les modèles ne sont ni tronqués ni remplacés par des liens vers le poste de build.

## CI GitHub

Le workflow .github/workflows/unix-release.yml peut être lancé manuellement ou lors du push d'un tag v*. Il utilise Ubuntu 24.04, macOS 15 ARM64 et macos-15-intel, conformément aux [labels de runners GitHub](https://docs.github.com/en/actions/reference/runners/github-hosted-runners). Il compile, vérifie les dépendances, exécute le diagnostic natif sur le bon OS et fournit les archives comme artefacts Actions. Il ne crée pas de release publique automatiquement.

Ce workflow n'a pas été exécuté à distance dans cette session : aucun push, tag ni publication GitHub n'a été fait.

## Versions natives Unix

- FFmpeg / FFprobe 8.1.2, build GPL conda-forge.
- ImageMagick 7.1.2-31, build conda-forge.
- cjxl / djxl et libjxl 0.12.0.
- LibVLC 3.0.23 : paquets Debian pour Linux, SDK de l'application officielle VideoLAN pour Mac.
- sqlite-vec 0.1.6, archive propre à chaque architecture.
- ONNX Runtime 1.31.0 et tokenizer 3.23.1 : actifs natifs NuGet du RID publié.

Les DLL redistribuables Microsoft et le paquet VLC Windows sont exclus des publications Unix. Les dépendances transitives et licences propres à chaque paquet sont conservées sous runtimes/<RID>/native/licenses et détaillées dans native-dependencies.json. Les notices de redistribution et de sources restent dans THIRD_PARTY_NOTICES.md et DEPENDENCIES.md.
