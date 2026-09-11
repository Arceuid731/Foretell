# Critères ciblés pour le raisonnement et la relecture

Ces neuf défauts étaient déjà identifiés dans la campagne précédente, avant lecture des nouvelles revues. Ils ne constituent pas un jeu de test indépendant ni une mesure exhaustive de justesse. Les relecteurs reçoivent les sources complètes et tous les éléments du candidat, sans cette liste ni le nom du modèle auteur. Leur prompt commun rappelle néanmoins les catégories d'erreurs observées et le vocabulaire du jeu : le contrôle par Qwen utilise exactement ce même prompt pour isoler l'intérêt de changer de modèle.

| ID | Instance | Défaut du candidat Qwen sans raisonnement | Résultat attendu d'une correction |
| --- | --- | --- | --- |
| F1 | Fractal | Mass Aetheroplasm demande de fuir le joueur marqué | Rejoindre la cible pour partager les dégâts |
| F2 | Fractal | Demi Ultima sous 10 % demande de se regrouper | Conserver les dégâts de groupe ordinaires et le DPS requis pour battre l'enrage sous 10 % |
| F3 | Fractal | Primordial Aether demande au joueur d'absorber les projections | L'absorption appartient au boss ; décrire ce qu'il faut observer/résoudre sans attribuer son action au joueur |
| K1 | Kefka | Blizzard Blitz impose d'entrer dans le télégraphe | Conserver les deux zones normales et leur inversion avec les points d'interrogation |
| K2 | Kefka | Thrumming Thunder conserve seulement l'évitement normal | Distinguer normal : sortir des lignes ; feinte : entrer dans les lignes |
| K3 | Kefka | Timely Teleport affiche seulement le placement derrière malgré l'alternative devant dans les détails | Alerte compatible avec les deux suites et leur condition |
| L1 | Dédale | Astral Realignment reçu déclenche une consigne de mitigation | Les porteurs attaquent le boss ; les autres protègent les pots et tuent les adds |
| L2 | Dédale / Phlegethon | Iron Giants prétend que les adds détruisent la plateforme | Les tuer avant que la lave recouvre la plateforme ; ne pas leur attribuer la destruction du sol ni importer les comètes de Behemoth |
| P1 | Palais | Résumé d'Ixtab demande d'éviter des dégâts inévitables | Soins/mitigation sans inventer un déplacement pour Shadow Flare |

Pour chaque revue, lire aussi **toutes** les autres remarques contre les sources : vrai défaut, simple reformulation/signalement injustifié, incertitude raisonnable ou correction nouvelle incorrecte. Une bonne détection ne suffit pas si son remplacement supprime une exception. Les propositions ne sont pas appliquées au guide, et la revue ne cherche pas à reconstruire les mécaniques absentes du candidat. Un ID déclaré relu ou une citation exacte ne prouve pas la qualité du jugement.

Les générations avec raisonnement sont également relues dans leur ensemble selon les [critères de couverture de la campagne initiale](model-comparison-checkpoints-2026-09-11.md), en signalant les omissions et erreurs supplémentaires. Les nouveaux résultats sont produits en anglais, comme les références archivées ; aucune conclusion sur le français ou de nouveaux guides enrichis de Raven n'en découle.
