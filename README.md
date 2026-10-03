# Overhaul

Overhaul est un mod pour **Valheim** qui réunit progression RPG, interface remaniée, gestion de l'équipement, améliorations du combat et confort de jeu dans un seul plugin : `Overhaul.dll`.

Le projet est en développement, actuellement en série **2.2**. Les fonctionnalités et leur équilibrage sont configurables. Les validations automatisées ne remplacent pas les essais en partie, notamment en multijoueur.

## Fonctionnalités principales

### Progression et combat

- Expérience, niveaux, points de statistiques et bonus passifs, avec progression persistante du personnage.
- Réglages d'expérience selon les biomes, catégories de monstres, étoiles et participation aux combats.
- Statistiques d'objets, vitesses d'attaque et effets des familles d'armes configurables.
- Durabilité maximale des armes, outils et armures doublée, y compris les gains par amélioration. Les objets existants conservent leurs points restants et peuvent être réparés jusqu'au nouveau maximum.
- Dégâts de coupe des haches doublés sur les arbres. Bonus de minage ×2 sur les roches, minerais et tas de boue, ainsi que sur les golems et les créatures vulnérables au minage ; dégâts inchangés contre les autres créatures.
- Les attaques usent les armes et outils lorsqu'elles touchent une créature. La coupe du bois à la hache et le minage à la pioche consomment 50 % de l'usure habituelle ; les coups dans le vide restent sans usure. Les tirs sont comptés à l'impact, une seule fois par attaque, avec les bonus de progression habituels.
- Comportements de créatures enrichis : agressivité, défense, réactions de groupe et fuite.
- Visée à l'arc accroupi, caméra décalée à l'épaule et impulsion de saut indépendante de la pente.

### Interface et inventaire

- Poids de tous les objets réduit de moitié, capacité de base de 600 et bonus du Megingjord de 400. Les réglages personnalisés de capacité restent conservés.
- Interface Auga intégrée et adaptée aux systèmes d'Overhaul, avec traductions françaises et anglaises.
- Emplacements d'équipement, apparences cosmétiques séparées des statistiques, raccourcis de nourriture et de munitions.
- Coffres renommables : nom enregistré, visible dans la fenêtre et en visant le coffre. `R` prend tout le contenu disponible et `Maj + R` permet de renommer.
- Accès public ou privé aux coffres et utilisation des ressources des coffres autorisés à proximité pour fabriquer et construire.
- La fenêtre de classes et de compétences est encore un **prototype d'interface** : ses compétences fictives ne constituent pas un système de classes jouable.

### Construction, agriculture et exploration

- Stations de fabrication et de réparation utilisables sans toit ni abri.

- Mangeoire de quatre emplacements réservés aux aliments des animaux, détectée à 20 m, avec alimentation à 4 m maximum même pour les animaux immobilisés. Son aspect passe de vide à rempli selon son contenu ; le cercle de portée apparaît pendant le placement. Icône pleine, filtres Divers, Meuble et Stockage, sans renommage.

- Réparation de tout objet réparable sur n'importe quelle station de réparation, sans exigence de type ni de niveau.
- Déplacement des constructions compatibles avec `H`, marteau en main, sans établi ; conservation de leur contenu.
- Plantation en grille issue de PlantEasily, intégrée au cultivateur. La récolte de zone et le ramassage en maintenant `E` utilisent les systèmes d'Overhaul.
- Sélection des destinations de portails avec XPortal intégré à la fenêtre Auga.
- Gardiens de donjons, salles de boss, récompenses et règles de réinitialisation configurables.
- Repères de véhicules, four à charbon acceptant plusieurs essences de bois avec priorité au bois normal, ajustements de recettes et de récolte.

## Installation

### Sauvegarde progressive du monde

Les mondes locaux au format natif 41 sont migrés automatiquement vers `<dossier du monde>/<nom du monde>.db`. La console affiche la progression de 0 à 100 %. La table `migration_state` n'est créée avec son unique ligne `complete` qu'après vérification de la migration et validation de la transaction. Une migration interrompue est conservée à part puis recommencée depuis les fichiers natifs, qui restent intacts.

