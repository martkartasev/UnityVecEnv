using UnityEngine;

namespace Scripts.VecEnv
{
    public static class Extensions
    {
        public static Vector3 ForceNormalizeVector(this Vector3 vec)
        {
            if (vec.magnitude > 1) return vec.normalized;
            return vec;
        }
    }
}