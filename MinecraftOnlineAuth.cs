using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace funnel_.core;

public class MinecraftOnlineAuth
{
    private static readonly HttpClient _httpClient = new HttpClient();

    /// <summary>
    /// Berechnet den speziellen Minecraft-SHA1-Hash (Hex-String mit Berücksichtigung negativer Werte).
    /// </summary>
    public static string CalculateMinecraftHash(string serverId, byte[] sharedSecret, byte[] serverPublicKey)
    {
        using (var sha1 = SHA1.Create())
        {
            using (var ms = new MemoryStream())
            {
                byte[] serverIdBytes = Encoding.ASCII.GetBytes(serverId);
                ms.Write(serverIdBytes, 0, serverIdBytes.Length);
                ms.Write(sharedSecret, 0, sharedSecret.Length);
                ms.Write(serverPublicKey, 0, serverPublicKey.Length);

                byte[] hash = sha1.ComputeHash(ms.ToArray());

                bool isNegative = (hash[0] & 0x80) == 0x80;
                if (isNegative)
                {
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

                string hex = BitConverter.ToString(hash).Replace("-", "").ToLower().TrimStart('0');
                if (hex.Length == 0) hex = "0";
                return isNegative ? "-" + hex : hex;
            }
        }
    }

    /// <summary>
    /// Sendet den legitimierten Join-Request an den offiziellen Mojang-Session-Server.
    /// </summary>
    public static async Task<bool> JoinSessionServerAsync(string accessToken, string uuid, string minecraftHash)
    {
        var payload = new MojangJoinPayload
        {
            AccessToken = accessToken,
            SelectedProfile = uuid.Replace("-", ""),
            ServerId = minecraftHash
        };

        const string mojangUrl = "https://mojang.com";

        try
        {
            var response = await _httpClient.PostAsJsonAsync(mojangUrl, payload);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private class MojangJoinPayload
    {
        [JsonPropertyName("accessToken")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("selectedProfile")] public string SelectedProfile { get; set; } = "";
        [JsonPropertyName("serverId")] public string ServerId { get; set; } = "";
    }
}
