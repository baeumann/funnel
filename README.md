# funnel – Minecraft proxy

A local Java-Edition proxy that sits between the game and a server, shows what the game is doing and can
send extra packets "as the player" (a bot).

Verwende Code mit Vorsicht.
Minecraft client ──► funnel (127.0.0.1:25565) ──► target server
│  reads every packet, tracks game state
└► console: say / cmd / respawn / raw ...

## Run

dotnet run -- [target[:port]] [debug] [--ids=packets.json]
dotnet run -- 192.168.1.20            # remote server on 25565
dotnet run -- localhost true          # debug: target port 5555

Point Minecraft at `localhost:25565`. Type `help` in the proxy console.

## Packet IDs (needed for names, decoding and most bot actions)

Packet IDs change with nearly every Minecraft release, so they are not hard coded. Generate the table for your
exact server version with the vanilla data generator:

java -DbundlerMainClass=net.minecraft.data.Main -jar server.jar --reports

Copy `generated/reports/packets.json` next to the program (or pass `--ids=`). If your version does not produce
that file, write a small one by hand – `log on` shows the numeric ID of every packet:

```json
{
  "play": {
    "clientbound": { "login": 43, "player_position": 64, "set_health": 87, "set_time": 94, "set_experience": 86, "system_chat": 100, "player_chat": 53 },
    "serverbound": { "chat": 5, "chat_command": 4, "client_command": 7, "swing": 47, "move_player_pos": 20, "move_player_pos_rot": 21, "move_player_rot": 22 }
  }
}
```

(the numbers above are placeholders, use your version's). For 1.20.2+ also add `configuration_acknowledged` to
`play`/`serverbound` and `finish_configuration` to `configuration`/`serverbound`, otherwise the proxy cannot follow
the switch between configuration and play. Without any table the connection is still forwarded correctly;
only naming, decoding and named injection are off. `raw` works without a table.

## Commands

| command | effect |
|---|---|
| `status` | state, protocol, compression, tracked health / position / time / xp |
| `chat` | last chat and system messages seen |
| `say <text>` / `cmd <command>` | chat or command as the player |
| `echo <text>` | text in the player's own chat window only |
| `respawn`, `swing`, `pos x y z` | basic actions |
| `raw <c\|s> <id hex> [payload hex]` | inject any packet (c = to client, s = to server) |
| `log on\|off`, `log filter <text>` | packet log with names and IDs |
| `chatlog on\|off` | print chat seen in the game |

From code: `proxy.Current.Bot`, `proxy.Current.Game` (state and `ChatReceived`), and the
`Session.PacketReceived` event for your own handlers. New decoders belong in `GameState.Apply`.

## How to Write a Plugin (.dll)

The proxy features a modular, runtime reflection-based plugin engine. You can compile your own custom logic (e.g., entity radars, block automations, loggers) into a separate `.dll` file, place it next to the program, and the core proxy will load it dynamically on startup without changing any core files.

### 1. Create a Plugin Project
Create a new C# Class Library (`.NET Standard 2.1` or `.NET 6.0+`) in your IDE and add a project reference to the `funnel.csproj` core assembly.

### 2. Implement the `IProxyPlugin` Interface
Create a class inside your project that extends `IProxyPlugin`. This interface gives you full access to new active connection slots and raw network streams:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using funnel_.core;
using funnel_.protocol;

namespace MyCustomModule;

public class SamplePlugin : IProxyPlugin
{
    public string Name => "TelemetryPlugin";
    public string Version => "1.0.0";

    // Called once globally when the proxy core scans and loads the .dll file
    public void OnLoad()
    {
        Console.WriteLine(\$"[Plugin-Load] {Name} v{Version} successfully mounted into RAM!");
    }

    // Called automatically every time a new Minecraft client links to a server
    public void OnSessionStarted(Session session)
    {
        Console.WriteLine(\$"[Plugin-Session] Hooking into live pipeline of Session #{session.Number}");

        // Subscribe to the unbent cleartext packet traffic stream inside the RAM
        session.OnPacketTraffic += (sender, args) =>
        {
            if (sender.State == ConnectionState.Play)
            {
                // Run your custom non-blocking asynchronous interaction logic here
                if (args.Direction == Direction.Clientbound && args.PacketId == 0x2D)
                {
                    Console.WriteLine(\$"[Plugin-Radar] Unsigned entity activity spotted -> ID: {args.Payload.FirstOrDefault()}");
                }
            }
        };
    }
}
```

### 3. Deploying your Module
1. Build/Compile your plugin project to generate the `MyCustomModule.dll` file.
2. Create a folder named `plugins` directly in the execution directory of funnel (next to your compiled `funnel.exe` or inside `bin/Debug/netX.X/`).
3. Drop your compiled `.dll` file inside that `/plugins` folder.
4. Launch the proxy using `dotnet run`. The loader console will report:  
   `[Loader] SUCCESS: Module 'TelemetryPlugin' (v1.0.0) loaded from file!`

## Limits you should know about

* **Online-mode servers:** after login the connection is AES encrypted with a key only the client and the
  server know. The proxy detects this, switches to a plain byte pipe and prints a notice; reading and injecting are
  impossible there. This works on offline-mode servers (local test servers, `online-mode=false`, LAN worlds
  in some setups).
* **Chat:** injected chat is unsigned. Servers that enforce secure profiles reject it; commands (`cmd`) are the more
  reliable route. If the real client has signed messages in flight, the server can kick with
  "chat validation failed".
* **Decoders per version:** player chat (1.19.3+), system chat (JSON up to 1.20.2, NBT from 1.20.3), position
  (changed in 1.21.2) are covered; anything newer that changed layout will simply not decode.
* **Position injection** (`pos`) is reported next to the real client's own movement, so the server sees both.
* Do not use bot packets on servers whose rules forbid automation or modified cli