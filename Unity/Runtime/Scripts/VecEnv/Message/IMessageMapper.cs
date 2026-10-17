using ExternalCommunication;

namespace Scripts.VecEnv.Message
{
    public interface IMessageMapper
    {
        InitializeEnvironment MapInitialize(InitializeEnvironments initialize);
        Reset MapReset(ExternalCommunication.Reset reset);
        Step MapStep(ExternalCommunication.Step step);

        ExternalCommunication.EnvironmentDescription MapEnvironmentDescription(EnvironmentDescription description);
        BatchedResetResults MapResetResults(AgentObservation[] agentObservations, Info[] infos);
        BatchedStepResults MapStepResults(AgentObservation[] agentObservations, EnvironmentState[] dones, float[] rewards, Info[] infos);
    }
}
