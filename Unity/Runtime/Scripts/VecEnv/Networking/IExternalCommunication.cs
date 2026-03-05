using System;
using Scripts.VecEnv.Message;
using EnvironmentDescription = Scripts.VecEnv.Message.EnvironmentDescription;
using Info = Scripts.VecEnv.Message.Info;
using Reset = Scripts.VecEnv.Message.Reset;
using Step = Scripts.VecEnv.Message.Step;

namespace Scripts.VecEnv.Networking
{
    public interface IExternalCommunication : IDisposable
    {
        public Reset? FetchReset();

        public Step? FetchNextStep();
        public InitializeEnvironment? FetchInitialize();
        public bool WaitForNextMessage(int timeoutMilliseconds);
        public void StepCompleted(AgentObservation[] agentObservations, EnvironmentState[] dones, float[] rewards, Info info);
        public void ResetCompleted(AgentObservation[] agentObservations);
        public void InitializeCompleted(EnvironmentDescription initialize);
    }
}

