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
        private readonly Dictionary<string, VisualObservationDescription> _visualDescriptionsByKey;
        private bool _disposed;

        public readonly ModelAsset PolicyAsset;

        public InferenceHelper(ModelAsset modelAsset, VisualObservationDescription[] visualDescriptions = null)
        {
            _model = ModelLoader.Load(modelAsset);
            _worker = new Worker(_model, BackendType.CPU);
            PolicyAsset = modelAsset;
            _visualDescriptionsByKey = BuildVisualDescriptionLookup(visualDescriptions);

            _actionContinuous = _model.outputs.Find(output => output.name == "action_continuous");
            _actionDiscrete = _model.outputs.Find(output => output.name == "action_discrete");
            _obsDiscrete = _model.inputs.Find(input => input.name == "obs_discrete");
            _obsContinuous = _model.inputs.Find(input => input.name == "obs_continuous");
            _visualInputs = _model.inputs
                .Where(input => !string.IsNullOrWhiteSpace(input.name) &&
                                input.name.StartsWith(VisualInputPrefix, StringComparison.Ordinal))
                .ToArray();
        }

        public AgentAction DoInference(AgentObservation observation)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(InferenceHelper));
            }

            var inputTensors = new List<Tensor>(_model.inputs.Count);
            try
            {
                BindInputs(observation, inputTensors);
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
                if (_visualDescriptionsByKey.Count == 0)
                {
                    throw new ArgumentException(
                        "Model defines visual observation inputs, but no visual observation descriptions were provided when creating the inference helper.");
                }

                var visualLookup = BuildVisualLookup(observation);
                foreach (var visualInput in _visualInputs)
                {
                    var visualKey = GetVisualObservationLookupKey(visualInput.name);
                    if (!visualLookup.TryGetValue(visualKey, out var binding))
                    {
                        throw new ArgumentException(
                            $"Model expects visual input '{visualInput.name}', but no matching visual observation named '{visualKey}' was provided.");
                    }

                    var inputTensor = CreateVisualInputTensor(binding.Observation, binding.Description, visualInput);
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
            VisualObservationDescription description,
            Model.Input modelInput)
        {
            if (description.Shape == null || description.Shape.Length == 0)
            {
                throw new ArgumentException($"Visual observation '{description.Name}' has no shape metadata.");
            }

            var sourceShape = description.Shape;
            var sourceElementCount = sourceShape.Aggregate(1, (acc, dim) => acc * dim);
            var targetShape = ResolveVisualTensorShape(modelInput, sourceShape);
            var targetElementCount = targetShape.Aggregate(1, (acc, dim) => acc * dim);
            var replicateSingleChannel = ShouldReplicateSingleChannel(sourceShape, targetShape);
            var tensorShape = CreateTensorShapeWithBatch(targetShape);

            switch (description.DataType)
            {
                case VisualObservationDataType.UInt8:
                {
                    if (visualObservation.Data == null || visualObservation.Data.Length != sourceElementCount)
                    {
                        throw new ArgumentException(
                            $"Visual observation '{description.Name}' has {visualObservation.Data?.Length ?? 0} bytes, expected {sourceElementCount}.");
                    }

                    var values = ConvertUInt8VisualData(visualObservation.Data, targetElementCount, replicateSingleChannel);

                    return new Tensor<float>(tensorShape, values);
                }

                case VisualObservationDataType.Float32:
                {
                    var expectedBytes = sourceElementCount * sizeof(float);
                    if (visualObservation.Data == null || visualObservation.Data.Length != expectedBytes)
                    {
                        throw new ArgumentException(
                            $"Visual observation '{description.Name}' has {visualObservation.Data?.Length ?? 0} bytes, expected {expectedBytes}.");
                    }

                    var values = ConvertFloat32VisualData(
                        visualObservation.Data,
                        sourceElementCount,
                        targetElementCount,
                        replicateSingleChannel);
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

        private static int[] ResolveVisualTensorShape(Model.Input modelInput, int[] sourceShape)
        {
            var parsedModelShape = TryParseDynamicTensorShape(modelInput.shape.ToString());
            if (parsedModelShape == null || parsedModelShape.Length != sourceShape.Length + 1)
            {
                return (int[])sourceShape.Clone();
            }

            var resolvedShape = new int[sourceShape.Length];
            for (var i = 0; i < resolvedShape.Length; i++)
            {
                resolvedShape[i] = parsedModelShape[i + 1] > 0 ? parsedModelShape[i + 1] : sourceShape[i];
            }

            if (ShapesMatch(sourceShape, resolvedShape))
            {
                return resolvedShape;
            }

            if (ShouldReplicateSingleChannel(sourceShape, resolvedShape))
            {
                return resolvedShape;
            }

            throw new ArgumentException(
                $"Visual observation '{modelInput.name}' expected shape {ShapeToString(resolvedShape)} but received {ShapeToString(sourceShape)}. " +
                "Re-export the ONNX for the current visual configuration or use a compatible visual input shape.");
        }

        private static float[] ConvertUInt8VisualData(
            byte[] sourceBytes,
            int targetElementCount,
            bool replicateSingleChannel)
        {
            if (!replicateSingleChannel)
            {
                var values = new float[targetElementCount];
                for (var i = 0; i < targetElementCount; i++)
                {
                    values[i] = sourceBytes[i];
                }

                return values;
            }

            return ReplicateSingleChannelToRgb(sourceBytes);
        }

        private static float[] ConvertFloat32VisualData(
            byte[] sourceBytes,
            int sourceElementCount,
            int targetElementCount,
            bool replicateSingleChannel)
        {
            var sourceValues = new float[sourceElementCount];
            Buffer.BlockCopy(sourceBytes, 0, sourceValues, 0, sourceBytes.Length);

            if (!replicateSingleChannel)
            {
                return sourceValues;
            }

            var values = new float[targetElementCount];
            for (var pixel = 0; pixel < sourceElementCount; pixel++)
            {
                var value = sourceValues[pixel];
                var destinationIndex = pixel * 3;
                values[destinationIndex] = value;
                values[destinationIndex + 1] = value;
                values[destinationIndex + 2] = value;
            }

            return values;
        }

        private static float[] ReplicateSingleChannelToRgb(byte[] sourceBytes)
        {
            var values = new float[sourceBytes.Length * 3];
            for (var pixel = 0; pixel < sourceBytes.Length; pixel++)
            {
                var value = sourceBytes[pixel];
                var destinationIndex = pixel * 3;
                values[destinationIndex] = value;
                values[destinationIndex + 1] = value;
                values[destinationIndex + 2] = value;
            }

            return values;
        }

        private static bool ShapesMatch(int[] left, int[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool ShouldReplicateSingleChannel(int[] sourceShape, int[] targetShape)
        {
            if (sourceShape.Length != 3 || targetShape.Length != 3)
            {
                return false;
            }

            return sourceShape[0] == targetShape[0] &&
                   sourceShape[1] == targetShape[1] &&
                   sourceShape[2] == 1 &&
                   targetShape[2] == 3;
        }

        private static int[] TryParseDynamicTensorShape(string shapeText)
        {
            if (string.IsNullOrWhiteSpace(shapeText))
            {
                return null;
            }

            var trimmed = shapeText.Trim();
            if (trimmed == "?")
            {
                return null;
            }

            trimmed = trimmed.Trim('(', ')');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return Array.Empty<int>();
            }

            var parts = trimmed.Split(',');
            var dimensions = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                var token = parts[i].Trim();
                dimensions[i] = int.TryParse(token, out var parsedValue) ? parsedValue : -1;
            }

            return dimensions;
        }

        private static string ShapeToString(int[] shape)
        {
            return $"({string.Join(", ", shape)})";
        }

        private static Dictionary<string, VisualObservationDescription> BuildVisualDescriptionLookup(
            VisualObservationDescription[] visualDescriptions)
        {
            return (visualDescriptions ?? Array.Empty<VisualObservationDescription>())
                .ToDictionary(description => SanitizeInputComponent(description.Name), description => description, StringComparer.Ordinal);
        }

        private Dictionary<string, VisualObservationBinding> BuildVisualLookup(AgentObservation observation)
        {
            var lookup = new Dictionary<string, VisualObservationBinding>(StringComparer.Ordinal);
            foreach (var visualObservation in observation.VisualObservations ?? Array.Empty<AgentVisualObservation>())
            {
                var key = SanitizeInputComponent(visualObservation.Name);
                if (!_visualDescriptionsByKey.TryGetValue(key, out var description))
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
