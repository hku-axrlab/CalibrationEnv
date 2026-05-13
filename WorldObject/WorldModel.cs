using System.Collections.Concurrent;
using System.Text.Json;

namespace CalibrationEnv
{
    [Serializable]
    class WorldUpdate
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

        public void RemoveAllFor( string guid )
        {
            List<string> staleUsers = new List<string>();
            foreach( UserData user in users.Values )
            {
                if ( user.home == guid )
                    staleUsers.Add(user.id);
            }

            List<string> staleObjects = new List<string>();
            foreach (WorldObject obj in objects.Values)
            {
                if (obj.home == guid)
                    staleObjects.Add(obj.id);
            }

            foreach (string id in staleUsers)
                users.Remove(id, out _);

            foreach (string id in staleObjects)
                objects.Remove(id, out _);

            // TODO: signal that these objects should be deleted (?)
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
            return JsonSerializer.Serialize(GetWorldModel(excludeFrom), jsonOptions);
        }

        public WorldUpdate GetWorldModel(string excludeFrom = "")
        {
            WorldUpdate data = new()
            {
                objects = [.. objects.Values.Where(x => x.home != excludeFrom)],
                users = [.. users.Values.Where(x => x.home != excludeFrom)]
            };
            return data;
        }

        public bool ContainsObject(string? id)
        {
            if (string.IsNullOrEmpty(id))
                return false;

            return objects.ContainsKey(id);
        }
    }
}
