# Le Bras du Père — analyse de la capture 0.13.12

L'export confirme une limitation de la liste et un rejet de détection dû à une phase périmée. La version 0.13.14 retire la limite de six rappels, rend la liste défilable et accepte une attaque reconnue sans ambiguïté même lorsque sa phase ne peut pas être déterminée.

## Éléments examinés

- Export : `foretell-analysis-T444-20260909-212914-953.zip`, session `20260909-191931`, plugin et exporteur `0.13.12.0`.
- SHA-256 : `203EDF3F2A192051455E6314DC845AF6BAD9111EECAB711A686CC6987524495A`.
- Territoire 444, contenu 114, Alexander - The Arm of the Father.
- Capture de décision : 42 672 observations lues, aucune rejetée, capture déclarée complète. Guides : 16 instantanés, 132 images de chronologie et 46 décisions d'association distinctes. Aucun trou de séquence, abandon d'audit ou omission de chronologie signalé.
- Sources historiques et adaptations du ZIP ; comparaison des identifiants avec les données du jeu installées. Le guide enregistré localement a aussi été validé avec le code corrigé : même empreinte source, neuf mécaniques, quatre phases conservées, aucune inférence.

Les observations, audits et échantillons d'affichage ne représentent pas des comptes d'attaques uniques. Une soumission de dessin ne prouve pas des pixels lisibles en jeu.

## Liste de gauche

Le guide préparé contient Cleave, Protean Wave, Splash, Sluice, Wash Away, Split, Tether, Debuffs (+/-) et Cascade.

Avant identification de phase, la sélection de six rappels privilégiait les réponses du rôle du joueur. Les lignes Cleave, Debuffs (+/-) et Cascade étaient absentes de cette sélection. En phase finale, sept entrées étaient admissibles, mais seules six apparaissaient : Cleave était écartée. Une seconde limite pouvait encore réduire les lignes selon la hauteur disponible, même si aucune ligne déjà sélectionnée n'est signalée comme coupée dans cet export.

La nouvelle sélection garde toutes les entrées admissibles, y compris celles d'autres rôles ou classées comme contexte. L'ordre du guide reste stable pendant les attaques. Une zone défilante conserve le titre du boss ; la molette fonctionne aussi lorsque la fenêtre est verrouillée. Une nouvelle occurrence active ramène sa ligne dans la zone visible sans imposer le défilement à chaque image.

Le filtre de phase reste disponible : mécaniques communes et signaux actifs sont conservés ; une phase inconnue expose tout le boss. Désactiver « Suivre la phase actuelle » dans Affichage permet de consulter toutes les phases ensemble.

## Alertes : plusieurs causes distinctes

| Mécanique | Constat dans la capture | Portée de la correction |
| --- | --- | --- |
| Protean Wave, Sluice, Cascade | Des associations avec les identifiants du jeu et des alertes de guide sont enregistrées. | Leur liste n'est plus limitée à six lignes. |
| Splash | Des alertes de guide existent, dont des échantillons partiellement coupés. | Nouveau rendu central avec largeur contrôlée, contour, icônes et fond réglable ; validation native hors jeu. |
| Wash Away | Le cast 4863 est rejeté avec `OtherPhase` alors que la phase retenue est la phase 1. Le guide l'affecte aux phases 2 et 4. Une alerte générique « Lessivage » pouvait apparaître, sans la consigne du guide. | L'association unique est désormais acceptée (`MatchedGameIDPhaseUncertain`). Le suivi abandonne la phase périmée et garde la phase inconnue au lieu de deviner entre 2 et 4. |
| Debuffs (+/-) | Deux observations de cast Ferrofluid 4858 n'ont aucun candidat dans le guide. L'entrée est manuelle et ne possède pas de déclencheur nommé Ferrofluid. | La ligne reste consultable. Le défilement ne fournit pas cette association ni l'observation des deux polarités. Les directions conditionnelles ne sont pas inventées. |
| Cleave, Split, Tether | Des descriptions sont présentes, mais leurs noms génériques ne suffisent pas à établir tous les événements correspondants. Blunt Resistance Down 573 n'a pas de déclencheur préparé. | Les rappels restent consultables ; aucune nouvelle association de combat n'est revendiquée pour ces entrées. |

La liste et les alertes centrales ont des sélections séparées. Le plafond de six rappels n'empêchait donc pas, à lui seul, la détection d'une attaque. Les 46 audits comprennent 16 associations acceptées, 22 événements sans candidat, sept sources étrangères et le rejet de phase de Wash Away.

Le rendu enregistré comporte 34 échantillons avec au moins un élément partiellement coupé, et 38 éléments centraux omis par priorité. Des alertes génériques de type « MARQUEUR » portent aussi des noms de statuts de tank. Leur attribution et leur priorité demandent une analyse séparée ; cette version ne prétend pas corriger cette classification. Le nouveau style central vise la lisibilité : orange, deux avertissements, contour noir et fond sombre. Les couleurs personnalisées persistent ; « Style contrasté » applique le nouveau style aux réglages existants.

Le découpage préparé reste imparfait : la source décrit une reprise des capacités de phase 1 dans la phase finale, mais les appartenances préparées de Protean Wave et Splash indiquent seulement la phase 1. Les quatre titres sont de véritables titres de phase, donc la correction 0.13.13 des attaques prises pour des phases ne les supprime pas. Cette version ne réécrit pas ces appartenances sémantiques. Le filtre peut être désactivé pour consulter les neuf rappels ensemble.

## Vérification de la correction

- Lecture du guide enregistré et validation normale du cache : neuf mécaniques, quatre phases, aucune réanalyse. Simulation du passage Protean Wave → Wash Away avec les identifiants officiels : association acceptée, phase inconnue, neuf lignes dont Wash Away active. La phase finale expose ses sept entrées préparées.
- Régressions : association partagée entre plusieurs phases, maintien des conditions de cible et de cumul, refus des déclencheurs ambigus, ordre stable, répétitions, nouvelles attaques simultanées et défilement manuel.
- Tests ImGui natifs : accès aux première et dernière lignes d'une liste de 23 mécaniques, verrouillée et déverrouillée, à plusieurs tailles ; titre fixe et molette fonctionnelle. Les contours/icônes des alertes ont été inspectés sur des fonds synthétiques clairs et sombres.
- Cache : aucune requête source pendant sept jours, y compris après recréation des services ; expiration, actualisation manuelle et cache endommagé testés. Une lecture ne repousse pas l'expiration et un guide préparé compatible ne lance pas de modèle.

La capture établit les défauts de 0.13.12. Les validations du correctif sont effectuées hors jeu ; une nouvelle session avec 0.13.14 reste nécessaire pour confirmer son comportement dans le combat réel.
