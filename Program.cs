using ResoniteLink;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace CalibrationEnv
{
    public class Program
    {
        private static List<WebSocket> clients = new List<WebSocket>();

        public static uint resonitePort = 0;
        public static int clientPort = 5678;

        public static async Task Main(string[] args)
        {
            // start server in background
            _ = Task.Run(async () =>
            {
                var listener = new TcpListener(IPAddress.Loopback, clientPort);
                listener.Start();

                Console.WriteLine($"WebSocket server started on ws://localhost:{clientPort}/ws/");

                while (true)
                {
                    var tcpClient = await listener.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        using var ws = WebSocket.CreateFromStream(tcpClient.GetStream(), true, null, TimeSpan.FromSeconds(30));
                        clients.Add(ws);
                        Console.WriteLine("Unity client connected");

                        var buffer = new byte[4096];
                        try
                        {
                            while (ws.State == WebSocketState.Open)
                            {
                                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                                if (result.MessageType == WebSocketMessageType.Close)
                                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                            }
                        }
                        catch { }
                        finally
                        {
                            clients.Remove(ws);
                            Console.WriteLine("Unity client disconnected");
                        }
                    });
                }
            });

            // prompt for port resonite
            Console.Write("Enter port number Resonite world: ");
            string? input = Console.ReadLine();
            if (!uint.TryParse(input, out resonitePort))
            {
                Console.WriteLine("Invalid port number.");
            }

            // open socket connection to resonite world
            using var socket = new ClientWebSocket();
            var uri = new Uri($"ws://localhost:{resonitePort}");
            await socket.ConnectAsync(uri, CancellationToken.None);

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
                var message = new ArraySegment<byte>(buffer);
                using var ms = new MemoryStream();

                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(message, CancellationToken.None);
                    ms.Write(message.Array!, message.Offset, result.Count);
                } while (!result.EndOfMessage);

                string response = Encoding.UTF8.GetString(ms.ToArray());
                Console.WriteLine($"Received: {response}");

                // forward response
                var forwardingBytes = Encoding.UTF8.GetBytes(response);

                foreach (var client in clients.ToList())
                {
                    if (client.State == WebSocketState.Open)
                        await client.SendAsync(forwardingBytes, WebSocketMessageType.Text, true, CancellationToken.None);
                }

                await Task.Delay(1000);
            }
        }
    }
}