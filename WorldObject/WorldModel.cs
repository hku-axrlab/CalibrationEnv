using System.Collections.Concurrent;
using System.Text.Json;

namespace CalibrationEnv
{
    internal class WorldModel
    {
        // collection of all objects in world, 
        // parsed and represened as WorldObject
        private ConcurrentDictionary<string, WorldObject?> objects = [];

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

        public void AddObjectID(string id)
        {
            if (objects.ContainsKey(id))
            {
                Console.WriteLine($"Object with {id} already added to world model!");
                return;
            }

            objects.TryAdd(id, null);
        }

        public void AddObjectData(string id, string data)
        {
            if (!objects.ContainsKey(id))
            {
                Console.WriteLine($"Object with requested {id} doesn't exist in world model!");
                return;
            }

            objects[id] = new WorldObject();
        }

        public bool ContainsObject(string? id)
        {
            if (string.IsNullOrEmpty(id))
                return false;

            return objects.ContainsKey(id);
        }

        public List<string> GetObjectKeysSnapshot()
        {
            return [.. objects.Keys];
        }
    }
}
