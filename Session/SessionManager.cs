using Fleck;
using System.Text.Json;

namespace CalibrationEnv
{
    internal class SessionManager
    {
        // port to clients, e.g. Unity, Unreal, ...
        private int clientPort = 4196;

        // adaptor handling communication with Resonite world
        private ResoniteAdaptor resoniteAdaptor;

        // connected clients collections, after connecting and messaging added to adaptors
        // to forward slot data responses to all connected clients
        private List<IWebSocketConnection> clients = new List<IWebSocketConnection>();
        private readonly object clientsLock = new object();
        private Dictionary<Guid, Adaptor> adaptors = new Dictionary<Guid, Adaptor>();

        // representation of world as received from ResoniteLink
        // parsed to a more convenient format for our clients
        private readonly WorldModel worldModel;

        public SessionManager()
        {
            // create new world model
            worldModel = new WorldModel();

            // create resonite adaptor,
            // which will handle communication with Resonite world and update world model accordingly
            resoniteAdaptor = new ResoniteAdaptor(worldModel, null);

            // start updating
            _ = Update();
        }

        private async Task Update()
        {
            // start fleck websocket server to clients
            // clients will subscribe first via port, then send connect msg with client type,
            // after subscribing, they will receive world data messages
            var server = new WebSocketServer($"ws://0.0.0.0:{clientPort}");
            server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    lock (clientsLock)
                    {
                        clients.Add(socket);
                    }

                    Console.WriteLine($"Client {socket.ConnectionInfo.Id} connected");
                };

                socket.OnClose = () =>
                {
                    lock (clientsLock)
                    {
                        clients.Remove(socket);
                    }

                    Console.WriteLine($"Client {socket.ConnectionInfo.Id} disconnected");
                };

                socket.OnMessage = msg =>
                {
                    Console.WriteLine($"Received from client {socket.ConnectionInfo.Id}: {msg}");

                    // check if connection actually opened
                    if (!clients.Contains(socket))
                    {
                        Console.WriteLine($"Msg from {socket.ConnectionInfo.Id} not processed - client not properly opened yet");
                        return;
                    }

                    // interpret msg as json
                    using var doc = JsonDocument.Parse(msg);
                    var root = doc.RootElement;

                    // 0 = connect msg, 1 = data msg
                    if (root.TryGetProperty("msgType", out var msgType))
                    {
                        switch (msgType.GetInt32())
                        {
                            case 0:
                                ProcessConnectMsg(socket, root);
                                break;

                            case 1:
                                ProcessDataMsg(socket, root);
                                break;

                            default:
                                Console.WriteLine($"Msg from {socket.ConnectionInfo.Id} invalid - unrecognized msgType {msgType}");
                                break;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"Msg from {socket.ConnectionInfo.Id} invalid - does not contain 'msgType'. \nMessage: {root.ToString()}");
                    }
                };
            });

            Console.WriteLine($"WebSocket server started on ws://0.0.0.0:{clientPort}");
        }

        private bool ProcessConnectMsg(IWebSocketConnection socket, JsonElement msgRoot)
        {
            // check if already created adaptor for socket
            if (adaptors.ContainsKey(socket.ConnectionInfo.Id))
            {
                Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} not processed - client already subscribed");
                return false;
            }

            // check if correct message format
            if (!msgRoot.TryGetProperty("clientType", out var clientType))
            {
                Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} invalid - does not contain 'clientType'. \nMessage: {msgRoot.ToString()}");
                return false;
            }

            // create and assign adaptor
            Adaptor? adaptor = null;
            switch (clientType.GetString()?.ToLower())
            {
                case "unity":
                case "unreal":
                    adaptor = new Adaptor(worldModel, socket);
                    break;

                default:
                    Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} not succesfully processed - clientType {clientType} isn't supported");
                    break;
            }

            // if succesfully created, add to adaptors
            if (adaptor != null)
            {
                adaptors.Add(socket.ConnectionInfo.Id, adaptor);
                Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} processed, adaptor created");

                return true;
            }

            return false;
        }

        private bool ProcessDataMsg(IWebSocketConnection socket, JsonElement msgRoot)
        {
            // check if adaptor exists for socket
            if (!adaptors.ContainsKey(socket.ConnectionInfo.Id))
            {
                Console.WriteLine($"Data msg from {socket.ConnectionInfo.Id} not processed - no adaptor created for client");
                return false;
            }

            // forward data msg to adaptor to process 
            adaptors[socket.ConnectionInfo.Id].Receive(msgRoot);

            return false;
        }
    }
}
