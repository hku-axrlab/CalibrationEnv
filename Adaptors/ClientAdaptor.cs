using Fleck;
using System.Numerics;
using System.Text.Json;

namespace CalibrationEnv
{
    internal class ClientAdaptor : Adaptor
    {
        // reference to connected socket
        private readonly IWebSocketConnection? socket;

        protected override int GetSendInterval() => 17;

        public ClientAdaptor(WorldModel worldModel, IWebSocketConnection? socket, string type) : base(worldModel)
        {
            this.socket = socket;
            if (socket != null)
                this.guid = GenerateId(type, socket.ConnectionInfo.ClientIpAddress, (uint)socket.ConnectionInfo.ClientPort);
        }

        protected override async Task SendStep()
        {
            if (socket != null && socket.IsAvailable)
            {
                await socket.Send(worldModel.GetWorldModelJson(guid));
                await Task.Delay(16);   // TODO: calculate how much longer we need to wait (should always be less than 16ms)
            }
        }

        public override void Receive(JsonElement msgRoot)
        {
            WorldUpdate update = new();

            JsonElement users;
            if ( msgRoot.TryGetProperty("users", out users) )
            {
                foreach (JsonElement userJson in users.EnumerateArray())
                {
                    ParseUser(userJson, ref update);
                }
            }
            

            JsonElement objects;
            if ( msgRoot.TryGetProperty("objects", out objects )) {
                foreach (JsonElement objectJson in objects.EnumerateArray())
                {
                    ParseObject(objectJson, ref update);
                }
            }            

            worldModel.ApplyUpdate(WorldUpdateSource.Client, update);
        }

        private void ParseUser(JsonElement userNode, ref WorldUpdate worldUpdate)
        {
            UserData userData = new();

            var name = userNode.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var id = userNode.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

            var boneNamesElement = userNode.GetProperty("boneNames");
            List<string> boneNames = [];
            foreach (JsonElement node in boneNamesElement.EnumerateArray())
            {
                boneNames.Add(node.ToString());
            }

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

            // very simple error handling - really should just be correct
            // otherwise, fix!
            if (boneTransforms.Count != boneNames.Count)
            {
                Console.WriteLine($"User parsed with wrong bones - won't be added. {boneTransforms.Count} Transforms, but {boneNames.Count} names!");
                return;
            }

            userData.name = name;
            userData.id = id;
            userData.home = guid;
            userData.boneNames = [.. boneNames];
            userData.boneTransforms = [.. boneTransforms];

            worldUpdate.users.Add(userData);
        }

        private void ParseObject(JsonElement objectNode, ref WorldUpdate worldUpdate)
        {
            WorldObject obj = new();

            // TODO: Parse
            var name = objectNode.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
            var id = objectNode.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
            var tag = objectNode.TryGetProperty("tag", out var tagProp) ? tagProp.GetString() : null;

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

            var dataElements = objectNode.GetProperty("variables");
            List<DataContainer> dataList = new List<DataContainer>(dataElements.GetArrayLength());
            foreach( JsonElement variable in dataElements.EnumerateArray())
            {
                
                string? dataName = variable.GetProperty("name").GetString();
                string? dataType = variable.GetProperty("type").GetString();
                JsonElement dataElement = variable.GetProperty("value");

                DataContainer data = new DataContainer(dataType, dataName, dataElement.Clone());
                dataList.Add(data);
            }

            obj.home = guid;
            obj.name = name;
            obj.tag = tag;
            obj.id = id;
            obj.transform.position = position;
            obj.transform.rotation = rotation;
            obj.transform.scale = scale;
            obj.data = dataList.ToArray();

            worldUpdate.objects.Add(obj);
        }
    }
}
