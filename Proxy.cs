using System.Net;
using System.Net.Sockets;
using funnel_.protocol;

namespace funnel_.core
{
    /// <summary>
    /// Listens for Minecraft clients on the local machine and connects each one to the target server.
    /// Every client gets its own <see cref="Session"/>; the most recent one is exposed as <see cref="Current"/>.
    /// </summary>
    public class Proxy
    {
        public static bool LOCAL_DEBUG = false;
        public const int DEBUG_PORT = 5555;

        private readonly int _port;
        private readonly string _targetHost;
        private readonly int? _targetPort;
        private readonly TcpListener _listener;
        private readonly List<Session> _sessions = new List<Session>();
        private bool _closing;

        public ProxyOptions Options { get; } = new ProxyOptions();
        public PacketIds Ids { get; }

        public Proxy(int port, string address, PacketIds? ids = null)
        {
            _port = port;
            (_targetHost, _targetPort) = SplitAddress(address);
            Ids = ids ?? PacketIds.Empty();
            _listener = new TcpListener(IPAddress.Parse("127.0.0.1"), _port);
        }

        /// <summary>The most recently connected client that is still online, or null.</summary>
        public Session? Current
        {
            get
            {
                lock (_sessions)
                {
                    return _sessions.Count > 0 ? _sessions[_sessions.Count - 1] : null;
                }
            }
        }

        /// <summary>Starts listening. Throws SocketException if the port is already in use.</summary>
        public void Start()
        {
            _listener.Start();
            Console.WriteLine(">> listening for client on " + _port + ".");
            _ = AcceptLoopAsync();
        }

        public void Close()
        {
            _closing = true;
            try { _listener.Stop(); } catch (SocketException) { }

            Session[] sessions;
            lock (_sessions)
            {
                sessions = _sessions.ToArray();
            }
            foreach (var session in sessions)
            {
                session.Dispose();
            }
        }

        private int TargetPort => LOCAL_DEBUG ? DEBUG_PORT : (_targetPort ?? _port);

        private async Task AcceptLoopAsync()
        {
            while (!_closing)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (_closing) { break; }
                    continue;
                }

                _ = HandleClientAsync(client);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            client.NoDelay = true;
            var server = new TcpClient { NoDelay = true };

            try
            {
                await server.ConnectAsync(_targetHost, TargetPort).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine(">> could not connect to target [" + _targetHost + ":" + TargetPort + "]: " + ex.Message);
                client.Close();
                server.Close();
                return;
            }

            Console.WriteLine(">> minecraft client connected, linked to target server [" + _targetHost + ":" + TargetPort + "].");

            var session = new Session(client, server, Ids, Options, _targetHost, TargetPort);
            session.Game.ChatReceived += line =>
            {
                if (Options.ShowChat)
                {
                    Console.WriteLine("[chat] " + line);
                }
            };

            lock (_sessions)
            {
                _sessions.Add(session);
            }

            try
            {
                await session.RunAsync().ConfigureAwait(false);
            }
            finally
            {
                lock (_sessions)
                {
                    _sessions.Remove(session);
                }
                session.Dispose();
                Console.WriteLine(">> [" + session.Number + "] connection closed.");
            }
        }

        private static (string host, int? port) SplitAddress(string address)
        {
            if (IPAddress.TryParse(address, out _))
            {
                return (address, null);
            }

            int colon = address.LastIndexOf(':');
            if (colon > 0 && int.TryParse(address.Substring(colon + 1), out int port))
            {
                return (address.Substring(0, colon), port);
            }

            return (address, null);
        }
    }
}
