using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;

namespace funnel_.core;

public class MinecraftOnlineAuth
{
    private static readonly HttpClient _httpClient = new HttpClient();
    private string _accessToken = "";
    private string _uuid = "";
    private string _username = "";

    /// <summary>
    /// Führt den interaktiven Microsoft OAuth2 Device Code Flow aus.
    /// Öffnet ein Browserfenster oder gibt den Code für ://microsoft.com im Terminal aus.
    /// </summary>
    public async Task<bool> LoginWithMicrosoftAsync()
    {
        try
        {
            Console.WriteLine("[Auth] Initialisiere Microsoft Login-Handler...");
            
            // Initialisiert den offiziellen OAuth-Flow von CmlLib
            var loginHandler = new JELoginHandlerBuilder().Build();
            
            Console.WriteLine("[Auth] Bitte folge den Anweisungen im Terminal/Browser zur Anmeldung...");
            // Startet den interaktiven Device-Flow
            var session = await loginHandler.AuthenticateInteractively();

            _accessToken = session.AccessToken;
            _uuid = session.UUID.Replace("-", ""); // Minecraft verlangt die UUID ohne Bindestriche
            _username = session.Username;

            Console.WriteLine($"\n[Auth] Erfolgreich als {_username} authentifiziert!");
            Console.WriteLine($"[Auth] UUID: {_uuid}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] Fehler bei der Microsoft-Authentifizierung: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Berechnet den speziellen Minecraft-SHA1-Hash (Hex-String mit Berücksichtigung negativer Werte).
    /// </summary>
    public static string CalculateMinecraftHash(string serverId, byte[] sharedSecret, byte[] serverPublicKey)
    {
        using (var sha1 = SHA1.Create())
        {
            // Erzeuge einen Stream aus ServerID + SharedSecret + Public Key
            using (var ms = new MemoryStream())
            {
                byte[] serverIdBytes = Encoding.ASCII.GetBytes(serverId);
                ms.Write(serverIdBytes, 0, serverIdBytes.Length);
                ms.Write(sharedSecret, 0, sharedSecret.Length);
                ms.Write(serverPublicKey, 0, serverPublicKey.Length);

                byte[] hash = sha1.ComputeHash(ms.ToArray());

                // Minecraft-Besonderheit: Der Hash wird als Hex-String im Zweierkomplement
                // dargestellt, falls das erste Bit eine 1 ist (negative Zahl).
                bool isNegative = (hash[0] & 0x80) == 0x80;
                if (isNegative)
                {
                    // Invertiere die Bits und addiere 1 (Zweierkomplement manuell)
                    bool carry = true;
                    for (int i = hash.Length - 1; i >= 0; i--)
                    {
                        hash[i] = (byte)~hash[i];
                        if (carry)
                        {
                            carry = hash[i] == 0xFF;
                            hash[i]++;
                        }
                    }
                }

                // Konvertiere in Hex und trimme führende Nullen
                string hex = BitConverter.ToString(hash).Replace("-", "").ToLower().TrimStart('0');
                return isNegative ? "-" + hex : hex;
            }
        }
    }

    /// <summary>
    /// Sendet den legitimierten Join-Request an den offiziellen Mojang-Session-Server.
    /// Muss aufgerufen werden, BEVOR die 'Encryption Response' an den Online-Server geschickt wird!
    /// </summary>
    public async Task<bool> JoinSessionServerAsync(string serverId, byte[] sharedSecret, byte[] serverPublicKey)
    {
        if (string.IsNullOrEmpty(_accessToken))
        {
            Console.WriteLine("[Auth] Fehler: Kein gültiges Access Token vorhanden. Bitte zuerst einloggen.");
            return false;
        }

        // 1. Berechne den kryptografischen SHA-1 Identifikations-Hash
        string minecraftHash = CalculateMinecraftHash(serverId, sharedSecret, serverPublicKey);
        Console.WriteLine($"[Auth] Berechneter Minecraft SHA-1 Hash: {minecraftHash}");

        // 2. Baue den JSON-Payload für Mojang
        var payload = new MojangJoinPayload
        {
            AccessToken = _accessToken,
            SelectedProfile = _uuid,
            ServerId = minecraftHash
        };

        const string mojangUrl = "https://mojang.com";

        try
        {
            Console.WriteLine("[Auth] Sende Verifizierungs-Hash an Mojang Session-Server...");
            var response = await _httpClient.PostAsJsonAsync(mojangUrl, payload);

            // Mojang antwortet bei Erfolg standardmäßig mit "240 No Content"
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine("[Auth] Mojang-Sitzung erfolgreich verifiziert! Der Online-Server wird uns passieren lassen.");
                return true;
            }

            string errorResponse = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"[Auth] Mojang hat die Sitzung abgelehnt: {response.StatusCode} - {errorResponse}");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Auth] Netzwerkfehler beim Verbinden mit Mojang: {ex.Message}");
            return false;
        }
    }

    // JSON-Strukturklasse für den HTTP-POST
    private class MojangJoinPayload
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = "";

        [JsonPropertyName("selectedProfile")]
        public string SelectedProfile { get; set; } = "";

        [JsonPropertyName("serverId")]
        public string ServerId { get; set; } = "";
    }
}
