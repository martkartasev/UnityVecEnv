using System;
using System.Collections.Generic;
using Scripts.VecEnv.Message;

namespace Scripts.VecEnv.Core
{
    internal static class InitializationParameterUtils
    {
        public static void Replace(
            IDictionary<string, EnvironmentParameter> destination,
            IEnumerable<EnvironmentParameter> parameters)
        {
            destination.Clear();
            if (parameters == null)
            {
                return;
            }

            foreach (var parameter in parameters)
            {
                var key = NormalizeKey(parameter.Key);
                if (destination.ContainsKey(key))
                {
                    throw new InvalidOperationException($"Duplicate initialization parameter key '{key}'.");
                }

                var normalizedParameter = parameter;
                normalizedParameter.Key = key;
                destination.Add(key, normalizedParameter);
            }
        }

        public static bool TryGet(
            IReadOnlyDictionary<string, EnvironmentParameter> parameters,
            string key,
            out EnvironmentParameter parameter)
        {
            return parameters.TryGetValue(NormalizeKey(key), out parameter);
        }

        public static string GetString(
            IReadOnlyDictionary<string, EnvironmentParameter> parameters,
            string key,
            string defaultValue = "")
        {
            if (!TryGet(parameters, key, out var parameter))
            {
                return defaultValue;
            }

            EnsureType(key, parameter, EnvironmentParameterValueType.String);
            return parameter.StringValue ?? string.Empty;
        }

        public static float GetFloat(
            IReadOnlyDictionary<string, EnvironmentParameter> parameters,
            string key,
            float defaultValue = 0f)
        {
            if (!TryGet(parameters, key, out var parameter))
            {
                return defaultValue;
            }

            if (parameter.ValueType == EnvironmentParameterValueType.Int)
            {
                return parameter.IntValue;
            }

            EnsureType(key, parameter, EnvironmentParameterValueType.Float);
            return parameter.FloatValue;
        }

        public static int GetInt(
            IReadOnlyDictionary<string, EnvironmentParameter> parameters,
            string key,
            int defaultValue = 0)
        {
            if (!TryGet(parameters, key, out var parameter))
            {
                return defaultValue;
            }

            EnsureType(key, parameter, EnvironmentParameterValueType.Int);
            return parameter.IntValue;
        }

        private static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Initialization parameter key cannot be null or whitespace.", nameof(key));
            }

            return key.Trim();
        }

        private static void EnsureType(
            string key,
            EnvironmentParameter parameter,
            EnvironmentParameterValueType expectedType)
        {
            if (parameter.ValueType == expectedType)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Initialization parameter '{key}' is {parameter.ValueType}, expected {expectedType}.");
        }
    }
}
