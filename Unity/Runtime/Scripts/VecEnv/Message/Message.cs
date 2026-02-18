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

    public enum AutoResetMode
    {
        NextStep,
        SameStep,
        Done,
    }

    public struct InitializeEnvironment
    {
        public int AgentCount;
    }

    public struct EnvironmentDescription
    {
        public int ContinuousActions;
        public int ContinuousObservations;

        public int[] DiscreteActions;
        
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
        public AgentInfo[] Infos;
        public AgentObservation[] Observations;
    }

    public struct AgentInfo
    {
        public float EpisodeReward;
        public int EpisodeLength;
        public int AgentIndex;
    }

    public struct AgentObservation
    {
        public readonly float[] Continuous;
        public readonly int[] Discrete;

        private int _continuousIndex;
        private int _discreteIndex;

        public AgentObservation(int continuousSize, int discreteSize)
        {
            Continuous = new float[continuousSize];
            Discrete = new int[discreteSize];

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
            Discrete[_continuousIndex] = value;
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