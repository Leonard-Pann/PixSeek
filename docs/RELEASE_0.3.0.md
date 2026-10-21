# PixSeek 0.3.0 — 21 octobre 2026

## Fonctionnalités

- Clic droit dans la visionneuse : Copier le média. Les photos exposent un bitmap indépendant du cache et le fichier original ; les vidéos exposent le fichier. Le presse-papier Windows est vidé vers les formats natifs persistants par FlushAsync. La copie reste valable après navigation, fermeture de la visionneuse et vidage du cache.
- Croix dans la partie droite du champ de recherche. Elle efface le texte et recharge la bibliothèque en conservant les filtres. Les recherches validées et cet effacement demandent un retour en haut, sans modifier la position lors des simples rafraîchissements de fond.
- Compression : choix d'un dossier importé dans une liste, saisie du chemin ou Parcourir ; JPEG XL/AV1 préselectionnés à chaque ouverture lorsque disponibles. Les listes ne sont plus vidées/reconstruites inutilement pendant qu'elles sont liées à ComboBox.
- Compression : les résultats et la reprise sont conservés dans SQLite, sans nouveau rapport JSON dans le dossier de sortie. Les anciens rapports existants ne sont pas supprimés automatiquement.
- Visionneuse : flèches centrées sur toute la surface, à la même hauteur pour photo et vidéo. Les timestamps de scènes concernent le résultat ouvert explicitement ; la navigation suivante/précédente démarre les vidéos à zéro. La progression ne déclenche plus un seek natif pendant l'initialisation.
- Personnes : panneau superposé, références depuis la sélection ou le sélecteur, choix des visages, mots-clés multiples, ajout et retrait de profils, analyse faciale en arrière-plan et priorité dans les résultats. Voir [PEOPLE.md](PEOPLE.md) pour les modèles, les étapes précises et les limites.

## Validation

Compilation .NET 10 / Avalonia 12.1.1 sans erreur ni avertissement. Suite complète : 38 tests réussis, aucun échec ni test ignoré ; rapport tests/PixSeek.Tests/TestResults/pixseek-0.3.0.trx. Les derniers tests ciblés ont aussi vérifié le clic du menu Copier, le démarrage vidéo après quelques centaines de millisecondes et l'annulation/réouverture du panneau de personnes. La publication autonome a passé les diagnostics natifs et son ouverture/fermeture Windows, code 0 ; les deux modèles faciaux embarqués ont été vérifiés au SHA-256. Rapports : artifacts/published-diagnostics-0.3.0.json et artifacts/native-window-0.3.0.json.

Nouveaux tests : copie de bitmap/fichier, survie après fermeture/cache, position des flèches, timestamp explicite puis navigation à zéro, croix de recherche et retour en haut, réouvertures du panneau et sélections effectives des ComboBox, dossier importé, absence de JSON de sortie, YuNet/SFace réels et recherche faciale persistée.

La copie est testée par aller-retour via l'API de presse-papier Avalonia Headless. Le collage dans Microsoft Teams/Discord n'a pas été automatisé et aucune publication de message externe n'a été effectuée. La reconnaissance faciale est validée sur le petit corpus documenté dans PEOPLE, pas sur une grande population ni sur les médias privés de l'utilisateur.

Windows est la plateforme locale de validation. Les scripts Linux/macOS incluent le téléchargement vérifié des modèles faciaux ; leur exécution n'est pas validée ici.
