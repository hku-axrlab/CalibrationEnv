using Fleck;
using System;
using System.Collections.Generic;
using System.Text;

namespace CalibrationEnv
{
    internal class UnityAdaptor : Adaptor
    {
        public UnityAdaptor(WorldModel worldModel, IWebSocketConnection socket) : base(worldModel, socket)
        {
        }
    }
}
