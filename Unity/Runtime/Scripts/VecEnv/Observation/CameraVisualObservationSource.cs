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
        public GameObject demoObject;
        [Header("Camera")] public Camera sourceCamera;
        public Vector3 localPosition = new(0f, 2.0f, -4.5f);
        public Vector3 localEulerAngles = new(18f, 0f, 0f);
        public LayerMask cullingMask = ~0;
        public CameraClearFlags clearFlags = CameraClearFlags.SolidColor;
        public Color backgroundColor = Color.black;
        public bool orthographic = false;
        public float fieldOfView = 60f;
        public float orthographicSize = 5f;
        public float nearClipPlane = 0.05f;
        public float farClipPlane = 200f;

        [Header("Output")] public int width = 84;
        public int height = 84;
        public VisualObservationContentMode contentMode = VisualObservationContentMode.Color;
        public VisualObservationColorMode colorMode = VisualObservationColorMode.Grayscale;
        public VisualObservationDataType dataType = VisualObservationDataType.UInt8;
        [Header("Depth")] public float depthRangeMinMeters = 0f;
        public float depthRangeMaxMeters = 20f;
        public HdrpDepthCaptureMode hdrpDepthCaptureMode = HdrpDepthCaptureMode.CustomPassDepth;

        [Header("Capture")] public VisualObservationCaptureMode captureMode = VisualObservationCaptureMode.EarlyAsync;

        private const TextureFormat ReadbackFormat = TextureFormat.RGBA32;
        private const int ReadbackChannels = 4;
        private const string DepthEncodeShaderName = "Hidden/VecEnv/DepthObservation";
        private const string DepthEncodeShaderResourcePath = "VecEnv/VecEnvDepthObservation";

        private Camera _runtimeCamera;
        private GameObject _runtimeCameraObject;
        private RenderTexture _renderTexture;
        private RenderTexture _captureRenderTexture;
        private Texture2D _readbackTexture;
        private Texture2D _debugPreviewTexture;
        private NativeArray<byte> _readbackBuffer;
        private AsyncGPUReadbackRequest _pendingRequest;
        private bool _capturePending;
        private bool _asyncReadbackDisabled;
        private bool _loggedAsyncReadbackFallback;
        private byte[] _latestObservationBytes;
        private float[] _floatScratch;
        private bool _hasLatestObservation;
        private Material _depthEncodeMaterial;
        private Material _demoMaterial;
        private Color32[] _debugPreviewPixels;
        private int _debugCaptureSummaryCount;
        private bool _debugLoggedNonZeroCapture;
        private string _lastReadbackMode = "None";
        private bool _hdrpCustomPassRuntimeSupported = true;
        private bool _loggedHdrpCustomPassFallback;
        private Component _hdrpDepthCustomPassBridgeComponent;
        private IHdrpDepthCaptureBridge _hdrpDepthCustomPassBridge;
        private static readonly Type HdRenderPipelineAssetType =
            Type.GetType("UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset, Unity.RenderPipelines.HighDefinition.Runtime", false);
        private static readonly Type HdrpDepthCustomPassBridgeType =
            Type.GetType("Scripts.VecEnv.Observation.HdrpDepthCustomPassBridge, MKA.GymVecEnv.Hdrp", false);

        private GraphicsFormat PreferredRenderTextureFormat =>
            AsyncGpuReadbackSupport.SelectPreferredReadbackFormat();

        private bool IsDepthMode => contentMode == VisualObservationContentMode.Depth;
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

        public override string DebugPreviewDetails =>
            $"{width}x{height} | {contentMode} | {(IsDepthMode ? "SingleChannel" : colorMode.ToString())} | {dataType} | {captureMode}" +
            (IsDepthMode ? $" | depth {Mathf.Max(0f, depthRangeMinMeters):0.##}-{Mathf.Max(depthRangeMinMeters, depthRangeMaxMeters):0.##}m" : string.Empty) +
            (IsDepthMode
                ? (UseHdrpCustomPassDepth ? " | HDRP CustomPassDepth" : " | CameraDepthTexture")
                : string.Empty) +
            (_asyncReadbackDisabled ? " | ReadPixels fallback" : " | RenderTexture");

        protected override VisualObservationDescription CreateDescription()
        {
            return new VisualObservationDescription
            {
                Shape = new[] { Mathf.Max(1, height), Mathf.Max(1, width), OutputChannels() },
                DataType = dataType,
                Low = 0f,
                High = dataType == VisualObservationDataType.UInt8 ? 255f : 1f
            };
        }

        protected override void OnInitialize()
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            _asyncReadbackDisabled = false;
            _loggedAsyncReadbackFallback = false;
            _hdrpCustomPassRuntimeSupported = true;
            _loggedHdrpCustomPassFallback = false;

            EnsureCamera();
            EnsureRenderTarget();
            EnsureBuffers();
            _hasLatestObservation = false;

            if (demoObject != null && demoObject.TryGetComponent<Renderer>(out var renderer))
            {
                var demoShader = Shader.Find("Unlit/Texture");
                if (demoShader != null)
                {
                    _demoMaterial = new Material(demoShader)
                    {
                        hideFlags = HideFlags.HideAndDontSave,
                        mainTexture = _renderTexture
                    };
                    renderer.material = _demoMaterial;
                }
            }
        }

        
        protected override void OnShutdown()
        {
            if (_capturePending)
            {
                AsyncGPUReadback.WaitAllRequests();
                _capturePending = false;
            }

            _hasLatestObservation = false;

            if (_readbackBuffer.IsCreated)
            {
                _readbackBuffer.Dispose();
            }

            if (_renderTexture != null)
            {
                if (_runtimeCamera != null)
                {
                    _runtimeCamera.targetTexture = null;
                }

                Destroy(_renderTexture);
                _renderTexture = null;
            }

            if (_captureRenderTexture != null)
            {
                Destroy(_captureRenderTexture);
                _captureRenderTexture = null;
            }

            if (_readbackTexture != null)
            {
                Destroy(_readbackTexture);
                _readbackTexture = null;
            }

            if (_debugPreviewTexture != null)
            {
                Destroy(_debugPreviewTexture);
                _debugPreviewTexture = null;
            }

            if (_depthEncodeMaterial != null)
            {
                Destroy(_depthEncodeMaterial);
                _depthEncodeMaterial = null;
            }

            if (_demoMaterial != null)
            {
                Destroy(_demoMaterial);
                _demoMaterial = null;
            }

            ClearHdrpCustomPassCapture();

            if (_runtimeCameraObject != null)
            {
                Destroy(_runtimeCameraObject);
                _runtimeCameraObject = null;
            }
        }

        protected override void OnBeginAsyncCapture()
        {
            if (captureMode == VisualObservationCaptureMode.OnDemandBlocking)
            {
                _hasLatestObservation = false;
                return;
            }

            UpdateCapture();
            if (_capturePending)
            {
                return;
            }

            CaptureToRenderTarget();
            UpdateAsyncReadbackAvailability();
            if (_asyncReadbackDisabled)
            {
                CaptureWithReadPixels();
                return;
            }

            _pendingRequest = AsyncGPUReadback.RequestIntoNativeArray(
                ref _readbackBuffer,
                _renderTexture,
                0,
                _renderTexture.graphicsFormat,
                null);
            _capturePending = true;
        }

        protected override void OnUpdateCapture()
        {
            if (!_capturePending)
            {
                return;
            }

            if (!_pendingRequest.done)
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

        private void CaptureBlocking()
        {
            if (_capturePending)
            {
                AsyncGPUReadback.WaitAllRequests();
                OnUpdateCapture();
                if (_hasLatestObservation)
                {
                    return;
                }
            }

            if (captureMode == VisualObservationCaptureMode.OnDemandBlocking)
            {
                CaptureToRenderTarget();
                CaptureWithReadPixels();
                return;
            }

            CaptureToRenderTarget();
            UpdateAsyncReadbackAvailability();

            if (!_asyncReadbackDisabled)
            {
                _pendingRequest = AsyncGPUReadback.RequestIntoNativeArray(
                    ref _readbackBuffer,
                    _renderTexture,
                    0,
                    _renderTexture.graphicsFormat,
                    null);
                _capturePending = true;
                AsyncGPUReadback.WaitAllRequests();
                OnUpdateCapture();
                if (_hasLatestObservation)
                {
                    return;
                }
            }

            CaptureWithReadPixels();
        }

        private void CaptureWithReadPixels()
        {
            EnsureBuffers();
            _lastReadbackMode = "ReadPixels";
            var previous = RenderTexture.active;
            RenderTexture.active = _renderTexture;
            var texture = EnsureReadbackTexture();
            texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            texture.Apply(false, false);
            RenderTexture.active = previous;

            var raw = texture.GetRawTextureData<byte>();
            if (!_readbackBuffer.IsCreated || _readbackBuffer.Length != raw.Length)
            {
                if (_readbackBuffer.IsCreated)
                {
                    _readbackBuffer.Dispose();
                }

                _readbackBuffer = new NativeArray<byte>(raw.Length, Allocator.Persistent);
            }

            NativeArray<byte>.Copy(raw, _readbackBuffer);
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

        private void CaptureToRenderTarget()
        {
            EnsureCamera();
            EnsureRenderTarget();
            EnsureBuffers();
            if (IsDepthMode)
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
                return;
            }

            _runtimeCamera.targetTexture = _renderTexture;
            _runtimeCamera.Render();
        }

        private void EnsureCamera()
        {
            if (sourceCamera != null)
            {
                _runtimeCamera = sourceCamera;
            }
            else if (_runtimeCamera == null)
            {
                _runtimeCameraObject = new GameObject($"{ObservationName}_Camera");
                _runtimeCameraObject.hideFlags = HideFlags.HideAndDontSave;
                _runtimeCameraObject.transform.SetParent(transform, false);
                _runtimeCameraObject.transform.localPosition = localPosition;
                _runtimeCameraObject.transform.localEulerAngles = localEulerAngles;
                _runtimeCamera = _runtimeCameraObject.AddComponent<Camera>();
            }

            _runtimeCamera.enabled = false;
            _runtimeCamera.clearFlags = clearFlags;
            _runtimeCamera.backgroundColor = backgroundColor;
            _runtimeCamera.cullingMask = cullingMask;
            _runtimeCamera.orthographic = orthographic;
            _runtimeCamera.fieldOfView = fieldOfView;
            _runtimeCamera.orthographicSize = orthographicSize;
            _runtimeCamera.nearClipPlane = nearClipPlane;
            _runtimeCamera.farClipPlane = farClipPlane;
            _runtimeCamera.allowHDR = false;
            _runtimeCamera.allowMSAA = false;
            if (IsDepthMode)
            {
                _runtimeCamera.depthTextureMode |= DepthTextureMode.Depth;
            }
            else
            {
                _runtimeCamera.depthTextureMode &= ~DepthTextureMode.Depth;
            }
        }

        private void EnsureRenderTarget()
        {
            _renderTexture = EnsureCompatibleRenderTexture(
                _renderTexture,
                needsDepthBuffer: !IsDepthMode || UseHdrpCustomPassDepth);

            if (IsDepthMode)
            {
                _captureRenderTexture = EnsureCompatibleRenderTexture(_captureRenderTexture, needsDepthBuffer: true);
                EnsureDepthResources();
                return;
            }

            if (_captureRenderTexture != null)
            {
                Destroy(_captureRenderTexture);
                _captureRenderTexture = null;
            }

            ClearHdrpCustomPassCapture();
        }

        private void EnsureBuffers()
        {
            var readbackBytes = width * height * ReadbackChannels;
            if (!_readbackBuffer.IsCreated || _readbackBuffer.Length != readbackBytes)
            {
                if (_readbackBuffer.IsCreated)
                {
                    _readbackBuffer.Dispose();
                }

                _readbackBuffer = new NativeArray<byte>(readbackBytes, Allocator.Persistent);
            }

            var floatCount = width * height * OutputChannels();
            if (dataType == VisualObservationDataType.Float32 &&
                (_floatScratch == null || _floatScratch.Length != floatCount))
            {
                _floatScratch = new float[floatCount];
            }

            if (_latestObservationBytes == null || _latestObservationBytes.Length != OutputByteCount())
            {
                _latestObservationBytes = new byte[OutputByteCount()];
            }
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

            if (_readbackTexture != null)
            {
                Destroy(_readbackTexture);
            }

            _readbackTexture = new Texture2D(width, height, ReadbackFormat, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            return _readbackTexture;
        }

        private void ConvertReadbackBuffer()
        {
            EnsureBuffers();

            var pixelCount = width * height;
            if (dataType == VisualObservationDataType.UInt8)
            {
                ConvertToUInt8(pixelCount);
                LogCaptureSummary();
                UpdateDebugPreviewTexture(pixelCount);
                _hasLatestObservation = true;
                return;
            }

            ConvertToFloat32(pixelCount);
            LogCaptureSummary();
            UpdateDebugPreviewTexture(pixelCount);
            _hasLatestObservation = true;
        }

        private void UpdateDebugPreviewTexture(int pixelCount)
        {
            if (!Application.isEditor)
            {
                return;
            }

            var previewTexture = EnsureDebugPreviewTexture();
            if (_debugPreviewPixels == null || _debugPreviewPixels.Length != pixelCount)
            {
                _debugPreviewPixels = new Color32[pixelCount];
            }

            if (dataType == VisualObservationDataType.UInt8)
            {
                PopulateUInt8PreviewPixels(pixelCount);
            }
            else
            {
                PopulateFloat32PreviewPixels(pixelCount);
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

            if (_debugPreviewTexture != null)
            {
                Destroy(_debugPreviewTexture);
            }

            _debugPreviewTexture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            return _debugPreviewTexture;
        }

        private void PopulateUInt8PreviewPixels(int pixelCount)
        {
            if (IsDepthMode || colorMode == VisualObservationColorMode.Grayscale)
            {
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    var value = _latestObservationBytes[pixel];
                    _debugPreviewPixels[pixel] = new Color32(value, value, value, 255);
                }

                return;
            }

            for (int pixel = 0; pixel < pixelCount; pixel++)
            {
                var src = pixel * 3;
                _debugPreviewPixels[pixel] = new Color32(
                    _latestObservationBytes[src],
                    _latestObservationBytes[src + 1],
                    _latestObservationBytes[src + 2],
                    255);
            }
        }

        private void PopulateFloat32PreviewPixels(int pixelCount)
        {
            if (_floatScratch == null || _floatScratch.Length == 0)
            {
                return;
            }

            if (IsDepthMode || colorMode == VisualObservationColorMode.Grayscale)
            {
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    var value = ToByte01(_floatScratch[pixel]);
                    _debugPreviewPixels[pixel] = new Color32(value, value, value, 255);
                }

                return;
            }

            for (int pixel = 0; pixel < pixelCount; pixel++)
            {
                var src = pixel * 3;
                _debugPreviewPixels[pixel] = new Color32(
                    ToByte01(_floatScratch[src]),
                    ToByte01(_floatScratch[src + 1]),
                    ToByte01(_floatScratch[src + 2]),
                    255);
            }
        }

        private void ConvertToUInt8(int pixelCount)
        {
            if (IsDepthMode)
            {
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    _latestObservationBytes[pixel] = _readbackBuffer[pixel * ReadbackChannels];
                }

                return;
            }

            if (colorMode == VisualObservationColorMode.Color)
            {
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    var src = pixel * ReadbackChannels;
                    var dst = pixel * 3;
                    _latestObservationBytes[dst] = _readbackBuffer[src];
                    _latestObservationBytes[dst + 1] = _readbackBuffer[src + 1];
                    _latestObservationBytes[dst + 2] = _readbackBuffer[src + 2];
                }

                return;
            }

            for (int pixel = 0; pixel < pixelCount; pixel++)
            {
                var src = pixel * ReadbackChannels;
                _latestObservationBytes[pixel] = ToGrayscale(
                    _readbackBuffer[src],
                    _readbackBuffer[src + 1],
                    _readbackBuffer[src + 2]);
            }
        }

        private void ConvertToFloat32(int pixelCount)
        {
            if (_floatScratch == null || _floatScratch.Length == 0)
            {
                _floatScratch = new float[pixelCount * OutputChannels()];
            }

            if (IsDepthMode)
            {
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    _floatScratch[pixel] = _readbackBuffer[pixel * ReadbackChannels] / 255f;
                }

                Buffer.BlockCopy(_floatScratch, 0, _latestObservationBytes, 0, _latestObservationBytes.Length);
                return;
            }

            if (colorMode == VisualObservationColorMode.Color)
            {
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    var src = pixel * ReadbackChannels;
                    var dst = pixel * 3;
                    _floatScratch[dst] = _readbackBuffer[src] / 255f;
                    _floatScratch[dst + 1] = _readbackBuffer[src + 1] / 255f;
                    _floatScratch[dst + 2] = _readbackBuffer[src + 2] / 255f;
                }
            }
            else
            {
                for (int pixel = 0; pixel < pixelCount; pixel++)
                {
                    var src = pixel * ReadbackChannels;
                    _floatScratch[pixel] = ToGrayscale(
                        _readbackBuffer[src],
                        _readbackBuffer[src + 1],
                        _readbackBuffer[src + 2]) / 255f;
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
            var shouldLog = _debugCaptureSummaryCount < 6 || (!_debugLoggedNonZeroCapture && observationSummary.max > 0);
            if (!shouldLog)
            {
                return;
            }

            Debug.Log(
                $"Visual observation '{ObservationName}' capture frame={Time.frameCount} mode={contentMode}/{dataType} " +
                $"path={(IsDepthMode ? (UseHdrpCustomPassDepth ? "HdrpCustomPass" : "CameraDepthTexture") : "Color")} " +
                $"readback={_lastReadbackMode} " +
                $"raw(len={rawSummary.length} min={rawSummary.min} max={rawSummary.max} sample=[{rawSummary.sample}]) " +
                $"obs(len={observationSummary.length} min={observationSummary.min} max={observationSummary.max} sample=[{observationSummary.sample}])");

            _debugCaptureSummaryCount++;
            if (observationSummary.max > 0)
            {
                _debugLoggedNonZeroCapture = true;
            }
        }

        private (int length, byte min, byte max, string sample) SummarizeReadbackBuffer()
        {
            if (!_readbackBuffer.IsCreated || _readbackBuffer.Length == 0)
            {
                return (0, 0, 0, string.Empty);
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

            return (_readbackBuffer.Length, min, max, string.Join(",", sampleValues));
        }

        private static (int length, byte min, byte max, string sample) SummarizeByteArray(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return (0, 0, 0, string.Empty);
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
            return (data.Length, min, max, sample);
        }

        private int OutputChannels()
        {
            if (IsDepthMode)
            {
                return 1;
            }

            return colorMode == VisualObservationColorMode.Color ? 3 : 1;
        }

        private int OutputByteCount()
        {
            var elements = width * height * OutputChannels();
            return dataType == VisualObservationDataType.UInt8 ? elements : elements * sizeof(float);
        }

        private static byte ToGrayscale(byte r, byte g, byte b)
        {
            return (byte)((299 * r + 587 * g + 114 * b + 500) / 1000);
        }

        private static byte ToByte01(float value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Clamp01(value) * 255f), 0, 255);
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

        private RenderTexture EnsureCompatibleRenderTexture(RenderTexture current, bool needsDepthBuffer,
            GraphicsFormat preferredFormatOverride = GraphicsFormat.None)
        {
            var requiredDepthBufferBits = needsDepthBuffer ? 24 : 0;
            var requiredDepthStencilFormat = needsDepthBuffer ? SelectPreferredDepthStencilFormat() : GraphicsFormat.None;
            if (current != null &&
                current.width == width &&
                current.height == height &&
                current.depth == requiredDepthBufferBits &&
                (!needsDepthBuffer || requiredDepthStencilFormat == GraphicsFormat.None ||
                 current.depthStencilFormat == requiredDepthStencilFormat) &&
                (preferredFormatOverride == GraphicsFormat.None || current.graphicsFormat == preferredFormatOverride))
            {
                if (!current.IsCreated())
                {
                    current.Create();
                }

                return current;
            }

            if (current != null)
            {
                if (_runtimeCamera != null && _runtimeCamera.targetTexture == current)
                {
                    _runtimeCamera.targetTexture = null;
                }

                Destroy(current);
            }

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

            renderTexture.Create();
            return renderTexture;
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

            _depthEncodeMaterial.SetFloat("_DepthMetersMin", Mathf.Max(0f, depthRangeMinMeters));
            _depthEncodeMaterial.SetFloat("_DepthMetersMax", Mathf.Max(depthRangeMinMeters + 0.001f, depthRangeMaxMeters));
        }

        private void EnsureHdrpCustomPassCapture()
        {
            if (_runtimeCamera == null || _renderTexture == null)
            {
                return;
            }

            if (_hdrpDepthCustomPassBridge == null)
            {
                if (HdrpDepthCustomPassBridgeType == null)
                {
                    return;
                }

                _hdrpDepthCustomPassBridgeComponent = _runtimeCamera.gameObject.GetComponent(HdrpDepthCustomPassBridgeType);
                if (_hdrpDepthCustomPassBridgeComponent == null)
                {
                    _hdrpDepthCustomPassBridgeComponent = _runtimeCamera.gameObject.AddComponent(HdrpDepthCustomPassBridgeType);
                }

                _hdrpDepthCustomPassBridge = _hdrpDepthCustomPassBridgeComponent as IHdrpDepthCaptureBridge;
            }

            if (_hdrpDepthCustomPassBridgeComponent == null || _hdrpDepthCustomPassBridge == null)
            {
                return;
            }

            _hdrpDepthCustomPassBridgeComponent.hideFlags = HideFlags.HideAndDontSave;
            _hdrpCustomPassRuntimeSupported = _hdrpDepthCustomPassBridge.Configure(
                _runtimeCamera,
                _renderTexture,
                cullingMask,
                Mathf.Max(0f, depthRangeMinMeters),
                Mathf.Max(depthRangeMinMeters + 0.001f, depthRangeMaxMeters),
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

        private void ClearHdrpCustomPassCapture()
        {
            if (_hdrpDepthCustomPassBridgeComponent != null)
            {
                Destroy(_hdrpDepthCustomPassBridgeComponent);
                _hdrpDepthCustomPassBridgeComponent = null;
                _hdrpDepthCustomPassBridge = null;
            }
        }
    }
}
