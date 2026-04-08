using Fleck;
using System.Collections;
using System.Collections.Concurrent;
using System.Text.Json;

namespace CalibrationEnv
{
    internal class SessionManager
    {
        // port to clients, e.g. Unity, Unreal, ...
        private readonly int clientPort = 4196;

        // adaptor handling communication with Resonite world
        private readonly ResoniteAdaptor resoniteAdaptor;

        // connected clients collections, after connecting and messaging added to adaptors
        // to forward slot data responses to all connected clients
        private readonly List<IWebSocketConnection> clients = [];
        private readonly ConcurrentDictionary<Guid, Adaptor> adaptors = new();

        // action queue to process seperately from client messages, e.g. for world model updates, to avoid blocking client message processing
        private readonly ConcurrentQueue<Action> actionQueue = new();

        // representation of world as received from ResoniteLink
        // parsed to a more convenient format for our clients
        private readonly WorldModel worldModel;

        public enum MessageType
        {
            Connect = 0,
            ResoniteData = 1,
            ClientData = 2
        }

        public SessionManager()
        {
            // create new world model
            worldModel = new WorldModel();

            // create resonite adaptor,
            // which will handle communication with Resonite world and update world model accordingly
            resoniteAdaptor = new ResoniteAdaptor(worldModel, null);
        }

        public async Task RunAsync(CancellationToken token)
        {
            // start fleck websocket server to clients
            // clients will subscribe first via port, then send connect msg with client type,
            // after subscribing, they will receive world data messages
            var server = new WebSocketServer($"ws://0.0.0.0:{clientPort}");
            server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    actionQueue.Enqueue(() =>
                    {
                        clients.Add(socket);
                        Console.WriteLine($"Client {socket.ConnectionInfo.Id} connected");
                    });
                };

                socket.OnClose = () =>
                {
                    actionQueue.Enqueue(() =>
                    {
                        clients.Remove(socket);
                        Console.WriteLine($"Client {socket.ConnectionInfo.Id} disconnected");
                    });
                };

                socket.OnMessage = msg =>
                {
                    actionQueue.Enqueue(() =>
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

                        // check msgType and process accordingly
                        if (!root.TryGetProperty("msgType", out var msgType))
                        {
                            Console.WriteLine($"Msg from {socket.ConnectionInfo.Id} invalid - does not contain 'msgType'. \nMessage: {root.ToString()}");
                            return;
                        }

                        if (!Enum.IsDefined(typeof(MessageType), msgType.GetInt32()))
                        {
                            Console.WriteLine($"Msg from {socket.ConnectionInfo.Id} invalid - unknown message type {msgType.GetInt32()}");
                            return;
                        }

                        var type = (MessageType)msgType.GetInt32();
                        switch (type)
                        {
                            case MessageType.Connect:
                                ProcessConnectMsg(socket, root);
                                break;

                            case MessageType.ResoniteData:
                                ProcessDataMsg(socket, root);
                                break;

                            case MessageType.ClientData:
                                //ProcessDataMsg(socket, root);
                                break;

                            default:
                                Console.WriteLine($"Msg from {socket.ConnectionInfo.Id} invalid - unrecognized msgType {msgType}");
                                break;
                        }

                    });
                };
            });

            Console.WriteLine($"WebSocket server started on ws://0.0.0.0:{clientPort}");

            // process actions from queue until cancellation requested
            while (!token.IsCancellationRequested)
            {
                while (actionQueue.TryDequeue(out var action))
                {
                    action();
                }

                await Task.Delay(1, token);
            }
        }

        private bool ProcessConnectMsg(IWebSocketConnection socket, JsonElement msgRoot)
        {
            // check if correct message format
            if (!msgRoot.TryGetProperty("clientType", out var clientType))
            {
                Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} invalid - does not contain 'clientType'. \nMessage: {msgRoot.ToString()}");
                return false;
            }

            // check for client type
            if (clientType.GetString()?.ToLowerInvariant() is not string clientTypeStr)
            {
                Console.WriteLine($"Connect msg invalid - clientType null");
                return false;
            }

            // create adaptor
            Adaptor? adaptor = null;
            switch (clientTypeStr)
            {
                case "unity":
                case "unreal":
                    adaptor = new Adaptor(worldModel, socket);
                    break;

                default:
                    Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} not succesfully processed - clientType {clientTypeStr} isn't supported");
                    break;
            }

            // failed to create adaptor for client type, msg and return
            if (adaptor == null)
            {
                Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} not processed - failed to create adaptor for clientType {clientTypeStr}");
                return false;
            }

            // try to add adaptor to adaptors collection
            if (adaptors.TryAdd(socket.ConnectionInfo.Id, adaptor))
            {
                Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} processed, adaptor created");
                return true;
            }
            else
            {
                Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} not processed - adaptor for client already subscribed");
                return false;
            }
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