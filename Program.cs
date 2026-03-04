using ResoniteLink;
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
        static async Task Main(string[] args)
        {
            // prompt portnumber resonite world
            Console.Write("Enter port number: ");
            string? input = Console.ReadLine();

            if (!uint.TryParse(input, out uint portNumber))
            {
                Console.WriteLine("Invalid port number.");
                return;
            }

            // open socket connection to resonite world
            using var socket = new ClientWebSocket();
            var uri = new Uri($"ws://localhost:{portNumber}");
            await socket.ConnectAsync(uri, CancellationToken.None);

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
            var buffer = new byte[4096];
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
        }
    }
}