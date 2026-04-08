using Fleck;
using ResoniteLink;
using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CalibrationEnv
{
    internal class ResoniteAdaptor : Adaptor
    {
        private new ClientWebSocket socket;
        private readonly SemaphoreSlim socketLock = new SemaphoreSlim(1, 1);
        
        // pending requests collection, to match responses with requests using message ID
        private readonly Dictionary<string, TaskCompletionSource<string>> pendingRequests = new Dictionary<string, TaskCompletionSource<string>>();
        private readonly object pendingLock = new object();

        // registered slot collection, to keep track of discovered slot ID's from Root slot,
        // and request data on those in batch, only interested in tagged slots
        private List<string> registeredSlots = new List<string>();
        private readonly object slotsLock = new object();

        public ResoniteAdaptor(WorldModel worldModel, IWebSocketConnection? socket) : base(worldModel, socket)
        {
            this.socket = new ClientWebSocket();

            // start updating
            _ = Send();
        }

        public override void Receive(JsonElement msgRoot)
        {
            worldModel.ApplyUpdate(WorldUpdateSource.Resonite, msgRoot);
        }

        protected override async Task Send()
        {
            // prompt for port to Resonite world
            Console.Write("Enter port number Resonite world: ");
            string? input = Console.ReadLine();
            if (!uint.TryParse(input, out uint resonitePort))
            {
                Console.WriteLine("Invalid port number.");
            }

            // open socket connection to Resonite world
            await socket.ConnectAsync(new Uri($"ws://localhost:{resonitePort}"), CancellationToken.None);
            Console.WriteLine("Connected to Resonite world!");

            // start tasks to get, process and forward Root and childern slots
            await Task.WhenAll(ReceiveLoop(), GetRootLoop(), GetChildernLoop());
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
                    result = await socket.ReceiveAsync(segment, CancellationToken.None);
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

        private async Task GetRootLoop()
        {
            while (true)
            {
                // get Root slot data 
                var msg = BuildGetSlotMsg("Root", false);
                string response = await SendRequestAsync(msg, "getSlot");

                // process response to extract child ID's and add to registered slots collection,
                // which will be used to request childern slot data in batch
                lock (slotsLock)
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

                // wait 
                await Task.Delay(rootMsgInterval);
            }
        }

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

                // get to core data from msg
                var jsonRoot = JsonDocument.Parse(response).RootElement;
                Console.WriteLine("\nReceived Batch children response with ID: " + jsonRoot.GetProperty("sourceMessageId"));
                
                var responses = jsonRoot.GetProperty("responses");

                // error handling 
                if (!CheckJSONResponse(jsonRoot, "batchResponse"))
                    return;

                Receive(jsonRoot);

                // wait
                await Task.Delay(childMsgInterval);
            }
        }

        #region HELPERS
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

            await socketLock.WaitAsync();
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            finally
            {
                socketLock.Release();
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
        #endregion
    }
}
