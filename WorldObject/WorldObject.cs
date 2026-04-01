using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace CalibrationEnv
{
    [Serializable]
    internal class WorldObject
    {
        public string id;
        public Transform transform;
        public DataContainer[] data;
    }

    [Serializable]
    internal struct Transform
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 scale;
    }

    [Serializable]
    internal struct DataContainer
    {
        public string type;
        public string name;
        public object value;
    }
}
