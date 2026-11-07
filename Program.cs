// See https://aka.ms/new-console-template for more information

using System.Net.Sockets;
using funnel_.core;
using funnel_.protocol;

//minecraft port
const int PORT = 25565;
const string bootLogo = "    ______   __  __    _   __    _   __    ______    __ \n   / ____/  / / / /   / | / /   / | / /   / ____/   / / \n  / /_     / / / /   /  |/ /   /  |/ /   / __/     / /  \n / __/    / /_/ /   / /|  /   / /|  /   / /___    / /___   _______\n/_/       \\____/   /_/ |_/   /_/ |_/   /_____/   /_____/  /______/\n                                                        ";

// usage: funnel [target[:port]] [debug true|false] [--ids=packets.json]
var positional = new List<string>();
string? idsPath = null;
foreach (var arg in args)
{
    if (arg.StartsWith("--ids=", StringComparison.OrdinalIgnoreCase))
    {
        idsPath = arg.Substring("--ids=".Length);
    }
    else
    {
        positional.Add(arg);
    }
}

Console.WriteLine("\n\n");
Console.Write(bootLogo);
Console.WriteLine("\n____________________________________________________________________________");
Console.WriteLine("\nWELCOME TO FUNNEL MINECRAFT PROXY [type 'help' for commands, 'quit' to exit]\n");

if (positional.Count >= 2)
{
    bool.TryParse(positional[1], out Proxy.LOCAL_DEBUG);

    if (Proxy.LOCAL_DEBUG)
    {
        Console.WriteLine("DEBUG MODE, USING SERVER PORT " + Proxy.DEBUG_PORT);
    }
}

// packet IDs differ per Minecraft version, see README.md
var ids = PacketIds.Empty();
var idCandidates = idsPath != null
    ? new[] { idsPath }
    : new[] { "packets.json", Path.Combine(AppContext.BaseDirectory, "packets.json") };
var idsFile = idCandidates.FirstOrDefault(File.Exists);
if (idsFile != null)
{
    try
    {
        ids = PacketIds.LoadFile(idsFile);
        Console.WriteLine("PACKET IDS [" + idsFile + "] " + ids.Count + " entries\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine("could not read packet ids from " + idsFile + ": " + ex.Message + "\n");
    }
}
else
{
    Console.WriteLine(idsPath != null
        ? "packet id file not found: " + idsPath + "\n"
        : "no packets.json found: forwarding works, but packets cannot be named, decoded or injected by name (see README.md)\n");
}

Proxy proxy;
if (positional.Count < 1)
{
    Console.WriteLine("TARGET [localhost]\n");
    proxy = new Proxy(PORT, "127.0.0.1", ids);
}
else
{
    Console.WriteLine("TARGET [" + positional[0] + "]\n");
    proxy = new Proxy(PORT, positional[0], ids);
}

try
{
    proxy.Start();
}
catch (SocketException ex)
{
    Console.WriteLine("could not listen on port " + PORT + ": " + ex.Message);
    Console.WriteLine("(is another proxy or a local 'Open to LAN' server already using it?)");
    return;
}

var commands = new Commands(proxy);
while (true)
{
    string? line = Console.ReadLine();
    if (line == null)
    {
        break;
    }
    if (line.Trim().Length == 0)
    {
        continue;
    }

    try
    {
        if (!await commands.RunAsync(line))
        {
            break;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("!! " + ex.Message);
    }
}

Console.WriteLine("PROXY EXITING!");
proxy.Close();
Console.WriteLine("BYE!");
