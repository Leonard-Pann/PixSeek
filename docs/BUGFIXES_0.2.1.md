# PixSeek 0.2.1 — corrections de lecture et de performances

Cette révision complète la [révision 0.2.0](UX_UPDATE_2026-10-20.md).

## Changements

- Vidéo : FFprobe fournit la rotation et le rapport de pixel (SAR). La surface LibVLC respecte le format d'affichage, et Avalonia utilise un ajustement proportionnel avec bandes latérales ou horizontales.
- Fin de lecture : EndReached fixe la position au maximum et affiche la durée complète arrondie à la seconde. Play recrée une entrée VLC, depuis le début ou depuis la position choisie après la fin. Une icône vectorielle de haut-parleur remplace la note de musique.
- Navigation : le média précédent disparaît immédiatement sur fond noir. Décodage, initialisation VLC et arrêt du lecteur passent hors du thread UI ; les requêtes obsolètes sont annulées. Le cache photo reste conservé.
- Indexation : jusqu'à trois workers de métadonnées/miniatures travaillent indépendamment d'un worker ONNX. La file contient des références aux médias planifiés, sans bitmaps ni vecteurs ; les statuts SQLite permettent la reprise. Les miniatures continuent même si l'initialisation ONNX attend. La progression conserve un total fixe et limite les notifications UI à environ dix par seconde, avec notification à chaque média indexé.
- Modèles : le toggle sélectionné devient actif immédiatement et reste utilisable. Les changements successifs sont sérialisés et la dernière sélection est prioritaire. La réindexation ne maintient plus les paramètres en état occupé.
- Galerie : chargement SQL par lots de 240 au défilement, virtualisation des lignes et nombre explicite de colonnes sur chaque ligne. Une dernière ligne incomplète garde des cases de même largeur.
- Filtres : SQL filtre toute la bibliothèque avant pagination ; vec0 reçoit les identifiants admissibles avant KNN. La migration 2 resynchronise les anciennes colonnes type/dossier/nom avec les fiches JSON. Les upserts mettent désormais aussi à jour ces colonnes.
- Compression avancée : JPEG XL et AV1 sont les valeurs initiales lorsque disponibles. ICompressionCodecPolicy centralise les décisions de défaut et peut être remplacée pour prendre en compte de futures capacités matérielles. Un choix explicite déjà enregistré est conservé.

## Validation Windows

Build .NET 10 / Avalonia 12.1.1 sans erreur ni avertissement. Suite complète : 34 tests réussis, aucun échec ni test ignoré (environ 1 min 40 s). Rapport TRX : tests/PixSeek.Tests/TestResults/pixseek-0.2.1.trx. La publication autonome 0.2.1 a également exécuté une inférence texte CPU 512D normalisée, chargé sqlite-vec/LibVLC et confirmé les codecs AV1/JXL/AVIF. Sa fenêtre native PixSeek a été ouverte puis fermée avec un code de sortie 0 ; rapports artifacts/published-diagnostics-0.2.1.json et artifacts/native-window-0.2.1.json.

Nouveaux tests : filtres et pagination de 3 200 fiches (2 400 photos / 800 vidéos), KNN filtré parmi 3 200 vecteurs avec une catégorie absente du top global, réparation de données historiques, défilement déclenchant le lot suivant, colonnes réelles des lignes, écran noir immédiat lors de navigation depuis le cache, vraie vidéo H.264 avec matrice de rotation, ratio anamorphique, fin exacte, replay et seek après fin.

Le test de concurrence suspend l'initialisation du service ONNX réel, constate que les 24 miniatures existent pendant cette attente, libère le service puis vérifie les 24 indexations réelles. Le test de modèles utilise une bibliothèque réelle et sélectionne l'autre modèle alors que l'indexation est en pause.

Les fixtures de 3 200 fiches/vecteurs servent aux tests de requêtes et d'affichage ; elles ne représentent pas 3 200 inférences ni une mesure de pertinence IA. Les vrais modèles et les vrais codecs restent utilisés par les tests sémantiques et de conversion.

## Utilisation et limites

Lancer artifacts/publish/win-x64/PixSeek.exe avec tout son dossier, ou dotnet run --project PixSeek.Desktop. Le modèle XLM-R/ViT-B32 reste inclus dans la publication ; aucune réinstallation des modèles ni suppression de cache n'est nécessaire.

Les corrections sont validées sous Windows sur des médias générés et le petit corpus public. Le corpus personnel d'environ 3 000 fichiers n'a pas été modifié ni réindexé par les tests. Les performances 4K/HDR, tous les appareils/caméras et macOS/Linux restent à évaluer. La recherche sémantique affiche au plus 240 médias ; le défilement continu concerne la photothèque hors recherche. L'IA reste sur CPU, avec parallélisme interne ONNX borné ; un taux de CPU précis n'est pas garanti.
