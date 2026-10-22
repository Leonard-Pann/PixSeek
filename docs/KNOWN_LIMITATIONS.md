# Limites connues

## Implémenté mais validation bornée

- La recherche locale française est testée sur trois images publiques et une vidéo à deux scènes ; sa qualité n'est pas mesurée sur une grande collection réelle. Le modèle est relativement volumineux et nécessite plusieurs Go de RAM libre.
- Le rendu des pages, la navigation et la virtualisation sont testés avec Avalonia Headless et inspectés. Le lancement de la fenêtre native est testé. Les sélecteurs de dossiers natifs et toutes les interactions utilisateur n'ont pas été automatisés.
- JPEG XL, JPEG réversible, AV1/audio sont testés sous Windows avec le jeu de médias décrit dans TESTING. Pas de campagne exhaustive sur toutes les caméras, profils ICC, sous-titres ou conteneurs.
- La publication autonome Windows a été exécutée sur la machine de développement, pas sur une VM vierge.

## Partiellement implémenté

- **Lecture vidéo** : LibVLC intégré, avec frames copiées dans une surface Avalonia (1920 × 1080 maximum pour le rendu). Cette voie utilise le CPU ; performances 4K/HDR et toutes les pistes/sous-titres restent à évaluer.
- **Visionneuse photo** : aperçu haute résolution limité à 4 096 pixels, zoom/pan/clavier ; pas de tuilage pleine résolution ni d'édition.
- **Recherche filtrée** : type, dossier et personne sont poussés dans vec0 avant KNN. Le résultat sémantique reste borné à 240 médias ; pas encore de pagination des résultats sémantiques. La photothèque hors recherche utilise le défilement continu. Les dates de tri correspondent à la modification du fichier, pas encore à une chronologie EXIF.
- **Scènes vidéo** : échantillonnage borné et meilleur timestamp par vidéo. Une scène entre deux frames peut être manquée ; pas de détection de changements de plans ni de liste de toutes les scènes correspondantes.
- **Indexation incrémentale** : taille/date/version/cache. Les déplacements ne réutilisent pas encore les embeddings par hash, et les doublons ne sont pas fusionnés. Réconciliation automatique au lancement et chaque minute, complétée par FileSystemWatcher.
- Les racines de bibliothèque qui se recouvrent sont refusées explicitement pour éviter une double propriété des mêmes fichiers. Les sous-dossiers peuvent être activés/désactivés dans Paramètres, la réconciliation est automatique.
- **Jobs** : reprise des scans par statuts persistés, compression journalisée avec reprise par relance du même dossier/options. Pause entre fichiers uniquement. Pas de gestionnaire universel durable de sous-tâches, ni de reprise à l'intérieur d'un encodeur.
- **Métadonnées** : dimensions, durée, codec, alpha/frames, dates de modification. Extraction/chronologie complètes EXIF/GPS/orientation/identité de fichier et recherche de doublons restent à enrichir. La vérification des métadonnées de conversion porte sur les informations critiques décrites dans COMPRESSION.
- **Cache** : disque invalidé par signature, décodage visible borné et méthode de nettoyage présente. Pas encore d'interface complète de quota ni de nettoyage automatique tenant compte de toutes les références de frames.
- **Paramètres** : thème, bibliothèque, modèle, intervalle/plafond vidéo et options de compression persistent. Cinq langues JSON sont disponibles et enregistrées automatiquement. Le choix du cache/quota depuis l'UI et des paramètres de compression additionnels restent à développer.
- **Packaging Unix** : les trois publications Release sont compilées depuis Windows, avec dépendances natives verrouillées et vérification statique de fermeture/architecture. Aucun lancement Linux/Mac exécuté ici. Les sélecteurs natifs, la lecture audio/vidéo et la compatibilité graphique devront être essayés sur ces systèmes ; signature Developer ID/notarisation Mac absentes des builds Windows. Voir UNIX_RELEASES_0.3.1.md.

## Non implémenté

- Accélération GPU, index ANN, génération automatique de tags, reconnaissance de personnes dans les vidéos, groupes chronologiques EXIF, recherche par doublons.
- Conversion HDR garantie, images animées/multipages, option vidéo MP4, adaptation audio Opus/AAC, redimensionnement/framerate avancés et encodages parallèles configurables : les chemins non validés ne sont pas proposés.
- Suppression/remplacement automatique d'originaux, installateur, mises à jour automatiques, signature/notarisation commerciale.
- Android/iOS/Browser, ARM64 desktop, tests/macOS/Linux exécutés et distribution publique juridiquement finalisée.

## Priorités suivantes

1. Essai Windows vierge et campagne sur un corpus varié avec EXIF/ICC, orientation, alpha, sous-titres et vidéos longues.
2. Visionneuse en tuiles pour très grandes photos.
3. Pagination de résultats sémantiques et recherche de multiples scènes.
4. Extraction EXIF complète, identité/hash pour déplacements et doublons, cache avec quota automatique.
5. Exécuter les jobs Unix, verrouiller leurs toolchains, finaliser licences/sources correspondantes, signer les paquets.

## Précisions 0.2.0

SigLIP2 et XLM-R/ViT sont testés avec leurs vrais modèles ONNX sur le petit corpus public ; aucun classement sur 100 000 médias n'est revendiqué. La recherche de noms parcourt les noms de la base hors thread UI et applique un seuil conservateur ; elle n'est pas un moteur FTS complet.

Les aperçus photo sont bornés à 4 096 pixels et trois bitmaps en cache. Les codecs courants sont décodés directement ; un format non reconnu par Skia utilise un aperçu ImageMagick persistant. Ce premier décodage de secours peut donc être plus lent. Le benchmark du test ne vaut pas pour tous les fichiers ni toutes les machines.

Le modèle actif ne se supprime pas pendant son utilisation ; sélectionner d'abord l'autre modèle installé. Une installation dans un répertoire protégé peut également empêcher la suppression des poids embarqués et produit alors une erreur explicite.

Les dépendances VLC Unix sont préparées depuis les verrous Debian/VideoLAN, sans installation séparée de VLC. Leur architecture et fermeture statique sont contrôlées ; le lancement, les périphériques audio et les sélecteurs natifs restent à vérifier sur Linux/Mac. Certaines erreurs techniques des outils natifs restent dans leur langue d'origine ; les textes courants de l'interface utilisent les JSON.

Depuis 0.3.0, les personnes dans les photos peuvent être retrouvées avec YuNet/SFace et des références choisies par l'utilisateur. Le corpus de validation est petit, le seuil conservateur et les erreurs restent possibles. Voir PEOPLE.md. Le collage dans Teams/Discord n'a pas été automatisé ; les formats image/fichier sont testés via le presse-papier Avalonia.

Depuis 0.3.1, le cache photo ne sérialise plus le préchargement avec l'ouverture d'un autre fichier. Les mesures du benchmark portent sur quatre photos générées de 12 MP et un court clip H.264 local ; elles ne constituent pas un profil du corpus personnel de l'utilisateur. Les aperçus de formats rares et les fichiers sur disque externe/réseau peuvent toujours être plus lents. L'index facial à l'import concerne uniquement les photos, pas les frames vidéo.
