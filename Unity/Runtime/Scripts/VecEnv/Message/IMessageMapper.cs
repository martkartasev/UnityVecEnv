using ExternalCommunication;

namespace Scripts.VecEnv.Message
{
    public interface IMessageMapper
    {
        Reset MapReset(ExternalCommunication.Reset resetParameters);
        AgentAction MapAction(ExternalCommunication.Action msg);
        Step MapStep(ExternalCommunication.Step step);
        InitializeEnvironment MapInitialize(InitializeEnvironments initialize);
        ExternalCommunication.Info MapInfo(Info info);
        ExternalCommunication.Observation MapObservationToExternal(AgentObservation agentObservation);
        ExternalCommunication.EnvironmentDescription MapEnvironmentDescription(EnvironmentDescription description);
    }
}