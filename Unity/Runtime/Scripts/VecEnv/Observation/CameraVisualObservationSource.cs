using System;
using Scripts.VecEnv.Message;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Scripts.VecEnv.Observation
{
    public enum VisualObservationColorMode
    {
        Grayscale,
        Color
    }

    public enum VisualObservationContentMode
    {
        Color,
        Depth
    }

    public enum HdrpDepthCaptureMode
    {
        CameraDepthTexture = 0,
        CustomPassDepth = 3
    }

    public enum VisualObservationCaptureMode
    {
        EarlyAsync,
        OnDemandBlocking
    }

    public class CameraVisualObservationSource : AgentVisualObservationSource
    {
        private const TextureFormat ReadbackFormat = TextureFormat.RGBA32;
        private const int ReadbackChannels = 4;
        private const string DepthEncodeShaderName = "Hidden/VecEnv/DepthObservation";
        private const string DepthEncodeShaderResourcePath = "VecEnv/VecEnvDepthObservation";

        private static readonly Type HdRenderPipelineAssetType =
            Type.GetType("UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset, Unity.RenderPipelines.HighDefinition.Runtime", false);

        private static readonly Type HdrpDepthCustomPassBridgeType =
            Type.GetType("Scripts.VecEnv.Observation.HdrpDepthCustomPassBridge, MKA.GymVecEnv.Hdrp", false);

        [Header("Demo")]
        public GameObject demoObject;

        [Header("Camera")]
        public Camera sourceCamera;
        public Vector3 localPosition = new(0f, 2.0f, -4.5f);
        public Vector3 localEulerAngles = new(18f, 0f, 0f);
        public LayerMask cullingMask = ~0;
        public CameraClearFlags clearFlags = CameraClearFlags.SolidColor;
        public Color backgroundColor = Color.black;
        public bool orthographic;
        public float fieldOfView = 60f;
        public float orthographicSize = 5f;
        public float nearClipPlane = 0.05f;
        public float farClipPlane = 200f;

        [Header("Output")]
        public int width = 84;
        public int height = 84;
        public VisualObservationContentMode contentMode = VisualObservationContentMode.Color;
        public VisualObservationColorMode colorMode = VisualObservationColorMode.Grayscale;
        public VisualObservationDataType dataType = VisualObservationDataType.UInt8;

        [Header("Depth")]
        public float depthRangeMinMeters;
        public float depthRangeMaxMeters = 20f;
        public HdrpDepthCaptureMode hdrpDepthCaptureMode = HdrpDepthCaptureMode.CustomPassDepth;

        [Header("Capture")]
        public VisualObservationCaptureMode captureMode = VisualObservationCaptureMode.EarlyAsync;

        private Camera _runtimeCamera;
        private GameObject _runtimeCameraObject;
        private RenderTexture _renderTexture;
        private RenderTexture _captureRenderTexture;
        private Texture2D _readbackTexture;
        private Texture2D _debugPreviewTexture;
        private NativeArray<byte> _readbackBuffer;
        private AsyncGPUReadbackRequest _pendingRequest;
        private byte[] _latestObservationBytes;
        private float[] _floatScratch;
        private Material _depthEncodeMaterial;
        private Material _demoMaterial;
        private Color32[] _debugPreviewPixels;
        private Component _hdrpDepthCustomPassBridgeComponent;
        private IHdrpDepthCaptureBridge _hdrpDepthCustomPassBridge;

        private bool _capturePending;
        private bool _hasLatestObservation;
        private bool _asyncReadbackDisabled;
        private bool _loggedAsyncReadbackFallback;
        private bool _hdrpCustomPassRuntimeSupported = true;
        private bool _loggedHdrpCustomPassFallback;
        private bool _debugLoggedNonZeroCapture;
        private int _debugCaptureSummaryCount;
        private string _lastReadbackMode = "None";

        private GraphicsFormat PreferredRenderTextureFormat => AsyncGpuReadbackSupport.SelectPreferredReadbackFormat();
        private int PixelCount => width * height;
        private int OutputChannelCount => IsDepthMode ? 1 : (UsesColorOutput ? 3 : 1);
        private int OutputElementCount => PixelCount * OutputChannelCount;
        private int OutputByteCount => UsesFloatOutput ? OutputElementCount * sizeof(float) : OutputElementCount;
        private bool IsDepthMode => contentMode == VisualObservationContentMode.Depth;
        private bool UsesColorOutput => !IsDepthMode && colorMode == VisualObservationColorMode.Color;
        private bool UsesFloatOutput => dataType == VisualObservationDataType.Float32;
        private float DepthPreviewMinMeters => Mathf.Max(0f, depthRangeMinMeters);
        private float DepthPreviewMaxMeters => Mathf.Max(depthRangeMinMeters, depthRangeMaxMeters);
        private float DepthEncodeMinMeters => Mathf.Max(0f, depthRangeMinMeters);
        private float DepthEncodeMaxMeters => Mathf.Max(depthRangeMinMeters + 0.001f, depthRangeMaxMeters);
        private string CapturePathLabel => IsDepthMode
            ? (UseHdrpCustomPassDepth ? "HdrpCustomPass" : "CameraDepthTexture")
            : "Color";

        private static bool IsHdrpActive =>
            HdRenderPipelineAssetType != null &&
            GraphicsSettings.currentRenderPipeline != null &&
            HdRenderPipelineAssetType.IsInstanceOfType(GraphicsSettings.currentRenderPipeline);

        private bool UseHdrpCustomPassDepth =>
            IsDepthMode &&
            hdrpDepthCaptureMode == HdrpDepthCaptureMode.CustomPassDepth &&
            IsHdrpActive &&
            HdrpDepthCustomPassBridgeType != null &&
            _hdrpCustomPassRuntimeSupported;

        public override Texture DebugPreviewTexture =>
            _debugPreviewTexture != null ? _debugPreviewTexture : (_renderTexture != null ? _renderTexture : _readbackTexture);

        public override string DebugPreviewDetails => BuildDebugPreviewDetails();

        protected override VisualObservationDescription CreateDescription()
        {
            return new VisualObservationDescription
            {
                Shape = new[] { Mathf.Max(1, height), Mathf.Max(1, width), OutputChannelCount },
                DataType = dataType,
                Low = 0f,
                High = UsesFloatOutput ? 1f : 255f
            };
        }

        protected override void OnInitialize()
        {
            ClampConfiguration();
            ResetCaptureState();
            EnsureCaptureResources();
            EnsureDemoMaterial();
        }

        protected override void OnShutdown()
        {
            WaitForPendingCaptureIfNeeded();
            _hasLatestObservation = false;

            DisposeReadbackBuffer();
            ReleaseTextures();
            ReleaseMaterials();
            ClearHdrpCustomPassCapture();
            DestroyOwnedRuntimeCamera();

            _runtimeCamera = null;
        }

        protected override void OnBeginAsyncCapture()
        {
            if (captureMode == VisualObservationCaptureMode.OnDemandBlocking)
            {
                _hasLatestObservation = false;
                return;
            }

            TryFinalizePendingCapture(waitForCompletion: false);
            if (_capturePending)
            {
                return;
            }

            CaptureFrameToOutputTexture();
            StartReadbackOrFallback();
        }

        protected override void OnUpdateCapture()
        {
            if (!_capturePending || !_pendingRequest.done)
            {
                return;
            }

            _capturePending = false;
            if (_pendingRequest.hasError)
            {
                DisableAsyncReadbackAndLogFallback(
                    $"Async GPU readback request returned hasError=true. {AsyncGpuReadbackSupport.BuildSummary(_renderTexture)}");
                CaptureWithReadPixels();
                return;
            }

            _lastReadbackMode = "AsyncGPUReadback";
            ConvertReadbackBuffer();
        }

        protected override void OnCaptureBlocking()
        {
            CaptureBlocking();
        }

        protected override bool HasLatestObservation()
        {
            return _hasLatestObservation;
        }

        protected override byte[] GetLatestObservationBytes()
        {
            return _latestObservationBytes;
        }

        private string BuildDebugPreviewDetails()
        {
            var details =
                $"{width}x{height} | {contentMode} | {(IsDepthMode ? "SingleChannel" : colorMode.ToString())} | {dataType} | {captureMode}";

            if (IsDepthMode)
            {
                details += $" | depth {DepthPreviewMinMeters:0.##}-{DepthPreviewMaxMeters:0.##}m";
                details += UseHdrpCustomPassDepth ? " | HDRP CustomPassDepth" : " | CameraDepthTexture";
            }

            details += _asyncReadbackDisabled ? " | ReadPixels fallback" : " | RenderTexture";
            return details;
        }

        private void ClampConfiguration()
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
        }

        private void ResetCaptureState()
        {
            _capturePending = false;
            _hasLatestObservation = false;
            _asyncReadbackDisabled = false;
            _loggedAsyncReadbackFallback = false;
            _hdrpCustomPassRuntimeSupported = true;
            _loggedHdrpCustomPassFallback = false;
            _debugLoggedNonZeroCapture = false;
            _debugCaptureSummaryCount = 0;
            _lastReadbackMode = "None";
        }

        private void EnsureCaptureResources()
        {
            EnsureCamera();
            EnsureRenderTargets();
            EnsureBuffers();
        }

        private void EnsureDemoMaterial()
        {
            if (demoObject == null || !demoObject.TryGetComponent<Renderer>(out var renderer))
            {
                return;
            }

            if (_demoMaterial == null)
            {
                var demoShader = Shader.Find("Unlit/Texture");
                if (demoShader == null)
                {
                    return;
                }

                _demoMaterial = new Material(demoShader)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                renderer.material = _demoMaterial;
            }

            _demoMaterial.mainTexture = _renderTexture;
        }

        private void CaptureBlocking()
        {
            TryFinalizePendingCapture(waitForCompletion: true);
            if (_hasLatestObservation)
            {
                return;
            }

            CaptureFrameToOutputTexture();
            if (captureMode == VisualObservationCaptureMode.OnDemandBlocking)
            {
                CaptureWithReadPixels();
                return;
            }

            UpdateAsyncReadbackAvailability();
            if (_asyncReadbackDisabled)
            {
                CaptureWithReadPixels();
                return;
            }

            RequestAsyncReadback();
            TryFinalizePendingCapture(waitForCompletion: true);
            if (_hasLatestObservation)
            {
                return;
            }

            CaptureWithReadPixels();
        }

        private void TryFinalizePendingCapture(bool waitForCompletion)
        {
            if (!_capturePending)
            {
                return;
            }

            if (waitForCompletion)
            {
                AsyncGPUReadback.WaitAllRequests();
            }

            OnUpdateCapture();
        }

        private void WaitForPendingCaptureIfNeeded()
        {
            if (!_capturePending)
            {
                return;
            }

            AsyncGPUReadback.WaitAllRequests();
            _capturePending = false;
        }

        private void StartReadbackOrFallback()
        {
            UpdateAsyncReadbackAvailability();
            if (_asyncReadbackDisabled)
            {
                CaptureWithReadPixels();
                return;
            }

            RequestAsyncReadback();
        }

        private void RequestAsyncReadback()
        {
            EnsureBuffers();
            _pendingRequest = AsyncGPUReadback.RequestIntoNativeArray(
                ref _readbackBuffer,
                _renderTexture,
                0,
                _renderTexture.graphicsFormat,
                null);
            _capturePending = true;
        }

        private void CaptureWithReadPixels()
        {
            EnsureBuffers();
            _lastReadbackMode = "ReadPixels";

            var previousActive = RenderTexture.active;
            try
            {
                RenderTexture.active = _renderTexture;
                var texture = EnsureReadbackTexture();
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false, false);

                var raw = texture.GetRawTextureData<byte>();
                EnsureReadbackBufferCapacity(raw.Length);
                NativeArray<byte>.Copy(raw, _readbackBuffer);
            }
            finally
            {
                RenderTexture.active = previousActive;
            }

            ConvertReadbackBuffer();
        }

        private void UpdateAsyncReadbackAvailability()
        {
            if (_asyncReadbackDisabled)
            {
                return;
            }

            var report = AsyncGpuReadbackSupport.Evaluate(_renderTexture);
            if (!report.IsSupported)
            {
                DisableAsyncReadbackAndLogFallback($"{report.Reason} {report.Summary}".Trim());
            }
        }

        private void DisableAsyncReadbackAndLogFallback(string reason)
        {
            _asyncReadbackDisabled = true;
            if (_loggedAsyncReadbackFallback)
            {
                return;
            }

            _loggedAsyncReadbackFallback = true;
            Debug.LogWarning(
                $"Async GPU readback disabled for visual observation '{ObservationName}'. Falling back to ReadPixels for this source. {reason}");
        }

        private void CaptureFrameToOutputTexture()
        {
            EnsureCamera();
            EnsureRenderTargets();
            EnsureBuffers();

            if (IsDepthMode)
            {
                CaptureDepthFrame();
                return;
            }

            _runtimeCamera.targetTexture = _renderTexture;
            _runtimeCamera.Render();
        }

        private void CaptureDepthFrame()
        {
            EnsureDepthResources();
            _runtimeCamera.targetTexture = _captureRenderTexture;
            _runtimeCamera.Render();

            if (UseHdrpCustomPassDepth)
            {
                return;
            }

            ConfigureDepthEncodeMaterial();
            Graphics.Blit(Texture2D.blackTexture, _renderTexture, _depthEncodeMaterial);
        }

        private void EnsureCamera()
        {
            if (sourceCamera != null)
            {
                DestroyOwnedRuntimeCamera();
                _runtimeCamera = sourceCamera;
            }
            else
            {
                EnsureOwnedRuntimeCamera();
            }

            ApplyCameraSettings(_runtimeCamera);
        }

        private void EnsureOwnedRuntimeCamera()
        {
            if (_runtimeCameraObject == null)
            {
                _runtimeCameraObject = new GameObject($"{ObservationName}_Camera")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                _runtimeCameraObject.transform.SetParent(transform, false);
            }

            _runtimeCameraObject.transform.localPosition = localPosition;
            _runtimeCameraObject.transform.localEulerAngles = localEulerAngles;

            _runtimeCamera = _runtimeCameraObject.GetComponent<Camera>();
            if (_runtimeCamera == null)
            {
                _runtimeCamera = _runtimeCameraObject.AddComponent<Camera>();
            }
        }

        private void ApplyCameraSettings(Camera camera)
        {
            if (camera == null)
            {
                return;
            }

            camera.enabled = false;
            camera.clearFlags = clearFlags;
            camera.backgroundColor = backgroundColor;
            camera.cullingMask = cullingMask;
            camera.orthographic = orthographic;
            camera.fieldOfView = fieldOfView;
            camera.orthographicSize = orthographicSize;
            camera.nearClipPlane = nearClipPlane;
            camera.farClipPlane = farClipPlane;
            camera.allowHDR = false;
            camera.allowMSAA = false;

            if (IsDepthMode)
            {
                camera.depthTextureMode |= DepthTextureMode.Depth;
            }
            else
            {
                camera.depthTextureMode &= ~DepthTextureMode.Depth;
            }
        }

        private void EnsureRenderTargets()
        {
            _renderTexture = EnsureCompatibleRenderTexture(
                _renderTexture,
                needsDepthBuffer: !IsDepthMode || UseHdrpCustomPassDepth);

            if (IsDepthMode)
            {
                _captureRenderTexture = EnsureCompatibleRenderTexture(_captureRenderTexture, needsDepthBuffer: true);
                EnsureDepthResources();
            }
            else
            {
                ReleaseRenderTexture(ref _captureRenderTexture);
                ClearHdrpCustomPassCapture();
            }

            if (_demoMaterial != null)
            {
                _demoMaterial.mainTexture = _renderTexture;
            }
        }

        private void EnsureBuffers()
        {
            EnsureReadbackBufferCapacity(PixelCount * ReadbackChannels);

            if (UsesFloatOutput)
            {
                if (_floatScratch == null || _floatScratch.Length != OutputElementCount)
                {
                    _floatScratch = new float[OutputElementCount];
                }
            }
            else
            {
                _floatScratch = null;
            }

            if (_latestObservationBytes == null || _latestObservationBytes.Length != OutputByteCount)
            {
                _latestObservationBytes = new byte[OutputByteCount];
            }
        }

        private void EnsureReadbackBufferCapacity(int length)
        {
            if (_readbackBuffer.IsCreated && _readbackBuffer.Length == length)
            {
                return;
            }

            DisposeReadbackBuffer();
            _readbackBuffer = new NativeArray<byte>(length, Allocator.Persistent);
        }

        private Texture2D EnsureReadbackTexture()
        {
            if (_readbackTexture != null &&
                _readbackTexture.width == width &&
                _readbackTexture.height == height &&
                _readbackTexture.format == ReadbackFormat)
            {
                return _readbackTexture;
            }

            DestroyObject(ref _readbackTexture);
            _readbackTexture = new Texture2D(width, height, ReadbackFormat, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            return _readbackTexture;
        }

        private void ConvertReadbackBuffer()
        {
            EnsureBuffers();

            if (UsesFloatOutput)
            {
                ConvertToFloat32();
            }
            else
            {
                ConvertToUInt8();
            }

            LogCaptureSummary();
            UpdateDebugPreviewTexture();
            _hasLatestObservation = true;
        }

        private void UpdateDebugPreviewTexture()
        {
            if (!Application.isEditor)
            {
                return;
            }

            var previewTexture = EnsureDebugPreviewTexture();
            if (_debugPreviewPixels == null || _debugPreviewPixels.Length != PixelCount)
            {
                _debugPreviewPixels = new Color32[PixelCount];
            }

            if (UsesFloatOutput)
            {
                PopulateFloat32PreviewPixels();
            }
            else
            {
                PopulateUInt8PreviewPixels();
            }

            previewTexture.SetPixels32(_debugPreviewPixels);
            previewTexture.Apply(false, false);
        }

        private Texture2D EnsureDebugPreviewTexture()
        {
            if (_debugPreviewTexture != null &&
                _debugPreviewTexture.width == width &&
                _debugPreviewTexture.height == height)
            {
                return _debugPreviewTexture;
            }

            DestroyObject(ref _debugPreviewTexture);
            _debugPreviewTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            return _debugPreviewTexture;
        }

        private void PopulateUInt8PreviewPixels()
        {
            if (!UsesColorOutput)
            {
                for (int pixel = 0; pixel < PixelCount; pixel++)
                {
                    var value = _latestObservationBytes[pixel];
                    _debugPreviewPixels[pixel] = new Color32(value, value, value, 255);
                }

                return;
            }

            for (int pixel = 0; pixel < PixelCount; pixel++)
            {
                var sourceIndex = pixel * 3;
                _debugPreviewPixels[pixel] = new Color32(
                    _latestObservationBytes[sourceIndex],
                    _latestObservationBytes[sourceIndex + 1],
                    _latestObservationBytes[sourceIndex + 2],
                    255);
            }
        }

        private void PopulateFloat32PreviewPixels()
        {
            if (_floatScratch == null || _floatScratch.Length == 0)
            {
                return;
            }

            if (!UsesColorOutput)
            {
                for (int pixel = 0; pixel < PixelCount; pixel++)
                {
                    var value = ToByte01(_floatScratch[pixel]);
                    _debugPreviewPixels[pixel] = new Color32(value, value, value, 255);
                }

                return;
            }

            for (int pixel = 0; pixel < PixelCount; pixel++)
            {
                var sourceIndex = pixel * 3;
                _debugPreviewPixels[pixel] = new Color32(
                    ToByte01(_floatScratch[sourceIndex]),
                    ToByte01(_floatScratch[sourceIndex + 1]),
                    ToByte01(_floatScratch[sourceIndex + 2]),
                    255);
            }
        }

        private void ConvertToUInt8()
        {
            if (IsDepthMode)
            {
                for (int pixel = 0; pixel < PixelCount; pixel++)
                {
                    _latestObservationBytes[pixel] = _readbackBuffer[pixel * ReadbackChannels];
                }

                return;
            }

            if (UsesColorOutput)
            {
                for (int pixel = 0; pixel < PixelCount; pixel++)
                {
                    var readbackIndex = pixel * ReadbackChannels;
                    var outputIndex = pixel * 3;
                    _latestObservationBytes[outputIndex] = _readbackBuffer[readbackIndex];
                    _latestObservationBytes[outputIndex + 1] = _readbackBuffer[readbackIndex + 1];
                    _latestObservationBytes[outputIndex + 2] = _readbackBuffer[readbackIndex + 2];
                }

                return;
            }

            for (int pixel = 0; pixel < PixelCount; pixel++)
            {
                var readbackIndex = pixel * ReadbackChannels;
                _latestObservationBytes[pixel] = ToGrayscale(
                    _readbackBuffer[readbackIndex],
                    _readbackBuffer[readbackIndex + 1],
                    _readbackBuffer[readbackIndex + 2]);
            }
        }

        private void ConvertToFloat32()
        {
            if (_floatScratch == null || _floatScratch.Length != OutputElementCount)
            {
                _floatScratch = new float[OutputElementCount];
            }

            if (IsDepthMode)
            {
                for (int pixel = 0; pixel < PixelCount; pixel++)
                {
                    _floatScratch[pixel] = _readbackBuffer[pixel * ReadbackChannels] / 255f;
                }

                Buffer.BlockCopy(_floatScratch, 0, _latestObservationBytes, 0, _latestObservationBytes.Length);
                return;
            }

            if (UsesColorOutput)
            {
                for (int pixel = 0; pixel < PixelCount; pixel++)
                {
                    var readbackIndex = pixel * ReadbackChannels;
                    var outputIndex = pixel * 3;
                    _floatScratch[outputIndex] = _readbackBuffer[readbackIndex] / 255f;
                    _floatScratch[outputIndex + 1] = _readbackBuffer[readbackIndex + 1] / 255f;
                    _floatScratch[outputIndex + 2] = _readbackBuffer[readbackIndex + 2] / 255f;
                }
            }
            else
            {
                for (int pixel = 0; pixel < PixelCount; pixel++)
                {
                    var readbackIndex = pixel * ReadbackChannels;
                    _floatScratch[pixel] = ToGrayscale(
                        _readbackBuffer[readbackIndex],
                        _readbackBuffer[readbackIndex + 1],
                        _readbackBuffer[readbackIndex + 2]) / 255f;
                }
            }

            Buffer.BlockCopy(_floatScratch, 0, _latestObservationBytes, 0, _latestObservationBytes.Length);
        }

        private void LogCaptureSummary()
        {
            if (!VerboseDebugLoggingEnabled)
            {
                return;
            }

            var rawSummary = SummarizeReadbackBuffer();
            var observationSummary = SummarizeByteArray(_latestObservationBytes);
            var shouldLog = _debugCaptureSummaryCount < 6 || (!_debugLoggedNonZeroCapture && observationSummary.Max > 0);
            if (!shouldLog)
            {
                return;
            }

            Debug.Log(
                $"Visual observation '{ObservationName}' capture frame={Time.frameCount} mode={contentMode}/{dataType} " +
                $"path={CapturePathLabel} readback={_lastReadbackMode} " +
                $"raw(len={rawSummary.Length} min={rawSummary.Min} max={rawSummary.Max} sample=[{rawSummary.Sample}]) " +
                $"obs(len={observationSummary.Length} min={observationSummary.Min} max={observationSummary.Max} sample=[{observationSummary.Sample}])");

            _debugCaptureSummaryCount++;
            if (observationSummary.Max > 0)
            {
                _debugLoggedNonZeroCapture = true;
            }
        }

        private BufferSummary SummarizeReadbackBuffer()
        {
            if (!_readbackBuffer.IsCreated || _readbackBuffer.Length == 0)
            {
                return BufferSummary.Empty;
            }

            byte min = byte.MaxValue;
            byte max = byte.MinValue;
            for (int i = 0; i < _readbackBuffer.Length; i++)
            {
                var value = _readbackBuffer[i];
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }
            }

            var sampleCount = Math.Min(8, _readbackBuffer.Length);
            var sampleValues = new string[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                sampleValues[i] = _readbackBuffer[i].ToString();
            }

            return new BufferSummary(_readbackBuffer.Length, min, max, string.Join(",", sampleValues));
        }

        private static BufferSummary SummarizeByteArray(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return BufferSummary.Empty;
            }

            byte min = byte.MaxValue;
            byte max = byte.MinValue;
            for (int i = 0; i < data.Length; i++)
            {
                var value = data[i];
                if (value < min)
                {
                    min = value;
                }

                if (value > max)
                {
                    max = value;
                }
            }

            var sampleCount = Math.Min(8, data.Length);
            var sample = string.Join(",", data.AsSpan(0, sampleCount).ToArray());
            return new BufferSummary(data.Length, min, max, sample);
        }

        private void EnsureDepthResources()
        {
            if (UseHdrpCustomPassDepth)
            {
                EnsureHdrpCustomPassCapture();
                return;
            }

            ClearHdrpCustomPassCapture();
            EnsureDepthEncodeMaterial();
            ConfigureDepthEncodeMaterial();
        }

        private RenderTexture EnsureCompatibleRenderTexture(
            RenderTexture current,
            bool needsDepthBuffer,
            GraphicsFormat preferredFormatOverride = GraphicsFormat.None)
        {
            var requiredDepthBufferBits = needsDepthBuffer ? 24 : 0;
            var requiredDepthStencilFormat = needsDepthBuffer ? SelectPreferredDepthStencilFormat() : GraphicsFormat.None;

            if (CanReuseRenderTexture(current, requiredDepthBufferBits, requiredDepthStencilFormat, preferredFormatOverride))
            {
                if (!current.IsCreated())
                {
                    current.Create();
                }

                return current;
            }

            ReleaseRenderTexture(ref current);
            return CreateRenderTexture(requiredDepthBufferBits, requiredDepthStencilFormat, preferredFormatOverride);
        }

        private bool CanReuseRenderTexture(
            RenderTexture current,
            int requiredDepthBufferBits,
            GraphicsFormat requiredDepthStencilFormat,
            GraphicsFormat preferredFormatOverride)
        {
            return current != null &&
                   current.width == width &&
                   current.height == height &&
                   current.depth == requiredDepthBufferBits &&
                   (requiredDepthBufferBits == 0 || requiredDepthStencilFormat == GraphicsFormat.None ||
                    current.depthStencilFormat == requiredDepthStencilFormat) &&
                   (preferredFormatOverride == GraphicsFormat.None || current.graphicsFormat == preferredFormatOverride);
        }

        private RenderTexture CreateRenderTexture(
            int requiredDepthBufferBits,
            GraphicsFormat requiredDepthStencilFormat,
            GraphicsFormat preferredFormatOverride)
        {
            var preferredFormat = preferredFormatOverride != GraphicsFormat.None
                ? preferredFormatOverride
                : PreferredRenderTextureFormat;

            RenderTexture renderTexture;
            if (preferredFormat != GraphicsFormat.None)
            {
                var descriptor = new RenderTextureDescriptor(width, height)
                {
                    depthBufferBits = requiredDepthBufferBits,
                    msaaSamples = 1,
                    graphicsFormat = preferredFormat,
                    useMipMap = false,
                    autoGenerateMips = false,
                    sRGB = false
                };

                if (requiredDepthStencilFormat != GraphicsFormat.None)
                {
                    descriptor.depthStencilFormat = requiredDepthStencilFormat;
                }

                renderTexture = new RenderTexture(descriptor);
            }
            else
            {
                renderTexture = new RenderTexture(width, height, requiredDepthBufferBits, RenderTextureFormat.ARGB32)
                {
                    antiAliasing = 1,
                    useMipMap = false,
                    autoGenerateMips = false
                };
            }

            renderTexture.hideFlags = HideFlags.HideAndDontSave;
            renderTexture.Create();
            return renderTexture;
        }

        private void ReleaseRenderTexture(ref RenderTexture texture)
        {
            if (texture == null)
            {
                return;
            }

            if (_runtimeCamera != null && _runtimeCamera.targetTexture == texture)
            {
                _runtimeCamera.targetTexture = null;
            }

            DestroyObject(ref texture);
        }

        private static GraphicsFormat SelectPreferredDepthStencilFormat()
        {
            if (SystemInfo.IsFormatSupported(GraphicsFormat.D24_UNorm_S8_UInt, GraphicsFormatUsage.Render))
            {
                return GraphicsFormat.D24_UNorm_S8_UInt;
            }

            if (SystemInfo.IsFormatSupported(GraphicsFormat.D32_SFloat_S8_UInt, GraphicsFormatUsage.Render))
            {
                return GraphicsFormat.D32_SFloat_S8_UInt;
            }

            if (SystemInfo.IsFormatSupported(GraphicsFormat.D16_UNorm, GraphicsFormatUsage.Render))
            {
                return GraphicsFormat.D16_UNorm;
            }

            return GraphicsFormat.None;
        }

        private void EnsureDepthEncodeMaterial()
        {
            if (_depthEncodeMaterial != null)
            {
                return;
            }

            var depthShader = Resources.Load<Shader>(DepthEncodeShaderResourcePath);
            if (depthShader == null)
            {
                depthShader = Shader.Find(DepthEncodeShaderName);
            }

            if (depthShader == null)
            {
                throw new InvalidOperationException(
                    $"Could not find shader '{DepthEncodeShaderName}' required for depth visual observations. " +
                    $"Looked in Resources at '{DepthEncodeShaderResourcePath}' and via Shader.Find.");
            }

            _depthEncodeMaterial = new Material(depthShader)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        private void ConfigureDepthEncodeMaterial()
        {
            if (_depthEncodeMaterial == null)
            {
                return;
            }

            _depthEncodeMaterial.SetFloat("_DepthMetersMin", DepthEncodeMinMeters);
            _depthEncodeMaterial.SetFloat("_DepthMetersMax", DepthEncodeMaxMeters);
        }

        private void EnsureHdrpCustomPassCapture()
        {
            if (_runtimeCamera == null || _renderTexture == null)
            {
                return;
            }

            if (!TryGetHdrpDepthCaptureBridge())
            {
                return;
            }

            _hdrpDepthCustomPassBridgeComponent.hideFlags = HideFlags.HideAndDontSave;
            _hdrpCustomPassRuntimeSupported = _hdrpDepthCustomPassBridge.Configure(
                _runtimeCamera,
                _renderTexture,
                cullingMask,
                DepthEncodeMinMeters,
                DepthEncodeMaxMeters,
                VerboseDebugLoggingEnabled);

            if (_hdrpCustomPassRuntimeSupported || _loggedHdrpCustomPassFallback)
            {
                return;
            }

            _loggedHdrpCustomPassFallback = true;
            Debug.LogWarning(
                $"HDRP custom-pass depth capture is unavailable for visual observation '{ObservationName}'. " +
                $"Falling back to CameraDepthTexture depth encoding. {_hdrpDepthCustomPassBridge.UnsupportedReason}");
        }

        private bool TryGetHdrpDepthCaptureBridge()
        {
            if (_hdrpDepthCustomPassBridgeComponent != null && _hdrpDepthCustomPassBridge != null)
            {
                return true;
            }

            if (HdrpDepthCustomPassBridgeType == null)
            {
                return false;
            }

            _hdrpDepthCustomPassBridgeComponent = _runtimeCamera.gameObject.GetComponent(HdrpDepthCustomPassBridgeType);
            if (_hdrpDepthCustomPassBridgeComponent == null)
            {
                _hdrpDepthCustomPassBridgeComponent = _runtimeCamera.gameObject.AddComponent(HdrpDepthCustomPassBridgeType);
            }

            _hdrpDepthCustomPassBridge = _hdrpDepthCustomPassBridgeComponent as IHdrpDepthCaptureBridge;
            return _hdrpDepthCustomPassBridgeComponent != null && _hdrpDepthCustomPassBridge != null;
        }

        private void ClearHdrpCustomPassCapture()
        {
            DestroyObject(ref _hdrpDepthCustomPassBridgeComponent);
            _hdrpDepthCustomPassBridge = null;
        }

        private void ReleaseTextures()
        {
            if (_runtimeCamera != null)
            {
                _runtimeCamera.targetTexture = null;
            }

            ReleaseRenderTexture(ref _renderTexture);
            ReleaseRenderTexture(ref _captureRenderTexture);
            DestroyObject(ref _readbackTexture);
            DestroyObject(ref _debugPreviewTexture);
        }

        private void ReleaseMaterials()
        {
            DestroyObject(ref _depthEncodeMaterial);
            DestroyObject(ref _demoMaterial);
        }

        private void DisposeReadbackBuffer()
        {
            if (_readbackBuffer.IsCreated)
            {
                _readbackBuffer.Dispose();
            }
        }

        private void DestroyOwnedRuntimeCamera()
        {
            if (_runtimeCamera != null && _runtimeCameraObject != null && _runtimeCamera.gameObject == _runtimeCameraObject)
            {
                _runtimeCamera.targetTexture = null;
            }

            DestroyObject(ref _runtimeCameraObject);
            if (sourceCamera == null)
            {
                _runtimeCamera = null;
            }
        }

        private static void DestroyObject<T>(ref T value) where T : UnityEngine.Object
        {
            if (value == null)
            {
                return;
            }

            Destroy(value);
            value = null;
        }

        private static byte ToGrayscale(byte r, byte g, byte b)
        {
            return (byte)((299 * r + 587 * g + 114 * b + 500) / 1000);
        }

        private static byte ToByte01(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 255f), 0, 255);
        }

        private readonly struct BufferSummary
        {
            public static BufferSummary Empty => new(0, 0, 0, string.Empty);

            public readonly int Length;
            public readonly byte Min;
            public readonly byte Max;
            public readonly string Sample;

            public BufferSummary(int length, byte min, byte max, string sample)
            {
                Length = length;
                Min = min;
                Max = max;
                Sample = sample ?? string.Empty;
            }
        }
    }
}
