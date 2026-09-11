# Modèles plus grands, sources et analyse par étapes — 11 septembre 2026

Cette campagne compare Qwen 3.5 9B et Gemma 4 12B à Qwen 3.5 4B sur cinq instances récemment parcourues, puis sépare les effets des consignes, du découpage et des sources supplémentaires. Les sorties sont relues selon des [repères explicites](capacity-stages-checkpoints-2026-09-11.md). Une sortie acceptée techniquement ne prouve pas que les conseils sont justes ; une mécanique absente ne compte pas comme une erreur corrigée.

L'itération **0.13.17** ajoute les deux profils et les consignes communes de vocabulaire. Qwen 4B reste le modèle par défaut ; l'extraction par étapes reste un prototype explicite. Les [mesures détaillées](capacity-stages-2026-09-11-measurements.json) couvrent **40 essais : 33 acceptés techniquement, 7 rejetés**. FFXIV est présent pendant tous les échantillons de 32 essais, partiellement pendant un essai, et absent pendant les sept derniers. Aucun modèle ne démontre une supériorité sémantique générale sur cette campagne.

## Protocole

- Sources complètes des journaux de la [comparaison précédente](model-comparison-2026-09-11.md), sans nouvelle recherche de stratégies pour corriger les réponses. Les repères s'appuient sur ces textes et sur le vocabulaire FFXIV, par exemple le sens de « stack marker ».
- RTX 5080, 16 303 Mio annoncés par `nvidia-smi`. FFXIV est ouvert pour la comparaison initiale, les essais par étapes et les deux essais à 32K ; sa présence est relevée à chaque échantillon. Il se ferme pendant l'essai Fractal avec Raven : 184 échantillons sur 228 contiennent le jeu. L'essai Dédale avec Raven et les contrôles de vocabulaire Qwen 4B et Gemma 12B suivants ont lieu sans le jeu. Leurs temps/VRAM ne doivent pas être comparés aux essais en jeu. La présence du processus ne mesure ni les FPS, ni la latence en combat, ni une charge de jeu constante.
- Vulkan, moteur `llama.cpp b10809`, processus neuf par essai, température 0, graine 42, raisonnement désactivé, limite de RAM du processus de 12 Gio, contexte de 65 536 tokens sauf mention contraire. Un seul moteur d'inférence à la fois.
- Temps depuis le démarrage du moteur jusqu'au résultat, incluant chargement et reprises, hors téléchargement et vérification préalable des fichiers. Un passage par condition, sur un poste actif avec des compilations/tests ponctuels : les différences de durée donnent un ordre de grandeur, pas un classement statistique de vitesse.
- Mesure de VRAM de toute la carte environ chaque seconde : jeu, bureau et moteur compris. Il ne s'agit pas de la mémoire du seul modèle. Le minimum libre n'est pas garanti dans une autre scène. Une réserve pilote explique que libre + utilisé diffère du total.
- Les journaux conservent sources, requêtes réelles, réponses, étapes, tokens, erreurs et empreintes. Les résultats compacts et leurs empreintes sont publiés à côté de ce rapport ; les archives complètes restent dans `build/capacity-review-2026-09-11/`.

Les exécutables de comparaison sont figés avant chaque traitement. `capacity-baseline-runner` contient les consignes de production précédentes ; `capacity-stages-v1-runner` ajoute le vocabulaire et la première extraction de faits ; `capacity-stages-v2-runner` remplace les citations à recopier par des identifiants de paragraphes. Les fichiers figés ne sont pas reconstruits au cours des essais.

## Modèles et mémoire

| Modèle | Fichier testé | Taille téléchargée | SHA-256 |
| --- | --- | ---: | --- |
| Qwen 3.5 9B | `Qwen3.5-9B-Q4_K_M.gguf` | 5 680 522 464 octets | `03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8` |
| Gemma 4 12B | `gemma-4-12B-it-qat-UD-Q4_K_XL.gguf` | 6 716 356 800 octets | `90fd44e29e0d7cffeb0fd00dc73cfdab9ed0b0e95306ecf7821ea634c940c370` |

