using System.Linq;
using ExternalCommunication;

namespace Scripts.VecEnv.Message
{
    public class DefaultMessageMapper : IMessageMapper
    {
        public ResetParameters MapResetParameters(ExternalCommunication.ResetParameters resetParameters)
        {
            return new ResetParameters
            {
                Continuous = resetParameters.Continuous.ToArray()
            };
        }

        public Reset MapReset(ExternalCommunication.Reset resetParameters)
        {
            var reset = new Reset();
            reset.ReloadScene = resetParameters.ReloadScene;
            reset.ParametersPerAgent = resetParameters.EnvsToReset.Select(MapResetParameters).ToArray();
            return reset;
        }

        public AgentAction MapAction(ExternalCommunication.Action msg)
        {
            return new AgentAction
            {
                Continuous = msg.Continuous.ToArray(),
                Discrete = msg.Discrete.ToArray()
            };
        }

        public Observation MapObservationToExternal(AgentObservation agentObservation)
        {
            var mapObservationToExternal = new Observation();
            mapObservationToExternal.Continuous.AddRange(agentObservation.Continuous);
            mapObservationToExternal.Discrete.AddRange(agentObservation.Discrete);
            return mapObservationToExternal;
        }

        public Step MapStep(ExternalCommunication.Step step)
        {
            var mapStep = new Step();
            mapStep.PhysicsStepCount = step.StepCount;
            mapStep.ApplyActionEveryStep = step.ApplyActionEveryPhysicsStep;
            mapStep.TimeScale = step.TimeScale;
            mapStep.AgentActions = step.Actions.Select(MapAction).ToArray();
            return mapStep;
        }

        public ExternalCommunication.EnvironmentDescription MapEnvironmentDescription(EnvironmentDescription description)
        {
            var mapEnvironmentDescription = new ExternalCommunication.EnvironmentDescription();
            var actionSpace = new Space
            {
                ContinuousSize = description.ContinuousActions
            };

            var observationSpace = new Space
            {
                ContinuousSize = description.ContinuousObservations
            };

            mapEnvironmentDescription.SingleActionSpace.Add(actionSpace);
            mapEnvironmentDescription.SingleObservationSpace.Add(observationSpace);

            mapEnvironmentDescription.TrueNumberOfEnvs = description.AgentCount;
            return mapEnvironmentDescription;
        }

        public InitializeEnvironment MapInitialize(InitializeEnvironments initialize)
        {
            var initializeEnvironment = new InitializeEnvironment();
            initializeEnvironment.AgentCount = initialize.RequestedNumberOfEnvs;
            return initializeEnvironment;
        }

        public ExternalCommunication.Info MapInfo(Info info)
        {
            var mapInfo = new ExternalCommunication.Info();
            if (info.Infos != null && info.Observations != null)
                for (int i = 0; i < info.Infos.Length; i++)
                {
                    mapInfo.FinalObservations.Add(MapObservationToExternal(info.Observations[i]));
                    mapInfo.FinalInfos.Add(MapInfoToExternal(info.Infos[i]));
                }

            return mapInfo;
        }


        private FinalInfo MapInfoToExternal(AgentInfo infoInfo)
        {
            var extInfo = new FinalInfo();
            extInfo.AgentIndex = infoInfo.AgentIndex;
            extInfo.EpisodeLength = infoInfo.EpisodeLength;
            extInfo.EpisodeReward = infoInfo.EpisodeReward;
            return extInfo;
        }
    }
}