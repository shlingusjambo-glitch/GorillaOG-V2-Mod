using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace GorillaOGV2;

[BepInPlugin("com.elywright.gorillaogv2", "GorillaOG V2", "1.0.0")]
public sealed class Plugin : BaseUnityPlugin
{
    internal static ConfigEntry<bool> DebugCube;
    internal static ConfigEntry<bool> LegacyPlayer;
    internal static ConfigEntry<int> RenderingDiagnosticMode;
    internal static ConfigEntry<bool> ZoneTour;
#if GOG_DEBUG
    internal const bool DebugBuild = true;
#else
    internal const bool DebugBuild = false;
#endif

    private void Awake()
    {
        DebugCube = Config.Bind("Verification", "PlayerDebugCube", false,
            "Replace every GorillaBodyRenderer with an obvious magenta cube for renderer-path verification.");
        LegacyPlayer = Config.Bind("Restoration", "LegacyPlayer", true,
            "Use the player mesh and textures extracted from manifest 910642836555163397.");
        RenderingDiagnosticMode = Config.Bind("Rendering", "HistoricalDiagnosticMode", 4,
            "Historical rendering diagnostic: 1=albedo, 2=lightmap, 3=albedo x decoded lightmap, 4=final historical material path.");

        ZoneTour = Config.Bind("Verification", "ZoneTour", false,
            "After spawn verification, switch to canyon, city and forest in turn, teleport to the 2023 spawn and save zone-*.png captures.");

        // Player restoration is patch-driven.
        new Harmony("com.elywright.gorillaogv2").PatchAll();

        // Historical environment is loaded as one unified, zone-aware scene package.
        // Modern world UI (computer, MOTD, rules, wardrobe, etc.) remains untouched.
        gameObject.AddComponent<HistoricalWorldController>();
    }
}

[HarmonyPatch(typeof(GorillaBodyRenderer), "Awake")]
internal static class GorillaBodyRendererAwakePatch
{
    private static void Postfix(GorillaBodyRenderer __instance)
    {
        if (__instance.GetComponent<DebugCubeController>() != null || __instance.GetComponent<LegacyPlayerController>() != null)
        {
            return;
        }

        if (Plugin.DebugCube.Value)
        {
            __instance.gameObject.AddComponent<DebugCubeController>().Initialize(__instance);
        }
        else if (Plugin.LegacyPlayer.Value)
        {
            __instance.gameObject.AddComponent<LegacyPlayerController>().Initialize(__instance);
        }
    }
}

[HarmonyPatch(typeof(GorillaBodyRenderer), "SetMaterialIndex")]
internal static class GorillaBodyRendererSetMaterialIndexPatch
{
    private static void Postfix(GorillaBodyRenderer __instance)
    {
        __instance.GetComponent<LegacyPlayerController>()?.ApplyBodyMaterial();
    }
}

[HarmonyPatch(typeof(GorillaBodyRenderer), "UpdateColor")]
internal static class GorillaBodyRendererUpdateColorPatch
{
    private static void Postfix(GorillaBodyRenderer __instance, Color color)
    {
        __instance.GetComponent<LegacyPlayerController>()?.SetColor(color);
    }
}

[HarmonyPatch(typeof(VRRig), "ShouldUseNewIKMethod")]
internal static class VRRigShouldUseNewIKMethodPatch
{
    private static bool Prefix(VRRig __instance, bool isReceivingNewIKData, ref bool __result)
    {
        // Disable body tracking for remote players so they appear as classic 2023 rigs
        if (!__instance.isOfflineVRRig)
        {
            __result = false;
            return false; // skip original method
        }
        return true;
    }
}

internal static class HistoricalCosmeticsGate
{
    private static readonly HashSet<string> Whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private static bool initialized;

    internal static void EnsureLoaded()
    {
        if (initialized) return;
        initialized = true;
        try
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GorillaOGV2.historical-cosmetics-whitelist.txt");
            if (stream != null)
            {
                using StreamReader reader = new StreamReader(stream);
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (!string.IsNullOrEmpty(line)) Whitelist.Add(line);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GorillaOGV2][COSMETICS] Failed loading whitelist: {ex}");
        }
    }

    internal static bool IsAllowed(string itemName)
    {
        if (string.IsNullOrEmpty(itemName) || itemName == "null" || itemName == "NOTHING") return true;
        EnsureLoaded();
        return Whitelist.Contains(itemName);
    }
}

