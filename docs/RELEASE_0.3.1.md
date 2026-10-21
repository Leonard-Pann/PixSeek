# PixSeek 0.3.1 — sélection, personnes et ouverture des médias

## Changements implémentés

- Le conteneur de ligne virtualisé n'a plus de chrome de survol/sélection. Seule la carte sous le pointeur reçoit un contour.
- Tout sélectionner / Tout désélectionner sont à gauche du filtre de type. La sélection globale couvre toutes les pages de la vue filtrée et reste utilisable pour la compression. Un compteur rend visibles les sélections hors écran ; les changements groupés ne notifient ce compteur qu'une fois.
- Le bouton de contrôle de l'index sémantique affiche Pause ; il garde son comportement pause/reprise.
- Toutes les personnes, à droite du tri, liste les profils sauvegardés avec leurs alias. Le filtre se combine avec type, dossier, recherche par nom et recherche sémantique. Son prédicat est appliqué avant pagination/top K. Effacer les filtres rétablit Toutes les personnes.
- Les visages sont détectés à l'import et au rattrapage de bibliothèques existantes, même sans personne enregistrée. Ce worker et ses sessions ONNX sont indépendants du worker sémantique et des miniatures. Ajouter la première personne réutilise les signatures/vecteurs prêts.
- PixSeek possède une icône originale lavande/corail, intégrée à l'exécutable Windows et à MainWindow. Les sept tailles ICO vont de 16 à 256 pixels. Source SVG et PNG : PixSeek/Assets. Reproduction : pwsh -File scripts/build-icon.ps1.

## Causes de lenteur corrigées

Le cache photo maintenait son sémaphore unique pendant le décodage et le fallback ImageMagick, y compris pendant un préchargement. Une ouverture et même un accès au cache pouvaient donc attendre une autre image. Les verrous ne protègent plus que les structures de données ; les demandes identiques fusionnent, et l'ouverture dispose d'un slot distinct du préchargement. Le vidage/annulation/éviction respectent les leases des bitmaps.

Pour une image déjà orientée, une allocation et une copie de surface sont évitées. Les PNG sont effectivement réduits à 4096 pixels maximum même lorsque Skia ne propose pas de décodage réduit. Un budget de 64 MP protège le décodage direct ; les grands fichiers et formats rares conservent le fallback ImageMagick/cache disque.

Pour les vidéos, une instance LibVLC est réutilisée, au lieu de redécouvrir ses plugins à chaque média. Au plus deux players peuvent être en préparation/arrêt, et l'ancien s'arrête pendant la préparation du nouveau. La géométrie d'affichage est enregistrée lors de l'import (rotation/SAR inclus), validée par signature à l'ouverture et rattrapée au prochain scan pour les anciens médias. La visionneuse réutilise aussi les métadonnées de repli en mémoire, avec un délai de probing borné à 10 s ; la vérification de compression utilise toujours FFprobe sans ce cache.

## Mesures

Le test OpeningBenchmark crée quatre images de 4000 × 3000 à partir du portrait NASA Eileen Collins, puis ouvre un court clip H.264. Il écrit les durées dans artifacts/opening-performance-0.3.1.json. Les valeurs mesurées sont des observations de la machine de développement et non un engagement pour d'autres formats, disques ou collections.

| Média | Première ouverture / frame | Depuis cache / 2e lecture |
|---|---:|---:|
| .jpg, 12 MP | 107 ms | 0.58 ms |
| .png, 12 MP | 160 ms | 0.65 ms |
| .webp, 12 MP | 73 ms | 0.51 ms |
| .jxl, 12 MP | 376 ms | 0.56 ms |
| H.264, 640 × 360 | 481 ms | 161 ms |

## Validation Windows

Le rapport complet est dans tests/PixSeek.Tests/TestResults/pixseek-0.3.1.trx. Les tests couvrent l'inférence réelle YuNet/SFace et sémantique, la sélection de 2400 photos parmi 3200 médias, 260 correspondances faciales paginées, les filtres avant top K, le survol d'une seule carte, les courses cache/annulation/vidage, le plafond des PNG, l'analyse faciale sans profil pendant un blocage volontaire de l'index sémantique, ainsi que la lecture vidéo et les conversions réelles JPEG XL/AV1 existantes.

dotnet restore --locked-mode et compilation Debug réussis sans avertissement ni erreur. Suite complète : **44 tests réussis, 0 échec, 0 ignoré** (2 min 16 s). Compilation Release : 0 avertissement, 0 erreur. Publication autonome Windows vérifiée (modèles SHA-256, licences et outils natifs). Fenêtre native réactive, fermeture normale : code 0. Diagnostic : ONNX CPU 512D normalisé, sqlite-vec 0.1.6, LibVLC 3.0.23, FFmpeg 8.1.2 et ImageMagick 7.1.2-34.

La capture artifacts/pixseek-hover-card.png permet l'inspection du survol. L'icône est testée par chargement du vrai MainWindow Avalonia ; les sept tailles de la ressource native et son contenu SHA-256 identique à la source sont contrôlés dans l'exécutable publié. Rapport : artifacts/release-validation-0.3.1.json.

## Lancer

Depuis le dépôt : dotnet run --project PixSeek.Desktop -c Release.

Version autonome : artifacts/publish/win-x64/PixSeek.exe. Conserver tout son dossier, avec modèles et dépendances natives.

## Limites

Le filtrage des personnes traite les photos, pas encore les visages dans les vidéos. Une bibliothèque ancienne doit finir son premier rattrapage facial ; elle ne peut pas produire des correspondances avant analyse. Après ce rattrapage, ajouter un nom ne réanalyse pas les photos inchangées.

La recherche sémantique reste limitée à 240 résultats ; Tout sélectionner sélectionne ces résultats lors d'une recherche. Hors recherche, toutes les correspondances filtrées sont sélectionnées sans limite de page.

Cette validation n'utilise pas le corpus personnel du développeur. Les formats rares, vidéos 4K/HDR et disques réseau/externes peuvent garder des latences supérieures. macOS et Linux n'ont pas été exécutés sur cette machine.
