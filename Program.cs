using Fleck;
using ResoniteLink;
using System.Diagnostics;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CalibrationEnv
{
    public class Program
    {
        // TODO: can I use the same socket on both tasks? 
        private static ClientWebSocket socket = new ClientWebSocket();
        private static List<IWebSocketConnection> clients = new List<IWebSocketConnection>();

        private static uint resonitePort = 0;
        private static int clientPort = 4196;

        private static int rootMsgInterval = 1000;
        private static int childMsgInterval = 17;

        private static List<string> registeredSlots = new List<string>();
        private static readonly object slotsLock = new object();

        public static async Task Main(string[] args)
        {
            // start fleck websocket server
            var server = new WebSocketServer($"ws://0.0.0.0:{clientPort}");
            server.Start(socket =>
            {
                socket.OnOpen = () =>
                {
                    clients.Add(socket);
                    Console.WriteLine("Unity client connected");
                };

                socket.OnClose = () =>
                {
                    clients.Remove(socket);
                    Console.WriteLine("Unity client disconnected");
                };

                socket.OnMessage = msg =>
                {
                    Console.WriteLine("Received from Unity: " + msg);
                    // TODO: respond to Unity msg
                };
            });

            Console.WriteLine($"WebSocket server started on ws://0.0.0.0:{clientPort}");

            // prompt for port resonite
            Console.Write("Enter port number Resonite world: ");
            string? input = Console.ReadLine();
            if (!uint.TryParse(input, out resonitePort))
            {
                Console.WriteLine("Invalid port number.");
            }

            // open socket connection to resonite world
            socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://localhost:{resonitePort}"), CancellationToken.None);
            Console.WriteLine("Connected to Resonite world!");

            // start loop to get Root slot and childern slots
            _ = GetRootLoop();
            _ = GetChildernLoop();

            // TODO: lol i dont wanna wait here for no reason
            // but like otherwise the app shuts down (:
            while (true)
            {
                await Task.Delay(10000);
            }
        }

        private static async Task GetRootLoop()
        {
            while (true)
            {
                // send msg to get Root 
                await socket.SendAsync(
                    GetMsgAsByteArray(BuildGetSlotMsg("Root", false), "getSlot"),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                );

                //Console.WriteLine("Message GetSlot, Root sent to the server.");

                // @Aaron: I would like this to be a reusable fucntion... 
                // But idk how to make something async that returns a string/has an out param. 

                // receive response
                var buffer = new byte[8192];
                var segment = new ArraySegment<byte>(buffer);
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(segment, default);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                string response = Encoding.UTF8.GetString(ms.ToArray());
                //Console.WriteLine($"Received from Resonite: {response}");

                // process, adds to collection used in multiple tasks
                // so lock for thread safety
                lock (slotsLock)
                {
                    ProcessGetRootResponse(response);
                }

                // forward to clients
                // TODO: don't forward this, forward the children responses
                // so remove soon
                /*foreach (var client in clients.ToList())
                {
                    if (client.IsAvailable)
                        await client.Send(response);
                }*/

                // wait
                await Task.Delay(rootMsgInterval);
            }
        }

        private static async Task GetChildernLoop()
        {
            while (true)
            {
                // do batch operation: get slot on all registered id's
                DataModelOperationBatch batchMsg = new DataModelOperationBatch();
                batchMsg.Operations = new List<Message>();
                foreach (var childID in registeredSlots)
                {
                    batchMsg.Operations.Add(BuildGetSlotMsg(childID, true));
                }

                // send msg
                await socket.SendAsync(
                    GetMsgAsByteArray(batchMsg, "dataModelOperationBatch"),
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                );

                // receive response
                var buffer = new byte[8192];
                var segment = new ArraySegment<byte>(buffer);
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(segment, default);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                string response = Encoding.UTF8.GetString(ms.ToArray());
                Console.WriteLine($"Received from Resonite: {response}");

                //lock (slotsLock)
                //{
                    // TODO: check if slot was removed from scene, if so remove from collection
                    //ProcessGetChildResponse(response);
                //}

                // forward to clients
                foreach (var client in clients.ToList())
                {
                    if (client.IsAvailable)
                        await client.Send(response);
                }

                // wait 
                await Task.Delay(childMsgInterval);
            }
        }

        private static GetSlot BuildGetSlotMsg(string slotID, bool includeComponentData)
        {
            // build message to Get Root slot
            return new GetSlot
            {
                MessageID = Guid.NewGuid().ToString(),
                SlotID = slotID,
                IncludeComponentData = includeComponentData,
                Depth = 0
            };
        }

        private static byte[] GetMsgAsByteArray(Message msg, string type)
        {
            // serialize message to json 
            var jsonNode = JsonSerializer.SerializeToNode(msg)!;
            jsonNode["$type"] = type;
            string json = jsonNode.ToJsonString();

            // return byte arry 
            return Encoding.UTF8.GetBytes(json);
        }

        private static void ProcessGetRootResponse(string response)
        {
            // get response as JSONElement 
            var jsonRoot = JsonDocument.Parse(response).RootElement;

            // error handling 
            if (!CheckJSONResponse(jsonRoot, "slotData"))
                return;

            // get core data 
            var data = jsonRoot.GetProperty("data");

            // get ID's from all children, 
            // add to collection if not yet discovered
            var children = data.GetProperty("children").EnumerateArray();

            foreach (var child in children)
            {
                // TODO: only consider relevant tags 

                var childID = child.GetProperty("id").GetString()!;
                var childTag = child.GetProperty("tag").GetProperty("value").GetString();

                // only interesseted in tagged childern
                if (string.IsNullOrEmpty(childTag))
                    continue;

                if (!registeredSlots.Contains(childID))
                    registeredSlots.Add(childID);
            }
        }

        private static void ProcessGetChildResponse(string response)
        {
            // get to core data from msg
            var jsonRoot = JsonDocument.Parse(response).RootElement;
            var responses = jsonRoot.GetProperty("responses");

            // error handling 
            if (!CheckJSONResponse(jsonRoot, "batchResponse"))
                return;

            // TODO: see if child was removed from scene, if so remove from collection
            // TODO: idk what else we wanna check here? just send it to the clients?
        }

        private static bool CheckJSONResponse(JsonElement root, string expectedMsgType)
        {
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
    }
}