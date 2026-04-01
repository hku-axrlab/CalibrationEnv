using Fleck;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace CalibrationEnv
{
    internal abstract class Adaptor
    {
        // reference to world model to obtain world data 
        protected WorldModel worldModel;

        // interval times for requesting slots, in ms
        protected int rootMsgInterval = 1000;
        protected int childMsgInterval = 17;

        protected Adaptor(WorldModel worldModel)
        {
            this.worldModel = worldModel;
        }

        protected void Receive()
        {
            // interpret current world model

        }

        protected void Send(JsonElement msgRoot)
        {
            // change current world model based on msg
            // msg is send from client connected to this adapter

        }
    }
}
