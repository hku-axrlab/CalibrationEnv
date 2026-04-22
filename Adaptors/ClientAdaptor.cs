using Fleck;
using ResoniteLink;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace CalibrationEnv
{
    internal class ClientAdaptor : Adaptor
    {
        // reference to connected socket
        private readonly IWebSocketConnection? socket;

        protected override int GetSendInterval() => 17;

        public ClientAdaptor(WorldModel worldModel, IWebSocketConnection? socket, string type ) : base(worldModel)
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
            }
        }
        public override void Receive(JsonElement msgRoot)
        {
            WorldUpdate update = new();

            JsonElement responses = msgRoot.GetProperty("users");
            foreach (JsonElement slotNode in responses.EnumerateArray())
            {
                ParseUser(slotNode, ref update);
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
            if(boneTransforms.Count != boneNames.Count)
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
    }
}
