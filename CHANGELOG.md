# Changelog

## 2.2.2 (in progress)

### Added
- **Rarity system**, driven by RaritySystem.cfg (the server’s file is sent to every player): rarities with their colour, aura, base stat bonus (a % of the damage, armour and block, upgrades included), empty enchantment lines and catalyst slots; catalyst items always carry their rarity; creatures listed in the file drop pieces of their gear with a random rarity. Rarity shown in item slots (multicolour rarities as a 45° gradient, the first colour in the middle), tooltips (item name, dividers and enchantment lines in the rarity colour, with its enchantment lines, empty ones as "Empty bonus +0", and catalyst slots) and as a ground aura. Admin test command o_rarity <item> [rarity]. (2.2.2.21)

### Fixed
- The empty inventory windows no longer flash on screen for a moment before the inventory slides in, the first time it is opened. (2.2.2.22)
- No more burst of 32 "Destroying components immediately" errors in the log when the inventory is first opened (the leveling stat cards removed the tooltips of their - and + buttons at the wrong moment). (2.2.2.16)

### Changed
- Dvergr circlets: they no longer thin fog, smoke and dust (the Dvergr circlet is back to its light only). The spirit circlet now only thins the Mistlands mist around you (10 % opacity up to twice a wisp’s clearing radius, back to full at three times); the rest of the world keeps its fog and smoke. (2.2.2.2)

## 2.2.1 (released)

### Added
- **Epic Loot compatibility.** Epic Loot's own item display, which broke the Auga interface, is replaced by Overhaul's: Auga tooltips show the rarity, the magic effects, shard slots and legendary/mythic set bonuses like the other stats, with shorter French effect names ("Vitesse d'attaque +10%" instead of "Augmente la vitesse d'attaque de 10%") taken from Overhaul's translation file. Item tooltips are reorganised to make room (wider, more compact, damage shown as the actual range with the elemental stat included, staff shield instead of blocking stats, remaining stats such as the chance to apply an effect shown with their value on the right) and scaled down if still taller than the screen. Status effect names and descriptions in tooltips are translated again. (2.2.1.59)
- Server option `[Items] StackSize100`, on by default: turn it off to keep the vanilla stack sizes instead of stacks of 100. It applies right away, even when changed during a game. (2.2.1.18)
  With the option off, stacks bigger than the vanilla limit in your inventory, or taken from a chest, are split into normal stacks (100 wood becomes 2 × 50); what does not fit is dropped at your feet. (2.2.1.19)
- Settings > Mods: **Dash (replaces sprint)** option, on by default. When turned off, the Run key sprints like in vanilla Valheim and drains stamina; sprint speed is scaled to Overhaul's faster jog, so sprinting stays faster than jogging. Per-player setting. (2.2.1.1)
- Settings > Mods: the **Take all** key can be changed (printed R key by default). Shift + this key still renames a chest and swaps gear with an armor stand, and on-screen hints show the chosen key. (2.2.1.3)

### Changed
- New item slot design (Compatible with Epic Loot rarities). (2.2.1.102)
- Trinkets give their bonus permanently while worn, like any other equipment, instead of only for a few seconds once the adrenaline bar is full. Adrenaline is disabled for now: no more adrenaline bar (kept enabled when Epic Loot is installed, for its adrenaline enchantments). (2.2.1.25)
- Station upgrades (workbench, forge…) can be placed or moved right next to each other: the "too close to another upgrade" limit is removed. They must still be within reach of their station. (2.2.1.17)
- Oven: each of the 4 slots takes a stack of one raw dish. The first one cooks in its place in the oven, is ejected in front of it once cooked (like other production machines, never despawning), then the next one starts. Cooked dishes left in the oven by earlier versions are ejected too. (2.2.1.7)
- Items dropped from the inventory fly towards the crosshair instead of the character's facing, keeping the vanilla arc and speed. (2.2.1.4)

### Fixed
- Since Valheim 1.0.17, the end-game credits could stay open invisibly in the background and grey out other windows (like portal window). They now stay closed unless the end credits are actually playing. (2.2.1.6)
- Dedicated server: a dungeon whose reset was refused (for example because it touches another tracked location) was retried every 5 minutes, regenerating a whole dungeon and its terrain each time. This could freeze the server for several minutes, disconnect players and block shutdown. A refused reset now waits 6 hours before the next attempt, and the log names the object that blocks it. (2.2.1.10)
- Dungeon and camp resets are no longer refused because of what is on the surface around them: objects of a neighbouring location are left in place, and a camp whose old layout differs from the reference is still reset. Only a player inside a dungeon can postpone a reset. (2.2.1.11)
- Surface locations (camps, ruins, Mistlands entrances…) no longer wait for nearby players before resetting: players standing in the reset area are moved just outside it, then the reset goes ahead. (2.2.1.13)
- Dungeon interiors can no longer overlap: every generated dungeon interior (Hildir's crypt and cave, Mörkhalla, the Ashlands tunnels…), not only those with an Overhaul boss room, moves at its next reset to its own free height lane high above the world. The entrance and exit doors, the interior lighting and weather follow it. A dungeon whose rooms do not fit in a lane keeps its original height. (2.2.1.16)

## 2.2.0 (released)
First public release.
