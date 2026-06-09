using Fleck;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace CalibrationEnv
{
    internal class SessionManager
    {
        // adaptor handling communication with Resonite world
        private readonly ResoniteAdaptor? resoniteAdaptor;

        // connected clients collections, after connecting and messaging added to adaptors
        // to forward slot data responses to all connected clients
        private readonly ConcurrentDictionary<Guid, IWebSocketConnection> clients = new();
        private readonly ConcurrentDictionary<Guid, Adaptor> adaptors = new();

        // action queue to process seperately from client messages, e.g. for world model updates, to avoid blocking client message processing
        private readonly ConcurrentQueue<Func<Task>> actionQueue = new();

        // representation of world as received from ResoniteLink
        // parsed to a more convenient format for our clients
        private readonly WorldModel worldModel;

        public enum MessageType
        {
            Connect = 0,
            ResoniteData = 1,
            ClientData = 2
        }

        public SessionManager(bool usingResonite)
        {
            // create new world model
            worldModel = new WorldModel();

            if (usingResonite)
            {
                // create resonite adaptor,
                // which will handle communication with Resonite world and update world model accordingly
                resoniteAdaptor = new ResoniteAdaptor(worldModel);
            }
        }

        public async Task RunAsync(CancellationToken token)
        {
            // start up resonite adaptor to connect to Resonite world 
            if (resoniteAdaptor != null)
            {
                await resoniteAdaptor.StartAsync(token);
            }

            // prompt for client connection info 
            IPAddress clientIPAddress = default;
            while (!token.IsCancellationRequested)
            {
                Console.Write("Enter IP address for client: ");
                var input = Console.ReadLine();

                if (IPAddress.TryParse(input, out var parsedIp))
                {
                    clientIPAddress = parsedIp;
                    break;
                }

                Console.WriteLine("Invalid IP address.\n");
            }

            uint clientPort = 0;
            while (!token.IsCancellationRequested)
            {
                Console.Write("Enter port number for client connections: ");
                var input = Console.ReadLine();

                if (uint.TryParse(input, out var parsedPort) && parsedPort <= 65535)
                {
                    clientPort = parsedPort;
                    break;
                }

                Console.WriteLine("Invalid port number.\n");
            }

            // start fleck websocket server to clients
            // clients will subscribe first via port, then send connect msg with client type,
            // after subscribing, they will send/receive world update messages
            var server = new WebSocketServer($"ws://{clientIPAddress}:{clientPort}");
            server.Start(socket =>
            {
                var id = socket.ConnectionInfo.Id;

                // on connection open, connect client
                socket.OnOpen = () =>
                    { actionQueue.Enqueue(async () => HandleConnect(socket)); };

                // on connection close, disconnect client
                socket.OnClose = () =>
                    { actionQueue.Enqueue(async () => HandleDisconnect(socket)); };

                // on msg received, process msg 
                socket.OnMessage = msg =>
                    { actionQueue.Enqueue(async () => await HandleMessage(socket, msg, token)); };
            });

            Console.WriteLine($"WebSocket server started on ws://{clientIPAddress}:{clientPort}");

            // process actions from queue while running
            while (!token.IsCancellationRequested)
            {
                while (actionQueue.TryDequeue(out var action))
                {
                    await action();
                }

                await Task.Delay(1, token);
            }

            // token cancellation requested, shutting down 
            Console.WriteLine("Shutting down...");

            if (resoniteAdaptor != null)
            {
                await resoniteAdaptor.EndAsync();
            }

            var snapshot = adaptors.Values.ToList();
            foreach (var adaptor in snapshot)
            {
                await adaptor.EndAsync();
            }
        }

        private void HandleConnect(IWebSocketConnection socket)
        {
            var id = socket.ConnectionInfo.Id;

            // attempt to add client to clients 
            if (clients.TryAdd(id, socket))
            {
                Console.WriteLine($"Client {id} connected");
            }
            else
            {
                Console.WriteLine($"Client {id} couldn't connect");
            }
        }

        private void HandleDisconnect(IWebSocketConnection socket)
        {
            var id = socket.ConnectionInfo.Id;

            // remove clients 
            clients.TryRemove(id, out _);

            // try to remove matching adaptor, and if found,
            // remove all data related to client from world model
            if (adaptors.TryRemove(id, out var adaptor))
            {
                worldModel.RemoveAllFor(adaptor.Id);
                Console.WriteLine($"Client {id} disconnected");
            }
            else
            {
                Console.WriteLine($"Client {id} disconnected. But data not properly removed, didn't find adaptor");
            }
        }

        private async Task HandleMessage(IWebSocketConnection socket, string msg, CancellationToken token)
        {
            var id = socket.ConnectionInfo.Id;

            // check properly connected client
            if (!clients.ContainsKey(id))
            {
                Console.WriteLine($"Msg from {id} not processed - client not properly opened yet");
                return;
            }

            // interpret msg as json
            using var doc = JsonDocument.Parse(msg);
            var root = doc.RootElement;

            // check msgType and process accordingly
            if (!root.TryGetProperty("msgType", out var msgType))
            {
                Console.WriteLine($"Msg from {id} invalid - does not contain 'msgType'. \nMessage: {root.ToString()}");
                return;
            }
            if (!Enum.IsDefined(typeof(MessageType), msgType.GetInt32()))
            {
                Console.WriteLine($"Msg from {id} invalid - unknown message type {msgType.GetInt32()}");
                return;
            }

            var type = (MessageType)msgType.GetInt32();
            switch (type)
            {
                // for connect msg, create adaptor and start it 
                case MessageType.Connect:
                    var adaptor = ProcessConnectMsg(socket, root);
                    if (adaptor != null)
                        await adaptor.StartAsync(token);
                    break;

                // for data msgs (from resonite and clients), process msg and forward to adaptor to handle
                case MessageType.ResoniteData:
                case MessageType.ClientData:
                    ProcessMsg(socket, root);
                    break;

                // for all other, unrecognized msg types, log and ignore
                default:
                    Console.WriteLine($"Msg from {id} invalid - unrecognized msgType {msgType}");
                    break;
            }
        }


        /// <summary>
        /// Processes a connect message from a client, creating an adapator for the client.
        /// </summary>
        /// <param name="socket">The WebSocket connection of the client.</param>
        /// <param name="msgRoot">The root element of the JSON message.</param>
        /// <returns>The created ClientAdaptor if successful, otherwise null.</returns>
        private Adaptor? ProcessConnectMsg(IWebSocketConnection socket, JsonElement msgRoot)
        {
            // check if correct message format
            if (!msgRoot.TryGetProperty("clientType", out var clientType))
            {
                Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} invalid - does not contain 'clientType'. \nMessage: {msgRoot}");
                return null;
            }

            // check for supported client type
            if (clientType.GetString()?.ToLowerInvariant() is not string clientTypeStr)
            {
                Console.WriteLine($"Connect msg invalid - clientType null");
                return null;
            }

            // check for provided interval ms setting
            msgRoot.TryGetProperty("sendRate", out var sendIntervalProp);
            if (!sendIntervalProp.TryGetInt32(out var sendInterval))
            {
                sendInterval = 33; // default to ~30 fps
            }

			// create adaptor
			Adaptor? adaptor = null;
            switch (clientTypeStr)
            {
                case "unity":
                case "unreal":
                case "python":
                case "godot":
                    adaptor = new ClientAdaptor(worldModel, socket, clientTypeStr, sendInterval);
                    break;
                default:
                    Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} not succesfully processed - clientType {clientTypeStr} isn't supported");
                    break;
            }

            // failed to create adaptor for client type, msg and return
            if (adaptor == null)
            {
                Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} not succesfully processed - failed to create adaptor for clientType {clientTypeStr}");
                return null;
            }

            // try to add adaptor to adaptors collection
            if (adaptors.TryAdd(socket.ConnectionInfo.Id, adaptor))
            {
                Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} processed, adaptor created");
                return adaptor;
            }
            else
            {
                Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} not succesfully processed - adaptor for client with id {socket.ConnectionInfo.Id} already subscribed");
                return null;
            }
        }

        /// <summary>
        /// Processes a data message by forwarding it to the associated adaptor for handling.
        /// </summary>
        /// <remarks>If no adaptor exists for the specified WebSocket connection, the message is not
        /// processed and the method returns false.</remarks>
        /// <param name="socket">The WebSocket connection from which the message was received.</param>
        /// <param name="msgRoot">The root element of the JSON message.</param>
        /// <returns>true if the message was successfully forwarded to an adaptor; otherwise, false.</returns>
        private bool ProcessMsg(IWebSocketConnection socket, JsonElement msgRoot)
        {
            // check if adaptor exists for socket
            if (!adaptors.TryGetValue(socket.ConnectionInfo.Id, out Adaptor? adaptor))
            {
                // return failuse and log 
                Console.WriteLine($"Data msg from {socket.ConnectionInfo.Id} not processed - no adaptor created for client");
                return false;
            }

            // forward msg to adaptor matching the socket,
            // handle and process msg 
            adaptor.Receive(msgRoot);

            // return success
            return true;
        }
    }
}