using System;
using System.Collections.Generic;
using ExternalCommunication;

namespace Scripts.VecEnv.Message
{
    public static class BatchedCustomInfoEncoder
    {
        public static int[] BuildIndices(Info[] infos)
        {
            if (infos == null || infos.Length == 0)
            {
                return Array.Empty<int>();
            }

            var indices = new List<int>(infos.Length);
            for (int i = 0; i < infos.Length; i++)
            {
                if (infos[i].custom != null && infos[i].custom.HasAnyValues)
                {
                    indices.Add(i);
                }
            }

            return indices.ToArray();
        }

        public static BatchedCustomInfo Encode(Info[] infos, int[] indices)
        {
            var batchedCustomInfo = new BatchedCustomInfo();
            if (indices == null || indices.Length == 0)
            {
                return batchedCustomInfo;
            }

            var layout = new Layout();
            var globalKeyTypes = new Dictionary<string, CustomInfoValueType>(StringComparer.Ordinal);
            for (int row = 0; row < indices.Length; row++)
            {
                var custom = infos[indices[row]].custom;
                if (custom == null)
                {
                    continue;
                }

                RegisterKeys(custom.FloatValues, CustomInfoValueType.Float, globalKeyTypes, layout.FloatKeyLookup, layout.FloatKeys);
                RegisterKeys(custom.IntValues, CustomInfoValueType.Int, globalKeyTypes, layout.IntKeyLookup, layout.IntKeys);
                RegisterKeys(custom.BoolValues, CustomInfoValueType.Bool, globalKeyTypes, layout.BoolKeyLookup, layout.BoolKeys);
                RegisterKeys(custom.Vector3Values, CustomInfoValueType.Vector3, globalKeyTypes, layout.Vector3KeyLookup, layout.Vector3Keys);
                RegisterKeys(custom.QuaternionValues, CustomInfoValueType.Quaternion, globalKeyTypes, layout.QuaternionKeyLookup, layout.QuaternionKeys);
            }

            var floatValues = new float[indices.Length * layout.FloatKeys.Count];
            var floatPresent = new byte[floatValues.Length];
            var intValues = new int[indices.Length * layout.IntKeys.Count];
            var intPresent = new byte[intValues.Length];
            var boolValues = new byte[indices.Length * layout.BoolKeys.Count];
            var boolPresent = new byte[boolValues.Length];
            var vector3Values = new float[indices.Length * layout.Vector3Keys.Count * 3];
            var vector3Present = new byte[indices.Length * layout.Vector3Keys.Count];
            var quaternionValues = new float[indices.Length * layout.QuaternionKeys.Count * 4];
            var quaternionPresent = new byte[indices.Length * layout.QuaternionKeys.Count];

            for (int row = 0; row < indices.Length; row++)
            {
                var custom = infos[indices[row]].custom;
                if (custom == null)
                {
                    continue;
                }

                PopulateFloatSection(custom, row, layout.FloatKeyLookup, layout.FloatKeys.Count, floatValues, floatPresent);
                PopulateIntSection(custom, row, layout.IntKeyLookup, layout.IntKeys.Count, intValues, intPresent);
                PopulateBoolSection(custom, row, layout.BoolKeyLookup, layout.BoolKeys.Count, boolValues, boolPresent);
                PopulateVector3Section(custom, row, layout.Vector3KeyLookup, layout.Vector3Keys.Count, vector3Values, vector3Present);
                PopulateQuaternionSection(custom, row, layout.QuaternionKeyLookup, layout.QuaternionKeys.Count, quaternionValues, quaternionPresent);
            }

            batchedCustomInfo.Keys.Add(layout.FloatKeys);
            batchedCustomInfo.ValuesF32 = ByteStringUtils.FloatArrayToByteString(floatValues);
            batchedCustomInfo.Present = ByteStringUtils.ByteArrayToByteString(floatPresent);
            batchedCustomInfo.KeysI32.Add(layout.IntKeys);
            batchedCustomInfo.ValuesI32 = ByteStringUtils.IntArrayToByteString(intValues);
            batchedCustomInfo.PresentI32 = ByteStringUtils.ByteArrayToByteString(intPresent);
            batchedCustomInfo.KeysBool.Add(layout.BoolKeys);
            batchedCustomInfo.ValuesBool = ByteStringUtils.ByteArrayToByteString(boolValues);
            batchedCustomInfo.PresentBool = ByteStringUtils.ByteArrayToByteString(boolPresent);
            batchedCustomInfo.KeysVector3.Add(layout.Vector3Keys);
            batchedCustomInfo.ValuesVector3F32 = ByteStringUtils.FloatArrayToByteString(vector3Values);
            batchedCustomInfo.PresentVector3 = ByteStringUtils.ByteArrayToByteString(vector3Present);
            batchedCustomInfo.KeysQuaternion.Add(layout.QuaternionKeys);
            batchedCustomInfo.ValuesQuaternionF32 = ByteStringUtils.FloatArrayToByteString(quaternionValues);
            batchedCustomInfo.PresentQuaternion = ByteStringUtils.ByteArrayToByteString(quaternionPresent);
            return batchedCustomInfo;
        }

