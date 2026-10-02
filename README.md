# Overhaul

Overhaul est un mod pour **Valheim** qui réunit progression RPG, interface remaniée, gestion de l'équipement, améliorations du combat et confort de jeu dans un seul plugin : `Overhaul.dll`.

Le projet est en développement, actuellement en série **2.2**. Les fonctionnalités et leur équilibrage sont configurables. Les validations automatisées ne remplacent pas les essais en partie, notamment en multijoueur.

## Fonctionnalités principales

### Progression et combat

- Expérience, niveaux, points de statistiques et bonus passifs, avec progression persistante du personnage.
- Réglages d'expérience selon les biomes, catégories de monstres, étoiles et participation aux combats.
- Statistiques d'objets, vitesses d'attaque et effets des familles d'armes configurables.
- Comportements de créatures enrichis : agressivité, défense, réactions de groupe et fuite.
- Visée à l'arc accroupi, caméra décalée à l'épaule et impulsion de saut indépendante de la pente.

### Interface et inventaire

- Interface Auga intégrée et adaptée aux systèmes d'Overhaul, avec traductions françaises et anglaises.
- Emplacements d'équipement, apparences cosmétiques séparées des statistiques, raccourcis de nourriture et de munitions.
- Coffres renommables : nom enregistré, visible dans la fenêtre et en visant le coffre. `R` prend tout le contenu disponible et `Maj + R` permet de renommer.
- Accès public ou privé aux coffres et utilisation des ressources des coffres autorisés à proximité pour fabriquer et construire.
- La fenêtre de classes et de compétences est encore un **prototype d'interface** : ses compétences fictives ne constituent pas un système de classes jouable.

### Construction, agriculture et exploration

- Déplacement des constructions compatibles avec `H`, marteau en main, sans établi ; conservation de leur contenu.
- Plantation en grille issue de PlantEasily, intégrée au cultivateur. La récolte de zone et le ramassage en maintenant `E` utilisent les systèmes d'Overhaul.
- Sélection des destinations de portails avec XPortal intégré à la fenêtre Auga.
- Gardiens de donjons, salles de boss, récompenses et règles de réinitialisation configurables.
- Repères de véhicules, four à charbon acceptant plusieurs essences de bois avec priorité au bois normal, ajustements de recettes et de récolte.

## Installation

Le dépôt contient les sources ; il ne constitue pas un package prêt à installer.

1. Installer BepInEx 5 et Jotunn compatibles avec la version de Valheim utilisée.
2. Copier le package compilé `Overhaul` dans `BepInEx/plugins/Overhaul`.
3. Utiliser la même version complète d'Overhaul sur le serveur et les clients : la révision fait partie du contrôle de compatibilité.
4. Retirer les plugins autonomes Auga, EquipmentAndQuickSlots, PlantEasily et XPortal, dont les fonctionnalités sont intégrées ici.

Les mods tiers qui référencent directement les anciennes DLL intégrées peuvent nécessiter une adaptation.

## Configuration

Les configurations distribuées couvrent les statistiques d'objets (`AlterItemStat.cfg`), la progression, les passifs, les armes, les monstres, l'interface et les donjons. Les valeurs de départ sont dans `Overhaul/Distribution` et `Overhaul/AlterItemStat.cfg`.

Les fichiers BepInEx sont créés sous `BepInEx/config`, dont `plopyy.valheim.Overhaul.cfg` et les configurations des modules intégrés. Les traductions sont dans `Localisation/translationsFR.json` et `translationsEN.json`. Certaines règles sont contrôlées et synchronisées par le serveur ; les préférences d'affichage et de touches restent locales.

## Sources et compilation

| Dossier | Contenu |
|---|---|
| `Overhaul` | Plugin principal, configurations, localisation et version |
| `AugaIntegration` | Interface intégrée et bibliothèque de composants Unity |
| `EquipmentIntegration` | Équipement et raccourcis |
| `PlantEasilyIntegration` | Plantation en grille |
| `XPortalIntegration` | Gestion des portails |
| `Authoring` | Scripts de préparation des assets Unity |
| `Validation` | Contrôles exécutés dans l'éditeur Unity |

### Environnement utilisé

- Windows, PowerShell et SDK .NET **10.0.302** dans l'environnement de compilation actuel.
- Cible du plugin : **.NET Framework 4.7.2**, langage C# **12**.
- Éditeur d'assets et de validation : **Unity 6000.0.75f1**.
- Les références sont résolues depuis un dossier `Libs` placé à côté du dépôt. Elles doivent provenir de l'environnement de jeu et des dépendances compatibles ; elles ne sont pas distribuées ici.

Les versions ci-dessous sont celles des fichiers utilisés pour Overhaul 2.2.0.0. Pour toutes les lignes indiquant **Client Valheim correspondant**, prendre les DLL du **client de la version de Valheim ciblée**, en conservant un ensemble cohérent provenant de cette même version. Cela concerne aussi les modules Unity et les bibliothèques .NET fournis avec le jeu. Une version d’assembly `0.0.0.0` ne signifie pas que le fichier est interchangeable entre versions de Valheim.

