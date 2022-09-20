using System.Net;
using System.Net.Sockets;

namespace funnel_.core
{
    public class Proxy
    {
        public static bool LOCAL_DEBUG = false;
        public const int DEBUG_PORT = 5555;
        private int _port;
        private string _targetAddress;

        private TcpClient targetMinecraftServerConnection;
        private TcpListener sourceMinecraftClientConnection;

        private Thread readTargetThread;
        private bool _keepReadTargetThreadRunning = false;
        private Thread sourceClientListenerThread;
        private bool _keepSourceClientListenerThreadRunning = false;
        private Thread readSourceThread;
        private bool _keepReadSourceThreadRunning  = false;
        private TcpClient sourceClient;

        private bool _closing = false;
        private bool _restarting = false;
        private int _restartFailCounter = 0;
        private bool _restartInProgress = false;

        private IPacketFacility _sourcePacketFacility;
        private IPacketFacility _targetPacketFacility;
        public Proxy(int port, string address) {
            this._port = port;
            this._targetAddress = address;

            _sourcePacketFacility = new PacketFacility();
            _targetPacketFacility = new PacketFacility();

            StartListeningForSourceClients();
        }

        private void StartClientListener() {
            _restarting = true;
            Console.WriteLine(">> listening for client on " + _port + ".");
            targetMinecraftServerConnection = new TcpClient();

            sourceMinecraftClientConnection = new TcpListener(IPAddress.Parse("127.0.0.1"), _port);
            sourceMinecraftClientConnection.Start();
            _restarting = false;
        }

        private void StartListeningForSourceClients() {
            try {
                Console.WriteLine(">> restarting...");
                StartClientListener();

                _keepSourceClientListenerThreadRunning = true;
                sourceClientListenerThread = new Thread(ListenForSourceClient);
                sourceClientListenerThread.Start();
            } catch(Exception) {
                _restartFailCounter++;
                if(_restartFailCounter >= 5) {
                    Console.WriteLine(">> exiting because of too many failed restart attempts.");
                    Close();
                    System.Environment.Exit(0);
                } else {
                    Console.WriteLine(">> restarting failed > re-try.");
                    Thread.Sleep(1000);
                    _restartInProgress = false;
                    Restart();
                }

            }
        }

        public void Restart() {
            if(_restartInProgress) {
                return;
            }
            _restartInProgress = true;
            if(_closing) {
                return;
            }
            DisconnectFromTarget();
            DisconnectFromSource();

            StartListeningForSourceClients();
            _restartInProgress = false;
        }
        public void Close() {
            _closing = true;
            DisconnectFromTarget();
            DisconnectFromSource();
        }

        private void ConnectToTarget() {
            if(!_restarting && _targetAddress != null && sourceClient != null && sourceClient.Connected) {

                try {
                    IPAddress parsedAddress;
                    if(IPAddress.TryParse(_targetAddress, out parsedAddress)) {

                        if(LOCAL_DEBUG) {
                            targetMinecraftServerConnection.Connect(parsedAddress, DEBUG_PORT);
                        } else {
                            targetMinecraftServerConnection.Connect(parsedAddress, _port);
                        }
                    } else {

                        if(LOCAL_DEBUG) {
                            targetMinecraftServerConnection.Connect(_targetAddress, DEBUG_PORT);
                        } else {
                            targetMinecraftServerConnection.Connect(_targetAddress, _port);
                        }
                    } 
                    
                } catch(Exception exception) {
                    Restart();
                    return;
                }

                Console.WriteLine(">> connected to target server [" + _targetAddress + "].");

                PurgeReadTargetThread();

                _keepReadTargetThreadRunning = true;
                readTargetThread = new Thread(ReadTarget);
                readTargetThread.Start();
            } else {
                Restart();
            }
        }

        private void DisconnectFromTarget() {
            PurgeReadTargetThread();

            targetMinecraftServerConnection.Close();
            targetMinecraftServerConnection.Dispose();
        }

        private void DisconnectFromSource() {
            PurgeSourceListenThread();
            PurgeReadSourceThread();

            sourceClient.Close();
            sourceClient.Dispose();
            sourceMinecraftClientConnection.Stop();
        }

        private void PurgeReadTargetThread() {
            if(readTargetThread != null) {
                _keepReadTargetThreadRunning = false;

                //wait 50ms to let thread exit
                Thread.Sleep(50);
            }
        }

        private void PurgeReadSourceThread() {
            if(readSourceThread != null) {
                _keepReadSourceThreadRunning = false;

                //wait 50ms to let thread exit
                Thread.Sleep(50);
            }
        }

        private void PurgeSourceListenThread() {
            if(sourceClientListenerThread != null) {
                _keepSourceClientListenerThreadRunning = false;

                //wait 50ms to let thread exit
                Thread.Sleep(50);
            }
        }

        private void ListenForSourceClient() {

            while(_keepSourceClientListenerThreadRunning) {
                try {
                    if(!_restarting && 
                        !_keepReadSourceThreadRunning && 
                        !_keepReadTargetThreadRunning && 
                        sourceMinecraftClientConnection != null && 
                        sourceMinecraftClientConnection.Pending()) {

                        sourceClient = sourceMinecraftClientConnection.AcceptTcpClient();
                        if(sourceClient != null) {
                            Console.WriteLine(">> minecraft client connected.");
                            PurgeSourceListenThread();
                            PurgeReadSourceThread();

                            _keepReadSourceThreadRunning = true;
                            readSourceThread = new Thread(ReadSource);
                            readSourceThread.Start();

                            ConnectToTarget();
                        }
                    }
                } catch(Exception) {
                    Restart();
                }

                Thread.Sleep(5);
            }
        }

        private void ReadSource() {

            while(_keepReadSourceThreadRunning) {
                try {
                    if(!_restarting && sourceClient != null && sourceClient.Connected && sourceClient.GetStream().DataAvailable) {
                        
                        var stream = sourceClient.GetStream();
                        
                        if(targetMinecraftServerConnection.Connected) {

                            byte[] buffer = new byte[sourceClient.Available];
                            stream.Read(buffer, 0, buffer.Length);

                            //_sourcePacketFacility.Add(buffer);

                            targetMinecraftServerConnection.GetStream().Write(buffer, 0, buffer.Length);
                        } 
                    } else if(sourceClient == null || !sourceClient.Connected) {
                        Restart();
                    }

                } catch(Exception) {
                    Restart();
                }

                Thread.Sleep(5);
            }
        }

        private void ReadTarget() {

            while(_keepReadTargetThreadRunning) {
                if(targetMinecraftServerConnection.Connected && targetMinecraftServerConnection.GetStream().DataAvailable) {

                    var stream = targetMinecraftServerConnection.GetStream();

                    if(sourceClient.Connected) {
                        try {
                            byte[] buffer = new byte[targetMinecraftServerConnection.Available];
                            stream.Read(buffer, 0, buffer.Length);

                            _targetPacketFacility.Add(buffer);

                            sourceClient.GetStream().Write(buffer, 0, buffer.Length);
                        } catch(Exception) {
                            Restart();
                        }
                    }

                } else if(!targetMinecraftServerConnection.Connected) {
                    Restart();
                    break;
                }

                Thread.Sleep(5);
            }
        }
    }
}