# Première tranche hybride — 6 septembre 2026

## Résultat et périmètre

Le chargement est générique : à l’entrée dans un contenu avec une identité ContentFinderCondition et un territoire concordants, Foretell demande sa page Console Games Wiki. Aucun identifiant de Praetorium, Orbonne ou autre instance n’est codé dans le chemin produit. Ces contenus sont des cas de vérification. Aucun téléchargement n’est lancé pour l’open world, identifié par l’absence de ContentFinderCondition.

`/foretell` → **Guides** expose l’état de récupération, le cache, l’actualisation et la préparation hors combat par recherche dans le catalogue du client. La liste latérale apparaît en mode Hybrid ou Foretell. Un cache périmé reste visible pendant son actualisation ; un échec conserve cette dernière fiche. Une page absente, ambiguë ou de structure non reconnue est explicitement indisponible. Il n’y a ni demande d’URL au joueur ni recherche approximative conduisant silencieusement vers une autre difficulté.

La fonctionnalité livrée est une base documentaire utilisable avec une synchronisation partielle. Elle ne constitue pas une couverture opérationnelle de tous les combats.

## Acquisition vérifiée

Les requêtes HTTP directes vers les pages et `mediawiki/api.php?action=parse` ont répondu 200 pour les deux références initiales. Le choix de l’adaptateur repose sur ces réponses réelles : JSON `formatversion=2`, HTML rendu et numéro de révision. Les titres, listes imbriquées, tableaux et paragraphes sont traités comme des données ; aucun script, média, lien externe ou instruction de page n’est exécuté.

Test du véritable client HTTP et du parseur du plugin, avec les noms anglais exacts des sheets :