[HarmonyPatch(typeof(VRRig), "IsItemAllowed")]
internal static class VRRigIsItemAllowedPatch
{
    private static bool Prefix(VRRig __instance, string itemName, ref bool __result)
    {
        if (!HistoricalCosmeticsGate.IsAllowed(itemName))
        {
            __result = false;
            return false;
        }
        return true;
    }
}

internal sealed class LegacyPlayerController : MonoBehaviour
{
    private GorillaBodyRenderer owner;
    private Material bodyMaterial;
    private GameObject chest;
    private SkinnedMeshRenderer historicalBody;
    private MeshRenderer historicalFace;
    private Color lastSyncedColor = Color.clear;
    private static MaterialPropertyBlock chestFacePropertyBlock;

    internal void Initialize(GorillaBodyRenderer bodyRenderer)
    {
        owner = bodyRenderer;
        GorillaMouthFlap mouthFlap = owner.rig.GetComponent<GorillaMouthFlap>();
        if (mouthFlap != null) mouthFlap.enabled = false;
        LegacyPlayerAssets assets = LegacyPlayerAssets.Instance;
        SkinnedMeshRenderer body = owner.GetBody(GorillaBodyType.Default);
        if (owner.rig != null && owner.rig.isOfflineVRRig)
        {
            string names = string.Join(",", Array.ConvertAll(body.bones, t => t == null ? "null" : t.name));
            Matrix4x4[] mb = body.sharedMesh.bindposes, hb = assets.Body.bindposes;
            Transform rigRoot = owner.rig.transform;
            string rel(Transform t) => t == null ? "null" : $"{rigRoot.InverseTransformPoint(t.position):F4}/{(Quaternion.Inverse(rigRoot.rotation) * t.rotation):F4}";
            Transform headB = Array.Find(body.bones, t => t != null && t.name.StartsWith("head"));
            Debug.Log($"[GorillaOGV2][PLAYER] rig-relative: body={rel(body.rootBone)} head={rel(headB)} bodyRenderer={rel(body.transform)} modernFace={rel(owner.faceRenderer?.transform)} faceParent={owner.faceRenderer?.transform.parent?.name}");
            Debug.Log($"[GorillaOGV2][PLAYER] modern bones({body.bones.Length})={names}\nmodern mesh '{body.sharedMesh.name}' verts={body.sharedMesh.vertexCount} bounds={body.sharedMesh.bounds} bind0={mb[0]} bind1={mb[1]}\n2023 mesh verts={assets.Body.vertexCount} bounds={assets.Body.bounds} bind0={(hb.Length > 0 ? hb[0].ToString() : "none")} bind1={(hb.Length > 1 ? hb[1].ToString() : "none")} adapted={assets.BodyAdapted}\nroot={body.rootBone?.name} rootLocal={body.rootBone?.localPosition}/{body.rootBone?.localRotation} body.local={body.transform.localPosition}/{body.transform.localRotation} parent={body.transform.parent?.name}");
        }
        assets.AdaptBodyToModernRig(body.sharedMesh.bindposes);

        bodyMaterial = new Material(assets.BodyMaterial);
        Color initialColor = owner.rig != null ? owner.rig.playerColor : Color.white;
        if (initialColor.r == 0f && initialColor.g == 0f && initialColor.b == 0f && initialColor.a == 0f)
        {
            initialColor = new Color(0.17f, 0.17f, 0.17f, 1f);
        }
        SetColor(initialColor);
        owner.rig.materialsToChangeTo[0] = bodyMaterial;

        GameObject historicalBodyObject = new GameObject("GorillaOGV2 Historical Body Renderer");
        historicalBodyObject.layer = body.gameObject.layer;
        historicalBodyObject.transform.SetParent(body.transform.parent, false);
        historicalBodyObject.transform.localPosition = body.transform.localPosition;
        historicalBodyObject.transform.localRotation = body.transform.localRotation;
        historicalBodyObject.transform.localScale = body.transform.localScale;
        historicalBody = historicalBodyObject.AddComponent<SkinnedMeshRenderer>();
        historicalBody.sharedMesh = assets.Body;
        historicalBody.bones = body.bones;
        historicalBody.rootBone = body.rootBone;
        historicalBody.localBounds = assets.Body.bounds;
        historicalBody.updateWhenOffscreen = body.updateWhenOffscreen;
        body.enabled = false;

        Transform bodyBone = null;
        Transform headBone = null;
        if (body.bones != null)
        {
            foreach (Transform b in body.bones)
            {
                if (b == null) continue;
                // 2023 bone names, or the current build's "_new" suffixed rig (same bind frames).
                if (b.name == "body" || b.name == "body_new") bodyBone = b;
                if (b.name == "head" || b.name == "head_new") headBone = b;
            }
        }
        if (bodyBone == null) bodyBone = body.rootBone;
        if (headBone == null) headBone = owner.faceRenderer != null ? owner.faceRenderer.transform.parent : body.rootBone;

        GameObject historicalFaceObject = new GameObject("GorillaOGV2 Historical Face Renderer");
        historicalFaceObject.layer = body.gameObject.layer;
        historicalFaceObject.transform.SetParent(headBone, false);
        historicalFaceObject.transform.localPosition = new Vector3(0f, -1.6420953f, 0.23085563f);
        historicalFaceObject.transform.localRotation = new Quaternion(-0.7524642f, 0f, 0f, 0.6586331f);
        historicalFaceObject.transform.localScale = Vector3.one;
        historicalFaceObject.AddComponent<MeshFilter>().sharedMesh = assets.Face;
        historicalFace = historicalFaceObject.AddComponent<MeshRenderer>();
        historicalFace.sharedMaterial = assets.ChestFaceMaterial;

        if (owner.faceRenderer != null)
        {
            owner.faceRenderer.enabled = false;
            owner.faceRenderer.gameObject.SetActive(false);
            if (owner.faceRenderer.TryGetComponent<MeshFilter>(out var modernFaceFilter))
            {
                modernFaceFilter.sharedMesh = null;
            }
        }
        if (owner.rig != null && owner.rig.faceSkin != null)
        {
            owner.rig.faceSkin.enabled = false;
        }

        chest = new GameObject("GorillaOGV2 Legacy Chest");
        chest.layer = body.gameObject.layer;
        chest.transform.SetParent(bodyBone, false);
        chest.transform.localPosition = new Vector3(0f, -1.2607757f, -0.011547923f);
        chest.transform.localRotation = new Quaternion(-0.7021969f, 0f, 0f, 0.7119828f);
        chest.transform.localScale = Vector3.one;
        chest.AddComponent<MeshFilter>().sharedMesh = assets.Chest;
        MeshRenderer chestRenderer = chest.AddComponent<MeshRenderer>();
        chestRenderer.sharedMaterial = assets.ChestFaceMaterial;

        ApplyBodyMaterial();
        Debug.Log($"[GorillaOGV2][PLAYER] Applied August 2023 player mesh to renderer {body.GetInstanceID()} on rig {owner.rig.GetInstanceID()}");
        if (Plugin.DebugBuild && owner.rig.isOfflineVRRig)
        {
            StartCoroutine(CaptureProof());
        }
    }

