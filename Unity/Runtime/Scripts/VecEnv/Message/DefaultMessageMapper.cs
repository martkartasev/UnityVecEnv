using System;
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
                AgentIndex = resetParameters.Index,
                Seed = resetParameters.HasSeed ? resetParameters.Seed : null,
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

        public ExternalCommunication.Observation MapObservationToExternal(AgentObservation agentObservation)
        {
            var mapObservationToExternal = new ExternalCommunication.Observation();
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
            actionSpace.DiscreteSize.AddRange(description.DiscreteActions ?? System.Array.Empty<int>());

            var observationSpace = new Space
            {
                ContinuousSize = description.ContinuousObservations
            };
            if ((description.VisualObservations?.Length ?? 0) > 0)
            {
                observationSpace.Name = "state";
            }
            observationSpace.DiscreteSize.AddRange(description.DiscreteObservations ?? System.Array.Empty<int>());

            mapEnvironmentDescription.SingleActionSpace.Add(actionSpace);
            mapEnvironmentDescription.SingleObservationSpace.Add(observationSpace);

            if (description.VisualObservations != null)
            {
                foreach (var visualObservation in description.VisualObservations)
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
                    visualSpace.Shape.Add(visualObservation.Shape ?? System.Array.Empty<int>());
                    mapEnvironmentDescription.SingleVisualObservationSpace.Add(visualSpace);
                }
            }

            if (description.Parameters != null)
            {
                foreach (var parameter in description.Parameters)
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

                    mapEnvironmentDescription.Parameters.Add(mappedParameter);
                }
            }

            mapEnvironmentDescription.TrueNumberOfEnvs = description.AgentCount;
            return mapEnvironmentDescription;
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
    }
}
