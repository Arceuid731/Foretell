# Col du dragon : échec de validation du 12 septembre 2026

## Cause et correction 0.13.18

Le journal fourni `analysis-20260912-125926-6a062cc5` concerne `C81-T142`, Qwen 3.5 4B, contexte 65 536, limite RAM 12 Gio, GPU activé. Il contient le wiki, Raven et le classeur communautaire. Les quatre appels au modèle terminent normalement ; l'échec final est une `InvalidDataException` du validateur Foretell.

Le wiki distingue deux libellés : `Fungah` au paragraphe 54, un cône frontal avec poussée, et `FUNGAH` au paragraphe 74, un cercle sur un joueur marqué. Le brouillon conserve ces deux noms. Le contrôle des doublons les convertissait tous deux en minuscules, puis demandait au modèle de les fusionner. Les deux réponses de réparation les conservent et sont rejetées pour la même raison.

Le contrôle conserve désormais ces distinctions quand chaque nom possède un libellé explicite, respectant la casse, dans ses propres citations. Il rejette encore deux occurrences du même nom, une simple variation d'espacement, une casse inventée et une citation ne portant pas le nom exact. Aucun nom de boss ou d'attaque particulier n'est inscrit dans cette règle. Le diagnostic des vrais doublons indique maintenant le boss et le nom concernés.

Les recherches d'événements conservent leurs contrôles d'ambiguïté : accepter deux entrées documentées ne permet pas de choisir arbitrairement entre elles lors d'une incantation. La résolution d'un nom de boss composé reste également un sujet distinct.

## Vérification limitée à ce défaut

- Avant correction : le rejeu du premier brouillon enregistré reproduit exactement l'exception des doublons.
- Après correction : les mêmes 15 mécaniques passent la validation et le contrôle de rechargement du cache ; validation observée de 54,1 ms, sans inférence. Les références de phase invalides sont retirées par le mécanisme de repli existant.
- Les tests couvrent les noms documentés distincts, les vrais doublons, les changements de casse non documentés, les citations incorrectes et le refus d'un événement ambigu.
- Aucun nouvel appel LLM, téléchargement de modèle, changement de préférence ni injection de guide préparé dans le cache du joueur.

Le journal montre 50,44 s de rédaction puis deux réparations de 33,01 et 32,47 s, produisant 4 827 tokens au total pour les seules réparations. La correction supprime leur cause de rejet. Le rejeu ne mesure pas la durée d'une nouvelle préparation complète : une réparation limitée aux phases peut encore intervenir ensuite.

Cette acceptation ne valide pas le sens des conseils. Le brouillon propose notamment de mitiger Megavolt, décrit comme une zone autour d'Ultros, et réduit Aqua Breath à un regroupement sans préserver correctement la condition d'imp. Ces défauts ne sont pas corrigés ni présentés comme résolus par cette version.

Le rejeu optionnel d'un brouillon enregistré ne lui applique plus la fixture de phases propre à Ultima Weapon, qui reste testée séparément.

```powershell
$env:FORETELL_ANALYSIS_REPLAY = '<journal JSON complet>'
./build/dotnet/dotnet.exe run --project ForetellRuntimeTests -c Release --no-restore
Remove-Item Env:/FORETELL_ANALYSIS_REPLAY
```