Les révisions précises sont épinglées dans `ForetellGuideModelCatalog.cs` et vérifiées par taille et SHA-256. Références : [Qwen GGUF](https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/tree/3885219b6810b007914f3a7950a8d1b469d598a5), [Gemma GGUF QAT](https://huggingface.co/unsloth/gemma-4-12B-it-qat-GGUF/tree/980b060c40a8539ac159e0501a3e0f66a6365af3), [modèle Google](https://huggingface.co/google/gemma-4-12B). Taille des poids, RAM engagée et VRAM sont des mesures différentes.

Sur les quinze essais initiaux à 64K, la marge minimale de VRAM libre est de **3,43 Gio pour Qwen 4B, 1,65 Gio pour Qwen 9B et 0,89 Gio pour Gemma 12B**. Les pics de RAM privée échantillonnés atteignent respectivement 5,89, 8,15 et 9,65 Gio. La limite de RAM habituelle de 6 Gio est donc trop basse pour ces essais des grands modèles. Le gestionnaire propose une limite de 12 Gio pour eux, sans modifier silencieusement une limite choisie par le joueur.

Deux essais supplémentaires gardent la source du Palais et l'ancien pipeline, avec un contexte réduit à 32K :

| Modèle, Palais à 32K | Résultat technique | Durée | Pic de VRAM de toute la carte | Minimum libre | Pic de RAM privée du moteur |
| --- | --- | ---: | ---: | ---: | ---: |
| Qwen 9B | Accepté, 5 mécaniques | 43,1 s | 13 307 Mio | 2 671 Mio (2,61 Gio) | 7 295 Mio |
| Gemma 12B | Accepté, 6 mécaniques | 73,0 s | 14 870 Mio | 1 108 Mio (1,08 Gio) | 9 329 Mio |

Ces passages ont lieu plus tard dans la session : la charge de jeu/bureau n'est pas figée et l'écart de VRAM ne peut pas être entièrement attribué au contexte. Ils établissent que 32K fonctionne pour cette source, pas pour tous les guides ni toutes les scènes de jeu. Gemma reste proche de la limite ; **Qwen 9B à 32K est le choix pratique pour poursuivre les essais sur cette machine**, sans classement de fiabilité globale. Le contexte par défaut du produit était déjà de 32K ; les essais ne changent pas les préférences enregistrées.

## Changer uniquement le modèle

Même source historique, même pipeline précédent, même contexte à 64K. Les quinze résultats passent la validation technique.

| Instance | Qwen 4B | Qwen 9B | Gemma 12B |
| --- | ---: | ---: | ---: |
| Palais des morts 31–40 | 37,7 s | 34,4 s | 49,4 s |
| Continuum fractal brutal | 156,0 s | 224,9 s | 164,6 s |
| Kefka | 72,7 s | 81,4 s | 94,4 s |
| Manoir des Haukke | 120,3 s | 112,0 s | 150,5 s |
| Dédale antique | 287,6 s | 181,4 s | 347,2 s |

Les grands modèles apportent des améliorations locales, mais aucun ne devient un gagnant fiable sur toute la campagne :

- **Fractal :** Qwen 9B traite correctement le partage de Mass Aetheroplasm, mais demande encore au joueur d'absorber la projection de Primordial Aether. Après réparation, il ne conserve que Death Spin chez Ultima Beast. Gemma couvre davantage d'attaques, mais demande d'éviter le marqueur de partage et d'absorber la projection. Les consignes correctes de Sophia/Zurvan ne suffisent pas à valider les autres.
- **Kefka :** Qwen 9B inverse la géométrie du Blizzard normal. Les deux modèles peuvent expliquer une exception dans les détails tout en affichant une alerte courte valable pour une seule branche. La première étape correcte de Timely Teleport reste incomplète si elle perd le déplacement suivant.
- **Palais :** Shadow Flare est correctement traité comme des dégâts à soigner/mitiger par les grands modèles, mais ils inventent encore des réactions d'évitement après Scream/Prey/Tornado. Le guide prescrit surtout de tuer les adds avant cette suite.
- **Haukke :** Gemma conserve mieux les mécaniques actuelles et les priorités. Qwen transforme un cône en consigne de dispersion et ajoute un déplacement au tank sur Void Thunder III. Le nom composé du duo Jester/Steward reste un problème distinct d'identité en jeu.
- **Dédale :** Gemma formule correctement l'alternative de l'alliance bénéficiant d'Astral Realignment, mais importe aussi des adds d'une autre rencontre dans Behemoth. Qwen 9B produit une référence très réduite. Les deux omettent des rencontres intermédiaires. Le nombre brut de mécaniques inclut des regroupements ou doublons et ne mesure pas la couverture.

## Vocabulaire et faits intermédiaires

Les définitions communes sont des instructions au modèle, pas une table de règles qui transforme un mot en action en jeu. Elles explicitent acteur/cible, partage, dispersion, dégâts inévitables, anneau et zone à bout portant, en conservant les exceptions documentées. Elles sont partagées par la génération groupée, la génération par boss et les réparations.

Trois essais Qwen 9B avec seulement ces nouvelles consignes aboutissent techniquement. Ils ne résolvent pas les erreurs de fond : Blizzard reste inversé, Primordial Aether demande encore une absorption au joueur, et Ultima Beast reste réduit à Death Spin. Un prompt plus explicite ne constitue donc pas une correction suffisante.

Trois contrôles Qwen 4B avec ces consignes aboutissent également, après fermeture du jeu : Fractal 83,4 s, Kefka 60,8 s, Palais 26,1 s. Ces temps ne sont pas comparables aux mesures initiales avec FFXIV. Primordial Aether devient une surveillance du mimétisme et Shadow Flare conserve soins/mitigation, mais Mass Aetheroplasm devient un tankbuster, Light Pillar demande de suivre les cercles, le Demi Ultima ordinaire disparaît et les exceptions de Kefka restent omises. Le glossaire est une instruction générale plus cohérente, **pas un correctif démontré de toutes les erreurs ni un moteur de règles par attaque**.

Les trois derniers contrôles Gemma 12B aboutissent sans le jeu : Fractal 93,7 s, Kefka 46,6 s, Palais 45,3 s. Le partage de Mass Aetheroplasm devient correct et les variantes trompeuses de Kefka figurent dans les détails. En revanche, Primordial Aether demande toujours au joueur d'absorber la projection, Thunder conserve une alerte valable seulement pour la variante normale, et Prey/Tornado inventent un éloignement des adds. Le Demi Ultima ordinaire affiche littéralement « Context only » sans être marqué comme contexte seul ; l'enrage demande de finir le cast, alors que c'est celui du boss qu'il faut devancer. Ces contrôles confirment que les nouvelles définitions ne suffisent pas à fiabiliser la rédaction ni ses alertes courtes.

L'autre traitement confie successivement au même modèle :

1. La lecture de toute la source et l'attribution des paragraphes aux boss, avec leurs conditions et leur provenance.
2. Pour chaque boss, l'extraction des faits : acteur, cible, effet, conditions, réponse du joueur, justification et paragraphes qui la soutiennent.
3. La rédaction de toutes les mécaniques, alertes, alternatives et déclencheurs depuis les faits **et les passages originaux**. Les faits restent une sortie de modèle susceptible d'être erronée.
4. Les éventuelles réparations, puis la publication de ce boss pendant que le suivant est analysé. Une préparation partielle ne devient jamais un cache complet.

La validation contrôle les références et la conservation des noms d'attaques extraits. Elle ne prouve ni que tous les faits ont été extraits, ni que les alternatives ou alertes sont justes. Aucune stratégie BMR n'est importée.

La première extraction demandait de recopier des citations : les trois essais Qwen échouent après les reprises autorisées, avec des citations reformulées, un mauvais paragraphe ou une réponse déclarée inconnue mais non vide. Un premier boss Fractal est néanmoins disponible à 101,8 s avant l'échec du deuxième à 200,1 s. Cette version est conservée dans les mesures comme échec, pas présentée comme un succès du découpage.

La seconde version demande seulement les numéros des paragraphes ; le code récupère le texte original. Son schéma lie une réponse inconnue à une chaîne vide par deux objets alternatifs complets, conformément aux [limites de conversion JSON Schema de llama.cpp](https://github.com/ggml-org/llama.cpp/blob/master/grammars/README.md). Cela retire une tâche de copie sans décider de la stratégie à la place du modèle.

| Extraction v2 à 64K | Qwen 9B | Gemma 12B |
| --- | --- | --- |
| Fractal | Échec de citation au deuxième boss, 138,7 s ; premier boss à 87,9 s | Accepté, 355,8 s ; premier boss à 194,0 s |
| Kefka | Accepté, 113,1 s | Accepté, 135,5 s |
| Palais | Échec de citation de Scream, 44,9 s | Même type d'échec, 62,3 s |

Cette étape intermédiaire permet de localiser les erreurs, sans les faire disparaître :

- Qwen inverse déjà le Blizzard normal dans ses faits. Il extrait correctement les branches trompeuses de Blizzard/Thunder, puis les perd dans sa rédaction. Timely Teleport a une bonne description mais une réponse conditionnelle qui demande aussi de se placer devant le cône.
- Gemma attribue correctement l'absorption de Primordial Aether au boss dans les faits, avec une réponse du joueur inconnue. Sa rédaction produit pourtant `Absorb the green projection`. C'est une régression introduite entre les deux étapes, pas une lacune du guide.
- Gemma corrige Mass Aetheroplasm en partage, conserve les huit attaques d'Ultima Beast et distingue les deux Demi Ultima dans les détails. L'alerte courte de Demi Ultima reste limitée à la mitigation. Plusieurs rôles sont trop restrictifs, par exemple l'évitement de la flaque Allagan Gravity réservé au soigneur.
- Sur Kefka, Gemma retrouve la géométrie normale de Blizzard et les exceptions dans les détails. Son alerte de Thunder ordonne néanmoins de rester entre les lignes pour toutes les variantes. Le regroupement de la tour conserve ses trois comportements, mais perd les noms distincts Indolent Will/Ave Maria comme mécaniques déclenchables.

La troisième version conserve la dernière extraction lorsqu'elle demande une correction et indique les paragraphes nommant la mécanique mal citée. Elle contrôle aussi que la rédaction conserve les paragraphes de **chaque branche extraite** d'une attaque nommée. Ce contrôle détecte la disparition de preuves, pas une mauvaise interprétation accompagnée de la bonne citation. Les sorties et échecs des versions précédentes restent inclus dans les mesures.

Les trois essais Qwen 9B de cette dernière version donnent :

| Extraction v3 à 64K | Résultat | Lecture des conseils |
| --- | --- | --- |
| Fractal | Accepté, 216,6 s ; premier boss à 63,4 s | Les huit attaques d'Ultima Beast sont conservées, le partage est correct et Primordial Aether devient une consigne de surveillance. Des erreurs demeurent : Sephirot demande de se regrouper sur les marqueurs, Aetheroplasm adresse l'évitement de proximité au tank, et l'enrage conserve un nom éditorial distinct. |
| Kefka | Rejeté, 125,6 s | Le nouveau contrôle détecte la disparition de la preuve de la variante trompeuse de Blizzard. Les deux réparations suivantes suppriment la plupart des attaques ; le résultat est rejeté au lieu d'être accepté comme une fiche très réduite. |
| Palais | Accepté, 58,8 s | La correction de citation débloque Scream et Shadow Flare. Soins/mitigation et priorité des adds sont présents, mais Scream/Tornado inventent encore un éloignement des joueurs marqués Prey. |

Ces temps de première publication concernent la sonde utilisant le même rappel de progression que le service, pas une mesure d'apparition à l'écran dans le jeu. Le découpage peut publier un boss avant la fin, mais n'est pas systématiquement plus rapide : le premier boss de l'essai Gemma v2 arrive après celui du pipeline précédent, et les reprises peuvent augmenter le délai total.

**Décision : conserver l'extraction par étapes comme prototype explicite, sans l'activer par défaut dans le service.** Les corrections de références et de conservation sont vérifiables ; la qualité des consignes reste insuffisante pour imposer ce nouveau pipeline. Les définitions de vocabulaire ne remplacent pas cette validation sémantique. Aucun second modèle ne corrige automatiquement une fiche et aucun raisonnement supplémentaire n'est activé.

## Sources redevenues disponibles

La collecte courante confirme que les quatre fournisseurs sont intégrés, mais pas présents pour toutes les instances :

| Instance | Sources disponibles à la collecte |
| --- | --- |
| Haukke | Wiki, Gamer Escape, Raven, classeur |
| Fractal et Dédale | Wiki, Raven, classeur ; Gamer Escape renvoie HTTP 403 |
| Kefka | Wiki, Gamer Escape ; Raven et classeur absents |
| Palais 31–40 | Wiki ; les trois autres absents |

Au Dédale, **Atomos, Vassago et les quatre joueurs par plateforme figurent déjà dans le wiki et le classeur historiques**. Leur absence dans les fiches générées est donc une omission du modèle, pas une absence d'information. Raven répète ces mécaniques sous une forme plus courte : l'essai mesure si cette présentation supplémentaire aide leur conservation. Sa fiche Haukke contient toutefois encore les anciennes lampes malgré sa mention de mise à jour 6.1. Ajouter une source peut donc également ajouter une contradiction de version. Le nombre de fournisseurs ne tranche pas laquelle appliquer.

Pour les essais d'ajout de sources, les textes historiques wiki/classeur sont conservés exactement et seuls les fournisseurs auparavant absents sont ajoutés. La collecte entièrement renouvelée est archivée séparément. Cela évite d'attribuer à Raven une différence causée par un remplacement simultané du wiki ou du classeur.

Avec Qwen 9B et le pipeline initial à 64K :

- **Fractal + Raven :** 14 mécaniques acceptées en 243,7 s, avec fermeture du jeu en cours d'essai. Primordial Aether devient une attente du mimétisme et les marqueurs Sophia/Zurvan restent corrects. L'invention d'un partage dans Even Pattern et la réduction d'Ultima Beast à Death Spin persistent.
- **Dédale + Raven :** 6 rencontres et 12 mécaniques acceptées en 85,1 s, sans le jeu. Atomos et Vassago sont enfin retenus, avec les quatre joueurs sur plateforme dans les détails, et les priorités Napalm/Balloon/Vassago. Les six noms de rencontres ont des candidats dans le catalogue du jeu. L'alerte de plateforme perd toutefois la répartition des joueurs, et celle déclenchée par Ecliptic Meteor demande encore de déposer les comètes au lieu de s'abriter. Cette amélioration de couverture ne valide pas toutes les alertes.

Le passage de quatre à six rencontres montre un bénéfice de représentation sur cet exemple. Il ne permet pas de conclure que Raven fournit une information absente du wiki/classeur, ni de lui attribuer le gain de vitesse puisque le jeu est fermé dans le dernier essai. Le regroupement reste orienté vers des « boss » dans le prompt ; les combats intermédiaires méritent donc aussi une vérification de sélection, indépendamment du nombre de sources.

## Identités et vérifications du code

L'inspection hors ligne utilise les données du jeu installé et conserve les candidats d'identifiants dans chaque essai accepté. Le duo `Manor Jester and Manor Steward` n'a aucun nom correspondant dans les trois sorties Haukke initiales ; les autres noms retenus ont des candidats, parfois multiples. Un candidat de catalogue ne prouve ni le déclenchement historique d'une alerte, ni sa cible, ni son bon timing. Cette itération ne prétend pas résoudre toutes les identités composées.

Les tests du prototype couvrent la séparation des boss, les alternatives opposées, les preuves de chaque branche, la correction d'une référence depuis la dernière réponse, la perte de noms lors d'une réparation, l'annulation et la conservation d'un premier boss si le suivant échoue. Ils utilisent des modèles factices et ne mesurent pas la qualité d'un LLM. Les tests runtime/core et le contrat de séparation avec les connaissances BMR passent ; les hôtes de test signalent correctement un échec contrôlé sans fenêtre interactive. L'audit NuGet en ligne local reste indisponible (`NU1900`), sans erreur de compilation.

## Reproduction

Les commandes sont des essais explicites avec téléchargement préalable de fichiers vérifiés ; elles ne font pas partie des tests unitaires ni de la CI. Chaque destination doit être nouvelle.

```powershell
dotnet run --project ForetellRuntimeTests -c Release -- --guide-capacity-experiment <journal-ou-source.json> <runtime> <nouveau-dossier> qwen3.5-9b facts-v3 65536
dotnet run --project ForetellRuntimeTests -c Release -- --guide-capacity-sources <journaux-historiques> <nouvelle-collecte>
dotnet run --project ForetellRuntimeTests -c Release -- --guide-capacity-augment <journaux-historiques> <collecte> <nouveaux-documents-augmentés>
```

Le mode `vocabulary` utilise la génération groupée avec les nouvelles consignes. Le mode `facts-v3` utilise les étapes successives et leurs derniers contrôles. Les versions historiques nécessitent leurs exécutables figés : un nom de mode n'en restaure pas le code. De même, le mot `baseline` dans un exécutable reconstruit ne restaure pas les anciennes consignes : pour reproduire le traitement historique, il faut utiliser son exécutable figé ou reconstruire le code antérieur correspondant.
