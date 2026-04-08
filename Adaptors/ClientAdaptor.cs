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

        public ClientAdaptor(WorldModel worldModel, IWebSocketConnection? socket) : base(worldModel)
        {
            this.socket = socket;
        }

        protected override async Task SendStep()
        {
            if (socket != null && socket.IsAvailable)
            {
                await socket.Send(worldModel.GetWorldModelJson().ToString());
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
