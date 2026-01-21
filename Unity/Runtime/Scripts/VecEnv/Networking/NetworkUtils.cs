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

        public static ExternalCommunication.Quaternion ToProtoQuaternion(this Quaternion quaternion)
        {
            var newQuaternion = new ExternalCommunication.Quaternion();
            newQuaternion.X = quaternion.x;
            newQuaternion.Y = quaternion.y;
            newQuaternion.Z = quaternion.z;
            newQuaternion.W = quaternion.w;
            return newQuaternion;
        }

        public static ExternalCommunication.Vector3 ToProtoVector(this Vector3 vector)
        {
            var protoVector = new ExternalCommunication.Vector3();
            protoVector.X = vector.x;
            protoVector.Y = vector.y;
            protoVector.Z = vector.z;
            return protoVector;
        }
    }
}