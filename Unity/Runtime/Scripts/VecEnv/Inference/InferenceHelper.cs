using System;
using Scripts.VecEnv.Message;
using Unity.InferenceEngine;

namespace Scripts.VecEnv.Inference
{
    public class InferenceHelper //TODO: Add warnings to editor when dimensions dont match
    {
        private Worker _worker;
        public readonly ModelAsset PolicyAsset;
        private readonly Model _model;
        private readonly Model.Output _actionContinuous;
        private readonly Model.Output _actionDiscrete;
        private readonly Model.Input _obsDiscrete;
        private readonly Model.Input _obsContinuous;

        public InferenceHelper(ModelAsset modelAsset)
        {
            _model = ModelLoader.Load(modelAsset);
            _worker = new Worker(_model, BackendType.CPU);
            PolicyAsset = modelAsset;

            _actionContinuous = _model.outputs.Find(output => output.name == "action_continuous");
            _actionDiscrete = _model.outputs.Find(output => output.name == "action_discrete");
            _obsDiscrete = _model.inputs.Find(output => output.name == "obs_discrete");
            _obsContinuous = _model.inputs.Find(output => output.name == "obs_continuous");
        }

        public AgentAction DoInference(AgentObservation observation)
        {
            var inputTensor = HandleInput(observation);

            _worker.Schedule(inputTensor);
            Tensor outputTensor = _worker.PeekOutput();

            var action = HandleOutput(outputTensor);
            inputTensor.Dispose();
            return action;
        }

        private Tensor HandleInput(AgentObservation observation)
        {
            if (observation.Continuous.Length > 0) // TODO: Add model spec check
            {
                TensorShape shape = new TensorShape(1, observation.Continuous.Length);
                Tensor inputTensor = new Tensor<float>(shape, observation.Continuous);
                return inputTensor;
            }

            if (observation.Discrete.Length > 0)
            {
                TensorShape shape = new TensorShape(1, observation.Discrete.Length);
                Tensor inputTensor = new Tensor<int>(shape, observation.Discrete);
                return inputTensor;
            }

            throw new ArgumentException("Observation did not contain any continuous or discrete observations");
        }


        private AgentAction HandleOutput(Tensor outputTensor)
        {
            var agentAction = new AgentAction();
            var result = outputTensor?.ReadbackAndClone();

            if (_actionDiscrete.name != null)
            {
                var array = (result as Tensor<int>)?.DownloadToArray();
                agentAction.Discrete = array;
            }

            if (_actionContinuous.name != null)
            {
                var array = (result as Tensor<float>)?.DownloadToArray();
                agentAction.Continuous = array;
            }
            
            outputTensor?.Dispose();
            result?.Dispose();
            return agentAction;
        }
    }
}