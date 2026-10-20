# Dépendances et reproductibilité

Les versions NuGet directes sont centralisées dans `Directory.Packages.props`. Le projet conserve Avalonia 12.1.1 stable présent au démarrage. .NET 10 est la cible ; SDK utilisé ici : 10.0.402, runtime 10.0.13.

## Windows x64

Source de vérité : `tools/binaries/win-x64.lock.json`.

- FFmpeg 8.1.2 full_build : https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-full_build.7z ; SHA-256 `0fff188997a499b5382e0f66e845d4556c48c54f0113ebed4853d556dbdd7059`. Build GPLv3, statique, configuration complète dans son README copié en publication. FFprobe/FFplay proviennent de la même archive.
- ImageMagick 7.1.2-34 portable Q16 x64 : release officielle GitHub, SHA-256 `52cc6a441c7884f5ff4274b410c9b90c4b7d54245dcdf4d32988bb4e4df4e5d1`, SBOM officiel dans `licenses/ImageMagick-SBOM.json`.
- sqlite-vec 0.1.6 windows-x86_64 : release officielle asg017, SHA-256 `f1c615577ad2e692d1e2fe046fe65994dafd8a8cae43e9e864f5f682dc295964` vérifié à partir du fichier checksums amont conservé.
- libjxl 0.11.1 Windows static : release officielle, SHA-256 calculé lors du téléchargement puis épinglé `8f53ebce91820c30c9fc9294f06380213c1e2e66b361718880580246b2be008e`. Pas de prétention à un hash signé publié par l'éditeur pour cette archive.

Le bootstrap échoue si un hash, outil ou codec obligatoire manque. Il n'installe rien globalement et n'utilise pas une URL « latest ». Le téléchargement des modèles suit aussi une révision et des hashes fixes, voir AI_MODELS.

### Microsoft C++ : déploiement local

ONNX Runtime référence `MSVCP140`, `MSVCP140_1`, `VCRUNTIME140` et `VCRUNTIME140_1` ; ImageMagick référence `VCOMP140`. Ces DLL sont **embarquées localement** dans la publication (à la racine et à côté des outils). Le runtime MSVC **14.51.36231** provient du dossier redistribuable de Visual Studio 2026 présent sur la machine de développement ; les onze DLL CRT/OpenMP sont épinglées par SHA-256 dans `tools/binaries/msvc-win-x64.lock.json`.

Pour reconstruire, `stage-windows-crt.ps1` utilise `vswhere` et ce dossier précis, ou accepte `-RedistPath`. Le bootstrap échoue clairement si ces fichiers redistribuables manquent. Il ne copie aucun runtime de débogage et n'installe aucun redistribuable système. Une machine de build dotée uniquement du SDK .NET doit fournir ces fichiers depuis une installation/licence Visual Studio adaptée. Le poste final n'a pas à les installer séparément. Les runners Windows doivent disposer de cette version ou être préparés avec elle ; ce point de CI n'est pas validé ici.

Conditions Microsoft et fonctionnement du déploiement local : https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files et https://learn.microsoft.com/en-us/cpp/windows/deployment-in-visual-cpp . Les bibliothèques sont soumises aux Microsoft Software License Terms, et ne sont pas couvertes par MIT. Un essai sur VM Windows vierge reste nécessaire.

## Sources et licences

`licenses/` contient les licences récupérées de sqlite-vec, libjxl, ONNX Runtime, OpenCLIP, ImageMagick et FFmpeg ; les archives natives conservent aussi leurs notices de dépendances. Le README de Gyan contient les options de compilation, versions des bibliothèques et origines associées. Les fichiers `.lock.json`, le SBOM et les notices doivent accompagner une distribution reproductible.

Avant diffusion publique, compléter les sources correspondantes et obligations de redistribution GPL ainsi que les licences des dépendances transitives NuGet. Les binaires FFmpeg ne sont pas qualifiés de LGPL. Ce prototype n'est pas un avis juridique ou un dossier de conformité final.

## Unix

sqlite-vec et ImageMagick source Linux sont épinglés avec SHA-256. Les autres outils sont préparés avec apt/Homebrew sur les runners natifs et leurs versions enregistrées. Leur verrouillage exhaustif n'est pas réalisé ; les artefacts Unix sont expérimentaux et non validés dans cette session Windows. Voir CROSS_PLATFORM.

## Ajouts 0.2.0

LibVLCSharp 3.10.2 et le SDK natif VideoLAN.LibVLC.Windows 3.0.23.1 sont verrouillés via NuGet et packages.lock.json (content hashes). Les sources et licences sont référencées dans THIRD_PARTY_NOTICES. Les builds Windows AnyCPU du prototype embarquent seulement les natifs x64 ; une publication win-arm64 sélectionne ceux d'ARM64 mais n'est pas validée ici.

SigLIP2 est épinglé indépendamment dans tools/models/siglip2.json. Le script install-model accepte -Model xlmr ou -Model siglip2. La publication Windows inclut XLM-R par défaut et ne copie que les actifs de son manifeste, pour éviter d'embarquer involontairement tous les modèles de test.
