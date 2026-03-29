using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Scripts.VecEnv.Observation
{
    internal readonly struct AsyncGpuReadbackSupportReport
    {
        public bool IsSupported { get; }
        public string Reason { get; }
        public string Summary { get; }

        public AsyncGpuReadbackSupportReport(bool isSupported, string reason, string summary)
        {
            IsSupported = isSupported;
            Reason = reason ?? string.Empty;
            Summary = summary ?? string.Empty;
        }
    }

    internal static class AsyncGpuReadbackSupport
    {
        private static readonly GraphicsFormat[] PreferredReadbackFormats =
        {
            GraphicsFormat.R8G8B8A8_UNorm,
            GraphicsFormat.B8G8R8A8_UNorm
        };

        public static GraphicsFormat SelectPreferredReadbackFormat()
        {
            foreach (var format in PreferredReadbackFormats)
            {
                if (SupportsRenderAndReadback(format))
                {
                    return format;
                }
            }

            return GraphicsFormat.None;
        }

        public static AsyncGpuReadbackSupportReport Evaluate(RenderTexture renderTexture)
        {
            var summary = BuildSummary(renderTexture);

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                return new AsyncGpuReadbackSupportReport(false, "No graphics device is active.", summary);
            }

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                return new AsyncGpuReadbackSupportReport(false, "SystemInfo.supportsAsyncGPUReadback is false.", summary);
            }

            if (renderTexture == null)
            {
                return new AsyncGpuReadbackSupportReport(false, "RenderTexture is null.", summary);
            }

            if (renderTexture.width <= 0 || renderTexture.height <= 0)
            {
                return new AsyncGpuReadbackSupportReport(false, "RenderTexture dimensions must be positive.", summary);
            }

            if (!renderTexture.IsCreated())
            {
                return new AsyncGpuReadbackSupportReport(false, "RenderTexture is not created.", summary);
            }

            var graphicsFormat = renderTexture.graphicsFormat;
            if (graphicsFormat == GraphicsFormat.None)
            {
                return new AsyncGpuReadbackSupportReport(false, "RenderTexture has no graphics format.", summary);
            }

            if (!SystemInfo.IsFormatSupported(graphicsFormat, GraphicsFormatUsage.ReadPixels))
            {
                return new AsyncGpuReadbackSupportReport(
                    false,
                    $"Graphics format '{graphicsFormat}' does not support ReadPixels on this device.",
                    summary);
            }

            return new AsyncGpuReadbackSupportReport(true, string.Empty, summary);
        }

        public static bool SupportsRenderAndReadback(GraphicsFormat graphicsFormat)
        {
            if (graphicsFormat == GraphicsFormat.None)
            {
                return false;
            }

            return SystemInfo.IsFormatSupported(graphicsFormat, GraphicsFormatUsage.Render) &&
                   SystemInfo.IsFormatSupported(graphicsFormat, GraphicsFormatUsage.ReadPixels);
        }

        public static string BuildSummary(RenderTexture renderTexture)
        {
            if (renderTexture == null)
            {
                return $"graphicsDeviceType={SystemInfo.graphicsDeviceType}, supportsAsyncGPUReadback={SystemInfo.supportsAsyncGPUReadback}, renderTexture=null";
            }

            return
                $"graphicsDeviceType={SystemInfo.graphicsDeviceType}, " +
                $"supportsAsyncGPUReadback={SystemInfo.supportsAsyncGPUReadback}, " +
                $"renderTexture={renderTexture.width}x{renderTexture.height}, " +
                $"renderTextureFormat={renderTexture.format}, " +
                $"graphicsFormat={renderTexture.graphicsFormat}, " +
                $"antiAliasing={renderTexture.antiAliasing}, " +
                $"isCreated={renderTexture.IsCreated()}";
        }
    }
}
