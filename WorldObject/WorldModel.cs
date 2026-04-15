using System.Collections.Concurrent;
using System.Text.Json;

namespace CalibrationEnv
{
    [System.Serializable]
    struct WorldUpdate
    {
        public List<WorldObject> objects;
		public List<UserData> users;

        public WorldUpdate()
        {
            objects = new List<WorldObject>();
            users = new List<UserData>();
        }
	}

    internal class WorldModel
    {
        // collection of all objects & users in world, stored by ID for easy reference
        private ConcurrentDictionary<string, WorldObject> objects = new ConcurrentDictionary<string, WorldObject>();
        private ConcurrentDictionary<string, UserData> users = new ConcurrentDictionary<string, UserData>();

        private JsonSerializerOptions jsonOptions = new JsonSerializerOptions { IncludeFields = true };

        public WorldModel()
        {

        }

        public void ApplyUpdate(WorldUpdateSource source, WorldUpdate update)
        {
			// Parse the WorldUpdate and apply to dictionaries
			foreach (WorldObject obj in update.objects)
            {
                if (objects.ContainsKey(obj.id))
                    objects[obj.id].ApplyFrom(obj);
                else if (!obj.markedForRemoval) // don't add if not added yet and removing anyways
                    objects[obj.id] = obj;
            }

			foreach (UserData usr in update.users)
			{
				if (users.ContainsKey(usr.id))
					users[usr.id].ApplyFrom(usr);
				else
					users[usr.id] = usr;
			}
		}

        public string GetWorldModelJson(string excludeFrom = "")
        {
            WorldUpdate data;            
			data.objects = objects.Values.Where(x => x.home != excludeFrom).ToList();
			data.users = users.Values.Where(x => x.home != excludeFrom).ToList();

            string jsonString = JsonSerializer.Serialize(data, jsonOptions);

            return jsonString;
        }

        public bool ContainsObject(string? id)
        {
            if (string.IsNullOrEmpty(id))
                return false;

            return objects.ContainsKey(id);
        }
    }
}