Après migration, SQLite sert au chargement et à la sauvegarde du monde. Les objets modifiés et les données globales sont enregistrés en arrière-plan, environ toutes les cinq secondes. Un arrêt normal vide les écritures en attente. Une interruption brutale peut perdre les modifications encore en attente. Le timer automatique du jeu ne déclenche plus de capture supplémentaire ni de copie complète de la base SQLite, même si son intervalle est réduit. Les autres demandes de sauvegarde du monde, notamment la commande de sauvegarde manuelle, créent des copies autonomes dans `sqlite-backups` (trois conservées).

Les profils des personnages conservent leur sauvegarde native côté client. Les inventaires des coffres et les autres inventaires du monde sont dans `inventory`, avec une ligne par emplacement occupé. Le nom personnalisé d'un coffre est dans `properties`, clé `Overhaul.ChestName`.

SQLite utilise temporairement des fichiers `.db-wal` et `.db-shm` pendant son fonctionnement. Pour copier un monde actif, demander une sauvegarde manuelle et utiliser la nouvelle copie une fois créée dans `sqlite-backups`. Pour copier son fichier `.db` directement, arrêter normalement le serveur au préalable. Le fichier `.owner` empêche deux instances Overhaul d'écrire simultanément dans le même monde.

Cette intégration cible les mondes locaux. Les mondes cloud gardent le fonctionnement natif. Les anciennes sauvegardes non découpées doivent d'abord être converties par la version compatible de Valheim. Windows x64 embarque SQLite 3.53.4 ; Linux nécessite `libsqlite3.so.0` version 3.52.0 ou supérieure et n'a pas été validé dans cet environnement.

Tests effectués sur une copie de monde : migration et reconstruction de 556 403 objets, reprise après interruption, écritures et suppressions, chargement dans les classes natives puis redémarrage. Les transferts personnage/coffre, les piles partielles et le refus quand l'inventaire est plein sont vérifiés après sauvegarde et rechargement natifs du personnage et du monde. Les compétences et données personnalisées sont conservées dans ces tests. Un essai multijoueur sur serveur réel reste nécessaire.

Le dépôt contient les sources ; il ne constitue pas un package prêt à installer.

1. Installer BepInEx 5 et Jotunn compatibles avec la version de Valheim utilisée.
2. Copier le package compilé `Overhaul` dans `BepInEx/plugins/Overhaul`.
3. Utiliser la même version complète d'Overhaul sur le serveur et les clients : la révision fait partie du contrôle de compatibilité.
4. Retirer les plugins autonomes Auga, EquipmentAndQuickSlots, PlantEasily et XPortal, dont les fonctionnalités sont intégrées ici.

Les mods tiers qui référencent directement les anciennes DLL intégrées peuvent nécessiter une adaptation.

## Configuration

Les multiplicateurs globaux de poids (×0,5) et de durabilité (×2) s'appliquent après les réglages des objets, y compris par qualité. L'ancienne capacité par défaut de 300 est migrée une seule fois vers 600 ; les capacités personnalisées sont conservées.

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
| `UnityEngine.JSONSerializeModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.ParticleSystemModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.PhysicsModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.TerrainPhysicsModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.TextRenderingModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.UI.dll` | 1.0.0.0 | 1.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `UnityEngine.UIModule.dll` | 0.0.0.0 | 0.0.0.0 | Client Valheim correspondant (`valheim_Data/Managed`) |
| `fastJSON.dll` | 2.4.0.0 | 2.4.0.2 | fastJSON |
| `overhaul_sqlite3.dll` | Native x64 | 3.53.4 | SQLite Windows x64, renommer `sqlite3.dll` depuis `sqlite-dll-win-x64-3530400.zip` ([SQLite](https://www.sqlite.org/download.html)) |

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
