using UnityEngine;

namespace Scripts.VecEnv
{
    public static class Extensions
    {
        public static float RangeNormalize(this float value, float min, float max)
        {
            return (value - min) * 2 / (max - min) - 1;
        }

        public static Vector3 ForceNormalizeVector(this Vector3 vec)
        {
            if (vec.magnitude > 1) return vec.normalized;
            return vec;
        }
    }
}