using System.Text;
using funnel_.protocol;

namespace funnel_.core;

/// <summary>
/// What the proxy has learned about the running game by watching packets. Updated from the proxy threads,
/// so every access goes through a lock. Decoders are keyed by packet *name*, which means they only run
/// when a packet ID table is loaded; layouts that changed between versions are chosen by protocol version.
/// </summary>
public sealed class GameState
{
    private const int MaxChatHistory = 50;

    private readonly object _gate = new object();
    private readonly List<string> _chat = new List<string>();

    public int? EntityId { get; private set; }
    public Guid? Uuid { get; set; }

    public bool HasPosition { get; private set; }
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Z { get; private set; }
    public float Yaw { get; private set; }
    public float Pitch { get; private set; }

    public float Health { get; private set; } = -1;
    public int Food { get; private set; } = -1;
    public float Saturation { get; private set; }

    public long WorldAge { get; private set; }
    public long TimeOfDay { get; private set; }

    public float XpProgress { get; private set; }
    public int XpLevel { get; private set; }
    public int XpTotal { get; private set; }

    /// <summary>Raised (on a proxy thread) for every chat or system message that reaches the client.</summary>
    public event Action<string>? ChatReceived;

    public IReadOnlyList<string> RecentChat()
    {
        lock (_gate)
        {
            return _chat.ToArray();
        }
    }

    public string Describe()
    {
        lock (_gate)
        {
            var sb = new StringBuilder();
            sb.AppendLine("  entity id : " + (EntityId?.ToString() ?? "?"));
            sb.AppendLine(HasPosition
                ? $"  position  : {X:F2} {Y:F2} {Z:F2}  yaw {Yaw:F1} pitch {Pitch:F1}"
                : "  position  : ?");
            sb.AppendLine(Health >= 0
                ? $"  health    : {Health:F1}  food {Food}  saturation {Saturation:F1}"
                : "  health    : ?");
            sb.AppendLine($"  time      : day time {TimeOfDay}  world age {WorldAge}");
            sb.Append($"  xp        : level {XpLevel}  ({XpTotal} total, {XpProgress:P0} to next)");
            return sb.ToString();
        }
    }

    /// <summary>Decodes one play packet. Malformed or unexpected payloads are ignored.</summary>
    public void Apply(Direction direction, string name, byte[] payload, int protocol)
    {
        try
        {
            var r = new PacketReader(payload);
            if (direction == Direction.Clientbound)
            {
                ApplyClientbound(name, r, protocol);
            }
            else
            {
                ApplyServerbound(name, r);
            }
        }
        catch (Exception ex) when (ex is EndOfStreamException || ex is InvalidDataException || ex is System.Text.Json.JsonException)
        {
            // Layout differs from what we expect for this version: skip it rather than disturbing the connection.
        }
    }

    private void ApplyClientbound(string name, PacketReader r, int protocol)
    {
        switch (name)
        {
            case "login":
                {
                    int entityId = r.ReadInt();
                    lock (_gate) { EntityId = entityId; }
                    break;
                }

            case "player_position":
                {
                    double x, y, z;
                    float yaw, pitch;
                    int flags;
                    if (protocol >= 768)
                    {
                        // 1.21.2+: teleport id, position, delta movement, rotation, int flags
                        r.ReadVarInt();
                        x = r.ReadDouble(); y = r.ReadDouble(); z = r.ReadDouble();
                        r.ReadDouble(); r.ReadDouble(); r.ReadDouble();
                        yaw = r.ReadFloat(); pitch = r.ReadFloat();
                        flags = r.ReadInt();
                    }
                    else
                    {
                        // up to 1.21.1: position, rotation, byte flags, teleport id
                        x = r.ReadDouble(); y = r.ReadDouble(); z = r.ReadDouble();
                        yaw = r.ReadFloat(); pitch = r.ReadFloat();
                        flags = r.ReadByte();
                    }
                    lock (_gate)
                    {
                        X = (flags & 0x01) != 0 ? X + x : x;
                        Y = (flags & 0x02) != 0 ? Y + y : y;
                        Z = (flags & 0x04) != 0 ? Z + z : z;
                        Yaw = (flags & 0x08) != 0 ? Yaw + yaw : yaw;
                        Pitch = (flags & 0x10) != 0 ? Pitch + pitch : pitch;
                        HasPosition = true;
                    }
                    break;
                }

            case "set_health":
                {
                    float health = r.ReadFloat();
                    int food = r.ReadVarInt();
                    float saturation = r.ReadFloat();
                    lock (_gate) { Health = health; Food = food; Saturation = saturation; }
                    break;
                }

            case "set_time":
                {
                    long age = r.ReadLong();
                    long time = r.ReadLong();
                    lock (_gate) { WorldAge = age; TimeOfDay = time; }
                    break;
                }

            case "set_experience":
                {
                    float progress = r.ReadFloat();
                    int level = r.ReadVarInt();
                    int total = r.ReadVarInt();
                    lock (_gate) { XpProgress = progress; XpLevel = level; XpTotal = total; }
                    break;
                }

            case "system_chat":
                {
                    // 1.19 - 1.20.2: JSON string, 1.20.3+: network NBT. Both are followed by an "overlay" flag.
                    string text = protocol >= 765 ? ChatText.FromNbt(r) : ChatText.FromJson(r.ReadString());
                    bool overlay = r.Remaining > 0 && r.ReadBool();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        AddChat(overlay ? "(actionbar) " + text : text);
                    }
                    break;
                }

            case "player_chat":
                {
                    // 1.19.3+ layout: sender uuid, index, optional 256 byte signature, plain message ...
                    if (protocol < 761)
                    {
                        break;
                    }
                    Guid sender = r.ReadUuid();
                    r.ReadVarInt();
                    if (r.ReadBool())
                    {
                        r.Skip(256);
                    }
                    string message = r.ReadString();
                    AddChat($"[{sender.ToString().Substring(0, 8)}] {message}");
                    break;
                }
        }
    }

    private void ApplyServerbound(string name, PacketReader r)
    {
        // The client reports its own position continuously, which is more current than the server's teleports.
        switch (name)
        {
            case "move_player_pos":
                {
                    double x = r.ReadDouble(), y = r.ReadDouble(), z = r.ReadDouble();
                    lock (_gate) { X = x; Y = y; Z = z; HasPosition = true; }
                    break;
                }

            case "move_player_pos_rot":
                {
                    double x = r.ReadDouble(), y = r.ReadDouble(), z = r.ReadDouble();
                    float yaw = r.ReadFloat(), pitch = r.ReadFloat();
                    lock (_gate) { X = x; Y = y; Z = z; Yaw = yaw; Pitch = pitch; HasPosition = true; }
                    break;
                }

            case "move_player_rot":
                {
                    float yaw = r.ReadFloat(), pitch = r.ReadFloat();
                    lock (_gate) { Yaw = yaw; Pitch = pitch; }
                    break;
                }
        }
    }

    private void AddChat(string line)
    {
        lock (_gate)
        {
            _chat.Add(line);
            if (_chat.Count > MaxChatHistory)
            {
                _chat.RemoveAt(0);
            }
        }
        ChatReceived?.Invoke(line);
    }
}
