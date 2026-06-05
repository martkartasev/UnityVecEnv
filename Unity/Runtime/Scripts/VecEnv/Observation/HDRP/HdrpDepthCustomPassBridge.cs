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
        private const string DepthOverrideShaderResourcePath = "VecEnv/HdrpDepthRender";

        private static bool _loggedPipelineDiagnostics;
        private CustomPassVolume _volume;
        private CameraDepthCapturePass _pass;
        private Camera _targetCamera;
        private HDAdditionalCameraData _additionalCameraData;
        public string UnsupportedReason { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStaticState()
        {
            _loggedPipelineDiagnostics = false;
            CameraDepthCapturePass.ResetStaticState();
        }

        public bool Configure(Camera targetCamera, RenderTexture targetTexture, LayerMask layerMask, float depthMinMeters,
            float depthMaxMeters, bool verboseDebugLogging)
        {
            _targetCamera = targetCamera;
            EnsureAdditionalCameraData(targetCamera);
            EnsureVolume();

            _volume.targetCamera = targetCamera;
            _volume.injectionPoint = CustomPassInjectionPoint.AfterOpaqueDepthAndNormal;
            _volume.isGlobal = true;
            _volume.priority = 1000f;

            _pass.targetCamera = targetCamera;
            _pass.targetTexture = targetTexture;
            _pass.layerMask = layerMask;
            _pass.depthMinMeters = Mathf.Max(0f, depthMinMeters);
            _pass.depthMaxMeters = Mathf.Max(depthMinMeters + 0.001f, depthMaxMeters);
            _pass.verboseDebugLogging = verboseDebugLogging;
            UnsupportedReason = GetUnsupportedReason();
            _pass.enabled = targetCamera != null && targetTexture != null && string.IsNullOrEmpty(UnsupportedReason);

            LogPipelineDiagnostics(targetCamera, targetTexture, layerMask, verboseDebugLogging);
            return string.IsNullOrEmpty(UnsupportedReason);
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
            _volume.injectionPoint = CustomPassInjectionPoint.AfterOpaqueDepthAndNormal;
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

        private static void LogPipelineDiagnostics(Camera targetCamera, RenderTexture targetTexture, LayerMask layerMask,
            bool verboseDebugLogging)
        {
            if (_loggedPipelineDiagnostics)
            {
                return;
            }

            _loggedPipelineDiagnostics = true;
            var qualityLevel = QualitySettings.GetQualityLevel();
            var qualityName = qualityLevel >= 0 && qualityLevel < QualitySettings.names.Length
                ? QualitySettings.names[qualityLevel]
                : qualityLevel.ToString();

            var pipelineAsset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            if (pipelineAsset == null)
            {
                Debug.LogWarning(
                    $"HDRP depth custom pass expected an HDRenderPipelineAsset, but GraphicsSettings.currentRenderPipeline is '{GraphicsSettings.currentRenderPipeline?.name ?? "null"}'.");
                return;
            }

            var settings = pipelineAsset.currentPlatformRenderPipelineSettings;
            if (verboseDebugLogging)
            {
                Debug.Log(
                    $"HDRP depth custom pass diagnostics: quality='{qualityName}', pipeline='{pipelineAsset.name}', " +
                    $"supportCustomPass={settings.supportCustomPass}, supportedLitShaderMode={settings.supportedLitShaderMode}, " +
                    $"camera='{targetCamera?.name ?? "null"}', layerMask={layerMask.value}, targetTexture={(targetTexture != null ? $"{targetTexture.width}x{targetTexture.height}" : "null")}.");
            }

            if (!settings.supportCustomPass)
            {
                Debug.LogWarning(
                    $"HDRP asset '{pipelineAsset.name}' has supportCustomPass disabled for the active player platform/quality level. " +
                    "Custom-pass depth observations will stay black until Custom Pass is enabled.");
            }

            if (settings.supportedLitShaderMode != RenderPipelineSettings.SupportedLitShaderMode.Both)
            {
                Debug.LogWarning(
                    $"HDRP asset '{pipelineAsset.name}' uses supportedLitShaderMode={settings.supportedLitShaderMode}. " +
                    "Unity documents that custom passes render through the forward path; player builds can fail unless Lit Shader Mode is set to Both.");
            }
        }

        private static string GetUnsupportedReason()
        {
            var pipelineAsset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            if (pipelineAsset == null)
            {
                return "GraphicsSettings.currentRenderPipeline is not an HDRenderPipelineAsset.";
            }

            var settings = pipelineAsset.currentPlatformRenderPipelineSettings;
            if (!settings.supportCustomPass)
            {
                return $"HDRP asset '{pipelineAsset.name}' has supportCustomPass disabled for the active platform.";
            }

            if (settings.supportedLitShaderMode != RenderPipelineSettings.SupportedLitShaderMode.Both)
            {
                return $"HDRP asset '{pipelineAsset.name}' uses supportedLitShaderMode={settings.supportedLitShaderMode}. " +
                       "Custom-pass renderer overrides require Lit Shader Mode to be set to Both.";
            }

            return null;
        }

        [System.Serializable]
        private sealed class CameraDepthCapturePass : CustomPass
        {
            private static bool _loggedMissingDepthShader;
            private static bool _loggedFirstExecute;
            [System.NonSerialized] internal Camera targetCamera;
            [System.NonSerialized] internal RenderTexture targetTexture;
            [System.NonSerialized] internal LayerMask layerMask;
            [System.NonSerialized] internal float depthMinMeters;
            [System.NonSerialized] internal float depthMaxMeters;
            [System.NonSerialized] internal bool verboseDebugLogging;
            [System.NonSerialized] private Material _depthOverrideMaterial;

            internal static void ResetStaticState()
            {
                _loggedMissingDepthShader = false;
                _loggedFirstExecute = false;
            }

            protected override bool executeInSceneView => false;

            protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd)
            {
                if (_depthOverrideMaterial != null)
                {
                    return;
                }

                var shader = Resources.Load<Shader>(DepthOverrideShaderResourcePath);
                if (shader == null)
                {
                    shader = Shader.Find(DepthOverrideShaderName);
                }
                if (shader != null)
                {
                    _depthOverrideMaterial = CoreUtils.CreateEngineMaterial(shader);
                    return;
                }

                if (_loggedMissingDepthShader)
                {
                    return;
                }

                _loggedMissingDepthShader = true;
                Debug.LogWarning(
                    $"Could not find HDRP depth override shader '{DepthOverrideShaderName}'. " +
                    $"Looked in Resources at '{DepthOverrideShaderResourcePath}' and via Shader.Find. " +
                    "HDRP custom-pass depth observations will remain black until the shader is included in the build.");
            }

            protected override void Execute(CustomPassContext ctx)
            {
                if (!enabled || targetCamera == null || targetTexture == null || _depthOverrideMaterial == null)
                {
                    return;
                }

                if (verboseDebugLogging && !_loggedFirstExecute)
                {
                    _loggedFirstExecute = true;
                    Debug.Log(
                        $"HDRP depth custom pass Execute() for camera '{targetCamera.name}' target='{targetTexture.width}x{targetTexture.height}' layerMask={layerMask.value} injectionPoint={injectionPoint}.");
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