    internal void ApplyBodyMaterial()
    {
        Material selected = owner.rig.setMatIndex == 0 ? bodyMaterial : owner.rig.materialsToChangeTo[owner.rig.setMatIndex];
        // 2023 SkinnedMeshRenderer 246805 carried ONE material for the 3-submesh 'gorilla'
        // mesh, so Unity drew submesh 0 only; submeshes 1-2 (chest/belly patches) were never
        // visible. Assigning three materials drew them as a second grey patch.
        historicalBody.sharedMaterials = new[] { selected };
    }

    internal void SetColor(Color color)
    {
        if (bodyMaterial != null)
        {
            bodyMaterial.color = color;
            if (bodyMaterial.HasProperty("_BaseColor")) bodyMaterial.SetColor("_BaseColor", color);
            if (bodyMaterial.HasProperty("_Color")) bodyMaterial.SetColor("_Color", color);
        }
        lastSyncedColor = color;
    }

    internal void LogAudit(string phase)
    {
        int modern = 0;
        foreach (GorillaBodyType type in new[] { GorillaBodyType.Default, GorillaBodyType.NoHead, GorillaBodyType.Skeleton })
        {
            SkinnedMeshRenderer renderer = owner.GetBody(type);
            if (renderer != null && renderer.enabled)
            {
                modern++;
                Debug.Log($"[GorillaOGV2][AUDIT {phase}] modern player renderer enabled type={type} object={renderer.gameObject.name}");
            }
        }
        Debug.Log($"[GorillaOGV2][AUDIT {phase}] modernPlayerRenderersEnabled={modern} modernFaceEnabled={owner.faceRenderer.enabled} historicalBodyEnabled={historicalBody != null && historicalBody.enabled} historicalFaceEnabled={historicalFace != null && historicalFace.enabled} historicalChestEnabled={chest != null && chest.GetComponent<Renderer>().enabled}");
    }

