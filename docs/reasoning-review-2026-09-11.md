# Raisonnement Qwen et revue croisée des consignes — 11 septembre 2026

**Décision : conserver les réglages du produit.** Le raisonnement limité à 2 048 tokens ne corrige pas suffisamment les erreurs de consigne et en introduit d'autres. Gemma 4 E2B n'est pas un vérificateur fiable avec les prompts testés. Une revue Qwen repère certains défauts, mais ses propositions ne peuvent pas être appliquées automatiquement non plus. Ce sont les résultats de **17 traitements réels**, pas une conclusion générale sur toutes les méthodes de revue croisée.

## Durées et résultats

Les deux dernières colonnes sont le **temps supplémentaire** d'une revue du candidat Qwen original. Elles ne désignent pas une nouvelle génération complète, ni la réparation/validation d'un guide corrigé.

| Instance | Qwen initial | Qwen avec raisonnement | Revue Gemma globale | Revue Qwen globale |
| --- | ---: | ---: | ---: | ---: |
| Palais 31–40 | 14,6 s | 44,0 s | +5,9 s | +6,1 s |
| Haukke | 53,1 s | 114,5 s | +13,0 s | +13,1 s |
| Kefka | 33,0 s | 68,3 s | +9,2 s | +9,5 s |
| Fractal brutal | 84,4 s | 75,6 s | +12,3 s | +20,1 s |
| Dédale antique | 143,8 s | 169,8 s | +17,6 s | +38,2 s |
| Total | 328,8 s | 472,3 s | +58,1 s | +87,0 s |

Le raisonnement ajoute environ **44 %** au temps total de ce passage. Il ne ralentit pas chaque cas : au Fractal, le nombre de reprises diminue. Les cinq analyses avec raisonnement passent le validateur technique actuel, tout en conservant des erreurs de sens. La revue Gemma globale coûte en moyenne 11,6 secondes, celle de Qwen 17,4 secondes ; le coût n'est donc pas le principal obstacle constaté ici.

Les [mesures détaillées](reasoning-review-measurements-2026-09-11.json) conservent les tokens par requête, les empreintes des fichiers bruts et les contrôles de citation/périmètre. Les cinq revues Gemma produisent 29 remarques ; Qwen en produit 34. Ces nombres ne sont **pas** des corrections validées. La liste des éléments déclarés relus correspond exactement au candidat dans seulement 2/5 revues Gemma et 1/5 revues Qwen. Plusieurs modèles ne listent que les éléments commentés ; le résultat ne permet donc pas d'attester une revue complète.

## Justesse des revues

Sur les neuf défauts ciblés antérieurement, la revue globale Gemma n'en signale correctement aucun ; Qwen en signale quatre (F1, F2, K2, L1), sans fournir une correction complète et directement applicable pour ces quatre cas. Il faut distinguer **détection** et **réparation**.

| Défaut | Gemma global | Qwen global | Gemma par élément, essai exploratoire |
| --- | --- | --- | --- |
| F1 — Mass Aetheroplasm : fuir le partage | Non | Détecté ; remplacement vide | Non ; confond nom du boss et nom de l'attaque |
| F2 — Demi Ultima : faux regroupement d'enrage | Non | Détecté ; remplacement vide et citation mal référencée | Détecté, mais remplace la description correcte et laisse la consigne fautive |
| F3 — Primordial Aether : action du boss donnée au joueur | Non | Non ; questionne seulement le déclencheur | Ne formule pas le défaut de propriétaire de l'action |
| K1 — Blizzard : mouvement normal/feinte | Non | Non | Signale l'instruction unique ; proposition sans les bonnes branches explicites |
| K2 — Thunder : variante trompeuse perdue | Non | Détecté ; propose d'entrer dans les lignes sans condition | Détecté ; proposition répétant l'évitement normal |
| K3 — Teleport : placement limité à derrière | Non | Non | Détecté ; modifie la description déjà conditionnelle, pas l'alerte |
| L1 — Astral Realignment : mitigation | Non | Détecté pour le tank ; vise le champ de preuve et laisse l'alerte intacte | Hors essai |
| L2 — Phlegethon : destruction de plateforme inventée | Non | Non | Hors essai |
| P1 — Résumé d'Ixtab : éviter les dégâts inévitables | Non | Non | Hors essai |

Autres remarques vérifiées :

- Gemma global déclare que la source ne dit pas que les adds de Motherbit sont intouchables, tout en citant une phrase qui le dit. Il critique aussi le fait de tuer les adds d'Ixtab alors que la source le demande. Pour Scream, il repère que soigner/réduire les dégâts n'est pas la réponse principale, mais propose d'éviter d'être touché au lieu de donner la priorité aux adds.
- Qwen global repère utilement le placement manquant pour Sweet Steel à Haukke. En revanche, il conteste Sophia/Zurvan en ignorant leurs conditions de couleur/élément déjà présentes. Pour Ceruleum Vent, il remplace une esquive injustifiée par un autre éloignement injustifié. À Kefka, une proposition inverse l'exception du regard jaune.
- Au Dédale, les deux relecteurs inventent de nombreuses insuffisances de citations pourtant présentes. Gemma associe même une remarque sur Thanatos à un ID de Bone Dragon. Ils ne distinguent pas systématiquement une précision utile, une reformulation et une contradiction.

