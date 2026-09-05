# Praetorium — diagnostic de la session 0.10.1 et corrections 0.10.2

## Ce que le ZIP établit

Fichier analysé localement : `foretell-analysis-T1044-20260905-205607-453.zip` (33 091 613 octets). Session du 5 septembre, **20:27:43–20:55:56, heure de Paris**, territoire 1044, version 0.10.1.0. Aucun enregistrement privé n’est publié avec ce rapport.

- 62 950 observations traitées en jeu ; 58 195 événements conservés dans la capture automatique ; 4 755 rejets. L’index déclare correctement la capture **partielle**. Lecture progressive, empreintes des segments et comptes vérifiés par le lecteur de Foretell.
- 619 entrées d’audit de décisions, 212 épisodes finalisés. « Finalisé » ne signifie pas « mécanique comprise et validée ».
- Les 118 `CastStart` présents dans la capture sont des actions du joueur. Les casts ennemis apparaissent dans l’audit des prédictions, mais leurs entrées enrichies manquent dans la capture. On ne peut donc pas rejouer fidèlement ce combat avec ce ZIP, ni inventer les entrées absentes.
- Le raw inclus est complet : 45 262 enregistrements, 23 260 paquets serveur, 19 072 paquets client, 2 930 actor controls, aucune erreur structurelle. Un raw intact ne remplace pas automatiquement les observations sémantiques manquantes.
- Les compteurs au moment de l’export indiquent 11 053 rejets de traitement sémantique, 2 593 dépassements de budget et 139 échecs d’update. Ils sont **cumulatifs et exportés dans un autre territoire** : ils ne doivent pas être attribués intégralement à ce Praetorium. Les anciennes données ne donnent pas la cause des exceptions ; la prochaine version enregistre le dernier détail d’exception dans l’analyse.

## Lignes de Gaius : problème avant le rendu

Le contexte des acteurs contient bien les sources des différentes lignes. Les prédictions suivantes ont des origines distinctes ; les entrées répétées du même identifiant de prédiction sont dédupliquées.

| Vague, heure de Paris | Sources de lignes présentes dans le contexte | Lignes distinctes proposées par Foretell |
| --- | ---: | ---: |
| 20:53:06–07, triple | 3 | 1 |
| 20:53:19–20, quintuple | 5 | 2 |
| 20:54:14–15, quintuple | 5 | 1 |
| 20:54:46–47, quintuple | 5 | 2 |

Les lignes retenues ont une longueur de 40 et une largeur de 4, avec une confiance de 94 %. Le seuil de rendu était 75 % et la limite 12 formes. Il manque déjà des prédictions dans l’audit ; le zoom ou cette limite ne suffisent donc pas à expliquer le problème.

