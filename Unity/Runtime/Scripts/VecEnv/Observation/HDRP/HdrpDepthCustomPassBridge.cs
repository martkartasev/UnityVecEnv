#if GYMVECENV_HDRP
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace Scripts.VecEnv.Observation
{
    [DisallowMultipleComponent]
    internal sealed class HdrpDepthCustomPassBridge : MonoBehaviour, IHdrpDepthCaptureBridge
    {
        private const string VolumeObjectName = "VecEnvHdrpDepthCustomPassVolume";
        private const string DepthOverrideShaderName = "Hidden/VecEnv/HdrpDepthRender";

        private CustomPassVolume _volume;
        private CameraDepthCapturePass _pass;
        private Camera _targetCamera;
        private HDAdditionalCameraData _additionalCameraData;

        public void Configure(Camera targetCamera, RenderTexture targetTexture, LayerMask layerMask, float depthMinMeters,
            float depthMaxMeters)
        {
            _targetCamera = targetCamera;
            EnsureAdditionalCameraData(targetCamera);
            EnsureVolume();

            _volume.targetCamera = targetCamera;
            _volume.injectionPoint = CustomPassInjectionPoint.BeforeTransparent;
            _volume.isGlobal = true;
            _volume.priority = 1000f;

            _pass.targetCamera = targetCamera;
            _pass.targetTexture = targetTexture;
            _pass.layerMask = layerMask;
            _pass.depthMinMeters = Mathf.Max(0f, depthMinMeters);
            _pass.depthMaxMeters = Mathf.Max(depthMinMeters + 0.001f, depthMaxMeters);
            _pass.enabled = targetCamera != null && targetTexture != null;
        }

        private void OnEnable()
        {
            EnsureVolume();
        }

        private void LateUpdate()
        {
            if (_volume != null)
            {
                _volume.targetCamera = _targetCamera;
            }
        }

        private void OnDisable()
        {
            if (_pass != null)
            {
                _pass.enabled = false;
                _pass.targetTexture = null;
                _pass.targetCamera = null;
            }

            DetachFromAdditionalCameraData();
        }

        private void OnDestroy()
        {
            DetachFromAdditionalCameraData();

            if (_volume != null)
            {
                _volume.customPasses.Clear();
                Destroy(_volume);
                _volume = null;
            }
        }

        private void EnsureVolume()
        {
            if (_volume == null)
            {
                _volume = GetComponent<CustomPassVolume>();
                if (_volume == null)
                {
                    _volume = gameObject.AddComponent<CustomPassVolume>();
                }
            }

            _volume.hideFlags = HideFlags.HideAndDontSave;
            _volume.name = VolumeObjectName;
            _volume.fadeRadius = 0f;
            _volume.priority = 1000f;
            _volume.injectionPoint = CustomPassInjectionPoint.BeforeTransparent;
            _volume.isGlobal = true;

            if (_pass != null)
            {
                return;
            }

            if (_volume.customPasses == null)
            {
                _volume.customPasses = new List<CustomPass>();
            }

            foreach (var existingPass in _volume.customPasses)
            {
                if (existingPass is CameraDepthCapturePass depthPass)
                {
                    _pass = depthPass;
                    return;
                }
            }

            _pass = new CameraDepthCapturePass
            {
                name = "VecEnv HDRP Depth Capture"
            };
            _volume.customPasses.Add(_pass);
        }

        private void EnsureAdditionalCameraData(Camera targetCamera)
        {
            if (targetCamera == null)
            {
                DetachFromAdditionalCameraData();
                return;
            }

            var nextAdditionalCameraData = targetCamera.GetComponent<HDAdditionalCameraData>();
            if (nextAdditionalCameraData == null)
            {
                nextAdditionalCameraData = targetCamera.gameObject.AddComponent<HDAdditionalCameraData>();
            }

            if (_additionalCameraData != nextAdditionalCameraData)
            {
                DetachFromAdditionalCameraData();
                _additionalCameraData = nextAdditionalCameraData;
            }

            _additionalCameraData.customRenderingSettings = true;
            _additionalCameraData.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.CustomPass, true);

            var overrideMask = _additionalCameraData.renderingPathCustomFrameSettingsOverrideMask;
            overrideMask.mask[(uint)FrameSettingsField.CustomPass] = true;
            _additionalCameraData.renderingPathCustomFrameSettingsOverrideMask = overrideMask;
        }

        private void DetachFromAdditionalCameraData()
        {
            if (_additionalCameraData == null)
            {
                return;
            }

            _additionalCameraData = null;
        }

        [System.Serializable]
        private sealed class CameraDepthCapturePass : CustomPass
        {
            [System.NonSerialized] internal Camera targetCamera;
            [System.NonSerialized] internal RenderTexture targetTexture;
            [System.NonSerialized] internal LayerMask layerMask;
            [System.NonSerialized] internal float depthMinMeters;
            [System.NonSerialized] internal float depthMaxMeters;
            [System.NonSerialized] private Material _depthOverrideMaterial;

            protected override bool executeInSceneView => false;

            protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
            {
                if (_depthOverrideMaterial != null)
                {
                    return;
                }

                var shader = Shader.Find(DepthOverrideShaderName);
                if (shader != null)
                {
                    _depthOverrideMaterial = CoreUtils.CreateEngineMaterial(shader);
                }
            }

            protected override void Execute(CustomPassContext ctx)
            {
                if (!enabled || targetCamera == null || targetTexture == null || _depthOverrideMaterial == null)
                {
                    return;
                }

                _depthOverrideMaterial.SetFloat("_DepthMetersMin", Mathf.Max(0f, depthMinMeters));
                _depthOverrideMaterial.SetFloat("_DepthMetersMax", Mathf.Max(depthMinMeters + 0.001f, depthMaxMeters));

                CustomPassUtils.RenderFromCamera(
                    ctx,
                    targetCamera,
                    targetTexture,
                    ClearFlag.Color | ClearFlag.Depth,
                    layerMask,
                    renderQueueFilter: RenderQueueType.All,
                    overrideMaterial: _depthOverrideMaterial,
                    overrideMaterialIndex: 0);
            }

            protected override void Cleanup()
            {
                CoreUtils.Destroy(_depthOverrideMaterial);
                _depthOverrideMaterial = null;
            }
        }
    }
}
#endif
