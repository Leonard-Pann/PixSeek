# Tests et validation

Prérequis d'intégration Windows : SDK .NET 10, `scripts/bootstrap-windows.ps1`, puis `scripts/install-model.ps1`. La suite utilise xUnit **3.2.2** et Avalonia Headless **12.1.1**, alignés sur Avalonia 12.

```powershell
dotnet restore
dotnet build
dotnet test --logger trx
```

Les tests CPU ONNX et codecs sont de vrais tests d'intégration, non substitués par des réponses programmées. Les modèles/binaries absents entraînent un échec explicite. Le petit corpus public est versionné dans `tests/fixtures` ; les fichiers générés et bases isolées restent dans `artifacts/tests`.

## Couverture

- Schéma et migrations idempotentes, insertion/mise à jour, KNN sqlite-vec, dimensions invalides, changement de modèle et retrait de bibliothèque.
- Normalisation RGB/NCHW, deux sessions ONNX, vecteurs finis/normalisés, répétabilité d'une image, vrai classement texte français sur chat/café/astronaute.
- Pipeline complet scan → métadonnées → cache → ONNX → sqlite-vec, vidéo de deux scènes réellement encodée puis échantillonnée, timestamp retrouvé, fichier corrompu, modification, réouverture, absence de recalcul et dossier déconnecté.
- PNG → JPEG XL lossless avec signatures des pixels ; JPEG → JXL réversible avec JPEG reconstruit au SHA-256 identique ; H.264+AAC → AV1 avec audio et décodage intégral ; erreurs, noms accentués, conservation des originaux et annulation de processus.
- AVIF avec alpha, JPEG XL perceptuel, profil ICC réellement présent et comparé au SHA-256, rejet déterministe d'un AVIF plus gros que son PNG d'origine, reprise d'un job interrompu sans écrasement et collision JPEG/PNG de même nom.
- Rendu réel Skia de la photothèque, de la recherche, des paramètres et du panneau superposé de compression, thème sombre et contrôle du nombre de lignes effectivement réalisées par le panneau virtualisé. Les lignes synthétiques de ce test servent seulement au test d'affichage ; elles ne sont pas des résultats sémantiques.
- Filtres type/date automatiques, absence de sélection des lignes, conservation des instances de cartes/lignes/miniatures et de la sélection pendant les rafraîchissements d'indexation, sans remise à zéro des collections.
- Clic droit sur une sélection, panneau dans le même TopLevel, modes et curseurs effectivement liés aux paramètres, conversion JPEG XL depuis le ViewModel avec destination personnalisée et sources conservées.
- Destination personnalisée exclue des scans ultérieurs ; refus de sortie identique à l'entrée ; progression d'indexation monotone avec total fixe et étapes par frame vidéo.

Les tests Headless écrivent `artifacts/pixseek-page-*.png`, `pixseek-gallery-dark.png` et `pixseek-compression-*.png`. Ils ne prouvent pas à eux seuls le bon fonctionnement du sélecteur natif de dossiers ou d'un lecteur vidéo avec sortie audio.

Le test exécute ensuite le vrai scan et la requête « un chat » via les ViewModels. `artifacts/pixseek-real-french-search.png` montre le chat réellement classé avant le café et l'astronaute. Les rapports TRX restent dans `tests/PixSeek.Tests/TestResults`.

## Test manuel Windows

1. Démarrer `dotnet run --project PixSeek.Desktop`.
2. Télécharger le modèle depuis Paramètres si nécessaire ; vérifier les diagnostics.
3. Ajouter `tests/fixtures`, attendre le scan ; rechercher « un chat » puis « une tasse de café ».
4. Ouvrir une image, zoomer, naviguer au clavier, ouvrir son emplacement.
5. Ajouter une vraie vidéo ; rechercher une scène puis ouvrir le résultat (FFplay externe au timestamp).
6. Depuis Compresser, choisir un dossier de copies de test et une sortie distincte ; lancer Équilibré. Répéter depuis un clic droit sur une sélection et vérifier que l'entrée n'est pas demandée. Tester JPEG XL sans perte via Paramètres avancés.
7. Vérifier `PixSeek Optimized/rapport-*.json`, la présence des sources et l'absence d'écrasement.
8. Interrompre le traitement puis relancer avec les mêmes options ; vérifier les éléments réutilisés.
9. Fermer/rouvrir et rescanner un dossier inchangé ; les miniatures et embeddings doivent être réutilisés.

## Publication

`publish-windows.ps1` produit `artifacts/publish/win-x64`. Le lancement natif et `PixSeek.exe --diagnostics <rapport.json>` ont été exécutés sur la machine de développement. Une VM Windows vierge, les interactions manuelles exhaustives et les systèmes macOS/Linux ne sont pas validés ici. Le rapport de développement distingue ces limites.