        private sealed class Layout
        {
            public readonly Dictionary<string, int> FloatKeyLookup = new(StringComparer.Ordinal);
            public readonly List<string> FloatKeys = new();
            public readonly Dictionary<string, int> IntKeyLookup = new(StringComparer.Ordinal);
            public readonly List<string> IntKeys = new();
            public readonly Dictionary<string, int> BoolKeyLookup = new(StringComparer.Ordinal);
            public readonly List<string> BoolKeys = new();
            public readonly Dictionary<string, int> Vector3KeyLookup = new(StringComparer.Ordinal);
            public readonly List<string> Vector3Keys = new();
            public readonly Dictionary<string, int> QuaternionKeyLookup = new(StringComparer.Ordinal);
            public readonly List<string> QuaternionKeys = new();
        }

        private static void RegisterKeys<T>(
            IReadOnlyDictionary<string, T> values,
            CustomInfoValueType valueType,
            Dictionary<string, CustomInfoValueType> globalKeyTypes,
            Dictionary<string, int> keyLookup,
            List<string> orderedKeys)
        {
            foreach (var key in values.Keys)
            {
                if (globalKeyTypes.TryGetValue(key, out var existingType) && existingType != valueType)
                {
                    throw new InvalidOperationException(
                        $"Custom info key '{key}' was emitted as both {existingType} and {valueType}.");
                }

                globalKeyTypes[key] = valueType;
                if (keyLookup.ContainsKey(key))
                {
                    continue;
                }

                keyLookup[key] = orderedKeys.Count;
                orderedKeys.Add(key);
            }
        }

        private static void PopulateFloatSection(
            CustomInfoBuilder custom,
            int row,
            Dictionary<string, int> keyLookup,
            int keyCount,
            float[] values,
            byte[] present)
        {
            if (keyCount == 0)
            {
                return;
            }

            foreach (var entry in custom.FloatValues)
            {
                var offset = row * keyCount + keyLookup[entry.Key];
                values[offset] = entry.Value;
                present[offset] = 1;
            }
        }

        private static void PopulateIntSection(
            CustomInfoBuilder custom,
            int row,
            Dictionary<string, int> keyLookup,
            int keyCount,
            int[] values,
            byte[] present)
        {
            if (keyCount == 0)
            {
                return;
            }

            foreach (var entry in custom.IntValues)
            {
                var offset = row * keyCount + keyLookup[entry.Key];
                values[offset] = entry.Value;
                present[offset] = 1;
            }
        }

        private static void PopulateBoolSection(
            CustomInfoBuilder custom,
            int row,
            Dictionary<string, int> keyLookup,
            int keyCount,
            byte[] values,
            byte[] present)
        {
            if (keyCount == 0)
            {
                return;
            }

            foreach (var entry in custom.BoolValues)
            {
                var offset = row * keyCount + keyLookup[entry.Key];
                values[offset] = entry.Value ? (byte)1 : (byte)0;
                present[offset] = 1;
            }
        }

        private static void PopulateVector3Section(
            CustomInfoBuilder custom,
            int row,
            Dictionary<string, int> keyLookup,
            int keyCount,
            float[] values,
            byte[] present)
        {
            if (keyCount == 0)
            {
                return;
            }

            foreach (var entry in custom.Vector3Values)
            {
                var keyOffset = row * keyCount + keyLookup[entry.Key];
                var valueOffset = keyOffset * 3;
                values[valueOffset] = entry.Value.x;
                values[valueOffset + 1] = entry.Value.y;
                values[valueOffset + 2] = entry.Value.z;
                present[keyOffset] = 1;
            }
        }

        private static void PopulateQuaternionSection(
            CustomInfoBuilder custom,
            int row,
            Dictionary<string, int> keyLookup,
            int keyCount,
            float[] values,
            byte[] present)
        {
            if (keyCount == 0)
            {
                return;
            }

            foreach (var entry in custom.QuaternionValues)
            {
                var keyOffset = row * keyCount + keyLookup[entry.Key];
                var valueOffset = keyOffset * 4;
                values[valueOffset] = entry.Value.x;
                values[valueOffset + 1] = entry.Value.y;
                values[valueOffset + 2] = entry.Value.z;
                values[valueOffset + 3] = entry.Value.w;
                present[keyOffset] = 1;
            }
        }
    }
}
