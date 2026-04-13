using System;
using System.Collections.Generic;
using ExternalCommunication;
using UnityEngine;
using Quaternion = UnityEngine.Quaternion;
using Vector3 = UnityEngine.Vector3;

namespace Scripts.VecEnv.Message
{
    public enum EnvironmentState
    {
        Running,
        Done,
        Truncated
    }

    public struct InitializeEnvironment
    {
        public int AgentCount;
    }

    public enum EnvironmentParameterValueType
    {
        String,
        Float,
        Int
    }

    public struct EnvironmentParameter
    {
        public string Key;
        public EnvironmentParameterValueType ValueType;
        public string StringValue;
        public float FloatValue;
        public int IntValue;
    }

    public sealed class EnvironmentParameterCollector
    {
        private readonly List<EnvironmentParameter> _parameters = new();
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

        public void Add(string key, string value)
        {
            AddInternal(new EnvironmentParameter
            {
                Key = NormalizeKey(key),
                ValueType = EnvironmentParameterValueType.String,
                StringValue = value ?? string.Empty
            });
        }

        public void Add(string key, float value)
        {
            AddInternal(new EnvironmentParameter
            {
                Key = NormalizeKey(key),
                ValueType = EnvironmentParameterValueType.Float,
                FloatValue = value
            });
        }

        public void Add(string key, int value)
        {
            AddInternal(new EnvironmentParameter
            {
                Key = NormalizeKey(key),
                ValueType = EnvironmentParameterValueType.Int,
                IntValue = value
            });
        }

        public EnvironmentParameter[] ToArray()
        {
            return _parameters.ToArray();
        }

        private void AddInternal(EnvironmentParameter parameter)
        {
            if (!_keys.Add(parameter.Key))
            {
                throw new InvalidOperationException($"Duplicate environment parameter key '{parameter.Key}'.");
            }

            _parameters.Add(parameter);
        }

        private static string NormalizeKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("Environment parameter key cannot be null or whitespace.", nameof(key));
            }

            return key.Trim();
        }
    }

    public struct EnvironmentDescription
    {
        public int ContinuousActions;
        public int ContinuousObservations;

        public int[] DiscreteActions;
        public int[] DiscreteObservations;
        public VisualObservationDescription[] VisualObservations;
        public EnvironmentParameter[] Parameters;
        
        public int AgentCount;
    }

    public struct Step
    {
        public AgentAction[] AgentActions;
        public int PhysicsStepCount;
        public float TimeScale;
        public bool ApplyActionEveryStep;
    }

    public struct Info
    {
        public float EpisodeReward;
        public int EpisodeLength;
        public int AgentIndex;
        public AgentObservation FinalObservation;
        public Dictionary<String, float> custom;
    }

    public enum VisualObservationDataType
    {
        UInt8,
        Float32
    }

    public struct VisualObservationDescription
    {
        public string Name;
        public int[] Shape;
        public VisualObservationDataType DataType;
        public float Low;
        public float High;
    }

    public struct AgentVisualObservation
    {
        public string Name;
        public byte[] Data;
        public bool DebugLoggingEnabled;
    }

    public struct AgentObservation
    {
        public readonly float[] Continuous;
        public readonly int[] Discrete;
        public AgentVisualObservation[] VisualObservations;

        private int _continuousIndex;
        private int _discreteIndex;

        public AgentObservation(int continuousSize, int discreteSize)
        {
            Continuous = new float[continuousSize];
            Discrete = new int[discreteSize];
            VisualObservations = Array.Empty<AgentVisualObservation>();

            _continuousIndex = 0;
            _discreteIndex = 0;
        }

        public AgentObservation AppendContinuous(float value)
        {
            Continuous[_continuousIndex] = value;
            _continuousIndex++;
            return this;
        }

        public AgentObservation AppendContinuous(float[] values)
        {
            foreach (var value in values)
            {
                AppendContinuous(value);
            }

            return this;
        }

        public AgentObservation AppendContinuous(Vector3 value)
        {
            AppendContinuous(value.x);
            AppendContinuous(value.y);
            AppendContinuous(value.z);
            return this;
        }

        public AgentObservation AppendContinuous(Quaternion value)
        {
            AppendContinuous(value.x);
            AppendContinuous(value.y);
            AppendContinuous(value.z);
            AppendContinuous(value.w);
            return this;
        }

        public AgentObservation AppendDiscrete(int value)
        {
            Discrete[_discreteIndex] = value;
            _discreteIndex++;
            return this;
        }

        public void LogContinuous()
        {
            string log = "";
            foreach (var continuous in Continuous)
            {
                log += continuous.ToString("0.00") + "  ";
            }

            Debug.Log(log);
        }
    }

    public struct Reset
    {
        public ResetParameters[] ParametersPerAgent;
        public bool ReloadScene;
    }

    public struct ResetParameters
    {
        public int AgentIndex;
        public float[] Continuous;
    }

    public struct AgentAction
    {
        public float[] Continuous;
        public int[] Discrete;

        public AgentAction(int continuousSize, int discreteSize)
        {
            Continuous = new float[continuousSize];
            Discrete = new int[discreteSize];
        }
    }
}
