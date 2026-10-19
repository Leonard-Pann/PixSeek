# Composants tiers de PixSeek

Les licences complètes récupérées figurent dans `licenses/` et dans le dossier natif publié. Le projet existant conserve sa licence MIT. Les composants tiers gardent leurs propres conditions.

| Composant | Version intégrée | Licence / origine |
|---|---|---|
| Avalonia | 12.1.1 | MIT, https://github.com/AvaloniaUI/Avalonia |
| CommunityToolkit.Mvvm | 8.4.2 | MIT, https://github.com/CommunityToolkit/dotnet |
| .NET / Microsoft.Extensions / Microsoft.Data.Sqlite | 10.0.13 | MIT, https://github.com/dotnet |
| ONNX Runtime CPU | 1.31.0 | MIT, https://github.com/microsoft/onnxruntime |
| Microsoft Visual C++ CRT / OpenMP | 14.51.36231 | Microsoft Software License Terms, fichiers redistribuables de Visual Studio 2026, déploiement local ; voir DEPENDENCIES.md |
| Tokenizers.HuggingFace | 3.23.1 | Apache-2.0 (bindings et moteur Hugging Face), https://github.com/IgnaciodelaTorreArias/Tokenizers.HuggingFace.DotNet |
| sqlite-vec | 0.1.6 | MIT ou Apache-2.0, https://github.com/asg017/sqlite-vec/tree/v0.1.6 |
| FFmpeg / FFprobe / FFplay | 8.1.2 Gyan full_build | **GPL-3.0-or-later**, https://www.gyan.dev/ffmpeg/builds/ ; tous les outils sont des processus séparés |
| ImageMagick portable Q16 | 7.1.2-34 | ImageMagick License, https://github.com/ImageMagick/ImageMagick/tree/7.1.2-34 |
| libjxl cjxl / djxl | 0.11.1 | BSD-3-Clause, https://github.com/libjxl/libjxl/tree/v0.11.1 |
| libjxl délégué ImageMagick | 0.12.0 | BSD-3-Clause, dans le binaire ImageMagick épinglé |
| CLIP ViT-B-32 / XLM-RoBERTa LAION-5B | révision ONNX 111f6ddca817bb6cd8343c3e3dc5ce39d9ba54b0 | Poids MIT selon la carte LAION ; tokenizer XLM-RoBERTa MIT ; export https://huggingface.co/immich-app/XLM-Roberta-Base-ViT-B-32__laion5b_s13b_b90k |
| Inter | dépendance Avalonia.Fonts.Inter | SIL Open Font License 1.1 |
| xUnit et SDK de tests | versions dans Directory.Packages.props | Apache-2.0 / MIT, uniquement développement |

FFmpeg est compilé avec des bibliothèques GPL, notamment x264/x265. Ce binaire n'est pas présenté comme LGPL. Ses options de compilation et versions des bibliothèques sont conservées dans `runtimes/win-x64/native/ffmpeg-README.txt` et dans le diagnostic `ffmpeg -version`. Sources correspondantes : https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz et liens de sources externes du README Gyan. Avant distribution publique, constituer une offre de sources et fournir les sources correspondantes de tous les composants GPL conformément à leurs licences. Le dossier de publication du prototype ne constitue pas à lui seul un dossier de conformité juridique final.

Les délégués ImageMagick ont leurs propres licences (libheif, libjxl, libjpeg, libpng, libtiff, WebP, lcms, etc.). Le SBOM officiel de l'archive est conservé dans `licenses/`. L'activation d'AV1/AVIF/HEIF ne constitue aucune garantie concernant les brevets applicables dans un pays donné.

Corpus de tests scikit-image v0.19.3 : `chelsea.png` (Stefan van der Walt, CC0), `coffee.png` (Rachel Michetti, CC0), `astronaut.png` (NASA, domaine public). Voir `tests/fixtures/README.md` et les conditions amont.
