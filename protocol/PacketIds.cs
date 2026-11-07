using System.Text.Json;

namespace funnel_.protocol;

/// <summary>
/// Maps packet names to numeric IDs (and back) for one Minecraft version.
///
/// Packet IDs change with almost every release, so they are loaded from a file instead of being hard coded.
/// The file is the vanilla data-generator report (reports/packets.json), see README.md. A simpler hand-written
/// form is accepted as well:
///   { "play": { "clientbound": { "set_health": 87 }, "serverbound": { "chat": 5 } } }
/// Names are matched without the "minecraft:" prefix.
/// </summary>
public sealed class PacketIds
{
    private readonly Dictionary<(ConnectionState, Direction, string), int> _ids = new();
    private readonly Dictionary<(ConnectionState, Direction, int), string> _names = new();

    public int Count => _ids.Count;
    public bool IsEmpty => _ids.Count == 0;

    public static PacketIds Empty() => new PacketIds();

    public static PacketIds LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Load(stream);
    }

    public static PacketIds Load(Stream stream)
    {
        var result = new PacketIds();
        using var doc = JsonDocument.Parse(stream);

        foreach (var stateProp in doc.RootElement.EnumerateObject())
        {
            if (!TryParseState(stateProp.Name, out var state) || stateProp.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var dirProp in stateProp.Value.EnumerateObject())
            {
                if (!TryParseDirection(dirProp.Name, out var direction) || dirProp.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var packet in dirProp.Value.EnumerateObject())
                {
                    int id;
                    if (packet.Value.ValueKind == JsonValueKind.Number)
                    {
                        id = packet.Value.GetInt32();
                    }
                    else if (packet.Value.ValueKind == JsonValueKind.Object && packet.Value.TryGetProperty("protocol_id", out var protocolId))
                    {
                        id = protocolId.GetInt32();
                    }
                    else
                    {
                        continue;
                    }

                    result.Add(state, direction, Normalize(packet.Name), id);
                }
            }
        }

        return result;
    }

    public void Add(ConnectionState state, Direction direction, string name, int id)
    {
        _ids[(state, direction, name)] = id;
        _names[(state, direction, id)] = name;
    }

    /// <summary>Returns the ID of the first name that is known, or null.</summary>
    public int? Find(ConnectionState state, Direction direction, params string[] names)
    {
        foreach (var name in names)
        {
            if (_ids.TryGetValue((state, direction, name), out var id))
            {
                return id;
            }
        }
        return null;
    }

    public string? NameOf(ConnectionState state, Direction direction, int id)
    {
        return _names.TryGetValue((state, direction, id), out var name) ? name : null;
    }

    private static string Normalize(string name)
    {
        const string prefix = "minecraft:";
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name.Substring(prefix.Length) : name;
    }

    private static bool TryParseState(string text, out ConnectionState state)
    {
        switch (text.ToLowerInvariant())
        {
            case "handshake": case "handshaking": state = ConnectionState.Handshake; return true;
            case "status": state = ConnectionState.Status; return true;
            case "login": state = ConnectionState.Login; return true;
            case "configuration": case "config": state = ConnectionState.Configuration; return true;
            case "play": state = ConnectionState.Play; return true;
            default: state = default; return false;
        }
    }

    private static bool TryParseDirection(string text, out Direction direction)
    {
        switch (text.ToLowerInvariant())
        {
            case "clientbound": case "toclient": direction = Direction.Clientbound; return true;
            case "serverbound": case "toserver": direction = Direction.Serverbound; return true;
            default: direction = default; return false;
        }
    }
}
