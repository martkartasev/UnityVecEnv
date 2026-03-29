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

    public class CameraVisualObservationSource : AgentVisualObservationSource
    {
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
        public VisualObservationColorMode colorMode = VisualObservationColorMode.Grayscale;
        public VisualObservationDataType dataType = VisualObservationDataType.UInt8;

        private const TextureFormat ReadbackFormat = TextureFormat.RGBA32;
        private const int ReadbackChannels = 4;

        private Camera _runtimeCamera;
        private GameObject _runtimeCameraObject;
        private RenderTexture _renderTexture;
        private Texture2D _readbackTexture;
        private NativeArray<byte> _readbackBuffer;
        private AsyncGPUReadbackRequest _pendingRequest;
        private bool _capturePending;
        private bool _asyncReadbackDisabled;
        private bool _loggedAsyncReadbackFallback;
        private byte[] _latestObservationBytes;
        private float[] _floatScratch;
        private bool _hasLatestObservation;

        private GraphicsFormat PreferredRenderTextureFormat =>
            AsyncGpuReadbackSupport.SelectPreferredReadbackFormat();
        public override Texture DebugPreviewTexture => _renderTexture != null ? _renderTexture : _readbackTexture;
        public override string DebugPreviewDetails =>
            $"{width}x{height} | {colorMode} | {dataType}" +
            (_asyncReadbackDisabled ? " | ReadPixels fallback" : " | RenderTexture");

        protected override VisualObservationDescription CreateDescription()
        {
            return new VisualObservationDescription
            {
                Shape = new[] { Mathf.Max(1, height), Mathf.Max(1, width), OutputChannels() },
                DataType = dataType,
                Low = dataType == VisualObservationDataType.UInt8 ? 0f : 0f,
                High = dataType == VisualObservationDataType.UInt8 ? 255f : 1f
            };
        }

        protected override void OnInitialize()
        {
            width = Mathf.Max(1, width);
            height = Mathf.Max(1, height);
            _asyncReadbackDisabled = false;
            _loggedAsyncReadbackFallback = false;

            EnsureCamera();
            EnsureRenderTarget();
            EnsureBuffers();
            _hasLatestObservation = false;
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

            if (_readbackTexture != null)
            {
                Destroy(_readbackTexture);
                _readbackTexture = null;
            }

            if (_runtimeCameraObject != null)
            {
                Destroy(_runtimeCameraObject);
                _runtimeCameraObject = null;
            }
        }

        protected override void OnBeginAsyncCapture()
        {
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
        }

        private void EnsureRenderTarget()
        {
            if (_renderTexture != null &&
                _renderTexture.width == width &&
                _renderTexture.height == height)
            {
                if (_runtimeCamera.targetTexture != _renderTexture)
                {
                    _runtimeCamera.targetTexture = _renderTexture;
                }

                return;
            }

            if (_renderTexture != null)
            {
                if (_runtimeCamera != null)
                {
                    _runtimeCamera.targetTexture = null;
                }

                Destroy(_renderTexture);
            }

            var preferredFormat = PreferredRenderTextureFormat;
            if (preferredFormat != GraphicsFormat.None)
            {
                var descriptor = new RenderTextureDescriptor(width, height)
                {
                    depthBufferBits = 24,
                    msaaSamples = 1,
                    graphicsFormat = preferredFormat,
                    useMipMap = false,
                    autoGenerateMips = false,
                    sRGB = false
                };
                _renderTexture = new RenderTexture(descriptor);
            }
            else
            {
                _renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                {
                    antiAliasing = 1,
                    useMipMap = false,
                    autoGenerateMips = false
                };
            }

            _renderTexture.Create();
            _runtimeCamera.targetTexture = _renderTexture;
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
                _hasLatestObservation = true;
                return;
            }

            ConvertToFloat32(pixelCount);
            _hasLatestObservation = true;
        }

        private void ConvertToUInt8(int pixelCount)
        {
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

        private int OutputChannels()
        {
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
    }
}
