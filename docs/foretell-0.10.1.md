# Foretell 0.10.1 — collecte automatique et analyse bornée

## Utilisation

Jouer avec Foretell, puis ouvrir **Knowledge**, le contenu testé et **Analysis ZIP**. La collecte utile à l’évaluation fonctionne automatiquement, même si **Extra readable recording (advanced)** est décoché. Aucune commande `record on/off` n’est nécessaire. Après avoir quitté le contenu, l’export peut aussi ajouter les journaux raw fermés.

Le ZIP correspond à la session choisie, pas à tout l’historique. Le contexte automatique de la session active peut être exporté : une barrière dans la file d’écriture ferme le segment en cours, puis les événements suivants continuent dans un nouveau segment. Les segments de l’export restent protégés du nettoyage pendant leur copie.

## Volumes et stockage

| Élément | Limite |
| --- | --- |
| Nouvelle capture automatique, par session de territoire | 64 Mio compressés maximum |
| Cache automatique total `foretell-captures/` | 256 Mio maximum |
| Ancienneté du cache | 14 jours, nettoyage à l’ouverture du prochain segment |
| Segment indépendant | 4 Mio décompressés, ou fermeture au prochain événement après une minute |
| Travail enregistré par session | 512 Mio décompressés / 512 segments maximum |
| File d’attente du nouveau journaliseur | 16 Mio estimés / 1 024 entrées maximum |
| Analysis ZIP exporté | 128 Mio maximum |

Ce sont des plafonds, pas une taille habituelle mesurée en jeu. Le volume réel dépend des événements et acteurs présents. Le quota réserve la taille maximale du prochain segment et sa métadonnée : une session peut donc s’arrêter un peu avant le plafond affiché. Elle reprend dans une nouvelle session de territoire. En cas de surcharge, d’événement surdimensionné ou de quota atteint, les pertes sont comptées et la capture est marquée partielle ; le moteur en jeu continue indépendamment.

Les captures automatiques anciennes sont supprimées avant de créer un segment qui dépasserait le budget total. Les captures temporairement utilisées par un export ou une évaluation sont protégées ; si aucun espace ne peut être libéré, la collecte s’arrête avec une indication explicite.

**Les journaux raw existants, les JSONL facultatifs et les ZIP déjà exportés ne sont pas inclus dans ce nouveau quota.** Ils conservent leur politique de stockage actuelle. Cette version n’efface pas les anciennes données privées ni la mémoire apprise. Il ne faut donc pas interpréter 256 Mio comme la taille maximale de tout le dossier Foretell.

La capture automatique a priorité dans le ZIP. Les fichiers raw/lisibles supplémentaires trop gros sont omis avec une explication dans `manifest.json`. La collision locale, lorsqu’elle correspond à la session active, est ajoutée si le budget le permet. Le manifeste décrit exactement les éléments inclus.

## Comment analyser sans charger des gigaoctets

Le ZIP contient `capture/index.json` : session, version, période, types et nombre d’événements, événements rejetés, état de complétude et empreinte SHA-256 de chaque petit segment `capture/*.jsonl.gz`. On commence par cet index et `foretell-analysis.json`, puis on ne traite que la capture sélectionnée.

L’outil lit progressivement les événements. Il fait une première passe pour contrôler l’intégrité et les dates, puis une seconde pour alimenter le moteur. Il garde au plus un petit segment compressé pour son contrôle d’intégrité, quelques événements et l’état de l’algorithme, sans constituer de liste contenant toute la session. Des limites supplémentaires bornent les lignes, l’index et le travail décompressé.

Avec .NET 10.0.400+ et les dépendances Dalamud installées :

```powershell
dotnet run --project ForetellRuntimeTests/ForetellRuntimeTests.csproj -c Release -- `
  --inspect C:/Captures/session.zip --out C:/Captures/summary

dotnet run --project ForetellRuntimeTests/ForetellRuntimeTests.csproj -c Release -- `
  --train C:/Captures/session-1.zip --evaluate C:/Captures/session-2.zip `
  --out C:/Captures/comparison
```

La seconde session doit être strictement postérieure à la première. L’évaluation ne réentraîne pas les poids ni les calibrations. Le moteur conserve l’ordre d’arrivée des événements de la capture, comme en jeu ; il ne recharge pas en parallèle les raw pour dupliquer les fenêtres déjà enregistrées. Le diagnostic de protocole raw reste un outil distinct.

## Ce que le replay permet et ses limites

Les observations acceptées par le moteur sont copiées avant leur traitement ultérieur : paramètres de l’attaque, positions, effets, caractéristiques disponibles et contexte borné des acteurs/groupe/combat. Aucun sous-ensemble de caractéristiques n’est silencieusement retiré pour faire gagner de la place. Chaque nouveau segment répète son premier contexte pour être lisible indépendamment.

Cela permet de comparer les algorithmes sur des entrées enregistrées et d’entraîner sur une session puis évaluer sur une autre. Le replay commence avec une mémoire distincte ; cette version n’enregistre pas un checkpoint complet des connaissances avant chaque session. Il ne prétend donc pas reproduire exactement toutes les prédictions d’un moteur déjà entraîné en jeu. L’audit du ZIP conserve les décisions effectivement enregistrées, dans sa limite de rétention.

Ce n’est ni une vidéo ni une restitution de l’évolution complète des collisions. Une capture partielle n’établit pas la fiabilité des résultats qui nécessitent les données manquantes. Les anciennes captures sans contexte ne peuvent pas récupérer rétroactivement ce qui n’avait pas été enregistré.

## Validation

Les tests du moteur réel couvrent l’export ZIP sans flux lisible, l’égalité des décisions après compression/lecture progressive, la copie indépendante des entrées, la poursuite après export, le contexte répété à chaque segment, les pertes explicites, les plafonds de session/cache, la protection pendant export, la rétention et la reprise dans une nouvelle session. Ils vérifient aussi le refus des lignes excessives, des parties manquantes et d’un gzip tronqué, ainsi que l’omission signalée d’un raw trop gros par le véritable exporteur.

Le rendu ImGui et le coût supplémentaire en situation de jeu restent à vérifier sur les prochains tests.
