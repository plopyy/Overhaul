# Overhaul Animation Preview

Outil de développement : joue en jeu, sur son propre personnage, une animation `.fbx` faite sur le squelette du joueur de Valheim. Ce n'est pas un mod pour les joueurs ; il n'est pas inclus dans `Overhaul.dll`.

## Installation

1. Copier le contenu du package `OverhaulAnimationPreview` dans `BepInEx/plugins/OverhaulAnimationPreview` (la DLL, `AssimpNet.dll`, `assimp.dll`, le dossier `Localisation` et la licence).
2. Lancer le jeu une fois : le dossier `BepInEx/AnimationPreview` est créé.

Windows 64 bits uniquement (`assimp.dll`). Rien n'est envoyé aux autres joueurs : chacun ne voit que son propre aperçu.

## Utilisation

1. Exporter l'animation en `.fbx` sur le squelette du joueur (mêmes noms d'os : `Hips`, `Spine`, `LeftArm`…) et la déposer dans `BepInEx/AnimationPreview` (sous-dossiers acceptés).
2. En jeu, **F7** affiche ou masque la fenêtre, à gauche de l'écran. Le curseur est libre tant qu'elle est affichée.
3. Choisir l'animation dans la liste, puis **Lire**. Masquer la fenêtre avec F7 pour tourner la caméra autour du personnage : l'animation continue.

Un fichier réexporté est rechargé automatiquement, sans relancer le jeu.

## Réglages de la fenêtre

- **Barre de lecture** : avancer ou reculer dans l'animation, même en pause.
- **Boucle** : rejouer en continu.
- **Mode** :
  - l'animation contient un déplacement (root motion) : elle démarre en *Root motion*, le personnage avance comme dans l'animation ; *Sur place* le garde immobile ;
  - l'animation est sur place : *Root motion* fait avancer le personnage tout droit.
- **Vitesse de l'animation** : ×0,1 à ×2.
- **Vitesse de déplacement** (en root motion) : multiplicateur du déplacement de l'animation (×0 à ×3), ou vitesse d'avancée en m/s (0 à 10) pour une animation sur place.

Le déplacement de l'animation est mesuré sur la racine du modèle (l'objet au-dessus de l'`Armature`).

## Configuration

`BepInEx/config/plopyy.valheim.Overhaul.AnimationPreview.cfg` : touche de la fenêtre et dossier des animations.

## Compilation

`dotnet build -c Release` dans ce dossier ; le package est copié dans `Packages/OverhaulAnimationPreview`. Les références viennent du dossier `Libs` de ValheimModdings. Assimp : `lib/`, licence `lib/AssimpNet-License.txt` (AssimpNet MIT, Assimp BSD).