| Page | Révision | Boss | Entrées de mécaniques | Groupes de phases | Consignes simples préparées |
| --- | ---: | ---: | ---: | ---: | ---: |
| [The Praetorium](https://ffxiv.consolegameswiki.com/mediawiki/index.php?title=The_Praetorium&oldid=1528623) | 1528623 | 3 | 20 | 3 | 6 |
| [The Orbonne Monastery](https://ffxiv.consolegameswiki.com/mediawiki/index.php?title=The_Orbonne_Monastery&oldid=1498240) | 1498240 | 4 | 46 | 6 | 2 |
| [Sastasha](https://ffxiv.consolegameswiki.com/mediawiki/index.php?title=Sastasha&oldid=1489668) | 1489668 | 3 | 5 | 3 | 0 |
| [The Bowl of Embers (Hard)](https://ffxiv.consolegameswiki.com/mediawiki/index.php?title=The_Bowl_of_Embers_(Hard)&oldid=1439247) | 1439247 | 1 | 6 | 5 | 0 |

Ces nombres décrivent une extraction documentaire. Un groupe sans mécanique nommée garde son texte de contexte ; un groupe sans titre signifie « mécaniques documentées », pas une phase reconnue en jeu. Le nombre de consignes préparées n’est pas un nombre d’alertes validées : certaines entrées homonymes sont volontairement non synchronisées et le partage nécessite un marqueur personnel qui n’est pas reconnu par cette tranche.

## Format, limites et conservation

Chaque document conserve le schéma, l’identité contenu/territoire, le titre canonique, la révision, la date de récupération, le SHA-256 du HTML source, les boss, les ancres et les phases. Chaque mécanique conserve sa description entière, ses conditions imbriquées et ses paragraphes de continuation. La préparation prudente des descriptions simples se fait sur le worker ; le rendu lit un résultat immuable.

Le worker possède une seule requête active et une seule demande en attente, remplaçable. Un changement de destination annule le travail précédent et empêche sa publication dans le mauvais contexte. Le délai total de récupération est borné à 25 secondes, le client HTTP à 20 secondes, la réponse à 2 Mio, l’arbre à 100 000 tokens et 64 niveaux, et le document à 32 boss / 512 mécaniques. Les expressions régulières ont un timeout. Les noms de fichiers sont dérivés uniquement d’identifiants numériques.

Le cache `foretell-guides` utilise une enveloppe à empreinte et un remplacement atomique. Il vérifie le schéma et l’identité à la lecture. La limite est de 128 fiches et 64 Mio ; une fiche est réactualisée au besoin après sept jours. Un cache corrompu est ignoré puis récupéré depuis la source si possible. Une erreur d’écriture n’empêche pas d’utiliser la fiche préparée en mémoire. Aucune donnée de combat ou de joueur n’est envoyée au wiki ; seule l’instance demandée apparaît dans la requête.

## Identité, langue et signaux

Les usages internes de `Service.LuminaSheet` restent en anglais. `Service.LuminaDisplayRow` fournit un accès distinct à la langue du client. Il n’y a aucun balayage des sheets Action/Status/BNpcName dans la boucle de jeu ; les lignes identifiées sont consultées et mises en cache avec un budget de lectures. Le catalogue ContentFinderCondition est chargé une seule fois sur un worker lorsque l’onglet de préparation est utilisé.

Vérification locale avec les fichiers du client : Praetorium CFC 16 / T1044, Orbonne CFC 636 / T826, Sastasha CFC 4 / T1036 et Ifrit brutal CFC 59 / T292. Les noms français proviennent bien des mêmes lignes localisées. Les données réelles contiennent quinze actions nommées « Innocence » et deux BNpcName nommés « Mustadio ». Ces recherches hors jeu montrent des **candidats**, pas des identifiants validés pour une rencontre ; aucun de ces candidats n’est codé dans le produit.

La synchronisation utilise le contenu et sa variante, l’acteur ennemi réel avec OID et BNpcName, et son Action de cast active. Le nom anglais officiel doit correspondre exactement à une seule entrée du boss et de ses phases. Une ressemblance, une entrée répétée entre phases, un helper non identifié ou une autre difficulté n’activent rien. Un cast fini, interrompu, remplacé ou provenant d’un boss mort ne reste pas surligné. L’échantillonnage lit les casts typés WorldState existants toutes les 200 ms ; il reste indépendant du budget de capture sémantique. Il ne reconstitue pas un signal déjà disparu.

Le surlignage signifie « ce cast correspond à cette entrée documentaire ». Le groupe de phase affiché est celui du document ; il ne démarre aucune horloge ni prédiction de phase. Les entrées complexes gardent leur limite de réponse. Les consignes centrales sont limitées aux réponses simples préparées et applicables : le tankbuster exige que le joueur soit la cible du cast, le partage documentaire reste sans alerte centrale faute de marqueur personnel établi. Les géométries radar/3D et les routes restent celles du moteur de signaux existant. Aucune forme ou probabilité n’est inventée à partir du wiki.

Les consignes simples et les libellés principaux sont fournis en EN/FR/DE/JA. Les noms d’action/boss reconnus utilisent les IDs contextualisés, puis les noms officiels localisés. Les statuts actuellement présents peuvent afficher leur nom officiel en regard d’un terme source ; cela ne leur attribue aucune règle tactique. Les noms sans correspondance contextuelle restent marqués `[EN]`, et les paragraphes complexes sont conservés en anglais avec « consigne à préparer ». La traduction tactique générale demandée reste donc partielle, explicitement visible.

## Ce qui demanderait un modèle

La récupération et la structure HTML n’exigent pas de LLM. La conversion générale d’un paragraphe en conditions, variantes, séquences et consignes personnelles localisées ne se réduit pas au petit classifieur actuel. Elle demanderait une extraction contrainte, reliée aux passages sources, puis une validation distincte de la correspondance aux signaux et des paramètres géométriques. Un modèle seul ne prouverait pas ces correspondances.

Aucun LLM, runtime GPU ou outil de synchronisation GitHub n’est introduit dans cette tranche. Les fiches n’ajoutent aucune exigence GPU et restent consultables sans modèle. Si cette extraction est ajoutée, elle devra être pilotée depuis le plugin, isolée dans un processus auxiliaire, bornée en RAM/temps et exécutée hors du chemin des alertes. Le futur petit outil GitHub reste différé.

## Vérifications

- Tests déterministes de parsing : contexte boss/phase, phases en paragraphes et titres frères, tableaux, listes conditionnelles, continuations, scripts ignorés, limites et provenance.
- Tests de correspondance : mauvais boss/contenu/territoire/variante, noms approchants, homonymes entre phases, identité absente, cast expiré ; liaison aux véritables types Actor/ActorCastInfo, interruption, remplacement et mort.
- Tests de consigne : aucune réduction de conditions complexes à une réponse générique, condition personnelle du tankbuster, absence de partage fondé sur le seul cast, langues des gabarits.
- Tests cache/HTTP : cache frais sans réseau, cache périmé utilisable hors ligne, corruption puis récupération, annulation au changement d’instance, arrêt du worker, taille HTTP maximale et rejet d’une page de challenge HTML.
- Suites ForetellRuntimeTests et ForetellCoreTests, contrat de télémétrie et compilation Release.

Commandes complémentaires : `ForetellRuntimeTests --wiki-smoke "the Praetorium" "the Orbonne Monastery" "Sastasha" "the Bowl of Embers (Hard)"` pour le vrai HTTP ; `ForetellRuntimeTests --guide-sheets "chemin/game/sqpack"` pour les identités et traductions du client, en lecture seule. Les fixtures CI sont synthétiques : aucune page wiki complète ni capture privée n’est ajoutée au dépôt.

Il n’y a pas de validation visuelle ImGui, de test en combat ou de mesure de coût en session réelle dans cette itération. La capture Orbonne 0.10.1 partielle n’est pas utilisée comme vérité complète. La synchronisation des variantes d’Analysis, des actions spéciales d’Agrias et des variantes de Thunder God reste à établir. Une extraction réussie ne valide pas ces mécanismes ni leurs zones.
