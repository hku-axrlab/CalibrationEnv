using ResoniteLink;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CalibrationEnv
{
    // TODO: make stand-alone application. Launch with params --with-resonite.
    internal class ResoniteAdaptor : Adaptor
    {
        // receive - used for ResoniteLink connection to receive data from Resonite world
        private ClientWebSocket receiveSocket;
        private readonly SemaphoreSlim receiveSocketLock = new(1, 1);

        // send - used for Resonite connection to send data to Resonite world
        private readonly int resonitePort = 5001;
        private HttpListener sendServer;
        private WebSocket sendSocket;
        private readonly SemaphoreSlim sendSocketLock = new(1, 1);

        // interval times for requesting slots from Resonite world, in ms
        protected readonly int rootMsgInterval = 1000;
        protected readonly int childMsgInterval = 17;
        protected override int GetSendInterval() => 17;

        private const int MAX_MESSAGE_SIZE = 1024 * 1024; // 1 MB
        private const int BUFFER_SIZE = 8192;

        // pending requests collection,
        // to match responses with requests using message ID
        private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> pendingRequests = [];

        // registered slot (world objects) collection,
        // to keep track of discovered slot ID's (children from Root slot with a tag),
        // and to request data for those in batches 
        // NOTE: byte value always 0, just wanted ConcurrentDic pattern
        private readonly ConcurrentDictionary<string, byte> registeredSlots = [];

        public ResoniteAdaptor(WorldModel worldModel) : base(worldModel) 
        {
            
        }

        public override async Task StartAsync(CancellationToken token)
        {
            // setup connections to resonite
            await ConnectToResonite(token);

            // call base (should return instantly)
            await base.StartAsync(token);

            // start tasks, and keep refs
            tasks.Add(ReceiveLoop(token));
            tasks.Add(GetRootLoop(token));
            tasks.Add(GetChildrenLoop(token));
        }

        private async Task ConnectToResonite(CancellationToken token)
        {
            uint inputPort;

            // connection to ResoniteLink 
            // used to obtain data from Resonite world
            while (!token.IsCancellationRequested)
            {
                // prompt for port to ResoniteLink world
                Console.Write("Enter port number ResoniteLink world: ");
                var input = Console.ReadLine();
                if (!uint.TryParse(input, out inputPort))
                {
                    Console.WriteLine("Invalid port number.\n");
                    continue;
                }

                // create new socket (per attempt, since it will break after failed attempt)
                receiveSocket?.Dispose();
                receiveSocket = new ClientWebSocket();

                // attempt connect with timeout
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                try
                {
                    await receiveSocket.ConnectAsync(new Uri($"ws://localhost:{inputPort}"), cts.Token);

                    Guid = GenerateId("resonite", "127.0.0.1", inputPort);
                    Console.WriteLine("Connected to ResoniteLink!\n");

                    break;
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Connection to ResoniteLink timed out.\n");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Connection to ResoniteLink failed: {ex.Message}\n");
                }
            }

            // setup connection to Resonite 
            // used to send data back to Resonite world
            while (!token.IsCancellationRequested)
            {
                try
                {
                    sendServer = new HttpListener();

                    sendServer.Prefixes.Add($"http://localhost:{resonitePort}/echo/");
                    sendServer.Start();

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var getContextTask = sendServer.GetContextAsync();
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(10), cts.Token);

                    var completed = await Task.WhenAny(getContextTask, timeoutTask);

                    if (completed != getContextTask)
                    {
                        Console.WriteLine("Connection to Resonite timed out.\n");

                        // stop listener! 
                        sendServer.Stop();

                        return;
                    }

                    var ctx = await getContextTask;

                    if (!ctx.Request.IsWebSocketRequest)
                    {
                        Console.WriteLine($"Connection to Resonite failed: not a WebSocket Request.\n");
                        ctx.Response.StatusCode = 400;
                        ctx.Response.Close();
                        return;
                    }

                    HttpListenerWebSocketContext wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null);
                    sendSocket = wsCtx.WebSocket;

                    Console.WriteLine("Connected to Resonite!\n");
                    break;
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Connection to Resonite timed out.\n");
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Connection to Resonite failed: {ex.Message}\n");
                    break;
                }
            }
        }
        
        protected override async Task Send(CancellationToken token)
        {
            // FIXME: returning when no socket setup
            // since it can fail setting up rn and hold the whole app hostage
            if (sendSocket == null)
                return;

            // RN: only send non-resonite users
            // TODO: send non-resonite Objects too

            Dictionary<string, WorldObject> remoteRoots = new Dictionary<string, WorldObject>();

            // get update from world
            WorldUpdate update = worldModel.GetWorldModel(Guid);

            foreach( var obj in update.objects)
            {
                if (obj.tag == "vRoot")
                    remoteRoots.Add(obj.home, obj);
            }

            // send msg per user per bone to resonite to update users
            // TODO: optimize by batching user data?
            foreach (var user in update.users)
            {
                for (int i = 0; i < user.boneNames.Length; i++)
                {
                    var position = user.boneTransforms[i].position;
                    var rotation = user.boneTransforms[i].rotation;

                    if ( remoteRoots.ContainsKey(user.home))
                    {
                        remoteRoots[user.home].transform.MakeRelative(ref position);
                        remoteRoots[user.home].transform.MakeRelative(ref rotation);
                    }

                    var msg = string.Join(';', user.name, user.id, user.boneNames[i],
                        position.X, position.Y, position.Z, rotation.X, rotation.Y, rotation.Z, rotation.W
                    );

                    await sendSocketLock.WaitAsync(token);
                    try
                    {
                        await sendSocket.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, token);
                    }
                    finally
                    {
                        sendSocketLock.Release();
                    }
                }
            }

            await Task.Delay(33, token);   // TODO: Calculate how much is left to target 30fps (maybe have this as a setting)
        }

        public override void Receive(JsonElement msgRoot)
        {
            WorldUpdate update = new();

            JsonElement responses = msgRoot.GetProperty("responses");
            foreach (JsonElement slotNode in responses.EnumerateArray())
            {
                ParseSlot(slotNode, ref update);
            }

            worldModel.ApplyUpdate(WorldUpdateSource.Resonite, update);
        }

        private async Task ReceiveLoop(CancellationToken token)
        {
            var buffer = new byte[BUFFER_SIZE];

            while (!token.IsCancellationRequested)
            {
                // get message by writing to memorystream as long
                // as required to receive complete msg 
                using var stream = new MemoryStream();

                WebSocketReceiveResult result;
                do
                {
                    result = await receiveSocket.ReceiveAsync(buffer, token);

                    if (result.MessageType == WebSocketMessageType.Close)
                        return;

                    stream.Write(buffer, 0, result.Count);

                    if (stream.Length > MAX_MESSAGE_SIZE)
                        throw new InvalidOperationException("WebSocket message too large");

                } while (!result.EndOfMessage);

                string msg = Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);

                // extract message ID from JSON and add to pending requests
                string? msgId = ExtractMessageID(msg);
                TaskCompletionSource<string>? tcs = null;

                if (msgId != null && pendingRequests.TryGetValue(msgId, out tcs))
                {
                    pendingRequests.TryRemove(msgId, out _);
                }
                else
                {
                    Console.WriteLine("Received untracked message: " + msg);
                }

                tcs?.SetResult(msg);

                await Task.Delay(1, token);
            }
        }

        private async Task GetRootLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                // get Root slot data 
                var msg = BuildGetSlotMsg("Root", false);
                string response = await SendRequestAsync(msg, "getSlot", token);

                // process response to extract child ID's and add to registered slots collection,
                // which will be used to request childern slot data in batch

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

                // check and add new slots if found
                foreach (var child in children)
                {
                    // add child id to registered slots
                    // but only interesseted in tagged childern
                    var childID = child.GetProperty("id").GetString()!;
                    var childTag = child.GetProperty("tag").GetProperty("value").GetString();

                    if (string.IsNullOrEmpty(childTag) || string.IsNullOrEmpty(childID))
                        continue;

                    registeredSlots.TryAdd(childID, 0);
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
                var snapshot = registeredSlots.Keys.ToList();

                // do batch operation: get slot on all registered id's
                var batchMsg = new DataModelOperationBatch
                {
                    MessageID = System.Guid.NewGuid().ToString(),
                    Operations = []
                };

                foreach (var childID in snapshot)
                {
                    batchMsg.Operations.Add(BuildGetSlotMsg(childID, true));
                }

                // wait to receive response
                string response = await SendRequestAsync(batchMsg, "dataModelOperationBatch", token);

                // get to core data from msg
                var jsonRoot = JsonDocument.Parse(response).RootElement;

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

        private async Task<string> SendRequestAsync(Message msg, string type, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingRequests[msg.MessageID] = tcs;

            byte[] bytes = GetMsgAsByteArray(msg, type);

            await receiveSocketLock.WaitAsync(token);
            try
            {
                await receiveSocket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
            }
            finally
            {
                receiveSocketLock.Release();
            }

            // wait for response with timeout
            var completed = await Task.WhenAny(tcs.Task, Task.Delay(10000, token));
            if (completed != tcs.Task)
            {
                pendingRequests.TryRemove(msg.MessageID, out _);
                throw new TimeoutException("No response received");
            }

            return await tcs.Task;
        }

        private GetSlot BuildGetSlotMsg(string slotID, bool includeComponentData)
        {
            // build message to get slot with give ID
            return new GetSlot
            {
                MessageID = System.Guid.NewGuid().ToString(),
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
            WorldObject worldObject = new();

            // check if object was removed,
            // which is determined by data element's value being null 
            // additionally succes will be false, errorInfo will contain deleted slot id
            var dataElement = slotNode.GetProperty("data");
            if (dataElement.ValueKind == JsonValueKind.Null)
            {
                // check for error msg
                var errorToken = slotNode.TryGetProperty("errorInfo", out var errorProp) ? errorProp.GetString() : null;
                if(errorToken == null)
                {
                    Console.WriteLine("Bad GetSlot response, can't determine requested id or data. Node: " + slotNode.ToString());
                    return;
                }

                // attempt to get id from error msg
                // example msg: "Slot with ID 'Reso_11F' not found."
                var match = Regex.Match(errorToken, @"'(?<id>Reso_[^']+)'");
                if (!match.Success)
                {
                    Console.WriteLine("Bad GetSlot response, can't determine requested id or data. Node: " + slotNode.ToString());
                    return;
                }

                // set field world object 
                worldObject.id = match.Groups["id"].Value;
                worldObject.markedForRemoval = true;

                // remove from registered slots so we stop requesting updates  
                registeredSlots.TryRemove(worldObject.id, out _);

                // do still add to the update, 
                // since removing the object is in fact an update! 
                worldUpdate.objects.Add(worldObject);

                return;
            }

            // Grab data from slot
            var id = dataElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var nameToken = dataElement.TryGetProperty("name", out var nameProp) && nameProp.TryGetProperty("value", out var nameVal) ? nameVal.GetString() : null;
            var tagToken = dataElement.TryGetProperty("tag", out var tagProp) && tagProp.TryGetProperty("value", out var tagVal) ? tagVal : (JsonElement?)null;
            var posToken = dataElement.TryGetProperty("position", out var posProp) && posProp.TryGetProperty("value", out var posVal) ? posVal : (JsonElement?)null;
            var rotToken = dataElement.TryGetProperty("rotation", out var rotProp) && rotProp.TryGetProperty("value", out var rotVal) ? rotVal : (JsonElement?)null;
            var scaleToken = dataElement.TryGetProperty("scale", out var scaleProp) && scaleProp.TryGetProperty("value", out var scaleVal) ? scaleVal : (JsonElement?)null;

            // Skip if no id and/or tag
            var tagValue = tagToken?.GetString() ?? "Untagged";
            if (id != "Root" && (id == null || string.IsNullOrEmpty(tagValue))) return;

            // Process transform
            Transform transform = new();
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
            JsonElement components = dataElement.GetProperty("components");
            foreach (JsonElement component in components.EnumerateArray())
            {
                //slotNode.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
                var type = component.GetProperty("componentType").GetString();
                if (type.Contains("DynamicValueVariable<"))
                {
                    JsonElement members = component.GetProperty("members");

                    // Get the variable from the template <>
                    int start = type.IndexOf("<");
                    int end = type.IndexOf(">");
                    string? inner = (start != -1 && end != -1 && end > start) ? type.Substring(start + 1, end - start - 1) : null;

                    string varType = inner == null ? "" : inner;
                    string? varName = members.GetProperty("VariableName").GetProperty("value").GetString();
                    JsonElement varValue = members.GetProperty("Value").GetProperty("value");

                    if (varName == null) continue;

                    data.Add(new DataContainer(varType, varName, varValue.Clone()));
                }
            }

            worldObject.id = id;
            worldObject.tag = tagValue;
            worldObject.name = nameToken == null ? "no_name" : nameToken;
            worldObject.home = Guid;
            worldObject.transform = transform;
            worldObject.data = [.. data];

            worldUpdate.objects.Add(worldObject);
        }
        #endregion
    }
}
