using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using funnel_.protocol;

namespace funnel_.core
{
    public class NetworkDiagnosticsPlugin : IProxyPlugin
    {
        public string Name => "NetworkDiagnostics";
        public string Version => "1.0.0";

        private long _totalBytesReceived = 0;
        private long _totalPacketsReceived = 0;
        private readonly HashSet<int> _loadedEntities = new();

        public void OnLoad()
        {
            Log("Erfolgreich als internes Diagnose-Werkzeug geladen.");
        }

        public void OnSessionStarted(Session session)
        {
            Log($"Starte Netzwerk-Überwachung für Session #{session.Number}...");

            session.OnPacketTraffic += (sender, args) =>
            {
                if (sender.State == ConnectionState.Play)
                {
                    AnalyzeTraffic(sender, args);
                }
            };
        }

        private void AnalyzeTraffic(Session session, PacketTrafficEventArgs args)
        {
            _totalPacketsReceived++;
            _totalBytesReceived += args.Length;

            if (_totalPacketsReceived % 500 == 0)
            {
                double kbytes = _totalBytesReceived / 1024.0;
                Log($"[Session #{session.Number}] Durchsatz: {kbytes:F2} KB | Pakete: {_totalPacketsReceived} | Entities: {_loadedEntities.Count}");
            }

            if (args.Direction == Direction.Clientbound)
            {
                if (args.PacketId == 0x2D || args.PacketId == 0x03)
                {
                    try
                    {
                        byte[] payloadCopy = args.Payload.ToArray();
                        using var ms = new MemoryStream(payloadCopy);
                        int entityId = MinecraftStreamUtil.ReadVarInt(ms);

                        if (entityId > 0)
                        {
                            bool isNew;
                            lock (_loadedEntities)
                            {
                                isNew = _loadedEntities.Add(entityId);
                            }
                            
                            if (isNew)
                            {
                                Log($"Server deklariert neues Objekt -> Entity-ID: {entityId}");
                            }
                        }
                    }
                    catch { }
                }
                else if (args.PacketId == 0x40)
                {
                    try
                    {
                        byte[] payloadCopy = args.Payload.ToArray();
                        using var ms = new MemoryStream(payloadCopy);
                        int count = MinecraftStreamUtil.ReadVarInt(ms);
                        for (int i = 0; i < count; i++)
                        {
                            int entityId = MinecraftStreamUtil.ReadVarInt(ms);
                            lock (_loadedEntities) 
                            { 
                                _loadedEntities.Remove(entityId); 
                            }
                        }
                    }
                    catch { }
                }
            }

            if (args.Direction == Direction.Serverbound)
            {
                if (args.PacketId == 0x30)
                {
                    Log("Spieler führt unsignierte Interaktion aus (Swing Arm Paket gesendet)");
                }
            }
        }

        private void Log(string message)
        {
            Console.WriteLine($"[{Name}] {message}");
        }
    }
}
