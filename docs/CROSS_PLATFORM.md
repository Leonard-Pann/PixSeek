# Plateformes et packaging

| RID | État de cette session |
|---|---|
| win-x64 | Build, tests, lancement natif et publication self-contained exécutés sous Windows 11 |
| osx-arm64 / osx-x64 | Architecture, scripts de staging/bundle et CI écrits ; non exécutés |
| linux-x64 | Staging des bibliothèques, script et CI écrits ; non exécutés |
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

Le workflow Ubuntu 24.04 installe FFmpeg et les bibliothèques de développement, compile **ImageMagick 7.1.2-34** depuis l'archive amont vérifiée puis stage les exécutables. `ldd` sert à copier leurs dépendances hors glibc/loader ; des petits lanceurs positionnent `LD_LIBRARY_PATH` pour ces outils seulement. Les exécutables doivent conserver le droit `+x`. Pas de contournement par Docker au runtime.

Le paquet vise au minimum la compatibilité glibc du runner (Ubuntu 24.04) ; il n'est pas annoncé compatible avec toutes les distributions. L'interface Avalonia nécessite le serveur graphique et des bibliothèques système usuelles (X11/Wayland selon backend, fontconfig, libICE, libSM, libX11 et les dépendances du backend). AppImage/Flatpak ne sont pas produits. Les packages apt de build sont enregistrés mais pas verrouillés à un snapshot ; le packaging final reproductible reste à compléter.

## CI et contrôle des natifs

Le workflow possède des jobs Windows, Ubuntu, macOS ARM64 et Intel avec des runners natifs. Le job Windows exécute la suite complète. Les jobs Unix compilent et exécutent le diagnostic sans interface ; ils ne sont pas une validation des interactions graphiques. Le workflow n'a pas été lancé depuis la machine locale et ses résultats sont inconnus.

Les archives sqlite-vec sont propres à chaque architecture et leurs hashes sont épinglés. ONNX Runtime et le tokenizer sont obtenus via NuGet avec leurs actifs RID. Un binaire Windows n'est jamais utilisé pour un autre OS. L'absence d'une extension ou d'un codec reste une erreur explicite, pas une fonctionnalité simulée.