| Bibliothèque | Version assembly | Version fichier | Provenance / préparation |
|---|---|---|---|
| `0Harmony.dll` | 2.9.0.0 | 2.9.0.0 | Harmony fourni avec BepInEx |
| `APIManager.dll` | 1.0.0.0 | 1.0.0.0 | APIManager |
| `assembly_guiutils_publicized.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant, puis publicisation |
| `assembly_postprocessing_publicized.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant, puis publicisation |
| `assembly_steamworks_publicized.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant, puis publicisation |
| `assembly_sunshafts_publicized.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant, puis publicisation |
| `assembly_utils_publicized.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant, puis publicisation |
| `assembly_valheim_publicized.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant, puis publicisation |
| `BepInEx.dll` | 5.4.23.5 | 5.4.23.5 | BepInEx 5 |
| `gui_framework.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `Jotunn.dll` | 2.24.3.0 | 2.24.3.0 | Jotunn |
| `Microsoft.CSharp.dll` | 4.0.0.0 | 4.6.57.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `mscorlib.dll` | 4.0.0.0 | 4.6.57.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `netstandard.dll` | 2.1.0.0 | 2.1.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `Newtonsoft.Json.dll` | 13.0.0.0 | 13.0.2 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `SoftReferenceableAssets.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `Splatform.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `System.Core.dll` | 4.0.0.0 | 4.6.57.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `System.Data.DataSetExtensions.dll` | 4.0.0.0 | 4.6.57.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `System.Data.dll` | 4.0.0.0 | 4.6.57.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `System.dll` | 4.0.0.0 | 4.6.57.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `System.Net.Http.dll` | 4.0.0.0 | 4.6.57.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `System.Xml.dll` | 4.0.0.0 | 4.6.57.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `System.Xml.Linq.dll` | 4.0.0.0 | 4.6.57.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `ui_lib.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `Unity.InputSystem.dll` | 1.19.0.0 | 1.19.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `Unity.TextMeshPro.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.AIModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.AnimationModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.AssetBundleModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.AudioModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.CoreModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.ImageConversionModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.IMGUIModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.InputLegacyModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.ParticleSystemModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.PhysicsModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.TerrainPhysicsModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.TextRenderingModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.UI.dll` | 1.0.0.0 | 1.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.UIModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `fastJSON.dll` | 2.4.0.0 | 2.4.0.2 | fastJSON |

Les fichiers suffixés `_publicized` sont les versions publicisées des assemblies du client : leurs membres internes ont été rendus accessibles pour compiler le mod. Ils doivent être préparés à partir des assemblies de la même version du jeu. Placer toutes les références dans `../Libs`.

Le fichier [build-inputs-2.2.0.0.json](build-inputs-2.2.0.0.json) recense ces 43 références locales directes et de framework avec leurs empreintes SHA-256. Il identifie les fichiers utilisés pour la version 2.2.0.0 ; ce n’est pas un résolveur de dépendances transitives.

### Récupérer et compiler le projet

Installer Git LFS, puis récupérer le dépôt :

```powershell
git lfs install
git clone https://github.com/plopyy/Overhaul.git
cd Overhaul
git lfs pull
```

Pour un clone existant : `git pull`, puis `git lfs pull`.

Organisation de l'environnement de développement :

```text
workspace/
  Overhaul/                    # Ce dépôt
  Libs/                        # Références de compilation
  Packages/Overhaul/           # Résultat de compilation
  Tools/Auga-main/AugaUnity/   # Projet Unity éditable, conservé séparément
```

Depuis la racine du dépôt :

```powershell
dotnet msbuild .\Overhaul\Overhaul.csproj /p:Configuration=Release /v:minimal /nologo
```

La compilation copie le package dans `../Packages/Overhaul`. Le quatrième nombre de version est incrémenté automatiquement à chaque compilation et remis à zéro lors d'un changement de série majeure ou mineure.

Les scripts d'authoring et de validation utilisent l'arborescence ci-dessus et le projet Unity séparé. Celui-ci reste nécessaire pour éditer les prefabs et reconstruire les bundles ; il n'est pas inclus dans ce dépôt.

## Licence et crédits

Overhaul est publié sous **GNU GPL v3**, voir [LICENSE](LICENSE). Les mentions et conditions propres aux composants tiers sont conservées dans leurs notices.

- **Auga** : RandyKnapp, n4 et Vapok — [sources originales](https://github.com/RandyKnapp/Auga).
- **EquipmentAndQuickSlots** : RandyKnapp, réécriture 3.x par MidnightsFX — sources 3.1.3, commit `db206923c6fd45e62f086c32e4106203cc6cf898` de [ValheimMods](https://github.com/RandyKnapp/ValheimMods).
- **PlantEasily** : Advize, version 2.2.2 — [sources originales](https://github.com/AdvizeGH/Advize_ValheimMods/tree/main/Advize_PlantEasily), GPL v3. Intégration adaptée au cycle de vie et à la récolte d'Overhaul.
- **XPortal** : SpikeHimself, version 1.2.25 — [sources originales](https://github.com/SpikeHimself/XPortal), GPL v3. Intégration adaptée à Overhaul et à son interface Auga.
- **Noto** : notice de police conservée dans `AugaIntegration/Assets/Noto-OFL.txt`.

Les licences et notices de PlantEasily et XPortal sont également distribuées avec le package dans `Licenses`. Les fichiers propriétaires de Valheim ne sont pas inclus dans ce dépôt.
