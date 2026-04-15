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
        private Dictionary<string, WorldObject> objects = new Dictionary<string, WorldObject>();
        private Dictionary<string, UserData> users = new Dictionary<string, UserData>();

        private JsonSerializerOptions jsonOptions = new JsonSerializerOptions { IncludeFields = true };

        public WorldModel()
        {
            
        }

        public void ApplyUpdate(WorldUpdateSource source, WorldUpdate update)
        {
			// Parse the WorldUpdate and apply to dictionaries
			foreach ( WorldObject obj in update.objects )
            {
                if (objects.ContainsKey(obj.id))
                    objects[obj.id].ApplyFrom(obj);
                else
                    objects[obj.id] = obj;
            }

			foreach (UserData usr in update.users)
			{
				if (users.ContainsKey(usr.id))
					users[usr.id].ApplyFrom(usr);
				else
					users[usr.id] = usr;
			}

			// TODO: See if child was removed from scene, if so remove from collection
            //          The question is... how?
		}

        public string GetWorldModelJson( string excludeFrom = "" )
        {
            WorldUpdate data;            
			data.objects = objects.Values.Where(x => x.home != excludeFrom).ToList();
			data.users = users.Values.Where(x => x.home != excludeFrom).ToList();

            string jsonString = JsonSerializer.Serialize(data, jsonOptions);

            return jsonString;
        }
    }
}
