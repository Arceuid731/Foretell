# Foretell 0.10 — apprentissage, validation et présentation

Cette version raccorde les six chantiers de la revue produit : prédiction avant impact, hypothèses concurrentes, séquences spatiales, trajectoires tenant compte du temps, présentation commune et évaluation détachée. Elle améliore le socle générique ; elle ne prouve pas une couverture universelle des mécaniques de FFXIV.

## Comportement livré

- **ML** : les entrées sont figées au déclencheur. Le résultat indépendant évalue la prédiction enregistrée avant de mettre à jour les poids. L'ancien classifieur, alimenté par des conséquences déjà observées, ne pilote plus les prédictions.
- **Interprétation** : toucher la majorité du groupe conserve les hypothèses raidwide et AOE évitable. Un marqueur conserve stack et spread. Les corrélations de déplacement, distance ou orientation ne suffisent plus à établir une consigne.
- **Géométrie** : des familles aux scores trop proches restent ambiguës. La validation vérifie les positions observées et le moment d'impact de la prédiction émise ; reconnaître la bonne forme au mauvais endroit ne suffit pas.
- **Séquences** : jusqu'à huit impacts ultérieurs sur douze secondes peuvent apprendre des positions relatives, orientations, formes et délais. Trois observations compatibles ouvrent une hypothèse ; seuls les événements suivants testent une séquence préalablement prévue.
- **Décisions** : radar, 3D et texte utilisent la même liste de dangers. Les lignes dépendant de leur cible suivent leurs extrémités jusqu'à la fin du cast. Les indices de changement du sol intègrent cette liste sous forme de polygones prudents.
- **Trajet** : évaluation d'un trajet direct, à vitesse de marche supposée, puis de la destination pendant les fenêtres d'impact. Les dangers représentés moins certains participent au rejet. Sol inconnu, capture dégradée, limites d'arène et contraintes personnelles non résolues bloquent la recommandation. Le résultat est revérifié avant affichage.
- **UI en anglais** : Overview présente alertes, provenance, terrain et maturité des connaissances. Knowledge détaille occurrences, essais évaluables, concordances, contradictions, hypothèses et séquences. Recordings accueille l'évaluation ; Diagnostics regroupe les compteurs techniques. Le radar affiche le temps restant et une légende compacte.
- **Replay** : un moteur et un monde managés distincts traitent les événements, sans échange temporaire de la mémoire active, services de jeu, hooks ou journaliseurs natifs. Lecture et évaluation explicites s'exécutent en arrière-plan.

Prévoir un prochain signal au bon moment et prévoir son danger sont comptabilisés séparément. L'évaluation gelée conserve les poids ML et les calibrations de timeline, de contexte temporel/HP et de séquences simultanées.

## Une occurrence peut-elle suffire ?

Une forme complète fournie par les données client peut être affichée dès le premier cast. Cela ne démontre ni toutes ses conséquences, ni la réponse à une mécanique composée, ni sa fiabilité empirique.

Pour une règle inférée, compter les casts est insuffisant. Plusieurs victimes d'une occurrence constituent un seul résultat. Si tout le monde esquive, si les positions à l'impact manquent ou si plusieurs explications restent compatibles, l'observation peut être utile sans permettre de validation. Tester une empreinte spatiale demande plusieurs positions touchées et non touchées, éloignées des bords incertains.

La borne inférieure de Wilson à 95 % donne ces meilleurs cas, avec uniquement des essais informatifs, indépendants et concordants :

| Borne recherchée | Résultats minimum |
| --- | ---: |
| 75 % | 12 |
| 95 % | 73 |
| 99 % | 381 |

