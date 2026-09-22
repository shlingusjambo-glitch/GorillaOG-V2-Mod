using UnityEngine;

namespace GorillaOGV2;

/// <summary>
/// August 2023 fitting-room mirror (City `ShoppingCenterAnchor/mirrors2 (1)`): the MirrorCenter
/// submesh showed a 1024x1024 render texture painted by the child camera `CameraC`
/// (fov 30, near 5.01, far 15.8, skybox clear, culling mask 1206910775). The package ships the mesh
/// but a render texture cannot be serialized, hence the white panel. Rebuilt here with the camera at
/// its 2023 world pose; it only renders while the player is near the mirror to keep the frame budget.
/// </summary>
internal sealed class HistoricalCityMirror : MonoBehaviour
{
    private static readonly Vector3 CameraPos = new Vector3(-47.2f, 17.2124f, -123.7625f);
    private static readonly Quaternion CameraRot = new Quaternion(0.15139f, -0.13408f, -0.69071f, 0.69428f);
    private static readonly Vector3 MirrorPos = new Vector3(-49.295f, 16.5124f, -118.8963f);
    private Camera cam; private RenderTexture rt; private float nextCheck;

    internal void Build(HistoricalWorldController world)
    {
        rt = new RenderTexture(1024, 1024, 16, RenderTextureFormat.ARGB32) { name = "August 2023 MirrorCenter", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, antiAliasing = 1 };
        rt.Create();
        GameObject camGo = new GameObject("August 2023 Mirror Camera | CameraC");
        camGo.transform.SetParent(transform, false);
        camGo.transform.position = CameraPos; camGo.transform.rotation = CameraRot;
        cam = camGo.AddComponent<Camera>();
        cam.fieldOfView = 30f; cam.nearClipPlane = 5.01f; cam.farClipPlane = 15.8f;
        cam.clearFlags = CameraClearFlags.Skybox; cam.backgroundColor = new Color(0.19215687f, 0.3019608f, 0.4745098f, 0f);
        cam.cullingMask = 1206910775; cam.targetTexture = rt; cam.depth = -5f; cam.allowMSAA = false; cam.allowHDR = false;
        cam.stereoTargetEye = StereoTargetEyeMask.None; cam.useOcclusionCulling = false;
        cam.enabled = false;

        int bound = 0;
        foreach (Renderer r in world.ZoneRoot("city").GetComponentsInChildren<Renderer>(true))
        {
            foreach (Material m in r.sharedMaterials)
            {
                if (m == null || m.name.IndexOf("MirrorCenter", System.StringComparison.Ordinal) < 0) continue;
                m.mainTexture = rt;
                if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", rt);
                if (m.HasProperty("_EmissionMap")) { m.SetTexture("_EmissionMap", rt); m.EnableKeyword("_EMISSION"); }
                if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", new Color(0.8122524f, 0.8122524f, 0.8122524f, 1f));
                bound++;
            }
        }
        Debug.Log($"[GorillaOGV2][MIRROR] City mirror camera built, render texture bound to {bound} MirrorCenter material(s)");
    }

    private void Update()
    {
        if (cam == null || Time.unscaledTime < nextCheck) return;
        nextCheck = Time.unscaledTime + 0.5f;
        Transform head = GorillaTagger.Instance != null && GorillaTagger.Instance.headCollider != null ? GorillaTagger.Instance.headCollider.transform : null;
        bool near = head != null && (head.position - MirrorPos).sqrMagnitude < 20f * 20f;
        if (cam.enabled != near) cam.enabled = near;
    }

    private void OnDestroy() { if (rt != null) rt.Release(); }
}
