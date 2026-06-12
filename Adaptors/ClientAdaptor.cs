using Fleck;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Text.Json;

namespace CalibrationEnv
{
    internal class ClientAdaptor : Adaptor
    {
        // reference to connected socket
        private readonly IWebSocketConnection? socket;

        [MemberNotNullWhen(true, nameof(socket))]
        protected override bool IsSendReady => socket != null && socket.IsAvailable;

        public ClientAdaptor(WorldModel worldModel, IWebSocketConnection? socket, string type, int sendIntervalMs) : base(worldModel, sendIntervalMs)
        {
            this.socket = socket;

            if (socket != null)
                Id = GenerateId(type, socket.ConnectionInfo.ClientIpAddress, (uint)socket.ConnectionInfo.ClientPort);
        }

        protected override async Task Send(CancellationToken token)
        {
            // if can't send, return and wait for next interval to retry
            if (!IsSendReady)
                return;

            await socket.Send(worldModel.GetWorldModelJson(Id));
        }

        public override void Receive(JsonElement msgRoot)
        {
            WorldUpdate update = new();

            // receive all users
            JsonElement users;
            if ( msgRoot.TryGetProperty("users", out users) )
            {
                foreach (JsonElement userJson in users.EnumerateArray())
                {
                    ParseUser(userJson, ref update);
                }
            }
            
            // and then all objects
            JsonElement objects;
            if ( msgRoot.TryGetProperty("objects", out objects )) {
                foreach (JsonElement objectJson in objects.EnumerateArray())
                {
                    ParseObject(objectJson, ref update);
                }
            }            

            // and apply update to world model
            worldModel.ApplyUpdate(WorldUpdateSource.Client, update);
        }

        private void ParseUser(JsonElement userNode, ref WorldUpdate worldUpdate)
        {
            UserData userData = new();

            // parse basic info
            var name = userNode.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var id = userNode.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

            // simple error handling - just skip users that don't have the required info, should be correct otherwise
            if (name == null || id == null)
            {
                Console.WriteLine($"User parsed without name or id - won't be added. Name: [{name}], ID: [{id}].");
                return;
            }

            // parse bone names
            var boneNamesElement = userNode.GetProperty("boneNames");
            List<string> boneNames = [];
            foreach (JsonElement node in boneNamesElement.EnumerateArray())
            {
                boneNames.Add(node.ToString());
            }

            // parse bone transforms
            var boneTransformsElement = userNode.GetProperty("boneTransforms");
            List<Transform> boneTransforms = [];
            foreach (JsonElement node in boneTransformsElement.EnumerateArray())
            {
                var posElement = node.GetProperty("position");
                var rotElement = node.GetProperty("rotation");
                var scaleElement = node.GetProperty("scale");

                Vector3 position = new Vector3(
                    posElement.GetProperty("x").GetSingle(),
                    posElement.GetProperty("y").GetSingle(),
                    posElement.GetProperty("z").GetSingle()
                    );

                Quaternion rotation = new Quaternion(
                    rotElement.GetProperty("x").GetSingle(),
                    rotElement.GetProperty("y").GetSingle(),
                    rotElement.GetProperty("z").GetSingle(),
                    rotElement.GetProperty("w").GetSingle()
                    );

                Vector3 scale = new Vector3(
                    scaleElement.GetProperty("x").GetSingle(),
                    scaleElement.GetProperty("y").GetSingle(),
                    scaleElement.GetProperty("z").GetSingle()
                    );

                boneTransforms.Add(new Transform(position, rotation, scale));
            }

            // parse optional extra data 
            List<DataContainer> dataList;
            if (userNode.TryGetProperty("variables", out var dataElements))
            {
                dataList = new(dataElements.GetArrayLength());
                foreach (JsonElement variable in dataElements.EnumerateArray())
                {
                    string? dataName = variable.GetProperty("name").GetString();
                    string? dataType = variable.GetProperty("type").GetString();
                    JsonElement dataElement = variable.GetProperty("value");

                    // simple error handling - just skip variables that don't have the required info, should be correct otherwise
                    if (dataType == null || dataName == null)
                    {
                        Console.WriteLine($"Object variable for {name} parsed without name or type - won't be added. Name: [{dataName}], Type: [{dataType}].");
                        continue;
                    }

                    DataContainer data = new(dataType, dataName, dataElement.Clone());
                    dataList.Add(data);
                }
            }
            else
            {
                dataList = new();
            }

            // very simple error handling - really should just be correct otherwise, fix it!
            if (boneTransforms.Count != boneNames.Count)
            {
                Console.WriteLine($"User parsed with wrong bones - won't be added. {boneTransforms.Count} Transforms, but {boneNames.Count} names!");
                return;
            }

            // setup user
            userData.name = name;
            userData.id = id;
            userData.home = Id;
            userData.boneNames = [.. boneNames];
            userData.boneTransforms = [.. boneTransforms];
            userData.data = [.. dataList];

            // add user to update
            worldUpdate.users.Add(userData);
        }

        private void ParseObject(JsonElement objectNode, ref WorldUpdate worldUpdate)
        {
            WorldObject obj = new();

            // parse basic info
            var name = objectNode.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var id = objectNode.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var tag = objectNode.TryGetProperty("tag", out var tagProp) ? tagProp.GetString() : null;

            // simple error handling - just skip objects that don't have the required info, should be correct otherwise
            if (name == null || id == null || tag == null)
            {
                Console.WriteLine($"Object parsed without name, id, or tag - won't be added. Name: [{name}], ID: [{id}], Tag: [{tag}].");
                return;
            }

            // parse transform
            var transformElement = objectNode.GetProperty("transform");
            var posElement = transformElement.GetProperty("position");
            var rotElement = transformElement.GetProperty("rotation");
            var scaleElement = transformElement.GetProperty("scale");

            Vector3 position = new Vector3(
                posElement.GetProperty("x").GetSingle(),
                posElement.GetProperty("y").GetSingle(),
                posElement.GetProperty("z").GetSingle()
                );

            Quaternion rotation = new Quaternion(
                rotElement.GetProperty("x").GetSingle(),
                rotElement.GetProperty("y").GetSingle(),
                rotElement.GetProperty("z").GetSingle(),
                rotElement.GetProperty("w").GetSingle()
                );

            Vector3 scale = new Vector3(
                scaleElement.GetProperty("x").GetSingle(),
                scaleElement.GetProperty("y").GetSingle(),
                scaleElement.GetProperty("z").GetSingle()
                );

            // parse optional extra data 
            var dataElements = objectNode.GetProperty("variables");
            List<DataContainer> dataList = new(dataElements.GetArrayLength());
            foreach( JsonElement variable in dataElements.EnumerateArray())
            {
                string? dataName = variable.GetProperty("name").GetString();
                string? dataType = variable.GetProperty("type").GetString();
                JsonElement dataElement = variable.GetProperty("value");

                // simple error handling - just skip variables that don't have the required info, should be correct otherwise
                if (dataType == null || dataName == null)
                {
                    Console.WriteLine($"Object variable for {name} parsed without name or type - won't be added. Name: [{dataName}], Type: [{dataType}].");
                    continue;
                }

                DataContainer data = new(dataType, dataName, dataElement.Clone());
                dataList.Add(data);
            }

            obj.home = Id;
            obj.name = name;
            obj.tag = tag;
            obj.id = id;
            obj.transform.position = position;
            obj.transform.rotation = rotation;
            obj.transform.scale = scale;
            obj.data = [.. dataList];

            worldUpdate.objects.Add(obj);
        }
    }
}