La comparaison en ligne confirme que Terminus Est produit des lignes depuis les croix, que Phantasmata multiplie ces sources, et que Ductus correspond aux cercles qui traversent la plateforme. Festina Lente demande un regroupement. Chez Nero, Augmented Shatter demande également un regroupement, tandis qu’Augmented Suffering repousse les joueurs vers la bordure électrique. Ces règles donnent un point de comparaison indépendant des sorties de Foretell. [Guide actuel du Praetorium](https://ffxiv.consolegameswiki.com/wiki/The_Praetorium).

Le [module BMR de Gaius](../BossMod/Modules/RealmReborn/Dungeon/D14Praetorium/D143Gaius.cs) ajoute ses rectangles dès l’apparition de chaque source, avec un délai écrit à la main de six secondes. Les casts correspondants durent environ trois secondes. Cela explique aussi une avance possible de BMR, distincte des pertes de Foretell. Cette version récupère les casts visibles manqués ; elle ne promet pas la même anticipation dès la première apparition d’un acteur inconnu.

## Comparaison des trois boss

Les caractéristiques détaillées ci-dessous sont confrontées aux modules de référence du dépôt : [Colossus](../BossMod/Modules/RealmReborn/Dungeon/D14Praetorium/D141Colossus.cs), [Nero](../BossMod/Modules/RealmReborn/Dungeon/D14Praetorium/D142Nero.cs), [Gaius](../BossMod/Modules/RealmReborn/Dungeon/D14Praetorium/D143Gaius.cs). Ils sont lus pour le diagnostic ; leurs identifiants, chronologies et arènes ne sont pas importés dans le moteur adaptatif.

| Mécanique de référence | Audit de la session | Conclusion |
| --- | --- | --- |
| Colossus : Ceruleum Vent, dégâts de groupe | 2 annonces sans forme ni consigne spécifique | Cast détecté ; sens « raidwide » non établi. |
| Prototype Laser Alpha, cercles successifs | 2 annonces ; seulement 2 cercles intérieurs et 2 extérieurs proposés | Présence partielle, pas de preuve de couverture de toutes les sources. |
| Prototype Laser Beta, cercles individuels | 1 annonce et 1 cercle proposé | Multiplicité et consigne de dispersion non établies. |
| Grand Sword, cône | 1 cône proposé | Géométrie fournie par le client, validation indépendante non démontrée. |
| Nero : Spine Shatter | 2 annonces sans forme | Détection du cast, sans consigne tankbuster fiable. |
| Iron Uprising / Augmented Uprising | 2 cônes de chaque type | Les cônes sont proposés ; le recul secondaire d’Iron Uprising n’est pas prouvé par ces formes. |
| Augmented Shatter | 2 cercles avec « Avoid » | **Consigne erronée** : un rayon seul ne permet pas de décider de fuir un cercle de partage. Corrigé par abstention explicite. |
| Augmented Suffering | 1 cercle « Avoid », rayon 40 | **Consigne erronée** : le chemin du télégraphe contient `nockback`. La correction produit un avertissement de recul, sans inventer distance ou point d’arrivée. |
| Activate / griffe / bordure électrique | 2 annonces Activate | Ni l’arrivée de la griffe ni le danger de la bordure ne sont validés ici. Une surface praticable peut rester dangereuse. |
| Wheel of Suffering | 1 cercle proposé | Forme détectée ; succession après partage encore à vérifier. |
| Gaius : Terminus Est / Phantasmata | Détail des quatre vagues ci-dessus ; 2 annonces Phantasmata | Récupération des casts manqués et conservation des sources simultanées. |
| Festina Lente | 2 cercles avec « Avoid » | Même ambiguïté que le partage de Nero ; ne plus conseiller de fuir sur le seul rayon. |
| Ductus | 2 annonces, 8 cercles proposés | Ne prouve pas la couverture de toutes les files ni toute leur évolution. |
| Innocence / Horrida Bella / Heirsbane | 1 / 2 / 5 annonces sans forme | Détection présente ; sens spécifique non validé. |
| Hand of the Empire / Veni Vidi Vici | Pas de prédiction dédiée dans cet audit | Couverture non démontrée ; ne pas déduire une absence en jeu d’un audit/capture incomplet. |

La session ne fournit aucun verdict positif explicite de validation indépendante dans cet audit. Les pourcentages de 94–96 % de ces formes proviennent des données du client ; ils ne signifient pas que leur sens tactique a été confirmé à ce niveau.

## Corrections livrées

### Terrain et cadrage

Le rendu précédent multipliait l’opacité par 0,45 dès qu’un rafraîchissement invalidait temporairement la topologie, puis remontait à 1 au résultat suivant. Le remplissage conserve maintenant son opacité pendant les rafraîchissements normaux ; seule une carte publiée devenue ancienne s’estompe progressivement après dix secondes. Les mises à jour du sol restent atomiques, sans transition qui maintiendrait visuellement un sol disparu. Les bandes de remplissage utilisent des quadrilatères sans lissage des jointures internes, pour éviter les coutures de transparence.

Le mode Auto utilise un cadre carré. Les coins des salles rectangulaires sont inclus dans le calcul de cadrage et le découpage suit ce cadre, y compris après rotation de la caméra. La vue s’adapte aux limites compactes observées ou à l’espace occupé par le boss, le groupe et les sources d’attaques. Le joueur reste centré en exploration ; dans un combat cadré, son marqueur se déplace dans la vue. Déplacement du centre et changements de zoom sont lissés, avec une tolérance aux petites variations.

Le minimum de 30 yalms devient le rayon d’exploration, pas un minimum imposé aux petites arènes. La limite maximale choisie par l’utilisateur reste respectée. Le terrain du premier boss couvre par endroits environ 97 × 95 unités : cadrer ce sol connecté comme s’il constituait l’arène agrandissait excessivement la vue. Une cour ou des murs éloignés ne prennent plus automatiquement priorité sur le cadrage du combat. Ce cadrage reste une estimation : il n’établit pas à lui seul la position exacte d’une barrière de combat invisible dans les collisions.

### Collecte et formes simultanées

Le parcours des lignes Lumina pouvait atteindre les pages Excel de stockage et copier leurs données binaires dans les événements d’acteurs. Cela crée des observations démesurées et du travail inutile, compatible avec les pertes constatées. Les pages, modules et développements récursifs de références sont désormais exclus **avant l’appel des getters**. Les valeurs de la ligne et les identifiants de références restent accessibles ; les extractions statiques sont mises en cache dans une limite bornée. Les données raw du jeu restent collectées.

Après un dépassement de budget, une vérification des casts ennemis encore actifs permet leur récupération dans les frames suivantes. Elle conserve la durée restante réelle, ignore les casts déjà acceptés ou terminés et respecte le budget de traitement. Elle ne reconstitue pas un cast déjà disparu ni un signal jamais exposé au client.

La limite de rendu compte désormais des groupes d’attaques simultanées : les différentes sources d’une même action restent ensemble, dans une borne séparée de 64 formes. Le radar et l’affichage 3D utilisent cette même sélection ; le calcul des routes garde l’ensemble des dangers du cadre de décision.

### Sens des alertes et interface

Un cercle non marqué, sans indication de zone au sol ni Omen, conserve ses paramètres dans les preuves mais ne devient plus automatiquement un cercle « Avoid ». Il apparaît en **WATCH** tant qu’une consigne n’est pas établie. Cela peut rendre certaines attaques sans télégraphe moins précises, mais évite notamment l’instruction inverse sur un partage. Un Omen dont le nom identifie un recul produit **KNOCKBACK**, sans confondre portée d’effet et distance de recul. Les zones au sol explicites et les lignes ordinaires gardent leurs formes dès le premier cast.

La mémoire passe au schéma 25. Les anciens modèles concernés perdent la consigne dérivée de ce prior erroné et les validations/étapes associées ; les comptes d’observations et les autres connaissances sont conservés. Cette correction ne demande pas d’effacer la mémoire du plugin.

« ↑ camera » est supprimé. La légende devient **Learning / Confident / Very high**, avec **Confidence · not damage severity**. La collecte automatique reste active, sans option Replay à cocher.

## Vérifications et prochain test

Tests effectués : cadrage d’une petite arène, conservation des quatre coins sur 32 orientations, transitions de zoom, suivi du joueur, absence de variation d’opacité au rythme normal, changement de limites après suppression de sol, rejet d’une frontière d’échantillonnage comme fausse salle fermée ; cinq casts récupérés avec un budget de deux par frame, absence de doublons, conservation de l’heure d’impact et cinq prédictions distinctes dans le moteur réel ; regroupement des formes, filtres de données Lumina réelles, cercles ambigus, Omen de recul, télégraphes explicites, migration de mémoire et régressions de capture/export.

Le contrat de collecte, les suites core/runtime et la compilation Release complètent ces vérifications. Le ZIP privé a été contrôlé avec le lecteur progressif et le raw avec le lecteur structurel. Aucun test hors jeu ne valide le rendu ImGui, le coût réel sur une nouvelle session ou une couverture complète des mécaniques.

Pour le prochain passage : garder Auto + Automatic et le remplissage souhaité, observer la stabilité de l’opacité et du zoom sur les trois boss, puis compter les trois/cinq lignes de Gaius. Générer un Analysis ZIP après la sortie permettra de vérifier la présence des casts ennemis, les récupérations, les pertes et les consignes. Aucun `record on` nécessaire.
