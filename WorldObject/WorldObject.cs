using System.Numerics;
using System.Text.Json;

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
    internal class Transform
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

        public Transform(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            this.position = position;
            this.rotation = rotation;
            this.scale = scale;
        }

        public Vector3 RotateVector(Vector3 value, Quaternion rotation)
        {
            float x2 = rotation.X + rotation.X;
            float y2 = rotation.Y + rotation.Y;
            float z2 = rotation.Z + rotation.Z;

            float wx2 = rotation.W * x2;
            float wy2 = rotation.W * y2;
            float wz2 = rotation.W * z2;
            float xx2 = rotation.X * x2;
            float xy2 = rotation.X * y2;
            float xz2 = rotation.X * z2;
            float yy2 = rotation.Y * y2;
            float yz2 = rotation.Y * z2;
            float zz2 = rotation.Z * z2;

            return new Vector3(
                value.X * (1.0f - yy2 - zz2) + value.Y * (xy2 - wz2) + value.Z * (xz2 + wy2),
                value.X * (xy2 + wz2) + value.Y * (1.0f - xx2 - zz2) + value.Z * (yz2 - wx2),
                value.X * (xz2 - wy2) + value.Y * (yz2 + wx2) + value.Z * (1.0f - xx2 - yy2));
        }

        public void MakeRelative( ref Vector3 position )
        {
            position = RotateVector(position - this.position, rotation);
        }

        public void MakeRelative( ref Quaternion rotation )
        {
            rotation *= this.rotation;
        }
    }

    [Serializable]
    internal class DataContainer
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
	internal class UserData
	{
		public string id;
		public string home;
		public string name;
        public string[] boneNames;
		public Transform[] boneTransforms;
        public DataContainer[]? data;

        public void ApplyFrom(UserData other)
		{
            name = other.name;
            boneNames = other.boneNames;
            boneTransforms = other.boneTransforms;
            data = other.data;
        }
	}
}
