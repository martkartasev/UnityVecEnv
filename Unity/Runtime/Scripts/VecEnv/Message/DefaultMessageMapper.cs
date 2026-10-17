using System;
using System.Linq;
using ExternalCommunication;

namespace Scripts.VecEnv.Message
{
    public class DefaultMessageMapper : IMessageMapper
    {
        public InitializeEnvironment MapInitialize(InitializeEnvironments initialize)
        {
            var initializeEnvironment = new InitializeEnvironment
            {
                AgentCount = initialize.RequestedNumberOfEnvs,
                Parameters = initialize.Parameters.Select(MapEnvironmentParameter).ToArray(),
                SceneName = initialize.SceneName ?? string.Empty,
                ReloadScene = initialize.ReloadScene
            };

            switch (initialize.AutoResetMode)
            {
                case ExternalCommunication.AutoResetMode.NextStep:
                    initializeEnvironment.AutoResetMode = EnvironmentAutoResetMode.NextStep;
                    break;
                case ExternalCommunication.AutoResetMode.SameStep:
                    initializeEnvironment.AutoResetMode = EnvironmentAutoResetMode.SameStep;
                    break;
                default:
                    throw new NotSupportedException($"Auto-reset mode {initialize.AutoResetMode} is not supported.");
            }

            return initializeEnvironment;
        }

        public Reset MapReset(ExternalCommunication.Reset reset)
        {
            var mapReset = new Reset();
            mapReset.ReloadScene = reset.ReloadScene;
            mapReset.ParametersPerAgent = reset.EnvsToReset.Select(MapResetParameters).ToArray();
            return mapReset;
        }

        public Step MapStep(ExternalCommunication.Step step)
        {
            var mapStep = new Step();
            mapStep.PhysicsStepCount = step.StepCount;
            mapStep.ApplyActionEveryStep = step.ApplyActionEveryPhysicsStep;
            mapStep.TimeScale = step.TimeScale;
            mapStep.AgentActions = step.Actions.Select(MapAction).ToArray();
            mapStep.UiStrings = step.UiStrings.ToArray();
            return mapStep;
        }

        public ExternalCommunication.EnvironmentDescription MapEnvironmentDescription(EnvironmentDescription description)
        {
            var mapEnvironmentDescription = new ExternalCommunication.EnvironmentDescription();
            var actionSpace = new Space
            {
                ContinuousSize = description.ContinuousActions,
            };
            actionSpace.DiscreteSize.AddRange(description.DiscreteActions ?? Array.Empty<int>());

            var observationSpace = new Space
            {
                ContinuousSize = description.ContinuousObservations
            };
            if ((description.VisualObservations?.Length ?? 0) > 0)
            {
                observationSpace.Name = "state";
            }
            observationSpace.DiscreteSize.AddRange(description.DiscreteObservations ?? Array.Empty<int>());

            mapEnvironmentDescription.SingleActionSpace.Add(actionSpace);
            mapEnvironmentDescription.SingleObservationSpace.Add(observationSpace);

            if (description.VisualObservations != null)
            {
                foreach (var visualObservation in description.VisualObservations)
                {
                    mapEnvironmentDescription.SingleVisualObservationSpace.Add(MapVisualObservationSpace(visualObservation));
                }
            }

            if (description.Parameters != null)
            {
                foreach (var parameter in description.Parameters)
                {
                    mapEnvironmentDescription.Parameters.Add(MapEnvironmentParameter(parameter));
                }
            }

            mapEnvironmentDescription.TrueNumberOfEnvs = description.AgentCount;
            return mapEnvironmentDescription;
        }

        public BatchedResetResults MapResetResults(AgentObservation[] agentObservations, Info[] infos)
        {
            var customIndices = BatchedCustomInfoEncoder.BuildIndices(infos);
            return new BatchedResetResults
            {
                Observation = BatchedObservationEncoder.Encode(agentObservations),
                CustomIndicesI32 = ByteStringUtils.IntArrayToByteString(customIndices),
                Custom = BatchedCustomInfoEncoder.Encode(infos, customIndices)
            };
        }

