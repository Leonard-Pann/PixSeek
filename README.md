# PixSeek

Photothèque desktop multilingue : recherche sémantique locale dans les photos et vidéos, recherche de personnes et compression. Avalonia 12.1.1 / .NET 10 ; inférence ONNX sur CPU, sans serveur.

## Compiler sous Windows x64

Installer le SDK .NET 10. Le bootstrap prépare les outils natifs et les DLL redistribuables MSVC 14.51.36231 depuis Visual Studio 2026 ; voir [DEPENDENCIES.md](DEPENDENCIES.md).

```powershell
pwsh -NoProfile -File scripts/bootstrap-windows.ps1
dotnet restore PixSeek.slnx
dotnet build PixSeek.slnx -c Release
dotnet run --project PixSeek.Desktop -c Release --no-build
```

La première compilation télécharge automatiquement XLM-RoBERTa + ViT-B/32 (environ 1,48 Go), YuNet et SFace (environ 39 Mo). Les poids sont préparés dans `models/xlmr` et `models/faces` à la racine du dépôt, vérifiés par SHA-256, puis copiés à côté de l'exécutable. Les compilations suivantes réutilisent les fichiers valides. Une connexion est nécessaire si un fichier manque ou est endommagé. Après installation complète, la recherche et la reconnaissance fonctionnent hors ligne.

Les poids sont ignorés par Git. Ils accompagnent les archives de release, sans entrer dans l'historique du dépôt.

## Fonctionnalités

- Dossiers référencés sans déplacement des originaux, scan récursif et surveillance des changements.
- Galerie virtualisée à défilement continu, filtres par type et dossier, sélection de médias.
- Recherche multilingue avec XLM-R/ViT-B/32 (512 dimensions), ou SigLIP2 optionnel (768 dimensions), classement hybride avec les noms de fichiers.
- Recherche de scènes vidéo à partir de frames réparties sur toute la durée ; ouverture au meilleur timestamp.
- Personnes : références choisies par l'utilisateur, alias et détection/reconnaissance YuNet/SFace. [Fonctionnement détaillé](docs/PEOPLE.md).
- Visionneuse intégrée, zoom photo et lecture vidéo LibVLC, navigation clavier, presse-papier.
- Compression JPEG XL, PNG, AVIF et AV1/MKV, validation des sorties, sources conservées, jobs persistés.
- Thèmes système/sombre/clair, cinq langues (anglais, français, italien, allemand, espagnol), paramètres sauvegardés automatiquement.

Les [limites connues](docs/KNOWN_LIMITATIONS.md) précisent le périmètre et les validations. Le packaging Windows x64 est testé ; Linux/macOS restent expérimentaux.

## Stockage

Les données utilisateur Windows sont dans `%LOCALAPPDATA%\PixSeek`, macOS dans `~/Library/Application Support/PixSeek`, Linux dans `$XDG_DATA_HOME/PixSeek` ou `~/.local/share/PixSeek`. `PIXSEEK_DATA_DIR` peut choisir un autre dossier de données pour les tests ou une installation portable.

| Contenu | Emplacement |
|---|---|
| Dossiers ajoutés, médias, embeddings photos/frames vidéo, personnes/alias/références et index facial | `<données>/pixseek.db` |
| Paramètres : thème, langue, modèle actif, échantillonnage, compression | `<données>/settings.json` |
| Miniatures, scènes vidéo et aperçus des visages | `<données>/cache/` |
| Journaux | `<données>/logs/` |
| Modèles XLM-R + ViT-B/32 | `<exécutable>/models/xlmr/` |
| Modèles YuNet et SFace | `<exécutable>/models/faces/` |
| Modèle SigLIP2 optionnel | `<exécutable>/models/siglip2/` |

Cette version utilise exclusivement `pixseek.db` dans le dossier de données PixSeek. Elle ne reprend pas automatiquement les profils d'une installation portant un ancien nom ; les fichiers de cette installation restent intacts. Les photos et vidéos originales restent à leur emplacement.

Les modèles sont toujours locaux au logiciel. Le dossier de l'application doit être accessible en écriture pour télécharger ou supprimer un modèle depuis les paramètres. SigLIP2 reste optionnel et n'est pas inclus dans la release par défaut ; changer de modèle reconstruit l'index sémantique.

## Tests

```powershell
# La compilation prépare automatiquement XLM-R et les modèles faciaux.
pwsh -File scripts/install-model.ps1 -Model siglip2
dotnet test PixSeek.slnx -c Release --logger trx
```

La suite utilise les vrais modèles ONNX, encodeurs multimédias et Avalonia Headless. Les médias publics sont documentés dans `tests/fixtures/README.md`. Les résultats et médias générés restent dans les dossiers ignorés par Git.

Préparation manuelle des modèles, si nécessaire :

```powershell
pwsh -File scripts/install-model.ps1          # models/xlmr
pwsh -File scripts/install-face-models.ps1    # models/faces
# -Destination permet de préparer explicitement un autre dossier.
```

## Publication

```powershell
pwsh -File scripts/publish-windows.ps1
```

La publication autonome est produite dans `artifacts/publish/win-x64`. XLM-R/ViT-B/32, YuNet et SFace sont inclus automatiquement par le même mécanisme que `dotnet build`. Pas de trimming ni de fichier unique : déplacer et distribuer **tout le dossier**, pas seulement `PixSeek.exe`. Les données personnelles AppData ne sont pas incluses.

Sous Linux/macOS, préparer les natifs sur la plateforme cible puis utiliser `scripts/publish-linux.sh` ou `scripts/publish-macos.sh`. Voir [CROSS_PLATFORM.md](docs/CROSS_PLATFORM.md) pour le staging, la signature et les limites.

Diagnostic sans interface :

```powershell
./artifacts/publish/win-x64/PixSeek.exe --diagnostics ./artifacts/diagnostics.json
```

## Organisation

```text
PixSeek/                    Interface Avalonia, contrôles, vues et ViewModels
PixSeek.Desktop/            Point d'entrée desktop et diagnostics CLI
src/PixSeek.Core/           Contrats, modèles et contrôle des jobs
src/PixSeek.Infrastructure/ SQLite, découverte, paramètres, chemins, logs
src/PixSeek.Media/          Miniatures, métadonnées, compression
src/PixSeek.AI/             ONNX, modèles, tokenizer, indexation sémantique/faciale
build/                     Tâche MSBuild de préparation des modèles
tests/                     Tests et corpus public
tools/                     Manifestes de modèles et sommes de contrôle natives
models/                    Poids téléchargés, ignorés par Git
runtimes/<RID>/native/      Outils et bibliothèques natifs, ignorés par Git
scripts/                   Bootstrap, build et publication
licenses/                  Licences et notices des composants utilisés
docs/                      Architecture, algorithmes, validations et limites
```

Les projets mobiles et navigateur de template ont été retirés. Les sources du projet sont sous [MIT](LICENSE). Les composants tiers conservent leurs conditions : [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), [licences conservées](licenses/README.md), [modèles IA](docs/AI_MODELS.md). Les obligations de redistribution des binaires FFmpeg/VLC restent détaillées dans les notices.
