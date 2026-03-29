using System;
using System.Collections.Generic;
using System.Linq;
using Scripts.VecEnv.Message;
using Unity.InferenceEngine;

namespace Scripts.VecEnv.Inference
{
    public class InferenceHelper : IDisposable //TODO: Add warnings to editor when dimensions dont match
    {
        private const string VisualInputPrefix = "obs_visual_";

        private readonly Worker _worker;
        private readonly Model _model;
        private readonly Model.Output _actionContinuous;
        private readonly Model.Output _actionDiscrete;
        private readonly Model.Input _obsDiscrete;
        private readonly Model.Input _obsContinuous;
        private readonly Model.Input[] _visualInputs;
        private bool _disposed;

        public readonly ModelAsset PolicyAsset;

        public InferenceHelper(ModelAsset modelAsset)
        {
            _model = ModelLoader.Load(modelAsset);
            _worker = new Worker(_model, BackendType.CPU);
            PolicyAsset = modelAsset;

            _actionContinuous = _model.outputs.Find(output => output.name == "action_continuous");
            _actionDiscrete = _model.outputs.Find(output => output.name == "action_discrete");
            _obsDiscrete = _model.inputs.Find(input => input.name == "obs_discrete");
            _obsContinuous = _model.inputs.Find(input => input.name == "obs_continuous");
            _visualInputs = _model.inputs
                .Where(input => !string.IsNullOrWhiteSpace(input.name) &&
                                input.name.StartsWith(VisualInputPrefix, StringComparison.Ordinal))
                .ToArray();
        }

