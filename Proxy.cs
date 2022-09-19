using System.Net;
using System.Net.Sockets;

namespace funnel.core
{
    public class Proxy
    {
        private int _port;
        private string _targetAddress;
        private TcpClient targetMinecraftServerConnection;
        private TcpListener sourceMinecraftClientConnection;

        private Thread readTargetThread;
        private bool _keepReadTargetThreadRunning = true;

        private Thread sourceClientListenerThread;
        private bool _keepSourceClientListenerThreadRunning = true;

        private Thread readSourceThread;
        private bool _keepReadSourceThreadRunning  = true;

        private TcpClient sourceClient;

        private bool _closing = false;
        public Proxy(int port, string address) {
            this._port = port;
            this._targetAddress = address;

            targetMinecraftServerConnection = new TcpClient();

            Console.WriteLine(">> starting to listen on port " + port + " for your minercraft client.");

            StartListeningForSourceClients();
        }

        private void StartClientListener() {
            sourceMinecraftClientConnection = new TcpListener(IPAddress.Parse("127.0.0.1"), _port);
            sourceMinecraftClientConnection.Start();
        }

        private void StartListeningForSourceClients() {
            StartClientListener();

            _keepSourceClientListenerThreadRunning = true;
            sourceClientListenerThread = new Thread(ListenForSourceClient);
            sourceClientListenerThread.Start();
        }

        public void Restart() {
            if(_closing) {
                return;
            }
            DisconnectFromTarget();
            DisconnectFromSource();

            StartListeningForSourceClients();
        }
        public void Close() {
            _closing = true;
            DisconnectFromTarget();
            DisconnectFromSource();
        }

        private void ConnectToTarget() {
            if(_targetAddress != null) {

                try {
                    targetMinecraftServerConnection.Connect(_targetAddress, _port);
                } catch(Exception exception) {
                    Console.WriteLine(">> connection to target [" + _targetAddress.ToString() + "] failed > " + exception.Message);
                    return;
                }

                if(!targetMinecraftServerConnection.Connected) {
                    Console.WriteLine(">> lost connection to target client > " + _targetAddress.ToString());
                    return;
                }

                PurgeReadTargetThread();

                _keepReadTargetThreadRunning = true;
                readTargetThread = new Thread(ReadTarget);
                readTargetThread.Start();
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
                
                if(sourceMinecraftClientConnection.Pending()) {
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

                Thread.Sleep(10);
            }

            Console.WriteLine(">> stopped listening for source client.");
        }

        private void ReadSource() {

            while(_keepReadSourceThreadRunning) {
                if(sourceClient.Connected && sourceClient.GetStream().DataAvailable) {
                    
                    var stream = sourceClient.GetStream();
                    

                    if(targetMinecraftServerConnection.Connected) {
                        
                        byte[] buffer = new byte[sourceClient.Available];
                        stream.Read(buffer, 0, sourceClient.Available);

                        //do manipulations on buffer, probably have to wait for whole packet before returning
                        targetMinecraftServerConnection.GetStream().Write(buffer, 0, buffer.Length);
                    } 
                } else if(!sourceClient.Connected) {
                    Console.WriteLine(">> lost source client connection, restarting...");
                    Restart();
                }

                Thread.Sleep(10);
            }

            Console.WriteLine(">> stopped reading from source client.");
        }

        private void ReadTarget() {

            while(_keepReadTargetThreadRunning) {
                if(targetMinecraftServerConnection.Connected && targetMinecraftServerConnection.GetStream().DataAvailable) {

                    var stream = targetMinecraftServerConnection.GetStream();

                    if(sourceClient.Connected) {
                        byte[] buffer = new byte[targetMinecraftServerConnection.Available];
                        stream.Read(buffer, 0, targetMinecraftServerConnection.Available);

                        sourceClient.GetStream().Write(buffer, 0, buffer.Length);
                    }
                } else if(!targetMinecraftServerConnection.Connected) {
                    Console.WriteLine(">> lost target server connection, restarting...");
                    Restart();
                }

                Thread.Sleep(10);
            }

            Console.WriteLine(">> stopped reading from target server.");
        }
    }
}