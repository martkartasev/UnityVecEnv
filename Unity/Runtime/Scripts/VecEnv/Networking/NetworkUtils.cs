using UnityEngine;

namespace Scripts.VecEnv.Networking
{
    public static class NetworkUtils
    {
        public static Quaternion ToUnityQuaternion(this ExternalCommunication.Quaternion vector)
        {
            return new Quaternion(vector.X, vector.Y, vector.Z, vector.W);
        }

        public static Vector3 ToUnityVector(this ExternalCommunication.Vector3 vector)
        {
            return new Vector3(vector.X, vector.Y, vector.Z);
        }
    }
}