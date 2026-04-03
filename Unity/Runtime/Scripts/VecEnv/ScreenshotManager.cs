using ExternalCommunication;
using Scripts.VecEnv.Networking;
using UnityEngine;

namespace Scripts.VecEnv
{
    public class ScreenshotManager : MonoBehaviour
    {
        public Camera renderCamera;
        public int width = 1920;
        public int height = 1080;

        private void Awake()
        {
            ResolveRenderCamera();
        }

        public void DoAwake()
        {
            ResolveRenderCamera();
        }

        internal byte[] TakeScreenshot(Screenshot screenshot)
        {
            var camera = ResolveRenderCamera();
            ApplyCameraTransform(camera, screenshot);

            var renderTexture = new RenderTexture(width, height, 24);
            var screenshotTexture = new Texture2D(width, height, TextureFormat.RGB24, false);
            var previousActive = RenderTexture.active;

            try
            {
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;

                camera.Render();
                screenshotTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                screenshotTexture.Apply();

                return screenshotTexture.EncodeToPNG();
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previousActive;
                Destroy(renderTexture);
                Destroy(screenshotTexture);
            }
        }

        private Camera ResolveRenderCamera()
        {
            if (renderCamera != null)
            {
                return renderCamera;
            }

            renderCamera = GetComponent<Camera>();
            if (renderCamera != null)
            {
                return renderCamera;
            }

            var taggedCamera = GameObject.FindGameObjectWithTag("ScreenshotCamera");
            if (taggedCamera == null || !taggedCamera.TryGetComponent(out renderCamera))
            {
                throw new MissingReferenceException("ScreenshotManager could not find a camera. Assign renderCamera or tag one camera as 'ScreenshotCamera'.");
            }

            return renderCamera;
        }

        private static void ApplyCameraTransform(Camera camera, Screenshot screenshot)
        {
            if (screenshot?.Camera == null)
            {
                return;
            }

            if (screenshot.Camera.Position != null)
            {
                camera.transform.position = screenshot.Camera.Position.ToUnityVector();
            }

            if (screenshot.Camera.Orientation != null)
            {
                camera.transform.rotation = screenshot.Camera.Orientation.ToUnityQuaternion();
                return;
            }

            if (screenshot.Camera.Euler != null)
            {
                camera.transform.rotation = UnityEngine.Quaternion.Euler(screenshot.Camera.Euler.ToUnityVector());
            }
        }
    }
}
