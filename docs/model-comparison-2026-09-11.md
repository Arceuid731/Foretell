# Comparaison réelle des modèles sur les analyses récentes — 11 septembre 2026

**Décision : conserver Qwen 3.5 4B comme compromis actuel pour la disponibilité et la couverture, sans le déclarer validé sur la justesse des consignes.** Qwen termine les cinq cas, Gemma quatre et Granite trois. Gemma est meilleur sur certaines alternatives de Kefka ; aucun des trois ne préserve correctement toutes les consignes importantes examinées. Changer simplement de modèle ne résout pas les défauts observés.

## Mesures

Les durées sont celles d'analyses complètes, démarrage et reprises inclus. « Échec » signifie qu'aucun guide complet validé n'a été produit. Les autres cases signifient uniquement que les contrôles techniques ont accepté le résultat.

| Instance | Qwen 3.5 4B | Gemma 4 E2B | Granite 4.1 3B |
| --- | ---: | ---: | ---: |
| Palais des morts 31–40 | 14,6 s | 38,5 s | 10,5 s |
| Manoir des Haukke | 53,1 s | 39,3 s | 44,2 s |
| Sigmascape V4.0 | 33,0 s | 31,1 s | 19,4 s |
| Continuum fractal brutal | 84,4 s | Échec — 61,4 s | Échec — 160,0 s |
| Dédale antique | 143,8 s | 49,1 s | Échec — 106,9 s |
| Guides complets acceptés | **5/5** | **4/5** | **3/5** |

Pic maximal de mémoire privée échantillonnée sur ces essais : Qwen **5,87 Gio**, Gemma **4,18 Gio**, Granite **7,44 Gio**. Pics de working set correspondants : 1,65 / 2,28 / 1,31 Gio. Ce sont deux mesures distinctes du processus Windows, pas une consommation totale RAM+VRAM. Les [mesures détaillées](model-comparison-measurements-2026-09-11.json) conservent les empreintes des poids, du moteur, des sources et des fichiers de résultat, ainsi que les tokens et les tentatives.

## Protocole

Comparaison des trois profils installés, avec le code de préparation de **0.13.16** (`a2f483c`, état du dépôt `569d4cd`) et les cinq dernières instances distinctes dont le journal local conserve intégralement les sources. Chaque couple instance/modèle utilise une nouvelle génération réelle. Aucun résultat de modèle simulé n'entre dans les mesures.

Les sources historiques restent identiques entre modèles : Console Games Wiki pour les cinq cas, avec Community Workbook pour Haukke, le Continuum fractal brutal et le Dédale antique. Raven et Gamer Escape étaient absents de ces documents enregistrés ; ce comparatif n'évalue donc pas un nouvel assemblage enrichi de Raven. Les sources ne sont pas téléchargées à nouveau.

Réglages communs : Vulkan, contexte de 65 536 tokens, budget de mémoire du processus de 12 Gio, température 0, seed 42, paramètres de conversation et poids quantifiés figés du catalogue. Moteur llama.cpp b10809, RTX 5080 16 Gio, pilote 610.62. Le jeu était fermé au démarrage des essais. Un seul modèle est chargé à la fois ; l'ordre des modèles tourne entre les instances. Il s'agit d'un passage par couple, pas d'une estimation statistique de reproductibilité.

Le temps inclut le démarrage du modèle, l'analyse, les tentatives de correction et la validation. La vérification préalable des fichiers est exclue ; le démarrage refait les vérifications normales du produit. Aucun cache de réponse ou de guide préparé n'est réutilisé. La mémoire privée est échantillonnée toutes les 500 ms et le pic de working set vient du compteur du processus ; ces valeurs ne mesurent pas la VRAM.

Les journaux de requêtes/réponses, sources exactes, résultats et relevés restent dans `build/model-comparison-2026-09-11/`, séparés des données installées. Les données de jeu et les caches du joueur ne sont pas modifiés. Les poids installés vérifiés sont lus via des liens physiques dans ce répertoire ; le moteur y est extrait séparément.

La revue s'appuie sur des [points précis relevés dans les sources](model-comparison-checkpoints-2026-09-11.md) avant l'inspection des consignes générées : attribution des boss, variantes opposées, priorités des adds, conditions de marqueurs et de phases, différences entre combats portant des noms d'attaques identiques, et applicabilité des anciennes stratégies. Une validation technique réussie, un nom présent dans une citation ou un nombre élevé de mécaniques ne constituent pas un score de justesse.

## Corpus