Le découpage par élément améliore la détection ciblée de Gemma (F2, K1, K2, K3), mais ne suffit pas à fiabiliser les corrections. Le Fractal prend **33,1 secondes pour 20 requêtes**, Kefka **19,0 secondes pour 12 requêtes**. Les 36 remarques comprennent des critiques de style, des erreurs et des réparations portant sur le mauvais champ. Pour Allagan Gravity de l'Ultima Beast, Gemma importe le nettoyage de Heavy lié à la flaque de Motherbit. Les bonnes réponses Sophia et Zurvan sont aussi dégradées dans ses propositions. Les 20 éléments du Fractal donnent 30 IDs déclarés relus à cause d'IDs supplémentaires/répétés ; les contrôles par requête sont conservés dans les mesures. Les sources complètes étaient présentes à chaque appel.

## Conséquence pour Foretell

Une revue croisée reste une hypothèse utile pour **signaler une incohérence et demander une correction ciblée**, mais aucun modèle de cette expérience ne peut servir d'arbitre automatique. L'accord de deux modèles ne remplacerait pas une validation contre les sources.

L'itération suivante devrait d'abord produire une représentation explicite de la cible, de la condition et de l'action justifiée, puis vérifier la consigne courte contre cette représentation et ses preuves. Le relecteur pourrait signaler une branche absente ou une inversion précise, avec un ID et un champ contrôlés. Il faudrait ensuite mesurer les résultats après correction, en incluant les bonnes consignes susceptibles de régresser, avant toute activation. Cette expérience n'a pas exécuté cette boucle de réparation complète.

Cette vérification appartiendrait à la préparation mise en cache du guide, pas à chaque alerte en combat. D'autres budgets de raisonnement, paramètres d'échantillonnage, prompts et tailles de modèles restent hors de cette campagne ; aucune de ces variantes n'est déclarée supérieure sans nouvel essai.

## Protocole

Expérience isolée sur les cinq documents historiques de la [comparaison initiale](model-comparison-2026-09-11.md). Aucun réglage, poids, cache du joueur ou code de production ne change. Les résultats expérimentaux conservent la révision de production uniquement pour exercer son validateur : **ils ne doivent jamais être copiés dans le cache du plugin**.

Trois traitements principaux :

1. Refaire l'analyse complète avec Qwen 3.5 4B et le raisonnement activé, en conservant les prompts du produit.
2. Donner à Gemma 4 E2B le guide Qwen initial, ses résumés et toutes ses consignes, avec les sources complètes, pour proposer uniquement des corrections justifiées.
3. Donner exactement la même tâche de revue à Qwen sans raisonnement. Ce contrôle distingue l'intérêt d'une seconde famille de modèle de celui d'un prompt consacré à la vérification.

Le prompt de revue rappelle les catégories d'erreurs observées et le vocabulaire FFXIV, sans fournir les réponses attendues ni identifier le modèle auteur. Il n'est donc pas un test indépendant de nos observations précédentes. Les [neuf défauts ciblés](reasoning-review-checkpoints-2026-09-11.md) servent de repères ; toutes les autres remarques sont aussi examinées contre leurs sources. Aucune correction proposée n'est appliquée automatiquement. Une revue techniquement terminée, une citation exacte ou la déclaration d'avoir lu un élément ne prouvent pas la justesse du jugement.

Après les premiers échecs de revue globale de Gemma, un essai exploratoire conserve le même prompt et la source complète, mais soumet un seul élément candidat par requête. Il traite **tous** les éléments du Fractal et de Kefka, pas seulement ceux connus comme faux. Ces essais supplémentaires servent à évaluer la granularité ; ils ne valident pas encore cette méthode sur les cinq instances.

Poids quantifiés et moteur identiques à la référence : Qwen 3.5 4B Q4_K_M, Gemma 4 E2B Q4_K_M, llama.cpp b10809 Vulkan, contexte 65 536, budget processus 12 Gio, un seul serveur à la fois. Le serveur expérimental reprend les arguments du produit, lie uniquement l'adresse de boucle locale et refuse tout repli CPU. Chaque traitement démarre un nouveau processus et utilise les fichiers locaux vérifiés ; aucun téléchargement n'est permis. Le jeu était fermé au démarrage.

Le raisonnement est activé via `--reasoning on` et `enable_thinking=true`, avec 2 048 tokens de budget et 2 560 tokens supplémentaires dans la limite totale de chaque réponse. Les traces confirment une sortie de raisonnement non vide. Température 0 et seed 42 restent identiques à la référence pour ce premier contrôle. Ce n'est pas une recherche de réglages optimaux : la [fiche officielle Qwen](https://huggingface.co/Qwen/Qwen3.5-4B#best-practices) recommande notamment un échantillonnage non nul et des limites de sortie plus élevées. Les résultats ci-dessous ne permettent pas de conclure sur toutes les configurations de raisonnement.

