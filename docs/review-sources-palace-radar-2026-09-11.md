# Sources, Palais des morts et cadrage radar — 11 septembre 2026

## Constats et corrections de la version 0.13.15

Les 33 caches locaux contenant un état par fournisseur indiquent 33 succès Console Games Wiki, 33 indisponibilités Gamer Escape, 23 indisponibilités et 10 absences de correspondance Raven. Le classeur communautaire compte 17 succès, une réutilisation de cache et 15 absences de guide. Ces chiffres décrivent les caches inspectés, pas la disponibilité générale des sites.

### Raven

L'accès HTTP/1.1 du client .NET reproduit un 403 nginx. Un programme minimal sous .NET 10.0.11, avec la même URL et le même User-Agent Foretell, reçoit 403 en HTTP/1.1 puis 200 en HTTP/2. Les essais de compression seuls n'ont pas résolu le problème dans le client du plugin. La négociation HTTP/2, avec repli HTTP/1.1 autorisé, a ensuite permis la récupération réelle par Foretell.

L'API publique annoncée par le site expose le type `tldr_guide` : [première page](https://ravensreminders.com/wp-json/wp/v2/tldr_guide?per_page=100), [seconde page](https://ravensreminders.com/wp-json/wp/v2/tldr_guide?per_page=100&page=2). Les réponses contiennent 100 puis 86 entrées, avec titre, lien public, contenu HTML rendu et date de modification. Le nouvel adaptateur conserve l'objet source complet, extrait le texte et les libellés d'icônes, et réutilise les téléchargements dix minutes entre instances. Les pages, octets, redirections et caches restent bornés. Une correspondance doit être unique sur le catalogue complet ; la difficulté reste discriminante. L'ensemble API + secours HTML dispose d'un budget de vingt secondes pour ne pas faire expirer l'agrégation des autres sources.

Le catalogue HTML reste un secours. Les noms avec tiret typographique et suffixe Alexander `(A3)`, ainsi que l'article initial facultatif, sont maintenant reconnus. Auparavant A3 pouvait être déclaré absent même avec un catalogue correctement téléchargé.

Test réel du code corrigé, depuis un répertoire de cache séparé :

| Instance | Wiki | Gamer Escape | Raven | Classeur |
| --- | --- | --- | --- | --- |
| Orbonne | Disponible | 403 | Disponible via API | Disponible |
| Alexander — The Arm of the Father | Disponible | 403 | Cache API, bonne entrée A3 | Pas de correspondance |
| Palais des morts, 21–30 | Disponible après correction | Page manquante | Pas de correspondance | Pas de correspondance |
| Dusk Vigil | Disponible | Disponible | Cache API | Cache |

Les [guides Raven d'Orbonne](https://ravensreminders.com/tldrguide/orbonne-monastery-the/), [d'A3](https://ravensreminders.com/tldrguide/alexander-the-arm-of-the-father/) et [de Dusk Vigil](https://ravensreminders.com/tldrguide/dusk-vigil-the/) ont respectivement fourni 2 417, 358 et 380 caractères de texte extrait, sans découpage sémantique. Leur concision correspond au contenu public de cette source, pas à une garantie de couverture de toutes les mécaniques. Les caches récents existants restent réutilisés pendant sept jours ; une actualisation manuelle permet d'ajouter immédiatement Raven.

### Gamer Escape

HTTP/2 améliore aussi l'accès à ce fournisseur : [Dusk Vigil](https://ffxiv.gamerescape.com/wiki/The_Dusk_Vigil) fournit un document utilisable dans le test final. Orbonne et A3 renvoient encore 403. Les essais antérieurs sur l'API et la page publique produisaient une page de blocage Cloudflare. Il n'y a donc pas de correction universelle établie ; le fournisseur reste isolé et son cache valide est conservé en cas d'échec. Aucune connexion interactive ni résolution de challenge n'a été ajoutée.

### Palais des morts

La session `20260910-203604`, territoire 563, version 0.13.14.0, couvre le 10 septembre de 22:36 à 23:18, heure de Paris. Son identité documentaire est C176/T563, `the Palace of the Dead (Floors 21-30)`. Les dernières trames du journal documentaire indiquent `Failed: InvalidDataException: No guide source is available.` La détection de l'instance avait donc bien réussi ; l'échec précédait l'analyse du modèle.

L'API wiki renvoie `missingtitle` pour le nom commençant par `The`, mais accepte `Palace of the Dead (Floors 21-30)` et sa révision 1243663. La [page des étages 21–30](https://ffxiv.consolegameswiki.com/wiki/Palace_of_the_Dead/Floors_21-30) contient bien Ningishzida et ses consignes. L'adaptateur corrige ce titre source en conservant le nom et les identifiants du jeu dans le document agrégé. Un test refuse explicitement une réponse correspondant aux étages 31–40.

La présence de boss dans BMR ne les rend pas automatiquement disponibles dans les guides Foretell : les modules de rencontre BMR ne sont pas importés comme connaissances. Aucun module BMR n'a été utilisé pour définir les corrections. L'apparition progressive de l'AoE signalée pour 100-tonze Swing est cohérente avec l'apprentissage existant ; cette investigation ne prouve pas séparément la progression de confiance de cette attaque.

### Radar

Le cadrage précédent refusait une salle reconnue si son rayon dépassait 1,5 fois le cadrage des acteurs, ou 1,25 fois pour une limite détectée par les murs. En solo ou avec un groupe serré près du boss, le cadrage des acteurs pouvait rester à 16 yalms : une salle valide de rayon 30 était alors écartée.

La salle observée est maintenant évaluée indépendamment de la dispersion du groupe. Elle doit contenir le joueur et le boss, rester dans le rayon maximal configuré et ne pas présenter les proportions d'un couloir. Une limite de murs peut être essayée si le plancher fermé n'est pas retenu. En mode circulaire, le calcul du rayon inclut la distance aux coins, et non seulement leurs projections sur les axes.

Les tests couvrent une salle de 60 × 50 yalms avec joueur et boss regroupés, seize orientations de caméra, les cadres carrés et circulaires, un boss extérieur, un plafond utilisateur plus petit et un couloir. Ils établissent le comportement du calcul ; les journaux inspectés ne donnent pas les pixels du radar pour confirmer chaque défaut visuel rapporté. Une arène dont les limites ne sont pas correctement observées peut encore nécessiter des améliorations.

## Validation et limites

- Compilation Windows avec le SDK 10.0.400 du dépôt ; aucune erreur de compilation. L'audit NuGet local a signalé un accès réseau indisponible, distinct de la compilation.
- Suites complètes ForetellRuntimeTests et ForetellCoreTests, contrat de télémétrie et vérifications du comportement non interactif des tests.
- Tests de pagination, cache partagé, rafraîchissement hors ligne, provenance, refus de variantes, liens externes et contenu protégé/vide.
- Acquisition réseau du vrai code corrigé, sans lancement de modèle ou benchmark GPU et sans modification des caches installés du joueur.

La récupération des nouvelles sources est vérifiée. Une nouvelle analyse locale des guides et une validation visuelle en jeu restent distinctes. Les refus HTTP restants ne sont pas présentés comme résolus.
