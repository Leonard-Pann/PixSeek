# Personnes — PixSeek 0.3.0

## Utilisation

Sélectionner quelques photos dans la photothèque puis cliquer sur **Ajouter une personne**, ou utiliser **Parcourir** dans le panneau superposé. Jusqu'à 20 photos de référence peuvent être ajoutées à la fois. Le panneau montre les visages détectés, pas des identités supposées.

Une photo avec un seul visage le sélectionne initialement. Pour une photo de groupe, aucun visage n'est sélectionné automatiquement : choisir celui de la personne concernée. Un seul visage peut être coché par photo. Les références doivent représenter la même personne.

Entrer un ou plusieurs mots-clés séparés par des virgules, points-virgules ou retours à la ligne, par exemple **Camille, Maman**, puis Ajouter. Les références sont conservées et l'analyse de la photothèque continue en arrière-plan. Rechercher ensuite **Maman** ou **Camille à la plage**. Les photos dont le visage correspond aux références sont placées avant les autres résultats ; le classement hybride existant est conservé pour les autres médias.

Un mot-clé déjà enregistré ajoute les nouvelles références à la même personne. Les mots-clés doivent distinguer les personnes ; deux profils différents ne peuvent pas partager un même alias. Les profils sont retirables depuis le même panneau.

## Modèles réels

Les fichiers proviennent du dépôt officiel [OpenCV Zoo](https://github.com/opencv/opencv_zoo/tree/48b10fa505aeb51fbd127fc60a5c660040e823b7), à une révision fixe :

| Rôle | Fichier | Taille | Licence publiée |
|---|---|---:|---|
| Détection et cinq points du visage | face_detection_yunet_2023mar.onnx | 232 589 octets | MIT |
| Représentation faciale SFace | face_recognition_sface_2021dec.onnx | 38 696 353 octets | Apache-2.0 |

Les SHA-256 et URL exacts figurent dans tools/models/faces.json. Les licences sont copiées dans licenses/YuNet-MIT.txt et licenses/Apache-2.0.txt. Les poids sont exécutés par ONNX Runtime CPU en C# ; aucune bibliothèque native OpenCV ou installation Python n'est nécessaire.

La compilation et la publication incluent automatiquement les deux modèles dans `models/faces`, à côté de l'exécutable. Pour les préparer manuellement dans `models/faces` à la racine du dépôt :

    pwsh -File scripts/install-face-models.ps1

Le mécanisme MSBuild est commun à Windows, Linux et macOS. Si les poids ne sont pas disponibles au lancement, le panneau les télécharge dans ce même dossier local avec progression, fichier temporaire et contrôle SHA-256 ; Annuler interrompt l'opération. La disponibilité hors ligne suit l'installation complète.

## Algorithme

1. ImageMagick décode la première image, applique l'orientation EXIF et convertit en sRGB. L'image est réduite proportionnellement puis complétée en noir à droite/en bas dans une surface 640 × 640.
2. YuNet reçoit un tenseur NCHW BGR float32, valeurs 0–255. Les sorties des strides 8/16/32 fournissent classes, objectness, boîtes et cinq points. Le score vaut sqrt(classe × objectness), seuil 0,85. Une suppression non maximale avec IoU 0,3 élimine les doubles détections ; au plus 100 visages par image.
3. Une similitude à quatre paramètres est ajustée par moindres carrés sur les cinq points (yeux, nez, coins de bouche). Le rééchantillonnage bilinéaire inverse produit un visage RGB 112 × 112, selon les coordonnées de référence SFace. Le tenseur RGB float32 garde les valeurs 0–255 comme le [chemin officiel OpenCV](https://github.com/opencv/opencv/blob/4.x/modules/objdetect/src/face_recognize.cpp).
4. SFace produit un vecteur de 128 dimensions. Il est contrôlé (valeurs finies, norme non nulle) puis normalisé L2. Les vecteurs des références restent distincts ; le réseau n'est pas réentraîné.
5. Un index facial séparé calcule ces vecteurs pour les photos de la bibliothèque. FaceScan persiste signature taille/date/version et état ; FaceObservation relie les visages aux médias. FaceVector est une table sqlite-vec 128D cosine, indépendante de la table sémantique XLM-R/SigLIP2.
6. Une recherche contenant un alias entier, normalisé sans casse ni accents, compare chaque référence aux visages de la bibliothèque. Le filtre dossier et la validité taille/date/version sont appliqués avant KNN. Une similarité cosine d'au moins **0,45** constitue une correspondance candidate. Le meilleur visage par média est retenu ; les résultats sont dédupliqués et prioritaires. Plusieurs profils mentionnés favorisent les photos correspondant au plus grand nombre d'entre eux.

Ce seuil est un choix prudent de prototype, pas une probabilité ni une preuve d'identité. La similarité et les références sont utilisées pour retrouver les personnes désignées par l'utilisateur. Les modèles ne donnent aucun nom à une personne inconnue.

## Stockage, reprise et limites

Les personnes, alias, vecteurs de référence et observations des visages sont dans `pixseek.db`, dans les données utilisateur (`%LOCALAPPDATA%/PixSeek` sous Windows). Les modèles seuls sont dans `<exécutable>/models/faces`. Les photos sources restent référencées et ne sont pas modifiées. Les visages alignés de prévisualisation sont dans le cache ; Paramètres permet de vider ce cache. Retirer une personne supprime ses alias et ses références vectorielles. Retirer le dernier profil supprime aussi l'index facial dérivé ; les vignettes résiduelles restent nettoyables par le bouton de cache.

L'indexation faciale dispose de son propre worker et de sessions CPU limitées à deux threads internes. Elle peut fonctionner en parallèle avec la galerie et l'indexation sémantique. Les mêmes signatures sont réutilisées au redémarrage et lors des réconciliations automatiques. Les fichiers modifiés sont réanalysés et les fichiers absents sont exclus des recherches.

Cette révision traite **les photos**, pas encore les visages des frames vidéo. Les petits visages, profils, occlusions, flous, jumeaux, changements d'âge et d'éclairage peuvent produire des erreurs ou des absences. La réduction à 640 pixels peut manquer de petits visages dans une grande photo de groupe. Aucun taux de reconnaissance général n'est revendiqué. Le classement reste limité à 240 médias et au voisinage borné de 1 920 visages par référence.

Les tests réels utilisent deux portraits NASA du domaine public : Eileen Collins et Neil Armstrong, des variantes redimensionnées/assombries et une image de groupe générée avec les deux portraits. Ils vérifient détection, normalisation, ressemblance de la même personne, distinction des deux personnes, choix explicite dans un groupe, persistance, filtrage par dossier, priorité dans la recherche et suppression. Ce petit corpus n'est pas une évaluation sur une population représentative.

Les licences citées sont celles publiées dans les dossiers officiels des modèles. La provenance des données d'entraînement SFace fait l'objet de questions en amont ([discussion OpenCV](https://github.com/opencv/opencv_zoo/issues/313)) ; ce prototype ne prétend pas résoudre cette question pour une distribution commerciale future.
