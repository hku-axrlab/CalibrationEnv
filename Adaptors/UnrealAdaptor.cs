using Fleck;
using System;
using System.Collections.Generic;
using System.Text;

namespace CalibrationEnv
{
    internal class UnrealAdaptor : Adaptor
    {
        public UnrealAdaptor(WorldModel worldModel, IWebSocketConnection socket) : base(worldModel, socket)
        {
        }
    }
}
