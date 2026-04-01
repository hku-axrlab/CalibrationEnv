using Fleck;
using ResoniteLink;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CalibrationEnv
{
    internal class SessionManager
    {
        // port to ResoniteLink, obtain from Resonite and input during prompt
        private uint resonitePort = 0;

        // port to clients, e.g. Unity, Unreal, ...
        private int clientPort = 4196;

        // websocket connection to Resonite world
        private ClientWebSocket resoniteSocket = new ClientWebSocket();
        private readonly SemaphoreSlim resoniteSocketLock = new SemaphoreSlim(1, 1);

        // pending requests collection, to match responses with requests using message ID
        private readonly Dictionary<string, TaskCompletionSource<string>> pendingRequests = new Dictionary<string, TaskCompletionSource<string>>();
        private readonly object pendingLock = new object();

        // connected clients collections, after connecting and messaging added to adaptors
        // to forward slot data responses to all connected clients
        private List<IWebSocketConnection> clients = new List<IWebSocketConnection>();
        private readonly object clientsLock = new object();
        private Dictionary<Guid, Adaptor> adaptors = new Dictionary<Guid, Adaptor>();

        // registered slot collection, to keep track of discovered slot ID's from Root slot,
        // and request data on those in batch, only interested in tagged slots
        private List<string> registeredSlots = new List<string>();
        private readonly object slotsLock = new object();

        // representation of world as received from ResoniteLink
        // parsed to a more convenient format for our clients
        private readonly WorldModel worldModel;

        public SessionManager()
        {
            // create new world model
            worldModel = new WorldModel();
        }

        public async Task Main()
        {
            // start fleck websocket server to client
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
                        Console.WriteLine($"Connect msg from {socket.ConnectionInfo.Id} not processed - client not properly opened yet");
                        return;
                    }

                    // msg format connect: 
                    // {
                    //     "msgType" = 0
                    //     "clientType" = "unity"|"unreal"
                    // }

                    // msg format data: 
                    // {
                    //     "msgType" = 1
                    //     <idk>
                    // }

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

            // prompt for port to Resonite world
            Console.Write("Enter port number Resonite world: ");
            string? input = Console.ReadLine();
            if (!uint.TryParse(input, out resonitePort))
            {
                Console.WriteLine("Invalid port number.");
            }

            // open socket connection to Resonite world
            resoniteSocket = new ClientWebSocket();
            await resoniteSocket.ConnectAsync(new Uri($"ws://localhost:{resonitePort}"), CancellationToken.None);
            Console.WriteLine("Connected to Resonite world!");

            // start tasks to get, process and forward Root and childern slots
            await Task.WhenAll(ReceiveLoop(), GetRootLoop(), GetChildernLoop());
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
                    adaptor = new UnityAdaptor(worldModel, socket);
                    break;

                case "unreal":
                    adaptor = new UnrealAdaptor(worldModel, socket);
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


            return false;
        }

        private async Task ReceiveLoop()
        {
            var buffer = new byte[8192];

            while (true)
            {
                // receive full message from Resonite world, which might come in multiple frames,
                // and combine to single string msg
                var segment = new ArraySegment<byte>(buffer);
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await resoniteSocket.ReceiveAsync(segment, CancellationToken.None);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                string msg = Encoding.UTF8.GetString(ms.ToArray());

                // extract message ID from JSON and add to pending requests
                string? msgId = ExtractMessageID(msg);
                TaskCompletionSource<string>? tcs = null;

                lock (pendingLock)
                {
                    if (msgId != null && pendingRequests.TryGetValue(msgId, out tcs))
                    {
                        pendingRequests.Remove(msgId);
                    }
                    else
                    {
                        Console.WriteLine("Received untracked message: " + msg);
                    }
                }

                if (tcs != null)
                {
                    tcs.SetResult(msg);
                }
            }
        }

        private string? ExtractMessageID(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("sourceMessageId", out var idProp))
                    return idProp.GetString();
                return null;
            }
            catch
            {
                return null;
            }
        }

        private async Task<string> SendRequestAsync(Message msg, string type)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pendingLock)
            {
                pendingRequests[msg.MessageID] = tcs;
            }

            byte[] bytes = GetMsgAsByteArray(msg, type);

            await resoniteSocketLock.WaitAsync();
            try
            {
                await resoniteSocket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                resoniteSocketLock.Release();
            }

            // wait for response with timeout
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(10000));

            if (completed != tcs.Task)
            {
                lock (pendingLock)
                {
                    pendingRequests.Remove(msg.MessageID);
                }

                throw new TimeoutException("No response received");
            }

            return await tcs.Task;
        }

        private GetSlot BuildGetSlotMsg(string slotID, bool includeComponentData)
        {
            // build message to get slot with give ID
            return new GetSlot
            {
                MessageID = Guid.NewGuid().ToString(),
                SlotID = slotID,
                IncludeComponentData = includeComponentData,
                Depth = 0
            };
        }

        private byte[] GetMsgAsByteArray(Message msg, string type)
        {
            // serialize message to json 
            var jsonNode = JsonSerializer.SerializeToNode(msg)!;
            jsonNode["$type"] = type;
            string json = jsonNode.ToJsonString();

            // return byte arry 
            return Encoding.UTF8.GetBytes(json);
        }

        private bool CheckJSONResponse(JsonElement root, string expectedMsgType)
        {
            // check if response has expected type, success is true and no error info, otherwise log error and return false
            var responseType = root.GetProperty("$type").GetString();
            var succes = root.GetProperty("success").GetBoolean();
            var errorInfo = root.GetProperty("errorInfo").GetString();
            if (responseType != expectedMsgType || !succes || !string.IsNullOrEmpty(errorInfo))
            {
                Console.WriteLine($"Error in response, {responseType}: {errorInfo} - succes {succes}");
                return false;
            }

            return true;
        }

        #region ROOT_SLOT
        private async Task GetRootLoop()
        {
            while (true)
            {
                // get Root slot data 
                var msg = BuildGetSlotMsg("Root", false);
                string response = await SendRequestAsync(msg, "getSlot");
                //Console.WriteLine("\nReceived Root response: " + response);

                // process response to extract child ID's and add to registered slots collection,
                // which will be used to request childern slot data in batch
                lock (slotsLock)
                {
                    ProcessGetRootResponse(response);
                }

                // wait 
                await Task.Delay(rootMsgInterval);
            }
        }

        private void ProcessGetRootResponse(string response)
        {
            // get response as JSONElement 
            var jsonRoot = JsonDocument.Parse(response).RootElement;

            // error handling 
            if (!CheckJSONResponse(jsonRoot, "slotData"))
                return;

            Console.WriteLine("\nReceived Root response with ID: " + jsonRoot.GetProperty("sourceMessageId"));

            // get core data 
            var data = jsonRoot.GetProperty("data");

            // get ID's from all children, 
            // add to collection if not yet discovered
            var children = data.GetProperty("children").EnumerateArray();

            foreach (var child in children)
            {
                // add child id to registered slots
                // but only interesseted in tagged childern
                var childID = child.GetProperty("id").GetString()!;
                var childTag = child.GetProperty("tag").GetProperty("value").GetString();

                if (string.IsNullOrEmpty(childTag))
                    continue;

                if (!registeredSlots.Contains(childID))
                    registeredSlots.Add(childID);
            }
        }
        #endregion

        #region CHILD_SLOTS
        private async Task GetChildernLoop()
        {
            while (true)
            {
                // get a copy of the current registered slots to do batch operation on
                List<string> snapshotRegisteredSlots;
                lock (slotsLock)
                {
                    snapshotRegisteredSlots = registeredSlots.ToList();
                }

                // do batch operation: get slot on all registered id's
                var batchMsg = new DataModelOperationBatch();
                batchMsg.MessageID = Guid.NewGuid().ToString();
                batchMsg.Operations = new List<Message>();
                foreach (var childID in snapshotRegisteredSlots)
                {
                    batchMsg.Operations.Add(BuildGetSlotMsg(childID, true));
                }

                // wait to receive response
                string response = await SendRequestAsync(batchMsg, "dataModelOperationBatch");
                //Console.WriteLine("\nReceived Batch children response: " + response);

                var jsonRoot = JsonDocument.Parse(response).RootElement;
                Console.WriteLine("\nReceived Batch children response with ID: " + jsonRoot.GetProperty("sourceMessageId"));

                // TODO: call process data

                // forward to clients

                // take snapshot of clients to forward to, to avoid locking during send operations 
                List<IWebSocketConnection> snapshotClients;
                lock (clientsLock)
                {
                    snapshotClients = clients.ToList();
                }

                // forward response to all connected clients,
                // if error sending to a client, schedule it for removal as it might be disconnected
                List<IWebSocketConnection> toRemoveClients = new List<IWebSocketConnection>();
                foreach (var client in snapshotClients)
                {
                    try
                    {
                        if (client.IsAvailable)
                        {
                            await client.Send(response);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error sending message to client, scheduling removal: {ex.Message}");
                        toRemoveClients.Add(client);
                    }
                }

                // remove any clients that had errors during send, as they might be / are probably disconnected
                if (toRemoveClients.Count > 0)
                {
                    lock (clientsLock)
                    {
                        foreach (var client in toRemoveClients)
                        {
                            clients.Remove(client);
                        }
                    }
                }

                // wait
                await Task.Delay(childMsgInterval);
            }
        }

        private void ProcessGetChildResponse(string response)
        {
            // get to core data from msg
            var jsonRoot = JsonDocument.Parse(response).RootElement;
            var responses = jsonRoot.GetProperty("responses");

            // error handling 
            if (!CheckJSONResponse(jsonRoot, "batchResponse"))
                return;

            // TODO: see if child was removed from scene, if so remove from collection
            // TODO: idk what else we wanna check here? 
        }
        #endregion

    }
}
