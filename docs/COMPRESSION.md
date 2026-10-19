# Compression : fonctionnement et garanties du prototype

Les originaux ne sont jamais écrasés, déplacés ou supprimés. La destination par défaut est `source/PixSeek Optimized/`, exclue des scans récursifs. Une autre destination est sélectionnable dans le panneau superposé ; les destinations personnalisées enregistrées sont également exclues des scans ultérieurs. Une destination identique à la source est refusée. Les sorties conservent les sous-dossiers, l'extension source et un suffixe de chemin SHA-256 pour éviter les collisions. Une sélection hors du dossier commun est placée sous `Sélection` dans la destination, sans chemin relatif traversant vers l'extérieur.

## Panneau de compression

Le bouton sous la recherche et le menu contextuel ouvrent un panneau dans la fenêtre principale. Une sélection conserve uniquement les fichiers sélectionnés et masque l'entrée ; sans sélection, un dossier d'entrée est demandé. La sortie est toujours modifiable. Annuler interrompt aussi une analyse en cours, sans lancer ensuite la conversion.

| Mode simple | Images | Vidéos |
|---|---|---|
| Qualité | JPEG XL distance 0,5 ; AVIF qualité 90 | AV1 CRF 24 |
| Équilibré | JPEG XL distance 1 ; AVIF qualité 75 | AV1 CRF 30 |
| Meilleure compression | JPEG XL distance 3 ; AVIF qualité 45 | AV1 CRF 38 |

Ces trois modes privilégient des compromis visuels, sans promettre une conversion sans perte. Le commutateur avancé expose le codec photo Auto/JPEG XL/AVIF/Conserver selon les capacités, un curseur qualité 1–100 et un choix sans perte JPEG XL ; côté vidéo, AV1/Conserver et un curseur distinct. Le curseur photo donne une distance JXL `max(0,1 ; 0,1 + (100 - qualité) × 0,09)` ou une qualité AVIF directe. Le curseur vidéo donne `CRF = arrondi(50 - qualité × 0,30)` (20–50). Plus le curseur est élevé, plus la fidélité est privilégiée.

## Profils centralisés

| Profil | Images | Vidéos |
|---|---|---|
| Préserver | JPEG réversible cjxl/djxl si disponible ; sinon JPEG XL distance 0 avec vérification des pixels | Conservées, aucun AV1 avec perte |
| Qualité élevée | JPEG XL distance 1 ; AVIF qualité 75 si choisi | AV1 CRF 30, SVT preset 8, résolution/framerate conservés |
| Maximum | JPEG XL distance 3 ; AVIF qualité 45 si choisi | AV1 CRF 38, SVT preset 8, dimensions conservées |

Les paramètres sont des choix initiaux, pas une équivalence universelle de qualité. L'effort JPEG XL (7 par défaut, 1–9) contrôle le coût d'encodage, distinct de la distance. Le mode AVIF lossless est refusé car cette voie n'est pas validée. Les vidéos déjà AV1, images JXL/AVIF, images animées/multipages et vidéos HDR sont conservées. Aucun downscale silencieux.

Le moteur conserve ses profils centralisés ci-dessus, dont Préserver pour les appels métier et tests. Le panneau présente les trois modes simples ci-dessus ; le sans perte image est un réglage avancé. Les pistes audio/sous-titres sont copiées, le conteneur vidéo est MKV, le pixel format est yuv420p10le, l'effort JXL est 7, la vitesse SVT 8 et la concurrence est fixée à un encodage.

## Commandes représentatives

L'application construit `ProcessStartInfo.ArgumentList` sans shell. Ces lignes sont des exemples lisibles des commandes du moteur.

```text
cjxl photo.jpg sortie.partial.jxl -d 0 -e 7 --lossless_jpeg=1
djxl sortie.partial.jxl reconstruction.jpg
magick photo.png -define jxl:distance=0 -define jxl:effort=7 JXL:sortie.partial.jxl
magick photo.jpg -define jxl:distance=1 -define jxl:effort=7 JXL:sortie.partial.jxl
magick photo.png -quality 75 -define heic:speed=4 AVIF:sortie.partial.avif
ffmpeg -nostdin -hide_banner -v error -n -i source.mp4 -map 0 -map_metadata 0 -map_chapters 0 -c copy -c:v libsvtav1 -crf 30 -pix_fmt yuv420p10le -preset 8 -svtav1-params lp=4 -progress pipe:1 -nostats -f matroska sortie.partial.mkv
```

Si SVT-AV1 est absent mais libaom-av1 présent : `-cpu-used`, `-b:v 0`, `-row-mt 1`, quatre threads. Si aucun n'est présent, le fichier est ignoré avec une raison explicite. La configuration Windows fournie dispose de SVT-AV1 et libaom-av1. Les formats JXL/AVIF sont activés après vérification de `magick identify -list format` ; la présence de `magick` seule ne suffit pas.

## Vérification

- Toutes les images : existence, taille non nulle, décodage complet, dimensions, frames, présence d'alpha si la source en a.
- Images lossless : comparaison des signatures ImageMagick des pixels décodés. Une simple égalité de dimensions ne suffit pas.
- JPEG réversible : décodage djxl en JPEG puis **égalité SHA-256 du fichier source reconstruit**. Ce mécanisme est distinct d'un réencodage sans perte des pixels.
- Métadonnées demandées : date EXIF de prise de vue si présente et profil ICC vérifiés ; les autres métadonnées sont conservées au mieux par l'encodeur, sans promesse exhaustive.
- Vidéos : sortie AV1, résolution, durée à ± max(500 ms, 1 %), nombres de pistes audio/sous-titres et total de streams ; décodage intégral FFmpeg avec `-xerror`. Les pistes sont copiées sans réencodage par défaut. Une incompatibilité de piste fait échouer le fichier et ne supprime rien.
- La source est à nouveau contrôlée (taille/date) avant validation, pour refuser un résultat calculé pendant sa modification.
- Le nom définitif n'est utilisé qu'après validation. `File.Move(..., overwrite:false)` protège les sorties existantes.
- Si la sortie n'est pas plus petite, elle est supprimée et classée « Aucun gain » par défaut. Le bilan final compte alors la taille de l'original ; le gain reste zéro.

L'analyse donne un nombre et une taille réels par format. Aucune estimation de gain précise n'est inventée. Le taux de compression dépend du contenu et des paramètres ; il n'est connu qu'après encodage.

## Progression et reprise

FFmpeg donne une progression dans le fichier grâce à `out_time_us`. Les conversions image n'offrent pas de pourcentage interne fiable ; l'UI avance à la fin des fichiers. Le temps écoulé et les tailles réellement obtenues sont utilisés. Aucun temps restant artificiel n'est calculé. La pause prend effet entre fichiers. L'annulation termine l'arbre de processus et nettoie la sortie temporaire.

Les éléments terminés sont persistés en SQLite. Une relance avec le même dossier, la même destination, la même sélection et les mêmes options retrouve un job interrompu. Si une sortie existe sans journal correspondant, elle est conservée et ignorée. Le rapport `rapport-<job>.json` contient sources, sorties, tailles, statuts, vérifications et erreurs. Il ne faut pas sommer la taille du dossier source et celle de la destination pour mesurer le gain : le prototype conserve délibérément les deux exemplaires.

## Licences

Les outils Windows Gyan sont GPLv3, pas LGPL. Les commandes complètes de compilation figurent dans leur README embarqué. Voir `THIRD_PARTY_NOTICES.md`, `DEPENDENCIES.md` et les licences avant redistribution publique.
