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
        private List<WorldObject> objects = [];

        JsonElement currentMsg;

        public WorldModel()
        {
            
        }

        public void ApplyUpdate(WorldUpdateSource source, JsonElement msg)
        {
            currentMsg = msg;
            // parse the json and update the world model
            // for now, we just store the json message
        
            // TODO: see if child was removed from scene, if so remove from collection
        }

        public JsonElement? GetWorldModelJson()
        {
            return currentMsg;
        }
    }
}
