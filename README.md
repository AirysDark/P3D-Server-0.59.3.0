# 2D-3D Style Engine Server

Reusable server backend derived from the P3D server codebase and being refactored into a project-neutral foundation for **2D-3D-style.engine** and future compatible projects.

The repository remains on the `P3D-Server-0.59.3.0` codebase for legacy protocol compatibility. Existing wire-level protocol fields are intentionally preserved unless explicitly migrated, so compatible clients are not broken by cosmetic or architectural renaming.

## Current direction

- Central server identity in `server.identity.json`
- Configurable server and product names
- Configurable protocol profile
- Configurable `ServerLogin` display/service name
- Optional legacy GameJolt-compatible authentication API
- Optional updater
- Configurable external swear-filter source settings
- Legacy multiplayer and RCON services retained
- Future separation between engine core, optional services, and game/protocol modules

## Configuration

Edit `server.identity.json`:

```json
{
  "ServerName": "2D-3D Style Server",
  "ProductName": "2D-3D-style.engine Server",
  "ProtocolProfile": "legacy-p3d",
  "GameJoltCompatibilityEnabled": true,
  "UpdaterEnabled": true,
  "UpdateManifestSource": "",
  "LoginServiceName": "ServerLogin",
  "LoginServicePort": 8080,
  "SwearFilterExternalSourceEnabled": false,
  "SwearFilterSource": ""
}
```

`GameJoltCompatibilityEnabled` controls the legacy-compatible authentication service. The internal implementation may remain GameJolt-compatible while the user-facing service identity is configured through `LoginServiceName`.

## Compatibility policy

Fields such as `PokemonVisible`, `PokemonPosition`, `PokemonSkin`, `BattlePokemonData`, and `PvP_Pokemon` may be part of the existing client/server wire protocol. They are not renamed blindly. Protocol changes should be isolated and versioned before legacy identifiers are removed.

## Build status

The identity and optional-service refactor is present on `master`. The broader physical project/solution rename from `Pokemon.3D.Server.*` to `Engine2D3D.Server.*` is a separate migration step and must be build-verified before it is considered complete.

## Scope

This repository is the only target for these server changes. The `2D-3D-style.engine` repository is not modified by this work.

## License and upstream attribution

This repository remains subject to the licensing and upstream obligations of the original codebase and its dependencies. Project-neutral refactoring does not remove those obligations.