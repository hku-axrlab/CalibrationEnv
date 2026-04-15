using Fleck;
using System;
using System.Collections.Generic;
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
            if ( socket != null )
                this.guid = GenerateId(type, socket.ConnectionInfo.ClientIpAddress, (uint)socket.ConnectionInfo.ClientPort);
		}

        protected override async Task SendStep()
        {
            if (socket != null && socket.IsAvailable)
            {
                await socket.Send(worldModel.GetWorldModelJson());
            }
        }
        public override void Receive(JsonElement msgRoot)
        {
			// TODO: to be implemented. 
			// not sure how msg from client will be structured
			// and how it's gonna change the WorldModel

		}
    }
}