    private void LateUpdate()
    {
        if (owner == null) return;
        SkinnedMeshRenderer modernDefault = owner.GetBody(GorillaBodyType.Default);
        SkinnedMeshRenderer modernNoHead = owner.GetBody(GorillaBodyType.NoHead);
        SkinnedMeshRenderer modernSkeleton = owner.GetBody(GorillaBodyType.Skeleton);
        if (modernDefault != null && modernDefault.enabled) modernDefault.enabled = false;
        if (modernNoHead != null && modernNoHead.enabled) modernNoHead.enabled = false;
        if (modernSkeleton != null && modernSkeleton.enabled) modernSkeleton.enabled = false;
        if (owner.faceRenderer != null && owner.faceRenderer.enabled) owner.faceRenderer.enabled = false;
        if (owner.rig != null && owner.rig.faceSkin != null && owner.rig.faceSkin.enabled) owner.rig.faceSkin.enabled = false;

        if (historicalBody != null && !historicalBody.enabled) historicalBody.enabled = true;
        if (historicalFace != null && !historicalFace.enabled) historicalFace.enabled = true;

        if (chest != null && historicalBody != null && chest.layer != historicalBody.gameObject.layer)
        {
            chest.layer = historicalBody.gameObject.layer;
        }

        if (chestFacePropertyBlock == null)
        {
            chestFacePropertyBlock = new MaterialPropertyBlock();
            chestFacePropertyBlock.SetColor("_BaseColor", Color.white);
            chestFacePropertyBlock.SetColor("_Color", Color.white);
        }

        Material faceMaterial = LegacyPlayerAssets.Instance.ChestFaceMaterial;
        if (historicalFace != null)
        {
            historicalFace.sharedMaterial = faceMaterial;
            historicalFace.SetPropertyBlock(chestFacePropertyBlock);
        }
        if (chest != null)
        {
            MeshRenderer renderer = chest.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = faceMaterial;
                renderer.SetPropertyBlock(chestFacePropertyBlock);
            }
        }