| Instance | Contenu / territoire | Caractères agrégés | Empreinte source |
| --- | --- | ---: | --- |
| Palace of the Dead 31–40 | 177 / 564 | 6 105 | `CE6A88DC0949D9040FDA3D2B112A9D50ED525AFF07A536F3AE3D0653C0BEB1FF` |
| Haukke Manor | 6 / 1040 | 55 772 | `B24922AE0B56E55A878831E34B0C550ECCEFB21DC54BB778364D1F6912BE69F4` |
| Sigmascape V4.0 | 289 / 751 | 23 201 | `20F25F93AD7F242ACA236BAC38DA7B8CBEED894D80DD8364D3E8D66B4A8AA8EE` |
| The Fractal Continuum (Hard) | 285 / 743 | 63 306 | `CFB1D4EDE7700F001F8487860C483A5FB8B1D1E2CAE84D93CB610783F5819C38` |
| The Labyrinth of the Ancients | 92 / 174 | 59 938 | `5E9DD7382B97D21B7517DE2550AFC41F051504600F0EF6BDD9016B3DFC6CA8FD` |

Les journaux datent des 10 et 11 septembre. Les captures détaillées encore présentes confirment les passages au palais, à Haukke et à Sigmascape ; les deux autres cas reposent sur leurs journaux d'analyse conservés. Ce n'est pas une relecture chronologique du rendu en jeu ni une mesure du taux d'alertes affichées.

## Constats vérifiés pendant la revue

### Palace of the Dead 31–40

- Qwen isole Ixtab et conserve les flaques en bordure, la priorité sur les adds et Shadow Flare. Son résumé contient néanmoins une formule contradictoire sur l'évitement des dégâts inévitables, et la consigne de Scream se limite à soigner/réduire les dégâts.
- Granite isole aussi Ixtab, mais attribue Prey aux adds au lieu de Scream et invente des réponses d'éloignement des joueurs marqués/des adds. Scream n'est plus une mécanique distincte.
- Gemma crée **15 entrées de boss**, dont 14 ennemis ordinaires, avec des alertes d'apparition sans consigne de combat utile. Ixtab est réduit à un bloc décrivant la séquence entière. Des titres de boss sont aussi acceptés comme phases de ces ennemis.

### Haukke Manor

- Qwen conserve les attaques de Claviger, Ice Spikes du Jester, Soul Drain du Steward et les principaux éléments du combat actuel d'Amandine, dont le regard après Seduce et le Handmaiden qui lance Stoneskin.
- Gemma conserve les nouveaux adds et Seduce, mais omet Void Fire II, Ice Spikes, Soul Drain et les versions III de Fire/Thunder. Le conseil de priorité Jester reste présent.
- Granite sépare les quatre acteurs, mais affirme simultanément de tuer Jester en premier et de tuer Steward en premier. Il qualifie Sweet Steel d'interruptible sans justification dans la source. Le regard est enfoui dans le résumé et Seduce est absent.
- Qwen et Gemma regroupent Jester et Steward sous le nom composé du titre wiki. **La vérification avec le catalogue du jeu ne trouve aucun identifiant pour ce nom composé.** Granite trouve les identifiants des acteurs séparés. Une bonne description du duo n'est donc pas suffisante pour son association aux acteurs en combat.
- Aucun des trois résultats n'impose les lampes obsolètes comme mécanique actuelle. Le piège est réel : dans la ligne 15 du classeur, le duo est en `G15`/« Second Boss », un ennemi qui s'enfuit en `I15`/« Third Boss », et l'ancienne Amandine en `K15`/« Fourth Boss ». Une conversion aveugle de l'ordinal vers les trois rencontres actuelles serait fausse.

### Sigmascape V4.0

- Qwen donne une consigne générique de déplacement vers le télégraphe pour Blizzard Blitz et conserve seulement la réponse normale de Thrumming Thunder. Les citations contiennent pourtant les versions normales et trompeuses : la perte existe déjà dans la réponse brute du modèle. Les deux regards de la statue sont correctement différenciés dans le texte.
- Granite fait aussi entrer dans le télégraphe de Blizzard sans condition, et résume la statue en invitations vagues à suivre sa mécanique. Un rappel séparé des attaques trompeuses ne corrige pas la consigne contradictoire de Blizzard.
- Gemma conserve mieux les alternatives de Blizzard, les deux télégraphes avec « ??? » et le changement de placement après Timely Teleport. En revanche, sa consigne du sommet de la statue dit seulement de regarder ailleurs et omet l'exception jaune Ave Maria qui demande de regarder la statue.

### The Fractal Continuum (Hard)

- Qwen distingue les deux Citadel Buster, les flaques propres à Motherbit et les marqueurs de l'Ultima Beast. Les associations Sophia/couleurs opposées et Zurvan/météore de même élément sont correctes dans les consignes détaillées.
- Des erreurs graves subsistent : Qwen demande d'éviter le joueur marqué pour **Mass Aetheroplasm**, qui est un partage de dégâts, et de se regrouper pour **Demi Ultima sous 10 %**, qui est un enrage à battre aux dégâts. Il attribue aussi au joueur l'absorption des projections de Primordial Aether, effectuée par le boss. Les descriptions peuvent être correctes alors que leur consigne courte est fausse.
- Gemma est rejeté pour un nom de déclencheur absent de sa propre citation, après correction. Aucun guide complet n'est produit pour ce couple ; ce résultat ne doit pas être compté comme un succès parce que le brouillon semble plausible.
- Granite atteint la limite de sortie de son premier repérage et déclenche le traitement en partitions. Il est finalement rejeté pour des informations d'Allagan Gravity absentes de ses propres citations, après correction. Temps total : environ 160 secondes.