        public AgentAction DoInference(AgentObservation observation, VisualObservationDescription[] visualDescriptions)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(InferenceHelper));
            }

            var inputTensors = new List<Tensor>(_model.inputs.Count);
            try
            {
                BindInputs(observation, visualDescriptions, inputTensors);
                _worker.Schedule();
                return HandleOutput();
            }
            finally
            {
                foreach (var tensor in inputTensors)
                {
                    tensor?.Dispose();
                }
            }
        }

        private void BindInputs(
            AgentObservation observation,
            VisualObservationDescription[] visualDescriptions,
            List<Tensor> inputTensors)
        {
            if (_obsContinuous.name != null)
            {
                if (observation.Continuous == null || observation.Continuous.Length == 0)
                {
                    throw new ArgumentException("Model expects 'obs_continuous' but observation did not provide continuous values.");
                }

                var inputTensor = new Tensor<float>(new TensorShape(1, observation.Continuous.Length), observation.Continuous);
                inputTensors.Add(inputTensor);
                _worker.SetInput(_obsContinuous.name, inputTensor);
            }

            if (_obsDiscrete.name != null)
            {
                if (observation.Discrete == null || observation.Discrete.Length == 0)
                {
                    throw new ArgumentException("Model expects 'obs_discrete' but observation did not provide discrete values.");
                }

                var inputTensor = new Tensor<int>(new TensorShape(1, observation.Discrete.Length), observation.Discrete);
                inputTensors.Add(inputTensor);
                _worker.SetInput(_obsDiscrete.name, inputTensor);
            }

            if (_visualInputs.Length > 0)
            {
                var visualLookup = BuildVisualLookup(observation, visualDescriptions);
                foreach (var visualInput in _visualInputs)
                {
                    var visualKey = GetVisualObservationLookupKey(visualInput.name);
                    if (!visualLookup.TryGetValue(visualKey, out var binding))
                    {
                        throw new ArgumentException(
                            $"Model expects visual input '{visualInput.name}', but no matching visual observation named '{visualKey}' was provided.");
                    }

                    var inputTensor = CreateVisualInputTensor(binding.Observation, binding.Description);
                    inputTensors.Add(inputTensor);
                    _worker.SetInput(visualInput.name, inputTensor);
                }
            }

            if (inputTensors.Count == 0)
            {
                throw new ArgumentException("Model did not define any recognized observation inputs.");
            }
        }

        private AgentAction HandleOutput()
        {
            var agentAction = new AgentAction();

            if (_actionDiscrete.name != null)
            {
                using var outputTensor = _worker.PeekOutput(_actionDiscrete.name);
                using var result = outputTensor?.ReadbackAndClone() as Tensor<int>;
                agentAction.Discrete = result?.DownloadToArray();
            }

            if (_actionContinuous.name != null)
            {
                using var outputTensor = _worker.PeekOutput(_actionContinuous.name);
                using var result = outputTensor?.ReadbackAndClone() as Tensor<float>;
                agentAction.Continuous = result?.DownloadToArray();
            }

            return agentAction;
        }

        private static Tensor CreateVisualInputTensor(
            AgentVisualObservation visualObservation,
            VisualObservationDescription description)
        {
            if (description.Shape == null || description.Shape.Length == 0)
            {
                throw new ArgumentException($"Visual observation '{description.Name}' has no shape metadata.");
            }

            var elementCount = description.Shape.Aggregate(1, (acc, dim) => acc * dim);
            var tensorShape = CreateTensorShapeWithBatch(description.Shape);

            switch (description.DataType)
            {
                case VisualObservationDataType.UInt8:
                {
                    if (visualObservation.Data == null || visualObservation.Data.Length != elementCount)
                    {
                        throw new ArgumentException(
                            $"Visual observation '{description.Name}' has {visualObservation.Data?.Length ?? 0} bytes, expected {elementCount}.");
                    }

                    var values = new float[elementCount];
                    for (var i = 0; i < elementCount; i++)
                    {
                        values[i] = visualObservation.Data[i];
                    }

                    return new Tensor<float>(tensorShape, values);
                }

                case VisualObservationDataType.Float32:
                {
                    var expectedBytes = elementCount * sizeof(float);
                    if (visualObservation.Data == null || visualObservation.Data.Length != expectedBytes)
                    {
                        throw new ArgumentException(
                            $"Visual observation '{description.Name}' has {visualObservation.Data?.Length ?? 0} bytes, expected {expectedBytes}.");
                    }

                    var values = new float[elementCount];
                    Buffer.BlockCopy(visualObservation.Data, 0, values, 0, expectedBytes);
                    return new Tensor<float>(tensorShape, values);
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(description.DataType), description.DataType, null);
            }
        }

        private static TensorShape CreateTensorShapeWithBatch(int[] shape)
        {
            var fullShape = new int[shape.Length + 1];
            fullShape[0] = 1;
            Array.Copy(shape, 0, fullShape, 1, shape.Length);
            return new TensorShape(fullShape);
        }

        private static Dictionary<string, VisualObservationBinding> BuildVisualLookup(
            AgentObservation observation,
            VisualObservationDescription[] visualDescriptions)
        {
            var descriptions = (visualDescriptions ?? Array.Empty<VisualObservationDescription>())
                .ToDictionary(description => SanitizeInputComponent(description.Name), description => description, StringComparer.Ordinal);

            var lookup = new Dictionary<string, VisualObservationBinding>(StringComparer.Ordinal);
            foreach (var visualObservation in observation.VisualObservations ?? Array.Empty<AgentVisualObservation>())
            {
                var key = SanitizeInputComponent(visualObservation.Name);
                if (!descriptions.TryGetValue(key, out var description))
                {
                    continue;
                }

                lookup[key] = new VisualObservationBinding(visualObservation, description);
            }

            return lookup;
        }

        private static string GetVisualObservationLookupKey(string inputName)
        {
            if (string.IsNullOrWhiteSpace(inputName) || !inputName.StartsWith(VisualInputPrefix, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            return inputName.Substring(VisualInputPrefix.Length);
        }

        private static string SanitizeInputComponent(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "input";
            }

            var chars = name.Trim().ToLowerInvariant().ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (!(char.IsLetterOrDigit(chars[i]) || chars[i] == '_'))
                {
                    chars[i] = '_';
                }
            }

            var sanitized = new string(chars).Trim('_');
            if (string.IsNullOrEmpty(sanitized))
            {
                sanitized = "input";
            }

            if (char.IsDigit(sanitized[0]))
            {
                sanitized = $"input_{sanitized}";
            }

            return sanitized;
        }

        private readonly struct VisualObservationBinding
        {
            public readonly AgentVisualObservation Observation;
            public readonly VisualObservationDescription Description;

            public VisualObservationBinding(AgentVisualObservation observation, VisualObservationDescription description)
            {
                Observation = observation;
                Description = description;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _worker.Dispose();
            _disposed = true;
        }
    }
}
