using ResoniteLink;
using Fleck;
using System.Text;
using System.Text.Json;
using System.Net.WebSockets;

namespace CalibrationEnv
{
    public class Program
    {
        private static List<IWebSocketConnection> clients = new List<IWebSocketConnection>();

        private static uint resonitePort = 0;
        private static int clientPort = 4196;

        private static int msgInterval = 1000;

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
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://localhost:{resonitePort}"), CancellationToken.None);
            Console.WriteLine("Connected to Resonite world!");

            // request data from resonite and broadcast to clients
            while (true)
            {
                // wait if there aren't any clients
                //if (clients.Count <= 0)
                //    continue;

                // build message to Get Root slot
                var messageObject = new GetSlot
                {
                    MessageID = Guid.NewGuid().ToString(),
                    SlotID = "Root",
                    IncludeComponentData = false,
                    Depth = 0
                };

                // serialize message to json and send to resonite world
                var jsonNode = JsonSerializer.SerializeToNode(messageObject)!;
                jsonNode["$type"] = "getSlot";
                string json = jsonNode.ToJsonString();
                var bytes = Encoding.UTF8.GetBytes(json);

                Console.WriteLine("Sending message: " + json);

                await socket.SendAsync(
                    bytes,
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None
                );

                Console.WriteLine("Message sent to the server.");

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

                // forward to clients
                foreach (var client in clients.ToList())
                {
                    if (client.IsAvailable)
                        client.Send(response);
                }

                // wait out interval time
                await Task.Delay(msgInterval); 
            }
        }
    }
}