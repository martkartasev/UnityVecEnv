using UnityEngine;

namespace Scripts.VecEnv.Observation
{
    public interface IHdrpDepthCaptureBridge
    {
        void Configure(Camera targetCamera, RenderTexture targetTexture, LayerMask layerMask, float depthMinMeters,
            float depthMaxMeters);
    }
}
