using Fleck;
using System;
using System.Collections.Generic;
using System.Text;

namespace CalibrationEnv
{
    internal class ResoniteAdaptor : Adaptor
    {
        public ResoniteAdaptor(WorldModel worldModel, IWebSocketConnection socket) : base(worldModel, socket)
        {
        }
    }
}
