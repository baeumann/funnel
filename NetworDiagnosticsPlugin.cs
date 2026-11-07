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
        public string Version => "1.1.6";

        private long _totalBytesReceived = 0;
        private long _totalPacketsReceived = 0;
        private readonly HashSet<int> _loadedEntities = new HashSet<int>();

        public void OnLoad()
        {
            Log("Erfolgreich als internes Diagnose-Werkzeug geladen.");
        }

        public void OnSessionStarted(Session session)
        {
            Log("Starte Netzwerk-Ueberwachung fuer Session #" + session.Number);

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

            // Alle 500 Pakete einen kurzen Status-Bericht im Terminal ausgeben
            if (_totalPacketsReceived % 500 == 0)
            {
                double kbytes = _totalBytesReceived / 1024.0;
                Log("[Session #" + session.Number + "] Durchsatz: " + kbytes.ToString("F2") + " KB | Pakete gesamt: " + _totalPacketsReceived + " | Aktive Entities: " + _loadedEntities.Count);
            }

            // Entity-Radar (Clientbound)
            if (args.Direction == Direction.Clientbound)
            {
                if (args.PacketId == 0x2D || args.PacketId == 0x03)
                {
                    try
                    {
                        byte[] payloadCopy = args.Payload.ToArray();
                        using (var ms = new MemoryStream(payloadCopy))
                        {
                            int entityId = ReadLocalVarInt(ms);
                            if (entityId > 0)
                            {
                                bool isNew;
                                lock (_loadedEntities) { isNew = _loadedEntities.Add(entityId); }
                                if (isNew)
                                {
                                    Log("Server deklariert neues Objekt -> Entity-ID: " + entityId);
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
        }

        private static int ReadLocalVarInt(Stream s)
        {
            int val = 0, size = 0, b;
            while (((b = s.ReadByte()) & 0x80) == 0x80) 
            { 
                if (b == -1) throw new EndOfStreamException(); 
                val |= (b & 0x7F) << (size++ * 7); 
                if (size > 5) throw new InvalidDataException(); 
            }
            return val | ((b & 0x7F) << (size * 7));
        }

        private void Log(string message)
        {
            Console.WriteLine("[" + Name + "] " + message);
        }
    }
}
