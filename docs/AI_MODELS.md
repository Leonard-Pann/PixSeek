# Modèle IA local

## Choix vérifié

[LAION CLIP ViT-B-32 XLM-RoBERTa Base LAION-5B](https://huggingface.co/laion/CLIP-ViT-B-32-xlm-roberta-base-laion5B-s13B-b90k), poids MIT d'après la carte du modèle. Il est entraîné sur LAION-5B avec un encodeur texte XLM-RoBERTa et possède une capacité multilingue documentée. Ce choix permet des requêtes françaises sans traduction cloud. Ce n'est pas le CLIP anglais OpenAI par défaut.

[Export ONNX Immich](https://huggingface.co/immich-app/XLM-Roberta-Base-ViT-B-32__laion5b_s13b_b90k), révision fixe `111f6ddca817bb6cd8343c3e3dc5ce39d9ba54b0`.

| Fichier | Taille | SHA-256 |
|---|---:|---|
| `textual/model.onnx` | 1 113 269 728 octets | ca4a815f14666700479ced66ee21cf7ab8878715ba71d144f18bc9571ef152cd |
| `visual/model.onnx` | 351 613 724 octets | 07a431b489757821d9e7460d5e98ecc03aa682f47ffb364b99879e1d6613a54b |
| `textual/tokenizer.json` | 17 082 734 octets | 3a56def25aa40facc030ea8b0b87f3688e4b3c39eb8b45d5702b3a1300fe2a20 |

Les configurations, URLs, tailles et tous les hashes sont dans `tools/models/manifest.json`. Aucune exportation Python n'est nécessaire : les deux graphes distribués ont été chargés et exécutés en C# sous Windows.

## Entrées validées

- Image : `image`, float32 `[1,3,224,224]`.
- Texte : `text`, int32 `[1,77]`.
- Sorties : un vecteur de 512 float32 par encodeur, normalisé L2 après inférence.
- Tokenizer : JSON Hugging Face XLM-RoBERTa Unigram, avec son normaliseur, prétraitement et post-traitement exacts. Aucun découpage artisanal par mots.
- Padding ID 1, BOS 0, EOS 2, maximum 77 tokens ; les tokens sont tronqués en gardant EOS. Le type entier est choisi d'après les métadonnées ONNX.

## Prétraitement image

ImageMagick décode la première frame, corrige l'orientation, convertit en sRGB, compose l'alpha sur blanc pour l'IA seulement, redimensionne le côté court à 224 en bicubique Catrom puis crop central 224 × 224. Les octets RGB sont convertis en float32 NCHW :

```text
mean = [0.48145466, 0.4578275, 0.40821073]
std  = [0.26862954, 0.26130258, 0.27577711]
pixel = (byte / 255 - mean[channel]) / std[channel]
```

Les paramètres viennent de `visual/preprocess_cfg.json`. Catrom est l'approximation bicubique utilisée par ce pipeline ImageMagick ; elle peut présenter de petites différences d'arrondi par rapport au resize Pillow de référence. Les tests valident la recherche sur corpus réel, pas une identité bit à bit avec l'implémentation Python d'Immich.

## Installation et état

Le bouton Paramètres télécharge en streaming avec progression, fichier `.partial`, contrôle SHA-256 et renommage après validation. Il permet de recommencer après erreur et d'annuler. Un fichier présent mais corrompu n'est pas chargé. La présence et la taille servent seulement à afficher « poids présents » ; le chargement vérifie les hashes. Deux sessions CPU sont réutilisées. Le parallélisme interne est borné à huit threads maximum. Prévoir plusieurs Go de RAM libre (les poids seuls représentent environ 1,48 Go).

`scripts/install-model.ps1` prépare le même ensemble dans `artifacts/models` pour les tests ou un dossier choisi avec `-Destination`. `publish-windows.ps1 -IncludeModel` permet d'embarquer les poids. Le fonctionnement hors ligne ne tente aucun accès cloud d'inférence.

## Recherche sqlite-vec

Syntaxe effectivement testée avec **0.1.6** :

```sql
CREATE VIRTUAL TABLE MediaVector USING vec0(embedding float[512] distance_metric=cosine);
SELECT rowid, distance FROM MediaVector WHERE embedding MATCH $vector AND k=$k;
```

Les paramètres sont des BLOB float32. Le score UI est `1 - distance`. Il s'agit d'une similarité, pas d'une probabilité ni d'une certitude. Les résultats sont ordonnés par distance croissante puis dédupliqués par média. Les filtres UI sont actuellement appliqués après les 240 meilleurs médias ; voir limites.

Le test réel retrouve en tête le chat, le café et l'astronaute pour trois requêtes françaises. Le test de pipeline indexe aussi une vidéo à deux scènes et retrouve un timestamp dans la partie astronaute. Ce petit corpus valide le câblage et quelques cas ; ce n'est pas une évaluation exhaustive de qualité sur une photothèque personnelle.

GPU : abstraction présente, seule l'exécution **CPU** est implémentée et testée. Aucun backend GPU fictif n'est proposé.