        if (owner.rig != null)
        {
            Color current = owner.rig.playerColor;
            if (current != lastSyncedColor && (current.r > 0f || current.g > 0f || current.b > 0f))
            {
                SetColor(current);
            }
        }
    }

    private IEnumerator CaptureProof()
    {
        yield return new WaitForSeconds(8f);
        string displayOutput = Path.Combine(Paths.PluginPath, "GorillaOGV2", "game-display.png");
        ScreenCapture.CaptureScreenshot(displayOutput);
        // Body-centred views on the live layers: front / side / three-quarter / back, all with
        // the scene lit as the player sees it, so material and mesh issues are visible.
        Transform head = owner.rig != null && owner.rig.headMesh != null ? owner.rig.headMesh.transform : historicalBody.transform;
        Bounds b = historicalBody.bounds;
        Vector3 center = Vector3.Lerp(b.center, head.position, 0.5f);
        Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        float d = 1.3f;
        (string name, Vector3 pos)[] shots =
        {
            ("front", center + fwd * d),
            ("side", center + right * d),
            ("quarter", center + (fwd + right).normalized * d + Vector3.up * 0.3f),
            ("back", center - fwd * d),
        };
        foreach (var shot in shots)
        {
            GameObject cameraObject = new GameObject("GorillaOGV2 Legacy Player Proof Camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.cullingMask = ~0;
            camera.nearClipPlane = 0.05f;
            camera.fieldOfView = 40f;
            camera.transform.position = shot.pos;
            camera.transform.LookAt(center);
            Capture(camera, Path.Combine(Paths.PluginPath, "GorillaOGV2", $"legacy-player-{shot.name}.png"));
            Destroy(cameraObject);
        }
        foreach (Renderer r in owner.rig.GetComponentsInChildren<Renderer>(false))
        {
            if (r.enabled && r.gameObject.activeInHierarchy)
                Debug.Log($"[GorillaOGV2][PLAYER] enabled renderer {r.GetType().Name} '{r.name}' parent={r.transform.parent?.name} bounds={r.bounds.center:F3}±{r.bounds.extents:F3} mat={r.sharedMaterial?.name}");
        }
        Debug.Log($"[GorillaOGV2][PLAYER] proof captures done; bodyBounds={b.center:F3}/{b.extents:F3} bodyMat={historicalBody.sharedMaterial?.shader.name} color={bodyMaterial.color}");
    }

    internal static void Capture(Camera camera, string output)
    {
        RenderTexture target = new RenderTexture(640, 640, 24);
        camera.targetTexture = target;
        camera.Render();
        RenderTexture.active = target;
        Texture2D image = new Texture2D(640, 640, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, 640, 640), 0, 0);
        image.Apply();
        File.WriteAllBytes(output, image.EncodeToPNG());
        RenderTexture.active = null;
        camera.targetTexture = null;
        Destroy(target);
        Destroy(image);
    }
}

internal sealed class LegacyPlayerAssets
{
    private static LegacyPlayerAssets instance;
    internal static LegacyPlayerAssets Instance => instance ??= new LegacyPlayerAssets();

    internal Mesh Body { get; }
    internal Mesh Chest { get; }
    internal Mesh Face { get; }
    internal Material BodyMaterial { get; }
    internal Material ChestFaceMaterial { get; }
    private bool bodyAdapted;
    internal bool BodyAdapted => bodyAdapted;

    private LegacyPlayerAssets()
    {
        using Stream stream = Resource("legacy-player.meshbin");
        using BinaryReader reader = new BinaryReader(stream);
        if (new string(reader.ReadChars(9)) != "GOG2MESH\u0001" || reader.ReadUInt32() != 3)
        {
            throw new InvalidDataException("Invalid embedded legacy player mesh set");
        }
        Body = ReadMesh(reader);
        Chest = ReadMesh(reader);
        Face = ReadMesh(reader);

        // The 2023 rig's "gorilla" renderer used sharedassets0 material 248 "darkfur":
        // built-in Standard, metallic 0, smoothness 0.09, specular highlights on,
        // glossy reflections off, lightfur (point filtered). URP Lit is the PBR equivalent.
        Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        BodyMaterial = new Material(shader) { name = "August 2023 darkfur" };
        Texture2D furTex = ReadTexture("lightfur.png");
        LegacyMaterial.Bind(BodyMaterial, furTex, Color.white);
        ConfigurePlayerMaterial(BodyMaterial, 0.09f, true);

        // Material 250 "gorillachestface": smoothness 0, specular off, reflections off, wrap repeat.
        ChestFaceMaterial = new Material(shader) { name = "August 2023 gorillachestface" };
        Texture2D chestFaceTex = ReadTexture("gorillachestface.png");
        LegacyMaterial.Bind(ChestFaceMaterial, chestFaceTex, Color.white);
        ConfigurePlayerMaterial(ChestFaceMaterial, 0f, false);
    }