### The Labyrinth of the Ancients

- Gemma conserve les quatre boss principaux et la condition d'Astral Realignment. La protection derrière les comètes est correcte dans le texte d'Ecliptic Meteor. Les étapes Atomos/Bombe allagoise sont omises ; le nombre de quatre joueurs par plateforme de Phlegethon, présent dans le classeur, n'est pas conservé.
- Qwen conserve également les quatre boss principaux, la protection des comètes et le retour à la plateforme pour Ancient Flare, sans importer la consigne des flaques de Scylla située ailleurs dans le même onglet. Il omet lui aussi les étapes Atomos/Bombe allagoise et le nombre de quatre joueurs. Des doublons descriptifs des squelettes augmentent le nombre de mécaniques sans augmenter la couverture.
- Qwen transforme la réponse à la réception d'Astral Realignment en réduction de dégâts pour le tank, alors que la source demande de prendre/attaquer le boss. Sa description conserve cette condition, mais sa consigne ne la restitue pas. Il présente certains adds comme des noms de sorts et invente une destruction de plateforme par les Iron Giants.
- Granite est rejeté sur les citations des squelettes après correction. Qwen termine après une réponse groupée tronquée, quatre analyses individuelles et une correction d'Ancient Flare ; ses 143,8 secondes comprennent ces reprises.

## Portée de la décision

Qwen est le seul profil qui produit un guide complet pour chaque cas de ce corpus et il conserve davantage d'éléments du combat actuel de Haukke et du Continuum fractal. C'est la raison de maintenir son choix par défaut. Ce n'est ni une victoire générale de famille de modèles, ni une validation de ses consignes : **les inversions de partage de dégâts et les alternatives normales/trompeuses perdues sont des défauts bloquants de qualité**.

Gemma est une alternative intéressante pour la restitution explicite des conditions, mais son erreur de liste de boss au palais, ses omissions et son échec sur le Continuum ne justifient pas de le substituer globalement. Granite n'est pas un repli fiable ici : deux préparations rejetées et des erreurs sémantiques dans les résultats acceptés.

Les contrôles techniques actuels laissent passer des erreurs de sens malgré des citations présentes. Les priorités mises en évidence sont la cohérence entre description et consigne courte, la conservation des alternatives opposées, l'identité séparée des acteurs d'une rencontre à plusieurs boss et la vérification du type/propriétaire des événements. Les résultats de cette campagne sont conservés avant toute modification de ces règles, afin de servir de référence.

La vérification complémentaire des identifiants utilise les vraies classes `GuideIdCatalog` et `GuideIdPlan` sur les données installées. Elle confirme les noms composés non résolus, mais ne rejoue pas une session et ne mesure pas un taux d'alertes en jeu. Des déclencheurs marqués manuels peuvent encore trouver un identifiant par leur nom et leurs preuves : ils ne sont pas tous assimilés à des alertes impossibles.

Dans le Continuum, les étiquettes de projections **Sephirot/Sophia/Zurvan**, utilisées par Qwen comme noms de sorts, ne trouvent pas d'identifiants de sort ou de statut. À Kefka, les libellés « Tricky… » de Gemma préservent mieux le sens des variantes mais ne se rattachent pas tels quels aux sorts du jeu. Ces résultats distinguent restitution lisible et déclenchement utilisable.

## Reproduction

Les commandes sont des sondes explicites ; la suite normale ne lance aucune inférence :

```text
ForetellRuntimeTests.dll --guide-compare-one <journal-enregistré.json> <runtime-local-vérifié> <nouveau-répertoire-résultat> <model-id>
ForetellRuntimeTests.dll --guide-compare-bindings <répertoire-des-résultats> <game-sqpack>
```

La première conserve les sources complètes, la conversation et un `result.json`, ainsi que `prepared.json` si la validation aboutit. Un échec d'analyse est enregistré puis permet au lot de poursuivre ; une erreur de configuration ou des poids non vérifiés arrêtent la commande. La seconde utilise le catalogue du jeu pour relever les candidats d'identifiants ; elle ne simule ni les phases, ni les cibles, ni la disponibilité historique de la fiche.

## Validation de l'outillage

Les 15 inférences réelles et les 12 inspections de guides acceptés ont été exécutées. Les suites runtime et core ainsi que le contrôle des processus de test sans fenêtre ont passé. La compilation locale réussit ; elle signale seulement l'indisponibilité de l'audit NuGet en ligne (`NU1900`). Aucun code du plugin, poids, réglage utilisateur ou numéro de version n'est modifié par cette itération : elle ajoute les sondes explicites et publie le bilan.
