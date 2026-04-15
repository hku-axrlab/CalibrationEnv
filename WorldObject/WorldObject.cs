using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.Json;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace CalibrationEnv
{
    [Serializable]
    internal class WorldObject
    {
        public string id;
        public string tag;
        public string name;
        public string home;
        public Transform transform;
        public DataContainer[]? data;

        public bool markedForRemoval = false;

        public WorldObject()
        {
            id = "";
            tag = "";
            name = "";
            home = "";
            transform = new Transform();
            markedForRemoval = false;
        }

		public void ApplyFrom(WorldObject other)
        {
            transform = other.transform;
			// Check if this is slow or not... might want to more efficiently copy data
			data = other.data;

            markedForRemoval = other.markedForRemoval;
        }
    }

    [Serializable]
    internal struct Transform
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;

        public Transform()
        {
            position = new Vector3();
            rotation = new Quaternion();
            scale = new Vector3();
        }
    }

    [Serializable]
    internal struct DataContainer
    {
        public string type;
        public string name;
        public JsonElement value;

        public DataContainer( string type, string name, JsonElement value )
        {
            this.type = type;
            this.name = name;
            this.value = value;
        }
    }

	[Serializable]
	internal struct UserData
	{
		public string id;
		public string home;
		public string name;
        public string[] boneNames;
		public Transform[] boneTransforms;

		public void ApplyFrom(UserData other)
		{
            name = other.name;
            // Check if this is slow or not... might want to more efficiently copy data
            boneNames = other.boneNames;
            boneTransforms = other.boneTransforms;
		}
	}
}
