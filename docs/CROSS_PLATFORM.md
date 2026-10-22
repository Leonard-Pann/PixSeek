# Plateformes et packaging

| RID | Compilation / publication locale | Exécution native |
|---|---|---|
| win-x64 | Réussie, avec tests et publication autonome | Fenêtre et diagnostics exécutés sous Windows 11 |
| linux-x64 | Release autonome compilée depuis Windows ; dépendances ELF et modèles vérifiés | À essayer sur Linux |
| osx-arm64 | Bundle Release compilé depuis Windows ; dépendances Mach-O ARM64 et modèles vérifiés | Non exécuté sur Mac |
| osx-x64 | Bundle Release compilé depuis Windows ; dépendances Mach-O x64 et modèles vérifiés | Non exécuté sur Mac |
| win-arm64 / linux-arm64 | Architecture prévue, sans release validée | Non exécuté |
| Android / iOS | Hors périmètre, adaptateurs natifs nécessaires | Non développé |

Les commandes exactes, prérequis système et résultats sont dans [UNIX_RELEASES_0.3.1.md](UNIX_RELEASES_0.3.1.md).

## Windows

Le bootstrap Windows vérifie les versions et SHA-256 épinglés dans tools/binaries/win-x64.lock.json. Les DLL redistribuables Microsoft C++ 14.51.36231 sont déployées localement via stage-windows-crt.ps1. La version utilise les outils natifs embarqués, sans PATH à configurer. Voir DEPENDENCIES.md.

Le diagnostic a réellement chargé le modèle CPU et produit un vecteur normalisé de 512 dimensions. Le lancement/arrêt natif a été contrôlé ; cela ne remplace pas un essai sur une VM vierge.

## Linux et macOS

La préparation actuelle remplace les anciens assemblages apt/Homebrew non épinglés par scripts/bootstrap-unix.ps1. Les trois verrous tools/binaries/<RID>.lock.json listent les URL, versions, builds, licences et SHA-256 de chaque paquet. Le script peut fonctionner depuis Windows avec 7-Zip, ou sur Unix avec 7zz ; les outils de préparation ne sont pas requis par l'utilisateur final.

Les natifs ImageMagick, libjxl et FFmpeg proviennent de conda-forge. Le SDK LibVLC Mac est extrait de l'archive officielle VideoLAN, séparément pour chaque architecture. Linux utilise les paquets Debian 12 LibVLC 3.0.23 et une fermeture de dépendances déterminée depuis les DT_NEEDED des bibliothèques/plugins conservés. Les composants d'interface graphique VLC/Sparkle/Growl sont exclus ; PixSeek rend les frames lui-même. sqlite-vec est fourni par une archive officielle par RID. ONNX/Skia/SQLite/tokenizer proviennent des actifs NuGet de la cible.

Les chemins sont relatifs à AppContext.BaseDirectory ; le lanceur détermine son dossier sans dépendre du répertoire courant. Les outils média ont leur propre environnement de chargement. Sur Linux, les bibliothèques graphiques de l'hôte sont prioritaires pour éviter une substitution GTK/GL par une version de repli.

Les données personnelles restent dans les emplacements utilisateur : LOCALAPPDATA/PixSeek, XDG_DATA_HOME/PixSeek ou ~/.local/share/PixSeek, et ~/Library/Application Support/PixSeek. Les modèles par défaut sont livrés dans l'application. Le téléchargement/suppression des modèles optionnels requiert actuellement un dossier applicatif accessible en écriture, comme indiqué dans README.

Les archives .tar.gz conservent les permissions exécutables et dédupliquent les copies identiques par liens physiques. La publication ne contient aucun PDB/DBG/MDB, binaire Windows ou SDK de compilation. Les manifests JSON et configurations XML utiles au runtime sont conservés.

## Limites et validation

Linux vise glibc >= 2.36 avec un desktop compatible Avalonia, ICU et les bibliothèques graphiques usuelles ; ni musl/Alpine, ni toutes les distributions anciennes ne sont couverts. Les extensions natives ne sont jamais substituées par une DLL Windows.

Les bundles Mac ciblent macOS 15 ou ultérieur. La compilation depuis Windows ne fournit pas de signature Developer ID ni de notarisation Apple. Les étapes de distribution Apple devront être exécutées sur Mac avec les identifiants appropriés ; aucune validation Gatekeeper n'est revendiquée ici.

La CI native est définie dans .github/workflows/unix-release.yml : Ubuntu 24.04, macOS 15 ARM64 et macos-15-intel. Elle contrôle le diagnostic du paquet sur le bon OS et publie des artefacts Actions. Elle n'a pas été exécutée à distance pendant cette session ; aucun push ni release GitHub n'a été effectué.
