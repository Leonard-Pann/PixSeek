# Composants tiers de PixSeek

Les licences complètes récupérées figurent dans `licenses/` et dans le dossier natif publié. Le projet existant conserve sa licence MIT. Les composants tiers gardent leurs propres conditions. Les textes communs sont regroupés dans `licenses/LGPL-2.1.txt` (LibVLC/LibVLCSharp) et `licenses/Apache-2.0.txt` (SFace, SigLIP2, Tokenizers.HuggingFace) ; les attributions et origines restent listées ci-dessous. La licence du corpus de tests reste avec les images, dans `tests/fixtures/LICENSE-scikit-image.txt`.

| Composant | Version intégrée | Licence / origine |
|---|---|---|
| Avalonia | 12.1.1 | MIT, https://github.com/AvaloniaUI/Avalonia |
| CommunityToolkit.Mvvm | 8.4.2 | MIT, https://github.com/CommunityToolkit/dotnet |
| .NET / Microsoft.Extensions / Microsoft.Data.Sqlite | 10.0.13 | MIT, https://github.com/dotnet |
| ONNX Runtime CPU | 1.31.0 | MIT, https://github.com/microsoft/onnxruntime |
| Microsoft Visual C++ CRT / OpenMP | 14.51.36231 | Microsoft Software License Terms, fichiers redistribuables de Visual Studio 2026, déploiement local ; voir DEPENDENCIES.md |
| Tokenizers.HuggingFace | 3.23.1 | Apache-2.0 (bindings et moteur Hugging Face), https://github.com/IgnaciodelaTorreArias/Tokenizers.HuggingFace.DotNet |
| sqlite-vec | 0.1.6 | MIT ou Apache-2.0, https://github.com/asg017/sqlite-vec/tree/v0.1.6 |
| FFmpeg / FFprobe | 8.1.2 Gyan full_build | **GPL-3.0-or-later**, https://www.gyan.dev/ffmpeg/builds/ ; tous les outils sont des processus séparés |
| ImageMagick portable Q16 | 7.1.2-34 | ImageMagick License, https://github.com/ImageMagick/ImageMagick/tree/7.1.2-34 |
| libjxl cjxl / djxl | 0.11.1 | BSD-3-Clause, https://github.com/libjxl/libjxl/tree/v0.11.1 |
| libjxl délégué ImageMagick | 0.12.0 | BSD-3-Clause, dans le binaire ImageMagick épinglé |
| CLIP ViT-B-32 / XLM-RoBERTa LAION-5B | révision ONNX 111f6ddca817bb6cd8343c3e3dc5ce39d9ba54b0 | Poids MIT selon la carte LAION ; tokenizer XLM-RoBERTa MIT ; export https://huggingface.co/immich-app/XLM-Roberta-Base-ViT-B-32__laion5b_s13b_b90k |
| Inter | dépendance Avalonia.Fonts.Inter | SIL Open Font License 1.1 |
| xUnit et SDK de tests | versions dans Directory.Packages.props | Apache-2.0 / MIT, uniquement développement |

FFmpeg est compilé avec des bibliothèques GPL, notamment x264/x265. Ce binaire n'est pas présenté comme LGPL. Ses options de compilation et versions des bibliothèques sont conservées dans `runtimes/win-x64/native/ffmpeg-README.txt` et dans le diagnostic `ffmpeg -version`. Sources correspondantes : https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz et liens de sources externes du README Gyan. Avant distribution publique, constituer une offre de sources et fournir les sources correspondantes de tous les composants GPL conformément à leurs licences. Le dossier de publication du prototype ne constitue pas à lui seul un dossier de conformité juridique final.

Les délégués ImageMagick ont leurs propres licences (libheif, libjxl, libjpeg, libpng, libtiff, WebP, lcms, etc.). Le SBOM officiel de l'archive est conservé dans `licenses/`. L'activation d'AV1/AVIF/HEIF ne constitue aucune garantie concernant les brevets applicables dans un pays donné.

Corpus de tests scikit-image v0.19.3 : `chelsea.png` (Stefan van der Walt, CC0), `coffee.png` (Rachel Michetti, CC0), `astronaut.png` (NASA, domaine public). Voir `tests/fixtures/README.md` et les conditions amont.

