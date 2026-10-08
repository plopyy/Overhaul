# Changelog (rework)

## 2.3.0 (in progress)

### Added
- Multiplayer rework groundwork: the character's skills are copied into its network object, so the dedicated server keeps them in memory while the player is connected. The game still runs and saves exactly as before; the server only reads this copy, and logs it when a player joins. Can be turned off with `[Rework] PlayerReplication`. (2.3.0.1)
