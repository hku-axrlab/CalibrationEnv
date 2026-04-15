using ResoniteLink;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace CalibrationEnv
{
    internal class ResoniteAdaptor : Adaptor
    {
        private ClientWebSocket socket;
        private readonly SemaphoreSlim socketLock = new SemaphoreSlim(1, 1);
        
        // pending requests collection, to match responses with requests using message ID
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> pendingRequests = [];

        // interval times for requesting slots, in ms
        protected readonly int rootMsgInterval = 10000;//1000;
        protected readonly int childMsgInterval = 5000;//17;
        protected override int GetSendInterval() => 17;

        public ResoniteAdaptor(WorldModel worldModel) : base(worldModel)
        {
            socket = new ClientWebSocket();
        }

        public override async Task StartAsync(CancellationToken token)
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

            guid = GenerateId("resonite", "127.0.0.1", resonitePort);

            // call base, should return instantly
            await base.StartAsync(token);

            // fire tasks, run completely as background task on threadpool
            _ = Task.Run(() => ReceiveLoop(token), token);
            _ = Task.Run(() => GetRootLoop(token), token);
            _ = Task.Run(() => GetChildrenLoop(token), token);
        }

        public override void Receive(JsonElement msgRoot)
        {
            WorldUpdate update = new WorldUpdate();

			JsonElement responses = msgRoot.GetProperty("responses");
            foreach (JsonElement slotNode in responses.EnumerateArray())
            {
                ParseSlot(slotNode, ref update);
			}

		    worldModel.ApplyUpdate(WorldUpdateSource.Resonite, update);
        }

        protected override Task SendStep()
        {
            // TODO: not sure how to handle world updates -> Resonite
            // possible idea below - keep track of commands/special objects?
            // commands would be send by clients?

            // First only send non-resonite users
            // Later send non-resonite Objects too

            // send queued client inputs to Resonite
            //var commands = worldModel.ConsumeOutgoingCommands();

            //foreach (var cmd in commands)
            //{
            //    await SendToResonite(cmd);
            //}

            return Task.CompletedTask;
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            // TODO: improve so buffers can't/won't overload
            var buffer = new byte[8192];

            while (!token.IsCancellationRequested)
            {
                // receive full message from Resonite world, which might come in multiple frames,
                // and combine to single string msg
                var segment = new ArraySegment<byte>(buffer);
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(segment, token);
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                string msg = Encoding.UTF8.GetString(ms.ToArray());

                // extract message ID from JSON and add to pending requests
                string? msgId = ExtractMessageID(msg);
                TaskCompletionSource<string>? tcs = null;

                if (msgId != null && pendingRequests.TryGetValue(msgId, out tcs))
                {
                    pendingRequests.TryRemove(msgId, out var removed);
                }
                else
                {
                    Console.WriteLine("Received untracked message: " + msg);
                }

                if (tcs != null)
                {
                    tcs.SetResult(msg);
                }
            }
        }

        private async Task GetRootLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // get Root slot data 
                var msg = BuildGetSlotMsg("Root", false);
                string response = await SendRequestAsync(msg, "getSlot");

                // process response to extract child ID's and add to registered slots collection,
                // which will be used to request childern slot data in batch
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

                Console.WriteLine("Data from Root response: " + children.ToList().Count + " children.\n" + data);

                foreach (var child in children)
                {
                    // add child id to registered slots
                    // but only interesseted in tagged childern
                    var childID = child.GetProperty("id").GetString()!;
                    var childTag = child.GetProperty("tag").GetProperty("value").GetString();

                    if (string.IsNullOrEmpty(childTag) || string.IsNullOrEmpty(childID))
                        continue;

                    if (!worldModel.ContainsObject(childID))
                        worldModel.AddObjectID(childID);
                }

                // wait 
                await Task.Delay(rootMsgInterval, token);
            }
        }

        private async Task GetChildrenLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                DateTime start = DateTime.Now;

                // get a copy of the current registered slots to do batch operation on
                List<string> snapshotRegisteredSlots = worldModel.GetObjectKeysSnapshot();

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
                // Console.WriteLine("\nReceived Batch children response with ID: " + jsonRoot.GetProperty("sourceMessageId"));
                
                var responses = jsonRoot.GetProperty("responses");

                Console.WriteLine("Data from Batch response: " + responses);
                // error handling 
                if (!CheckJSONResponse(jsonRoot, "batchResponse"))
                    return;

                Receive(jsonRoot);

                // Make sure we account for the time the request took, so we don't introduce unnecessary delay
                TimeSpan spent = DateTime.Now - start;

				// wait for childMsgInterval minus time spent here
				await Task.Delay(Math.Max(childMsgInterval - (int)spent.TotalMilliseconds, 0), token);
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
            pendingRequests[msg.MessageID] = tcs;

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
                pendingRequests.TryRemove(msg.MessageID, out var removed);
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

        private void ParseSlot(JsonElement slotNode, ref WorldUpdate worldUpdate)
        {
            WorldObject worldObject = new WorldObject();

            // Grab data from slot
            var id = slotNode.GetProperty("data").TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var nameToken = slotNode.GetProperty("data").TryGetProperty("name", out var nameProp) && nameProp.TryGetProperty("value", out var nameVal) ? nameVal.GetString() : null;
            var tagToken = slotNode.GetProperty("data").TryGetProperty("tag", out var tagProp) && tagProp.TryGetProperty("value", out var tagVal) ? tagVal : (JsonElement?)null;
            var posToken = slotNode.GetProperty("data").TryGetProperty("position", out var posProp) && posProp.TryGetProperty("value", out var posVal) ? posVal : (JsonElement?)null;
            var rotToken = slotNode.GetProperty("data").TryGetProperty("rotation", out var rotProp) && rotProp.TryGetProperty("value", out var rotVal) ? rotVal : (JsonElement?)null;
            var scaleToken = slotNode.GetProperty("data").TryGetProperty("scale", out var scaleProp) && scaleProp.TryGetProperty("value", out var scaleVal) ? scaleVal : (JsonElement?)null;

            // Skip if no id and/or tag
            var tagValue = tagToken?.GetString() ?? "Untagged";
            if (id != "Root" && (id == null || string.IsNullOrEmpty(tagValue))) return;

            // Process transform
            Transform transform = new Transform();
            Vector3 position;
            Quaternion rotation;
            Vector3 scale;

            if (posToken.HasValue)
            {
                float x = posToken.Value.TryGetProperty("x", out var px) ? px.GetSingle() : 0f;
                float y = posToken.Value.TryGetProperty("y", out var py) ? py.GetSingle() : 0f;
                float z = posToken.Value.TryGetProperty("z", out var pz) ? pz.GetSingle() : 0f;
                position = new Vector3(x, y, z);
                transform.position = position;
            }

            if (rotToken.HasValue)
            {
                float x = rotToken.Value.TryGetProperty("x", out var rx) ? rx.GetSingle() : 0f;
                float y = rotToken.Value.TryGetProperty("y", out var ry) ? ry.GetSingle() : 0f;
                float z = rotToken.Value.TryGetProperty("z", out var rz) ? rz.GetSingle() : 0f;
                float w = rotToken.Value.TryGetProperty("w", out var rw) ? rw.GetSingle() : 1f;
                rotation = new Quaternion(x, y, z, w);
                transform.rotation = rotation;
            }

            if (scaleToken.HasValue)
            {
                float x = scaleToken.Value.TryGetProperty("x", out var sx) ? sx.GetSingle() : 1f;
                float y = scaleToken.Value.TryGetProperty("y", out var sy) ? sy.GetSingle() : 1f;
                float z = scaleToken.Value.TryGetProperty("z", out var sz) ? sz.GetSingle() : 1f;
                scale = new Vector3(x, y, z);
                transform.scale = scale;
            }

            

            // Process components for variable data
            List<DataContainer> data = new List<DataContainer>();
            JsonElement components = slotNode.GetProperty("data").GetProperty("components");
            foreach (JsonElement component in components.EnumerateArray())
            {
				//slotNode.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
				var type = component.GetProperty("componentType").GetString();
                if ( type.Contains("DynamicValueVariable<"))
                {
                    JsonElement members = component.GetProperty("members");

					// Get the variable from the template <>
					int start = type.IndexOf("<");
					int end = type.IndexOf(">");
					string? inner = (start != -1 && end != -1 && end > start) ? type.Substring(start + 1, end - start - 1) : null;

					string varType = inner == null ? "" : inner;
					string? varName = members.GetProperty("VariableName").GetProperty("value").GetString();
					JsonElement varValue = members.GetProperty("Value");

                    if ( varName == null) continue;

                    data.Add(new DataContainer(varType, varName, varValue));
				}
			}

            worldObject.id = id;
            worldObject.tag = tagValue;
			worldObject.name = nameToken == null ? "no_name" : nameToken;
			worldObject.home = guid;
			worldObject.transform = transform;
            worldObject.data = data.ToArray();

			worldUpdate.objects.Add(worldObject);
		}
		#endregion
	}
}
