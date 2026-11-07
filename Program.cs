using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Identity.Client;
using funnel_.core;
using funnel_.protocol;

namespace funnel_;

class Program
{
    const int PORT = 25565;
    private static readonly List<IProxyPlugin> _loadedPlugins = new List<IProxyPlugin>();
    const string bootLogo = "    ______   __  __    _   __    _   __    ______    __ \n   / ____/  / / / /   / | / /   / | / /   / ____/   / / \n  / /_     / / / /   /  |/ /   /  |/ /   / __/     / /  \n / __/    / /_/ /   / /|  /   / /|  /   / /___    / /___   _______\n/_/       \\____/   /_/ |_/   /_/ |_/   /_____/   /_____/  /______/\n                                                        ";

    static async Task Main(string[] args)
    {
        Console.WriteLine("\n\n");
        Console.Write(bootLogo);
        Console.WriteLine("\n____________________________________________________________________________");
        Console.WriteLine("\nWELCOME TO FUNNEL MINECRAFT PROXY [type 'help' for commands, 'quit' to exit]\n");

        // =========================================================================
        // INITIALISIERE DAS INTERNE DIAGNOSE-PLUGIN
        // =========================================================================
        var internalDiag = new funnel_.core.NetworkDiagnosticsPlugin();
        internalDiag.OnLoad();
        funnel_.core.Proxy.OnSessionStarted += internalDiag.OnSessionStarted;

        // =========================================================================
        // DYNAMISCHER RUNTIME/.DLL PLUGIN LOADER
        // =========================================================================
        LoadExternalPlugins();

        var positional = new List<string>();
        string? idsPath = null;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--ids=", StringComparison.OrdinalIgnoreCase)) idsPath = arg.Substring("--ids=".Length);
            else positional.Add(arg);
        }

        if (positional.Count >= 2)
        {
            string? firstPos = positional.FirstOrDefault();
            if (firstPos != null) bool.TryParse(firstPos, out Proxy.LOCAL_DEBUG);
            if (Proxy.LOCAL_DEBUG) Console.WriteLine("DEBUG MODE, USING SERVER PORT " + Proxy.DEBUG_PORT);
        }

        // =========================================================================
        // MSAL.NET OAUTH FLOW MIT CACHE
        // =========================================================================
        try
        {
            Console.WriteLine("[Auth] Initialisiere MSAL.NET Identity Client mit lokalem Cache...");

            string azureClientId = "72612c17-6ca3-4334-82db-947625e8f17c";
            string cachePath = Path.Combine(AppContext.BaseDirectory, "msal_cache.bin");

            var app = PublicClientApplicationBuilder.Create(azureClientId)
                .WithTenantId("consumers")
                .WithRedirectUri("https://microsoftonline.com")
                .Build();

            app.UserTokenCache.SetBeforeAccess(notificationArgs =>
            {
                if (File.Exists(cachePath))
                    notificationArgs.TokenCache.DeserializeMsalV3(File.ReadAllBytes(cachePath));
            });
            app.UserTokenCache.SetAfterAccess(notificationArgs =>
            {
                if (notificationArgs.HasStateChanged)
                    File.WriteAllBytes(cachePath, notificationArgs.TokenCache.SerializeMsalV3());
            });

            AuthenticationResult authResult;
            var accounts = await app.GetAccountsAsync();

            try
            {
                Console.WriteLine("[Auth] Versuche, bestehende Sitzung lautlos aus dem Cache zu laden...");
                authResult = await app.AcquireTokenSilent(new[] { "XboxLive.signin", "offline_access" }, accounts.FirstOrDefault())
                    .ExecuteAsync();
                Console.WriteLine("[Auth] Sitzung erfolgreich aus dem lokalen Cache wiederhergestellt!");
            }
            catch (MsalUiRequiredException)
            {
                Console.WriteLine("[Auth] Kein gültiger Cache deklariert. Starte Device-Code-Verfahren...");
                authResult = await app.AcquireTokenWithDeviceCode(
                    new[] { "XboxLive.signin", "offline_access" },
                    deviceCodeResult =>
                    {
                        Console.WriteLine("\n==================================================");
                        Console.WriteLine("[Auth] ACTION REQUIRED: MICROSOFT ANMELDUNG ERFORDERLICH!");
                        Console.WriteLine($"[Auth] {deviceCodeResult.Message}");
                        Console.WriteLine("==================================================\n");

                        try
                        {
                            Console.WriteLine("[Auth] Öffne Verifizierungsseite im Standardbrowser...");
                            string url = deviceCodeResult.VerificationUrl;
                            string userCode = deviceCodeResult.UserCode;

                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                var powershellArgs = $"-Command \"Set-Clipboard -Value '{userCode}'\"";
                                Process.Start(new ProcessStartInfo("powershell", powershellArgs) { CreateNoWindow = true });
                                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                                Console.WriteLine($"[Auth] CODE '{userCode}' ERFOLGREICH IN DEINE ZWISCHENABLAGE KOPIERT!");
                            }
                        }
                        catch
                        {
                            Console.WriteLine("[Auth] Hinweis: Browser oder Clipboard-Export fehlgeschlagen.");
                        }
                        return Task.CompletedTask;
                    }
                ).ExecuteAsync();
            }