        public BatchedStepResults MapStepResults(AgentObservation[] agentObservations, EnvironmentState[] dones, float[] rewards, Info[] infos)
        {
            var customIndices = BatchedCustomInfoEncoder.BuildIndices(infos);
            var finalIndices = BatchedObservationEncoder.BuildFinalIndices(dones);
            return new BatchedStepResults
            {
                Observation = BatchedObservationEncoder.Encode(agentObservations),
                RewardsF32 = ByteStringUtils.FloatArrayToByteString(rewards),
                Dones = ByteStringUtils.StateArrayToByteString(dones, EnvironmentState.Done),
                Truncates = ByteStringUtils.StateArrayToByteString(dones, EnvironmentState.Truncated),
                CustomIndicesI32 = ByteStringUtils.IntArrayToByteString(customIndices),
                Custom = BatchedCustomInfoEncoder.Encode(infos, customIndices),
                FinalIndicesI32 = ByteStringUtils.IntArrayToByteString(finalIndices),
                FinalInfo = BatchedObservationEncoder.EncodeFinalInfo(agentObservations, infos, finalIndices)
            };
        }

        private static ResetParameters MapResetParameters(ExternalCommunication.ResetParameters resetParameters)
        {
            return new ResetParameters
            {
                AgentIndex = resetParameters.Index,
                Seed = resetParameters.HasSeed ? resetParameters.Seed : null,
                Continuous = resetParameters.Continuous.ToArray()
            };
        }

        private static AgentAction MapAction(ExternalCommunication.Action msg)
        {
            return new AgentAction
            {
                Continuous = msg.Continuous.ToArray(),
                Discrete = msg.Discrete.ToArray()
            };
        }

        private static EnvironmentParameter MapEnvironmentParameter(ExternalCommunication.EnvironmentParameter parameter)
        {
            var mappedParameter = new EnvironmentParameter
            {
                Key = parameter.Key ?? string.Empty
            };

            switch (parameter.ValueCase)
            {
                case ExternalCommunication.EnvironmentParameter.ValueOneofCase.StringValue:
                    mappedParameter.ValueType = EnvironmentParameterValueType.String;
                    mappedParameter.StringValue = parameter.StringValue ?? string.Empty;
                    break;
                case ExternalCommunication.EnvironmentParameter.ValueOneofCase.FloatValue:
                    mappedParameter.ValueType = EnvironmentParameterValueType.Float;
                    mappedParameter.FloatValue = parameter.FloatValue;
                    break;
                case ExternalCommunication.EnvironmentParameter.ValueOneofCase.IntValue:
                    mappedParameter.ValueType = EnvironmentParameterValueType.Int;
                    mappedParameter.IntValue = parameter.IntValue;
                    break;
                default:
                    throw new InvalidOperationException($"Environment parameter '{mappedParameter.Key}' is missing a typed value.");
            }

            return mappedParameter;
        }

        private static ExternalCommunication.EnvironmentParameter MapEnvironmentParameter(EnvironmentParameter parameter)
        {
            var mappedParameter = new ExternalCommunication.EnvironmentParameter
            {
                Key = parameter.Key ?? string.Empty
            };

            switch (parameter.ValueType)
            {
                case EnvironmentParameterValueType.String:
                    mappedParameter.StringValue = parameter.StringValue ?? string.Empty;
                    break;
                case EnvironmentParameterValueType.Float:
                    mappedParameter.FloatValue = parameter.FloatValue;
                    break;
                case EnvironmentParameterValueType.Int:
                    mappedParameter.IntValue = parameter.IntValue;
                    break;
            }

            return mappedParameter;
        }

        private static VisualObservationSpace MapVisualObservationSpace(VisualObservationDescription visualObservation)
        {
            var visualSpace = new VisualObservationSpace
            {
                Name = visualObservation.Name ?? string.Empty,
                DataType = visualObservation.DataType == VisualObservationDataType.Float32
                    ? ExternalCommunication.VisualObservationDataType.Float32
                    : ExternalCommunication.VisualObservationDataType.Uint8,
                Low = visualObservation.Low,
                High = visualObservation.High
            };
            visualSpace.Shape.Add(visualObservation.Shape ?? Array.Empty<int>());
            return visualSpace;
        }
    }
}
