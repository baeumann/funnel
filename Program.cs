using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using funnel_.core;
using funnel_.protocol;

namespace funnel_;

class Program
{
    const int PORT = 25565;
    const string bootLogo = "    ______   __  __    _   __    _   __    ______    __ \n   / ____/  / / / /   / | / /   / | / /   / ____/   / / \n  / /_     / / / /   /  |/ /   /  |/ /   / __/     / /  \n / __/    / /_/ /   / /|  /   / /|  /   / /___    / /___   _______\n/_/       \\____/   /_/ |_/   /_/ |_/   /_____/   /_____/  /______/\n                                                        ";

    static async Task Main(string[] args)
    {
        var positional = new List<string>();
        string? idsPath = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--ids=", StringComparison.OrdinalIgnoreCase)) idsPath = arg.Substring("--ids=".Length);
            else positional.Add(arg);
        }

        Console.WriteLine("\n\n");
        Console.Write(bootLogo);
        Console.WriteLine("\n____________________________________________________________________________");
        Console.WriteLine("\nWELCOME TO FUNNEL MINECRAFT PROXY [type 'help' for commands, 'quit' to exit]\n");

        if (positional.Count >= 2)
        {
            string? firstPos = positional.FirstOrDefault();
            if (firstPos != null)
            {
                bool.TryParse(firstPos, out Proxy.LOCAL_DEBUG);
            }

            if (Proxy.LOCAL_DEBUG) Console.WriteLine("DEBUG MODE, USING SERVER PORT " + Proxy.DEBUG_PORT);
        }

        // =========================================================================
        // AUTOMATISCHER SCAN & HOCHPRÄZISER FALLBACK (Sicher für Git-Commits)
        // =========================================================================
        try
        {
            Console.WriteLine("[Auth] Starte automatischen Token-Scan...");
            
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string mcdir = Path.Combine(appData, ".minecraft");
            
            string accountsPath = Path.Combine(mcdir, "launcher_accounts.json");
            string profilesPath = Path.Combine(mcdir, "launcher_profiles.json");
            
            string jsonContent = "";
            string fileUsed = "";

            if (File.Exists(accountsPath))
            {
                jsonContent = await File.ReadAllTextAsync(accountsPath);
                fileUsed = "launcher_accounts.json";
            }
            else if (File.Exists(profilesPath))
            {
                jsonContent = await File.ReadAllTextAsync(profilesPath);
                fileUsed = "launcher_profiles.json";
            }

            if (!string.IsNullOrEmpty(jsonContent))
            {
                // Extrahiere verschachtelte Token-Strukturen unabhängig von der JSON-Tiefe
                var tokenMatch = Regex.Match(jsonContent, @"""access_?[tT]oken""\s*:\s*""([^""]{100,})""");
                var uuidMatch = Regex.Match(jsonContent, @"""(?:uuid|id)""\s*:\s*""([a-fA-F0-9]{32})""");
                
                if (!uuidMatch.Success)
                {
                    uuidMatch = Regex.Match(jsonContent, @"""([a-fA-F0-9]{32})""");
                }

                if (tokenMatch.Success && uuidMatch.Success)
                {
                    string extractedToken = tokenMatch.Groups[1].Value;
                    string extractedUuid = uuidMatch.Groups[1].Value.Replace("-", "");

                    Session.MicrosoftAccessToken = extractedToken;
                    Session.MicrosoftUuid = extractedUuid;

                    Console.WriteLine("\n==================================================");
                    Console.WriteLine($"[Auth] SESSSION-DATEN GELADEN! ({fileUsed})");
                    Console.WriteLine($"[Auth] UUID: {extractedUuid.Substring(0, 8)}...");
                    Console.WriteLine("[Auth] Online-Token aktiv im RAM verankert.");
                    Console.WriteLine("==================================================\n");
                }
                else
                {
                    Console.WriteLine($"[Auth] HINWEIS: Token in '{fileUsed}' temporär gesperrt/leer. Nutze Offline-Fallback.");
                }
            }
            else
            {
                // Fallback über Umgebungsvariablen (Erlaubt die lokale Übergabe von echten Keys ohne Code-Leaks)
                string envToken = Environment.GetEnvironmentVariable("MINECRAFT_PROXY_TOKEN") ?? "";
                string envUuid = Environment.GetEnvironmentVariable("MINECRAFT_PROXY_UUID") ?? "";

                if (!string.IsNullOrEmpty(envToken))
                {
                    Session.MicrosoftAccessToken = envToken;
                    Session.MicrosoftUuid = envUuid.Replace("-", "");
                    Console.WriteLine("[Auth] Sitzungs-Konfiguration über System-Umgebungsvariablen geladen.");
                }
                else
                {
                    Console.WriteLine("[Auth] Keine Anmeldedaten bereitgestellt. Starte im Offline-Modus.");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] Fehler beim Initialisieren der Sitzung: {ex.Message}");
        }

        // =========================================================================
        // RELEVANTE PFAD-AUFLÖSUNG (Ersetzt feste D:\\ Zuweisungen)
        // =========================================================================
        var ids = PacketIds.Empty();
        var idCandidates = idsPath != null ? new[] { idsPath } : new[] { "packets.json", Path.Combine(AppContext.BaseDirectory, "packets.json") };
        var idsFile = idCandidates.FirstOrDefault(File.Exists);
        if (idsFile != null)
        {
            try
            {
                ids = PacketIds.LoadFile(idsFile);
                Console.WriteLine("PACKET IDS [" + Path.GetFileName(idsFile) + "] " + ids.Count + " entries\n");
            }
            catch (Exception ex)
            {
                Console.WriteLine("could not read packet ids from " + idsFile + ": " + ex.Message + "\n");
            }
        }

        string targetAddress = positional.Count < 1 ? "127.0.0.1" : (positional.FirstOrDefault() ?? "127.0.0.1");
        Proxy proxy = new Proxy(PORT, targetAddress, ids);

        try { proxy.Start(); }
        catch (SocketException ex)
        {
            Console.WriteLine("could not listen on port " + PORT + ": " + ex.Message);
            return;
        }

        var commands = new Commands(proxy);
        while (true)
        {
            string? line = Console.ReadLine();
            if (line == null) break;
            if (line.Trim().Length == 0) continue;

            try { if (!await commands.RunAsync(line)) break; }
            catch (Exception ex) { Console.WriteLine("!! " + ex.Message); }
        }

        Console.WriteLine("PROXY EXITING!");
        proxy.Close();
        Console.WriteLine("BYE!");
    }
}