Les durées expérimentales incluent extraction/vérification du moteur, chargement, requêtes, validation et reprises ; la vérification préalable des poids est exclue. La référence refait aussi la vérification des poids pendant son démarrage normal : les durées de requêtes sont conservées séparément pour éviter d'attribuer ce petit écart au modèle. La fermeture du processus n'entre pas dans ces chronomètres. Chaque revue globale effectue une requête ; les revues par élément peuvent réutiliser le préfixe de source en mémoire du serveur, sans réutiliser les réponses. Un seul passage par traitement : ces mesures ne donnent pas une variance statistique.

## Constats sur les générations avec raisonnement

- **Fractal :** Mass Aetheroplasm devient « Mitigate stack », sans demander de rejoindre la cible ; Primordial Aether demande toujours au joueur d'absorber le primal. L'enrage est reconnu comme un DPS requis, mais Qwen invente une entrée distincte « Demi Ultima (enrage) » et la consigne courte de l'attaque ordinaire devient aussi « DPS race ». Les marqueurs Sophia et Zurvan restent correctement décrits. Les résumés de boss deviennent de simples objectifs de progression.
- **Kefka :** les deux feintes sont ajoutées comme entrées séparées, mais Blizzard normal demande toujours d'entrer dans le télégraphe. Timely Teleport reste limité au placement derrière dans l'alerte malgré l'alternative devant dans la description. Les regards normal et trompeur sont séparés ; leur condition n'est plus explicitée dans chaque alerte. Décrire deux entrées ne garantit pas de déclencher la bonne variante en jeu.
- **Palais :** le résumé n'invite plus à éviter les dégâts inévitables, mais la consigne courte de Shadow Flare devient « Avoid Shadow Flare », alors que son détail dit de réduire des dégâts inévitables. Scream demande aussi au joueur de marquer les joueurs. La priorité de tuer les adds disparaît comme mécanique distincte.
- **Haukke :** les principales attaques du combat actuel restent présentes et les anciennes lampes ne sont pas réintroduites. Le duo conserve son nom composé non résolu dans le catalogue du jeu. Le résumé d'Amandine demande au joueur d'invoquer les adds. Trois corrections de phases alourdissent le traitement sans corriger ce sens.
- **Dédale :** Astral Realignment ne dit plus de réduire les dégâts, mais se limite au tank qui prend le boss ; l'action des DPS porteurs reste absente. La description de Holmgang Chain inverse la relation entre tuer les adds et libérer le pot. Après réparation, Phlegethon ne conserve qu'Ancient Flare : les adds ont disparu, ce qui ne compte pas comme une correction de leurs consignes. Atomos/Bombe et les quatre joueurs par plateforme restent absents. L'ancienne invention de destruction de plateforme concernait Phlegethon ; les comètes de Behemoth ne doivent pas être importées pour la corriger.

## Reproduction

Sondes explicites, jamais exécutées par la suite de tests ordinaire :

```text
ForetellRuntimeTests.dll --guide-reasoning-experiment <journal-original.json> <runtime-vérifié> <nouveau-répertoire> qwen-thinking
ForetellRuntimeTests.dll --guide-reasoning-experiment <répertoire-candidat-Qwen> <runtime-vérifié> <nouveau-répertoire> gemma-review
ForetellRuntimeTests.dll --guide-reasoning-experiment <répertoire-candidat-Qwen> <runtime-vérifié> <nouveau-répertoire> qwen-review
ForetellRuntimeTests.dll --guide-reasoning-experiment <répertoire-candidat-Qwen> <runtime-vérifié> <nouveau-répertoire> gemma-focused-review
```

Le répertoire candidat contient `source.json` et `prepared.json` issus de la campagne initiale. Chaque traitement conserve le journal intégral, les empreintes, réglages, temps et tokens dans `result.json`. Les revues ajoutent `candidate.json`, `review.json` et `review-checks.json` ; les générations complètes ajoutent `prepared.json` si elles passent le validateur existant. Les données brutes restent sous `build/reasoning-review-2026-09-11/`.

## Validation de l'outillage

Les 17 traitements ont terminé et leurs journaux complets sont conservés. L'export des mesures vérifie les empreintes de source, le nombre de requêtes, la température et la seed des requêtes effectives. La sonde initiale a servi aux cinq générations avec raisonnement et aux cinq revues Gemma globales ; l'ajout du mode par élément a ensuite servi aux cinq contrôles Qwen et aux deux revues Gemma détaillées. Le prompt de revue et les sources sont identiques ; les empreintes des deux assemblages de sonde sont conservées dans les mesures.

La compilation et la suite runtime complète passent avec le code final de la sonde. L'empreinte des tests déterministes reste `73B988089165FDD760CB391172D357E6040B4907CC7D3FB10D1096A44212B7CE`. Le build local signale seulement l'audit NuGet en ligne indisponible (`NU1900`). Aucun serveur expérimental ne reste chargé à la fin ; aucun processus de jeu n'était présent aux contrôles de début et de fin. Cette itération ajoute uniquement de l'outillage de test et le rapport : la version distribuée reste 0.13.16.