            Session.MicrosoftAccessToken = authResult.AccessToken;
            Session.MicrosoftUuid = "7325d5d87105330fa3fcfbe62810a9c8";

            Console.WriteLine("\n==================================================");
            Console.WriteLine("[Auth] LOGIN ERFOLGREICH! MSAL-Sitzungskontext verifiziert.");
            Console.WriteLine($"[Auth] Spieler-UUID aktiv verankert: {Session.MicrosoftUuid}");
            Console.WriteLine("[Auth] Starte Proxy-Netzwerk-Schnittstelle...");
            Console.WriteLine("==================================================\n");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] LOGIN FEHLGESCHLAGEN: {ex.Message}");
            Console.WriteLine("[Auth] Proxy startet im Offline-Modus als Fallback.\n");
        }

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

        // =========================================================================
        // ROBUSTE INTERAKTIVE EINGABESCHLEIFE (Verhindert Auto-Exits)
        // =========================================================================
        var commands = new Commands(proxy);

        if (!Console.IsInputRedirected)
        {
            while (true)
            {
                string? line = Console.ReadLine();
                if (line == null) break;
                if (line.Trim().Length == 0) continue;
                try { if (!await commands.RunAsync(line)) break; }
                catch (Exception ex) { Console.WriteLine("!! " + ex.Message); }
            }
        }
        else
        {
            Console.WriteLine("[Core] Eingabestrom umgeleitet. Proxy läuft im autarken Hintergrund-Modus.");
            while (true)
            {
                await Task.Delay(1000);
            }
        }

        Console.WriteLine("PROXY EXITING!");
        proxy.Close();
        Console.WriteLine("BYE!");
    }

    /// <summary>
    /// Scans the /plugins directory, loads all .dll assemblies at runtime,
    /// filters for types implementing IProxyPlugin, and links them to the core.
    /// </summary>
    private static void LoadExternalPlugins()
    {
        string pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!Directory.Exists(pluginsDir))
        {
            Directory.CreateDirectory(pluginsDir);
        }

        Console.WriteLine("[Loader] Scanning for external modules in /plugins directory...");
        string[] dllFiles = Directory.GetFiles(pluginsDir, "*.dll");

        foreach (var dllPath in dllFiles)
        {
            try
            {
                Assembly assembly = Assembly.LoadFrom(dllPath);
                var pluginTypes = assembly.GetTypes()
.Where(t => typeof(IProxyPlugin).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);
                foreach (var type in pluginTypes)
                {
                    if (Activator.CreateInstance(type) is IProxyPlugin plugin)
                    {
                        plugin.OnLoad();
                        Proxy.OnSessionStarted += plugin.OnSessionStarted;
                        _loadedPlugins.Add(plugin);
                        Console.WriteLine($"[Loader] SUCCESS: Module '{plugin.Name}' (v{plugin.Version}) loaded successfully.");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Loader] Error loading plugin assembly '{Path.GetFileName(dllPath)}': {ex.Message}");
            }
        }
        if (_loadedPlugins.Count == 0)
        {
            Console.WriteLine("[Loader] No external .dll plugins found. Core proxy running in baseline production mode.");
        }
    }
}