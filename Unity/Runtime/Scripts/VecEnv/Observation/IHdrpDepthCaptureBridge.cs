using UnityEngine;

namespace Scripts.VecEnv.Observation
{
    public interface IHdrpDepthCaptureBridge
    {
        string UnsupportedReason { get; }
        bool Configure(Camera targetCamera, RenderTexture targetTexture, LayerMask layerMask, float depthMinMeters,
            float depthMaxMeters, bool verboseDebugLogging);
    }
}
