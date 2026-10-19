# Limites connues

## Implémenté mais validation bornée

- La recherche locale française est testée sur trois images publiques et une vidéo à deux scènes ; sa qualité n'est pas mesurée sur une grande collection réelle. Le modèle est relativement volumineux et nécessite plusieurs Go de RAM libre.
- Le rendu des pages, la navigation et la virtualisation sont testés avec Avalonia Headless et inspectés. Le lancement de la fenêtre native est testé. Les sélecteurs de dossiers natifs et toutes les interactions utilisateur n'ont pas été automatisés.
- JPEG XL, JPEG réversible, AV1/audio sont testés sous Windows avec le jeu de médias décrit dans TESTING. Pas de campagne exhaustive sur toutes les caméras, profils ICC, sous-titres ou conteneurs.
- La publication autonome Windows a été exécutée sur la machine de développement, pas sur une VM vierge.

## Partiellement implémenté

- **Lecture vidéo** : FFplay embarqué fonctionne comme lecteur externe et reçoit le timestamp. Pas de surface LibVLC intégrée à Avalonia. Ses commandes clavier sont indiquées dans les paramètres.
- **Visionneuse photo** : aperçu haute résolution limité à 3 200 pixels, zoom/pan/clavier ; pas de tuilage pleine résolution ni d'édition.
- **Recherche filtrée** : filtres type/dossier/dates appliqués aux 240 meilleurs médias récupérés. Des résultats pertinents au-delà de ce sous-ensemble peuvent être exclus ; pas encore de filtre poussé dans la requête vec0. Les dates correspondent à la modification du fichier, pas encore à une chronologie EXIF.
- **Scènes vidéo** : échantillonnage borné et meilleur timestamp par vidéo. Une scène entre deux frames peut être manquée ; pas de détection de changements de plans ni de liste de toutes les scènes correspondantes.
- **Indexation incrémentale** : taille/date/version/cache. Les déplacements ne réutilisent pas encore les embeddings par hash, et les doublons ne sont pas fusionnés. Le scan manuel réconcilie ; pas de FileSystemWatcher continu.
- Les racines de bibliothèque qui se recouvrent sont refusées explicitement pour éviter une double propriété des mêmes fichiers. Les sous-dossiers peuvent être activés/désactivés dans Paramètres, puis rescannez.
- **Jobs** : reprise des scans par statuts persistés, compression journalisée avec reprise par relance du même dossier/options. Pause entre fichiers uniquement. Pas de gestionnaire universel durable de sous-tâches, ni de reprise à l'intérieur d'un encodeur.
- **Métadonnées** : dimensions, durée, codec, alpha/frames, dates de modification. Extraction/chronologie complètes EXIF/GPS/orientation/identité de fichier et recherche de doublons restent à enrichir. La vérification des métadonnées de conversion porte sur les informations critiques décrites dans COMPRESSION.
- **Cache** : disque invalidé par signature, décodage visible borné et méthode de nettoyage présente. Pas encore d'interface complète de quota ni de nettoyage automatique tenant compte de toutes les références de frames.
- **Paramètres** : thème, bibliothèque, modèle, intervalle/plafond vidéo et options de compression persistent. Localisation anglaise, choix du cache/quota depuis l'UI et paramètres de compression additionnels restent à développer.
- **Packaging Unix** : scripts et CI présents ; non exécutés et dépendances de build apt/Homebrew partiellement non épinglées. Fermeture des modules ImageMagick, signature macOS et compatibilité graphique à valider sur place.

## Non implémenté

- LibVLC intégré, accélération GPU, index ANN, génération de tags, reconnaissance de personnes, groupes chronologiques EXIF, recherche par doublons et monitoring automatique.
- Conversion HDR garantie, images animées/multipages, option vidéo MP4, adaptation audio Opus/AAC, redimensionnement/framerate avancés et encodages parallèles configurables : les chemins non validés ne sont pas proposés.
- Suppression/remplacement automatique d'originaux, installateur, mises à jour automatiques, signature/notarisation commerciale.
- Android/iOS/Browser, ARM64 desktop, tests/macOS/Linux exécutés et distribution publique juridiquement finalisée.

## Priorités suivantes

1. Essai Windows vierge et campagne sur un corpus varié avec EXIF/ICC, orientation, alpha, sous-titres et vidéos longues.
2. Lecteur LibVLC Avalonia et visionneuse en tuiles pour très grandes photos.
3. Filtres vectoriels avant KNN, pagination de résultats et recherche de multiples scènes.
4. Extraction EXIF complète, identité/hash pour déplacements et doublons, cache avec quota automatique.
5. Exécuter les jobs Unix, verrouiller leurs toolchains, finaliser licences/sources correspondantes, signer les paquets.