    private static void ConfigurePlayerMaterial(Material material, float smoothness, bool specularHighlights)
    {
        if (material.HasProperty("_Metallic")) material.SetFloat("_Metallic", 0f);
        if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
        if (material.HasProperty("_SpecularHighlights")) material.SetFloat("_SpecularHighlights", specularHighlights ? 1f : 0f);
        if (material.HasProperty("_EnvironmentReflections")) material.SetFloat("_EnvironmentReflections", 0f);
        if (specularHighlights) material.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
        else material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
        material.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
    }

    internal void AdaptBodyToModernRig(Matrix4x4[] bindPoses)
    {
        if (!bodyAdapted)
        {
            Vector3[] vertices = Body.vertices;
            Vector3[] normals = Body.normals;
            Vector4[] tangents = Body.tangents;
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = new Vector3(vertices[i].x, vertices[i].z, -vertices[i].y);
                normals[i] = new Vector3(normals[i].x, normals[i].z, -normals[i].y);
                tangents[i] = new Vector4(tangents[i].x, tangents[i].z, -tangents[i].y, tangents[i].w);
            }
            Body.vertices = vertices;
            Body.normals = normals;
            Body.tangents = tangents;
            Body.RecalculateBounds();
            bodyAdapted = true;
        }
        Body.bindposes = bindPoses;
    }

    internal static Mesh ReadMesh(BinaryReader reader)
    {
        Mesh mesh = new Mesh { name = ReadString(reader) };
        Vector3[] vertices = ReadVector3(reader);
        if (vertices.Length > 65535) mesh.indexFormat = IndexFormat.UInt32;
        mesh.vertices = vertices;
        mesh.normals = ReadVector3(reader);
        mesh.tangents = ReadVector4(reader);
        mesh.uv = ReadVector2(reader);
        float[][] weights = ReadFloatVectors(reader, 4);
        int[][] indices = ReadIntVectors(reader, 4);
        if (weights.Length != 0)
        {
            BoneWeight[] boneWeights = new BoneWeight[weights.Length];
            for (int i = 0; i < weights.Length; i++)
            {
                boneWeights[i] = new BoneWeight
                {
                    weight0 = weights[i][0], weight1 = weights[i][1], weight2 = weights[i][2], weight3 = weights[i][3],
                    boneIndex0 = indices[i][0], boneIndex1 = indices[i][1], boneIndex2 = indices[i][2], boneIndex3 = indices[i][3]
                };
            }
            mesh.boneWeights = boneWeights;
        }
        int bindPoseCount = reader.ReadInt32();
        Matrix4x4[] bindPoses = new Matrix4x4[bindPoseCount];
        for (int i = 0; i < bindPoseCount; i++)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    bindPoses[i][row, column] = reader.ReadSingle();
                }
            }
        }
        mesh.bindposes = bindPoses;
        int subMeshCount = reader.ReadInt32();
        mesh.subMeshCount = subMeshCount;
        for (int i = 0; i < subMeshCount; i++)
        {
            int count = reader.ReadInt32();
            int[] triangles = new int[count];
            for (int j = 0; j < count; j++) triangles[j] = reader.ReadInt32();
            mesh.SetTriangles(triangles, i, false);
        }
        mesh.RecalculateBounds();
        return mesh;
    }

    private static Vector2[] ReadVector2(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        Vector2[] values = new Vector2[count];
        for (int i = 0; i < count; i++) values[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
        return values;
    }

    private static Vector3[] ReadVector3(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        Vector3[] values = new Vector3[count];
        for (int i = 0; i < count; i++) values[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        return values;
    }

    private static Vector4[] ReadVector4(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        Vector4[] values = new Vector4[count];
        for (int i = 0; i < count; i++) values[i] = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        return values;
    }

    private static float[][] ReadFloatVectors(BinaryReader reader, int width)
    {
        int count = reader.ReadInt32();
        float[][] values = new float[count][];
        for (int i = 0; i < count; i++)
        {
            values[i] = new float[width];
            for (int j = 0; j < width; j++) values[i][j] = reader.ReadSingle();
        }
        return values;
    }

    private static int[][] ReadIntVectors(BinaryReader reader, int width)
    {
        int count = reader.ReadInt32();
        int[][] values = new int[count][];
        for (int i = 0; i < count; i++)
        {
            values[i] = new int[width];
            for (int j = 0; j < width; j++) values[i][j] = reader.ReadInt32();
        }
        return values;
    }

    internal static Texture2D ReadTexture(string name, bool linear = false)
    {
        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear) { name = name };
        using Stream stream = Resource(name);
        byte[] bytes = new byte[stream.Length];
        stream.Read(bytes, 0, bytes.Length);
        ImageConversion.LoadImage(texture, bytes, false);
        texture.wrapMode = TextureWrapMode.Repeat;
        texture.filterMode = FilterMode.Point;
        texture.anisoLevel = 1;
        return texture;
    }

    internal static Stream Resource(string name)
    {
        return Assembly.GetExecutingAssembly().GetManifestResourceStream($"GorillaOGV2.{name}")
            ?? throw new FileNotFoundException(name);
    }

    private static string ReadString(BinaryReader reader)
    {
        return new string(reader.ReadChars(reader.ReadInt32()));
    }
}

