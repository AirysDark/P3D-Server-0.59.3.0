# Pokémon 3D – GameJolt Edition (Server)

Standalone **Pokémon 3D server backend** compatible with the updated P3D-Client.  
Provides authentication, save synchronization, and multiplayer session handling through the new GameJolt-linked login system.

---

## ⚡ Features

- 🧩 **Account Registration**
  - Automatically registers users from the GameJolt login flow.
  - Generates a default save on first join.

- 💾 **Online Save Management**
  - Saves stored under each user’s GameJolt ID.
  - Automatically loads the correct save when the user rejoins.

- 🔐 **Secure Auth**
  - Validates credentials using GameJolt API keypair.
  - Rejects offline saves for verified servers.

- ⚙️ **Server Configuration**
  - Supports JSON-based config for port, host, and authentication key.

- 📡 **Protocol Matching**
  - Enforces matching protocol version with clients.
  - Prevents desyncs and incompatible builds.

- 🧱 **Lightweight Architecture**
  - Runs via .NET or Mono — no external database required.

---

## 🛠️ Installation

### Prerequisites
- Windows or Linux (Mono)
- .NET Framework 4.8+ or .NET 6 Runtime

### Steps
1. Clone or download:
   ```bash
   git clone https://github.com/<yourusername>/P3D-Server.git
   cd P3D-Server
   ```
2. Build using Visual Studio or `dotnet build`.
3. Run:
   ```bash
   bin/Release/P3D-Server.exe
   ```

The server will start on the default port `15124` and listen for connections.

---

## ⚙️ Configuration

| File | Description |
|------|--------------|
| `config/server.json` | Port, MOTD, GameJolt API settings |
| `saves/` | Player save files (by GameJolt ID) |
| `logs/` | Runtime logs and join info |

Example `server.json`:
```json
{
  "server_name": "P3D Local Test",
  "port": 15124,
  "require_online_saves": true,
  "gamejolt_verify": true,
  "max_players": 16
}
```

---

## 🔗 Integration Flow

1. Player logs in from **P3D-Client** with their GameJolt account.
2. The client passes the verified GameJolt ID to the server on join.
3. The server:
   - Checks if the user has a save under `/saves/{user_id}/`.
   - If missing → creates a default save.
   - Loads the save and admits the player to the session.

Server log example:
```
[INFO] New connection: AirysDark (uid=6bd6e7e51ad144bca0c9c5db9788e1e9)
[INFO] Default save created for AirysDark
[INFO] Player joined successfully using verified GameJolt credentials.
```

---

## 🧩 Developer Info

- Protocol versioning handled by `ServersManager.PROTOCOLVERSION`
- Default MOTD and capacity set in `server.json`
- Online/offline validation inside `PlayerJoinHandler.vb`
- Compatible with `P3D-Client` builds after v0.59.3-online

---

## 🧑‍💻 Authors

- **AirysDark** – Server developer / protocol maintainer  
- Original engine: **Kolben Games**

---

## ⚖️ License

Non-commercial educational use only.  
All Pokémon assets are © Nintendo / Game Freak.  
This codebase is a derivative of the original Pokémon 3D project by Kolben Games.
