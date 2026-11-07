using System.Text;
using System.Text.Json;

namespace funnel_.protocol;

/// <summary>
/// Turns Minecraft text components (JSON before 1.20.3, network NBT from 1.20.3) into plain text.
/// Formatting such as colours is dropped; translatable components use a few built-in templates
/// and fall back to "key (arg, arg)".
/// </summary>
public static class ChatText
{
    private static readonly Dictionary<string, string> Templates = new()
    {
        ["chat.type.text"] = "<{0}> {1}",
        ["chat.type.emote"] = "* {0} {1}",
        ["chat.type.announcement"] = "[{0}] {1}",
        ["multiplayer.player.joined"] = "{0} joined the game",
        ["multiplayer.player.left"] = "{0} left the game",
    };

    public static string FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Flatten(FromElement(doc.RootElement), 0);
    }

    /// <summary>Reads a nameless network-NBT text component (1.20.2+ wire format).</summary>
    public static string FromNbt(PacketReader reader)
    {
        int type = reader.ReadByte();
        if (type == 0)
        {
            return string.Empty;
        }
        return Flatten(ReadTag(reader, type, 0), 0);
    }

    private static object? FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                return element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(FromElement(item));
                }
                return list;
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var prop in element.EnumerateObject())
                {
                    dict[prop.Name] = FromElement(prop.Value);
                }
                return dict;
            default:
                return null;
        }
    }

    private static object? ReadTag(PacketReader r, int type, int depth)
    {
        if (depth > 64)
        {
            throw new InvalidDataException("NBT nested too deeply");
        }

        switch (type)
        {
            case 1: return (long)(sbyte)r.ReadByte();
            case 2: return (long)(short)r.ReadUShort();
            case 3: return (long)r.ReadInt();
            case 4: return r.ReadLong();
            case 5: return (double)r.ReadFloat();
            case 6: return r.ReadDouble();
            case 7: r.Skip(r.ReadInt()); return null;
            case 8: return ReadNbtString(r);
            case 9:
                {
                    int elementType = r.ReadByte();
                    int count = r.ReadInt();
                    var list = new List<object?>();
                    if (count > 0 && elementType == 0)
                    {
                        throw new InvalidDataException("NBT list of END tags");
                    }
                    for (int i = 0; i < count; i++)
                    {
                        list.Add(ReadTag(r, elementType, depth + 1));
                    }
                    return list;
                }
            case 10:
                {
                    var dict = new Dictionary<string, object?>();
                    while (true)
                    {
                        int childType = r.ReadByte();
                        if (childType == 0)
                        {
                            break;
                        }
                        string name = ReadNbtString(r);
                        dict[name] = ReadTag(r, childType, depth + 1);
                    }
                    return dict;
                }
            case 11: r.Skip(checked(r.ReadInt() * 4)); return null;
            case 12: r.Skip(checked(r.ReadInt() * 8)); return null;
            default:
                throw new InvalidDataException("unknown NBT tag type " + type);
        }
    }

    private static string ReadNbtString(PacketReader r)
    {
        int length = r.ReadUShort();
        return Encoding.UTF8.GetString(r.ReadBytes(length));
    }

    private static string Flatten(object? node, int depth)
    {
        if (depth > 64 || node == null)
        {
            return string.Empty;
        }

        switch (node)
        {
            case string s:
                return s;
            case List<object?> list:
                return string.Concat(list.Select(item => Flatten(item, depth + 1)));
            case Dictionary<string, object?> dict:
                {
                    var sb = new StringBuilder();
                    if (dict.TryGetValue("text", out var text))
                    {
                        sb.Append(Flatten(text, depth + 1));
                    }
                    else if (dict.TryGetValue("translate", out var translate))
                    {
                        var args = new List<string>();
                        if (dict.TryGetValue("with", out var withNode) && withNode is List<object?> withList)
                        {
                            foreach (var arg in withList)
                            {
                                args.Add(Flatten(arg, depth + 1));
                            }
                        }
                        sb.Append(Translate(Flatten(translate, depth + 1), args));
                    }

                    if (dict.TryGetValue("extra", out var extra))
                    {
                        sb.Append(Flatten(extra, depth + 1));
                    }
                    return sb.ToString();
                }
            default:
                return Convert.ToString(node, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
    }

    private static string Translate(string key, List<string> args)
    {
        if (Templates.TryGetValue(key, out var template))
        {
            for (int i = 0; i < args.Count; i++)
            {
                template = template.Replace("{" + i + "}", args[i]);
            }
            return template;
        }
        return args.Count == 0 ? key : key + " (" + string.Join(", ", args) + ")";
    }
}