internal sealed class DebugCubeController : MonoBehaviour
{
    private GorillaBodyRenderer owner;
    private GameObject cube;

    internal void Initialize(GorillaBodyRenderer bodyRenderer)
    {
        owner = bodyRenderer;
        SkinnedMeshRenderer activeBody = owner.ActiveBody;
        if (activeBody == null)
        {
            Debug.LogError("[GorillaOGV2] GorillaBodyRenderer had no active body after Awake");
            Destroy(this);
            return;
        }

        cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "GorillaOGV2 Player Renderer Proof Cube";
        Destroy(cube.GetComponent<Collider>());
        cube.transform.SetParent(activeBody.transform, false);
        cube.transform.localPosition = new Vector3(0f, 0.35f, 0f);
        cube.transform.localRotation = Quaternion.identity;
        cube.transform.localScale = new Vector3(0.75f, 0.75f, 0.75f);
        cube.GetComponent<MeshRenderer>().material.color = Color.magenta;
        Debug.Log($"[GorillaOGV2] Debug cube attached to renderer {activeBody.GetInstanceID()} on rig {owner.rig.GetInstanceID()}");
        if (Plugin.DebugBuild && owner.rig.isOfflineVRRig)
        {
            StartCoroutine(CaptureProof());
        }
    }

    private IEnumerator CaptureProof()
    {
        yield return new WaitForSeconds(3f);
        cube.layer = 31;
        MeshRenderer cubeRenderer = cube.GetComponent<MeshRenderer>();
        Shader unlit = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlit != null)
        {
            cubeRenderer.material.shader = unlit;
            cubeRenderer.material.color = Color.magenta;
        }

        GameObject cameraObject = new GameObject("GorillaOGV2 Player Proof Camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.cullingMask = 1 << 31;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0f, 0.25f, 0.25f, 1f);
        camera.transform.position = cube.transform.position + Vector3.forward * 2f;
        camera.transform.LookAt(cube.transform.position);

        RenderTexture target = new RenderTexture(640, 640, 24);
        camera.targetTexture = target;
        camera.Render();
        RenderTexture.active = target;
        Texture2D image = new Texture2D(640, 640, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, 640, 640), 0, 0);
        image.Apply();
        string output = Path.Combine(Paths.PluginPath, "GorillaOGV2", "player-debug-proof.png");
        File.WriteAllBytes(output, image.EncodeToPNG());
        Debug.Log($"[GorillaOGV2] Wrote player renderer proof to {output}");

        RenderTexture.active = null;
        camera.targetTexture = null;
        Destroy(target);
        Destroy(image);
        Destroy(cameraObject);
    }

    private void LateUpdate()
    {
        if (owner == null) return;
        for (GorillaBodyType type = GorillaBodyType.Default; type <= GorillaBodyType.Skeleton; type++)
        {
            SkinnedMeshRenderer renderer = owner.GetBody(type);
            if (renderer != null)
            {
                renderer.enabled = false;
            }
        }
        owner.faceRenderer.enabled = false;
        if (cube != null && !cube.activeSelf)
        {
            cube.SetActive(true);
        }
    }
}
