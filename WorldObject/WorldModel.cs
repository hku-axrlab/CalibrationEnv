using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace CalibrationEnv
{
    internal class WorldModel
    {
        // collection of all objects in world, 
        // parsed and represened as WorldObject
        private List<WorldObject> objects = new List<WorldObject>();

        JsonElement currentMsg;

        public WorldModel()
        {
            
        }

        public void UpdateWorldModel(JsonElement msg)
        {
            currentMsg = msg;
            // parse the json and update the world model
            // for now, we just store the json message
        }

        public JsonElement? GetWorldModelJson()
        {
            return currentMsg;
        }
    }
}