## Ajouts 0.2.0

- LibVLCSharp **3.10.2**, VideoLAN, LGPL-2.1-or-later : https://code.videolan.org/videolan/LibVLCSharp ; licence dans licenses/LGPL-2.1.txt.
- VideoLAN.LibVLC.Windows **3.0.23.1**, paquet natif officiel VideoLAN, LGPL-2.1-or-later selon ses métadonnées NuGet : https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.23.1 ; moteur/plugins publiés séparément de l'assembly applicatif, remplacement dynamique possible. Source et construction du paquet : https://code.videolan.org/videolan/libvlc-nuget ; sources VLC : https://code.videolan.org/videolan/vlc. Les modules embarqués et dépendances conservent leurs conditions propres ; fournir leurs sources correspondantes avant redistribution publique.
- SigLIP2 ViT-B/16 WebLI, Apache-2.0 (poids amont timm/ViT-B-16-SigLIP2) : https://huggingface.co/timm/ViT-B-16-SigLIP2 ; export ONNX Immich, révision 5f3fc29104ebcf51e9d893af57604badd51a3ea6 : https://huggingface.co/immich-app/ViT-B-16-SigLIP2__webli. SHA-256 et tailles dans tools/models/siglip2.json.
- SkiaSharp est une dépendance d'Avalonia.Skia, licence MIT avec notices de Skia et dépendances natives : https://github.com/mono/SkiaSharp.

- YuNet face_detection_yunet_2023mar.onnx : OpenCV Zoo, révision 48b10fa505aeb51fbd127fc60a5c660040e823b7, licence MIT (Shiqi Yu), licenses/YuNet-MIT.txt.
- SFace face_recognition_sface_2021dec.onnx : même dépôt/révision, licence Apache-2.0 publiée avec le modèle, licenses/Apache-2.0.txt. Empreintes et tailles : tools/models/faces.json. Provenance et limites : docs/PEOPLE.md.
- Portrait de Neil Armstrong, NASA S69-31741, domaine public : https://commons.wikimedia.org/wiki/File:Neil_Armstrong_pose.jpg ; copie de test réduite dans tests/fixtures/neil-armstrong.jpg, aucune affiliation NASA suggérée.

## Distributions Unix 0.3.1

Les paquets Linux/macOS ajoutent des binaires conda-forge : FFmpeg/FFprobe 8.1.2 (variante GPL), ImageMagick 7.1.2-31, libjxl/cjxl/djxl 0.12.0 et leurs dépendances transitives. Chaque distribution conserve son verrou exact et les licences/métadonnées de paquets dans runtimes/<RID>/native/licenses/conda. Les recettes et sources amont sont disponibles dans les feedstocks officiels https://github.com/conda-forge/ffmpeg-feedstock, https://github.com/conda-forge/imagemagick-feedstock et https://github.com/conda-forge/libjxl-feedstock ; le verrou identifie chaque archive source de paquet binaire par SHA-256.

LibVLC Linux est 3.0.23-0+deb12u1 (Debian), avec les copyrights des paquets embarqués sous native/licenses/debian. Sources Debian : https://sources.debian.org/src/vlc/ et https://deb.debian.org/debian/pool/main/v/vlc/. Le SDK macOS 3.0.23 est extrait de https://download.videolan.org/pub/videolan/vlc/3.0.23/macosx/ (hash officiel vérifié) ; source VLC : https://download.videolan.org/pub/videolan/vlc/3.0.23/vlc-3.0.23.tar.xz. Les composants gardent leurs conditions propres ; les remarques précédentes sur les sources correspondantes et la redistribution s'appliquent aussi aux artefacts Unix.

Les outils de build 7-Zip 25.01 (LGPL et licences de ses composants) et micromamba 2.9.0 (BSD-3-Clause) servent uniquement à la préparation, ne sont pas distribués avec PixSeek et ne sont pas nécessaires à son exécution. Aucun interpréteur Python ou JavaScript n'est embarqué pour exécuter l'application.
