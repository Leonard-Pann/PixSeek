# Plateformes et packaging

| RID | État de validation |
|---|---|
| win-x64 | Build, tests, lancement natif et publication self-contained exécutés sous Windows 11 |
| osx-arm64 / osx-x64 | Architecture et scripts de staging/bundle écrits ; non exécutés |
| linux-x64 | Staging des bibliothèques et script de publication écrits ; non exécutés |
| win-arm64 / linux-arm64 | Architecture prévue ; pas de bootstrap ni validation |
| Android / iOS | Hors périmètre ; modèles métier réutilisables, adaptateurs natifs nécessaires |

## Windows

`bootstrap-windows.ps1` télécharge des versions fixes via HTTPS, vérifie SHA-256 puis extrait par le `tar` de Windows 11. Il ne dépend ni du PATH de FFmpeg ni d'une installation ImageMagick. `win-x64.lock.json` épingle les origines ; le hash libjxl est calculé lors de la récupération de l'archive amont puis épinglé, les autres sont fournis par leurs éditeurs/distributeurs.

`publish-windows.ps1` inclut les outils, configurations ImageMagick, sqlite-vec, ONNX Runtime, moteur tokenizer natif, bibliothèques SQLite et runtime .NET. Aucun fichier du projet n'est recherché au runtime en dehors des ressources publiées et données utilisateur. La publication est un dossier portable ; aucun installateur MSIX n'est fourni. Les binaires statiques Gyan sont volumineux. Une installation du SDK est nécessaire uniquement pour compiler.

Le runtime Microsoft C++ CRT/OpenMP 14.51.36231 est déployé localement avec les binaires ; il n'est pas demandé à l'utilisateur final. Pour le préparer, le build utilise les fichiers redistribuables de Visual Studio 2026 via `stage-windows-crt.ps1`, avec hashes épinglés. Un runner dépourvu de cette version doit être préparé avant le bootstrap ; il ne faut pas remplacer silencieusement les fichiers par une version plus ancienne.

Le diagnostic publié a réellement chargé le modèle et obtenu un vecteur normalisé de 512 dimensions. Le lancement de la fenêtre a été contrôlé dans cette session. Cela ne remplace pas un essai sur une VM Windows vierge sans Visual Studio/redistribuables : ce dernier n'a pas été exécuté.

## macOS

Sur un runner ou Mac de même architecture : installer le SDK .NET 10 puis les outils **de build** via `brew install ffmpeg imagemagick jpeg-xl dylibbundler`. Exécuter le staging puis la publication comme indiqué au README.

`stage-unix.sh` copie les exécutables et utilise dylibbundler pour copier la fermeture des dépendances Mach-O et réécrire les références relatives. `publish-macos.sh` fabrique `PixSeek.app`, conserve les ressources à côté du point d'entrée (chemins basés sur `AppContext.BaseDirectory`) et effectue une signature ad hoc. Une vraie distribution doit signer les composants imbriqués avec Developer ID, les bonnes entitlements .NET/Avalonia, activer le hardened runtime, notariser via `notarytool`, puis agrafer le ticket. Le script ne prétend pas accomplir cette validation Apple.

Homebrew est requis pour préparer ce build, pas pour l'usage final visé. Les formules de build Homebrew ne sont pas entièrement épinglées dans ce prototype ; les versions sont enregistrées dans `build-packages.txt`. Leur reproductibilité et la fermeture des modules/délégués ImageMagick restent à vérifier sur Mac. Les artefacts CI portent le suffixe experimental.

## Linux

Le staging Linux nécessite FFmpeg, ImageMagick et les bibliothèques de développement sur le poste de compilation. `ldd` sert à copier leurs dépendances hors glibc/loader ; des petits lanceurs positionnent `LD_LIBRARY_PATH` pour ces outils seulement. Les exécutables doivent conserver le droit `+x`. Pas de contournement par Docker au runtime.

Le paquet vise au minimum la compatibilité glibc du runner (Ubuntu 24.04) ; il n'est pas annoncé compatible avec toutes les distributions. L'interface Avalonia nécessite le serveur graphique et des bibliothèques système usuelles (X11/Wayland selon backend, fontconfig, libICE, libSM, libX11 et les dépendances du backend). AppImage/Flatpak ne sont pas produits. Les packages apt de build sont enregistrés mais pas verrouillés à un snapshot ; le packaging final reproductible reste à compléter.

## CI et contrôle des natifs

Le dépôt ne fournit actuellement pas de workflow GitHub Actions. Les scripts doivent être exécutés sur des runners natifs avant d'automatiser une release : suite complète sous Windows ; compilation, diagnostic et essais graphiques propres à chaque plateforme Unix. Aucun résultat de CI distante n'est revendiqué.

Les archives sqlite-vec sont propres à chaque architecture et leurs hashes sont épinglés. ONNX Runtime et le tokenizer sont obtenus via NuGet avec leurs actifs RID. Un binaire Windows n'est jamais utilisé pour un autre OS. L'absence d'une extension ou d'un codec reste une erreur explicite, pas une fonctionnalité simulée.

## LibVLC intégré depuis 0.2.0

Windows : LibVLCSharp 3.10.2 + VideoLAN.LibVLC.Windows 3.0.23.1 via NuGet. Les fichiers x64 de libvlc et plugins sont copiés sous libvlc/win-x64. Les architectures x86 et ARM64 inutiles ne sont pas copiées dans la publication x64. Le player est chargé à partir des fichiers publiés, sans VLC installé.

Unix : préparer le SDK LibVLC et plugins sur le runner (Linux : libvlc-dev, vlc-plugin-base, pkg-config ; Mac : VLC.app de la bonne architecture et dylibbundler). stage-unix.sh appelle stage-vlc-unix.sh, qui rassemble le moteur, les modules et bibliothèques sous runtimes/<RID>/native/vlc. wrap-unix-player.sh place un lanceur relatif devant l'apphost et définit les chemins du loader et des plugins ; publish-linux.sh et publish-macos.sh l'appellent. Le code charge explicitement ce répertoire quand il est présent.

Cette nouvelle voie Unix est une stratégie de packaging non exécutée sur la machine Windows. La fermeture des dépendances, les modules audio, la signature du bundle et les chemins dyld doivent être validés sur runners natifs ; aucun succès Mac/Linux n'est revendiqué pour cette révision.
