using Unity.InferenceEngine;
using UnityEngine;

namespace Scripts.VecEnv.Inference
{
    public class InferenceHelper
    {
        private Worker _worker;
        public readonly ModelAsset InferencePolicy;

        public InferenceHelper(ModelAsset modelAsset)
        {
            var runtimeModel = ModelLoader.Load(modelAsset);
            _worker = new Worker(runtimeModel, BackendType.CPU);
            InferencePolicy = modelAsset;
        }

        public float[] DoInference(float[] data)
        {
            TensorShape shape = new TensorShape(1, data.Length);
            Tensor<float> inputTensor = new Tensor<float>(shape, data);

            _worker.Schedule(inputTensor);
            Tensor<float> outputTensor = _worker.PeekOutput() as Tensor<float>;
            
            var result = outputTensor?.ReadbackAndClone();
            var array = result?.DownloadToArray();

            inputTensor.Dispose();
            outputTensor?.Dispose();
            result?.Dispose();

            return array;
        }
    }
}