Ces nombres ne sont **ni une obligation de subir autant de casts, ni une probabilité de survie, ni une garantie de comprendre toute la mécanique**. Les seuils de présentation peuvent aussi utiliser une forme client explicite. Les erreurs augmentent les besoins ; des observations corrélées ou toujours ambiguës ne justifient pas l'interprétation statistique. Deux contradictions récentes suspendent les conseils forts. Knowledge expose les compteurs et le minimum d'essais supplémentaires dans l'hypothèse optimiste d'aucune nouvelle erreur.

## Évaluer des sessions séparées

Le flux normalisé reste facultatif : `/foretell record on` avant la capture, puis `/foretell record off`. La version 0.10 y ajoute un contexte borné : acteurs proches, groupe, état de combat et de contenu, boss observé, données client et paramètres d'apprentissage. Les journaux raw restent disponibles indépendamment.

Avec .NET 10.0.400 ou supérieur et les dépendances Dalamud installées, depuis le dépôt :

```powershell
dotnet run --project ForetellRuntimeTests/ForetellRuntimeTests.csproj -c Release -- `
  --train C:/Captures/training.jsonl `
  --evaluate C:/Captures/later-session.jsonl `
  --out C:/Captures/evaluation
```

Le programme refuse un même fichier ou des périodes se chevauchant. Il produit `training-report.json`, `evaluation-report.json` et `evaluation-decisions.json`. Le dernier fichier contient la partie récente de l'audit borné ; compteurs et empreinte SHA-256 portent sur toutes les décisions de l'exécution. L'évaluation ne réentraîne pas le modèle. Une ligne rejetée empêche la certification des résultats de cette capture.

Le bouton **Evaluate latest recording** effectue une évaluation chronologique avec une mémoire distincte. Il ne remplace pas la comparaison entre entraînement et session ultérieure proposée par la commande.

Les anciens JSONL restent lisibles. Sans contexte enregistré, ils ne constituent pas des essais évaluables. Deux captures historiques relues pendant cette itération contiennent 775 et 1 004 observations lisibles ; elles n'apportent aucune validation prédictive de cette version. Deux autres anciens fichiers inspectés contiennent une unique ligne JSON tronquée. Aucune capture privée n'est ajoutée au dépôt.

## Migration

La mémoire passe au schéma 24. Les observations contextuelles, échantillons et données client sont conservés. Les compteurs de validation spatiale sont réinitialisés, car leur ancienne définition ne testait pas l'origine et le timing. Les formes empiriques non distinguées redeviennent ambiguës et les ajustements globaux dérivés sont reconstruits. Les poids pré-impact sont séparés de l'ancien modèle. Overview permet leur remise à zéro explicite sans effacer les enregistrements.

## Validation et limites

Les tests du cœur couvrent les besoins statistiques, l'abstention, l'absence de fuite d'informations futures, le modèle gelé, les trajectoires au fil du temps, les dangers moins certains, les polygones de sol, les géométries mobiles et les erreurs de position/timing.

Les tests du moteur réel s'exécutent sans initialiser les services Dalamud. Ils vérifient reproductibilité, immutabilité des entrées, séparation des métriques, empreintes mal placées, apprentissage d'étapes et gel des calibrations. Ils font partie des workflows de build et de publication avec le contrat de télémétrie.

La validation en jeu du rendu ImGui, des passages couverts et du coût par frame reste nécessaire. Le replay sémantique ne restitue pas les pixels ni l'évolution complète de la collision. Une animation seule ne certifie jamais qu'un sol a disparu.

Les étiquettes pouvant entraîner le classifieur restent limitées aux effets résolus suffisamment informatifs, notamment AOE spatiale et déplacement forcé typé. Empilements, échanges de débuffs, attributions de tours, orientations et puzzles arbitraires nécessitent encore des observations discriminantes et des contraintes adaptées. Le planificateur teste des trajets directs ; il ne résout pas une stratégie collective complète.

Un danger sans signal observable avant son premier impact ne peut pas être deviné de façon fiable. L'objectif reste de mieux anticiper lorsque l'information le permet, et d'indiquer ce qui manque dans les autres cas.
