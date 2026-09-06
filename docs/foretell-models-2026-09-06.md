# Modèles locaux et nouveau parcours Foretell — 6 septembre 2026

## Recommandation à évaluer, pas encore un changement de modèle

Priorité proposée : **Granite 4.1 3B**, puis comparaison avec **Gemma 4 E2B** et **Qwen3.5 4B**. C'est un choix de candidats basé sur leurs caractéristiques publiées, pas un classement mesuré sur des guides FFXIV. Le français, l'extraction fidèle, les conditions et le JSON importent davantage ici que les benchmarks de code ou de mathématiques.

La date d'une vidéo, d'un README ou d'une quantification ne donne pas la date du modèle. Parmi ces trois candidats, aucun ne respecte « sorti depuis moins de deux mois » au 6 septembre. La recherche n'a pas établi de remplaçant officiel de juillet/août satisfaisant simultanément ce critère, la taille strictement inférieure à 8B et ce cas d'usage. Cela ne démontre pas qu'il n'en existe aucun.

| Modèle / variante | Taille pertinente | Évaluation pour Foretell et sources primaires |
| --- | --- | --- |
| Granite 4.1 3B | 3B dense | Sorti le **29 avril 2026**, français pris en charge, extraction et résumé annoncés, Apache 2.0. Premier candidat proposé pour la sortie structurée sans raisonnement prolongé. [Fiche IBM](https://huggingface.co/ibm-granite/granite-4.1-3b), [architecture et entraînement](https://huggingface.co/blog/ibm-granite/granite-4-1). La variante 8B n'est pas strictement sous 8B. |
| Gemma 4 E2B-it | 2,3B effectifs, **5,1B avec embeddings** | Candidat compact multilingue, contexte annoncé de 128K. La famille E2B/E4B apparaît au **31 mars 2026** dans le calendrier Google. E4B atteint **8B** avec embeddings ; 12B et le MoE 26B ne respectent pas la limite. [Fiche Google](https://ai.google.dev/gemma/docs/core/model_card_4), [calendrier](https://ai.google.dev/gemma/docs/releases). |
| Qwen3.5 4B | 4B | Candidat généraliste compact plus récent que le Qwen3-1.7B actuellement intégré, mais pas un modèle de l'été : lancement des petites tailles le **2 mars 2026**. [Fiche Qwen](https://huggingface.co/Qwen/Qwen3.5-4B), [annonces de l'équipe](https://github.com/QwenLM/Qwen3.8#news). |
| Qwen3 0.6–8B | Selon variante ; retenir les tailles < 8B | Famille de 2025, pas nouvelle parce qu'elle figure dans une vidéo récente. [Dépôt officiel](https://github.com/QwenLM/Qwen3). L'intégration existante utilise 1.7B Q8_0 ; elle reste en place dans cette version. |
| Phi-4 mini-reasoning | 3,8B | 128K annoncés, mais Microsoft indique une conception/évaluation centrée sur le raisonnement mathématique. Moins prioritaire pour extraire rapidement un guide français ; ce n'est pas une preuve d'incapacité. [Fiche Microsoft](https://huggingface.co/microsoft/Phi-4-mini-reasoning). |
| SmolLM3 | 3B | Français, Apache 2.0, contexte natif 64K et extension 128K par YaRN. Bon candidat de référence léger ; pas automatiquement meilleur sur les consignes conditionnelles. [Fiche HuggingFaceTB](https://huggingface.co/HuggingFaceTB/SmolLM3-3B). |
| Ministral 3 Instruct 2512 | Variante 3B | Autre candidat compact à comparer, version de décembre 2025 ; les variantes 8B/14B dépassent la contrainte stricte. [Fiche Mistral](https://huggingface.co/mistralai/Ministral-3-3B-Instruct-2512). |
| Tiny Aya Earth | Famille compacte multilingue | Intéressant pour les langues, mais fiche sous **CC-BY-NC-4.0** et accès soumis à acceptation. Pas de téléchargement/acceptation automatique ni de modèle par défaut proposé sans traiter ces contraintes. [Fiche Cohere Labs](https://huggingface.co/CohereLabs/tiny-aya-earth). |
| North Mini Code | **30B totaux**, 3B actifs | Sorti le 9 juin 2026, spécialisé code, hors budget de poids malgré « Mini ». [Annonce Cohere](https://cohere.com/blog/north-mini-code). |
| Nemotron 3 Nano | **30B totaux**, environ 3,5B actifs | Hors budget de poids. Les paramètres actifs d'un MoE ne sont pas la quantité de poids à stocker/charger. [Fiche NVIDIA](https://huggingface.co/nvidia/NVIDIA-Nemotron-3-Nano-30B-A3B-BF16). |

La machine observée dispose d'un Ryzen 7 9800X3D, d'une RTX 5080 avec environ 16 Gio de VRAM et d'environ 64 Gio de RAM. Cela ne donne ni un budget libre pendant FFXIV ni une garantie de fluidité. Aucun nouveau modèle n'a été téléchargé, chargé ou benchmarké pendant cette itération.

Avant activation d'un autre modèle : fixer le GGUF, sa révision et son SHA256 ; vérifier licence, compatibilité llama.cpp, template, JSON contraint et arrêt du raisonnement ; mesurer chargement, RAM/VRAM et latence hors jeu ; comparer ensuite fidélité aux sources, attribution au boss, non-fusion des mécaniques, préservation des alternatives et taux de rejet. Un sélecteur de modèles arbitraires n'est pas encore livré ; ces candidats ne sont pas des profils validés.

## Livré dans 0.12.4

- **Instance** : guide de l'instance réelle, état de préparation, boss actuel/à venir, mécaniques et signal associé. Une fiche consultée dans le catalogue n'est pas présentée comme le boss en combat.
- **Sources et guides** : acquisition automatique, catalogue et document intégral disponible dans le cache extrait. Le fournisseur actuellement branché reste Console Games Wiki ; couverture non garantie.
- **IA locale** : modèle exact, téléchargement/vérification, chargement, attente, comptage, inférence, déchargement ; identité du processus et backend réellement utilisé. Un cache prêt ne signifie plus un modèle chargé. L'état est aussi visible dans l'en-tête et le panneau d'entrée.
- **Affichage** : liste transparente des mécaniques, déverrouillage, dimensions/couleurs, alerte centrale, texte, radar et 3D. Les identifiants de configuration et les positions existantes sont conservés.
- **Avancé** : mémoire observée, calibration, timeline, enregistrements et Analysis ZIP, apprentissage/stockage et diagnostics. BMR, Hybrid et les vérifications par observation ne sont pas supprimés.

Le contexte est configurable de 4096 à 32768 tokens (16384 par défaut), avec une limite de RAM engagée de 4 à 12 Gio (6 par défaut). Ce plafond n'est pas une réservation et ne borne pas la VRAM. Agrandir le contexte augmente les ressources et le temps de préparation ; si le processus atteint sa limite, un échec reste possible. Les changements relancent les travaux incomplets avec les nouveaux paramètres, sans effacer les résumés déjà acceptés.

L'ancienne exclusion des passages de plus de 7000 caractères disparaît. Le prompt complet passe par le template du serveur puis son tokenizer ; une réserve couvre la réponse et les tokens de contrôle. Les dépassements restent non préparés avec une raison visible, sans découpe implicite. La translation de contexte du serveur est désactivée. Cela ne supprime pas les limites de sécurité de téléchargement/extraction des guides, et ne transforme pas un résumé de 120 mots en analyse exhaustive d'une page. [API du moteur épinglé](https://github.com/ggml-org/llama.cpp/blob/b10809/tools/server/README.md).

L'analyse exporte les réglages de contexte/RAM, l'état du processus et la dernière raison de rejet au moment de la session. Les instantanés sont échantillonnés et bornés, pas une trace exhaustive de chaque transition. Ils n'exposent pas la clé du serveur local et ne réinjectent pas de texte IA dans l'apprentissage lors des replays.

## Changement de paradigme : cible encore à implémenter

Le moteur actuel découpe toujours le wiki avant de demander des traductions par passage. Ce n'est **pas** encore l'analyse sémantique globale demandée. Le défaut de fusion de paragraphes ne peut donc pas être attribué uniquement au petit modèle, ni déclaré corrigé par cette refonte d'interface.

La cible doit séparer trois couches :

1. Acquisition par fournisseur → document conservé avec URL, révision/hash, texte complet utile et repères de provenance ; pas de dépendance commune à une hiérarchie HTML de boss.
2. Analyse IA du document → boss, phases, mécaniques, conditions, courte consigne et extraits justificatifs, dans un schéma validé. Si le document dépasse le contexte utilisable, découpage couvrant **toute** la source puis réconciliation avec contrôle de couverture ; jamais simplement les premiers N caractères. Les sources restent des données, pas des instructions exécutables.
3. Association déterministe au jeu → boss actuel, identités officielles, signaux réellement observés et géométrie établie. L'IA ne fabrique ni ActionID ni dimensions ni conditions observées. Ambiguïtés et conflits entre sources restent explicites.

Cette cible, les fournisseurs supplémentaires et le choix effectif du prochain modèle restent distincts des changements livrés ici. La refonte n'annonce pas une couverture universelle ou un remplacement complet des modules BMR.
