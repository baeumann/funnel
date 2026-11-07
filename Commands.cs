using System.Globalization;
using funnel_.protocol;

namespace funnel_.core;

/// <summary>The interactive console of the proxy. Every command acts on the most recently connected client.</summary>
public sealed class Commands
{
    private const string HelpText = @"
commands:
  status                      connection state, protocol version, tracked game state
  chat                        last chat / system messages seen
  say <text>                  send a chat message as the player (1.19.3+, unsigned)
  cmd <command>               run a command as the player, e.g. cmd gamemode creative
  echo <text>                 show a message in the player's own chat window only
  respawn                     press the respawn button
  swing                       swing the main hand
  pos <x> <y> <z>             report a position to the server
  raw <c|s> <id> [hex]        inject any packet; c = to client, s = to server, id in hex
                              e.g. raw s 12 0000000000000001
  log on|off                  print every packet in both directions
  log filter <text>|off       only log packets whose name or id contains <text>
  chatlog on|off              print chat/system messages seen in the game (default on)
  help                        this text
  quit                        stop the proxy
";

    private readonly Proxy _proxy;

    public Commands(Proxy proxy)
    {
        _proxy = proxy;
    }

    /// <summary>Runs one command line. Returns false when the proxy should exit.</summary>
    public async Task<bool> RunAsync(string line)
    {
        var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return true;
        }

        string command = parts[0].ToLowerInvariant();
        string argument = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (command)
        {
            case "quit":
            case "exit":
                return false;

            case "help":
            case "?":
                Console.WriteLine(HelpText);
                return true;

            case "log":
                HandleLog(argument);
                return true;

            case "chatlog":
                _proxy.Options.ShowChat = !argument.Equals("off", StringComparison.OrdinalIgnoreCase);
                Console.WriteLine("chat messages will " + (_proxy.Options.ShowChat ? "" : "not ") + "be printed");
                return true;
        }

        var session = _proxy.Current;
        if (session == null)
        {
            Console.WriteLine("no minecraft client is connected");
            return true;
        }

        switch (command)
        {
            case "status":
                Console.WriteLine(DescribeSession(session));
                break;

            case "chat":
                var history = session.Game.RecentChat();
                if (history.Count == 0)
                {
                    Console.WriteLine("(nothing yet)");
                }
                foreach (var message in history)
                {
                    Console.WriteLine(message);
                }
                break;

            case "say":
                Require(argument, "say <text>");
                await session.Bot.SayAsync(argument);
                break;

            case "cmd":
                Require(argument, "cmd <command>");
                await session.Bot.CommandAsync(argument);
                break;

            case "echo":
                Require(argument, "echo <text>");
                await session.Bot.EchoAsync(argument);
                break;

            case "respawn":
                await session.Bot.RespawnAsync();
                break;

            case "swing":
                await session.Bot.SwingAsync();
                break;

            case "pos":
                var coordinates = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (coordinates.Length != 3
                    || !double.TryParse(coordinates[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                    || !double.TryParse(coordinates[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y)
                    || !double.TryParse(coordinates[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                {
                    Console.WriteLine("usage: pos <x> <y> <z>");
                    break;
                }
                await session.Bot.MoveToAsync(x, y, z);
                break;

            case "raw":
                await HandleRawAsync(session, argument);
                break;

            default:
                Console.WriteLine("unknown command '" + command + "', try 'help'");
                break;
        }

        return true;
    }

    private void HandleLog(string argument)
    {
        var options = _proxy.Options;
        var words = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

        if (words.Length == 0 || words[0].Equals("on", StringComparison.OrdinalIgnoreCase))
        {
            options.LogPackets = true;
            Console.WriteLine("packet logging on");
        }
        else if (words[0].Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            options.LogPackets = false;
            Console.WriteLine("packet logging off");
        }
        else if (words[0].Equals("filter", StringComparison.OrdinalIgnoreCase))
        {
            string? filter = words.Length > 1 ? words[1].Trim() : null;
            if (filter == null || filter.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                options.LogFilter = null;
                Console.WriteLine("log filter cleared");
            }
            else
            {
                options.LogFilter = filter;
                options.LogPackets = true;
                Console.WriteLine("logging packets matching '" + filter + "'");
            }
        }
        else
        {
            Console.WriteLine("usage: log on|off|filter <text>");
        }
    }

    private static async Task HandleRawAsync(Session session, string argument)
    {
        var words = argument.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2 || (words[0] != "c" && words[0] != "s"))
        {
            Console.WriteLine("usage: raw <c|s> <id hex> [payload hex]");
            return;
        }

        int id = Convert.ToInt32(words[1].Replace("0x", string.Empty), 16);
        byte[] payload = words.Length > 2
            ? Convert.FromHexString(words[2].Replace(" ", string.Empty).Replace("0x", string.Empty))
            : Array.Empty<byte>();

        await session.SendRawAsync(words[0] == "c" ? Direction.Clientbound : Direction.Serverbound, id, payload);
        Console.WriteLine("sent " + payload.Length + " byte packet 0x" + id.ToString("X2") + " " + (words[0] == "c" ? "to the client" : "to the server"));
    }

    private static string DescribeSession(Session session)
    {
        return $"session {session.Number}: state {session.State}, protocol {session.ProtocolVersion}, "
            + $"player {session.Username ?? "?"}, compression {(session.CompressionThreshold >= 0 ? session.CompressionThreshold.ToString() : "off")}, "
            + $"{(session.Encrypted ? "ENCRYPTED (pass-through only)" : "readable")}, {session.Ids.Count} packet ids loaded\n"
            + session.Game.Describe();
    }

    private static void Require(string argument, string usage)
    {
        if (argument.Length == 0)
        {
            throw new ArgumentException("usage: " + usage);
        }
    }
}
