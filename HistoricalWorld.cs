using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace GorillaOGV2;

internal static class LegacyMaterial
{
    internal static void Bind(Material material, Texture texture, Color color)
    {
        material.mainTexture = texture;
        material.mainTextureScale = Vector2.one;
        material.mainTextureOffset = Vector2.zero;
        if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
        if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
        if (material.HasProperty("_BaseMap_ST")) material.SetVector("_BaseMap_ST", new Vector4(1f, 1f, 0f, 0f));
        if (material.HasProperty("_MainTex_ST")) material.SetVector("_MainTex_ST", new Vector4(1f, 1f, 0f, 0f));
        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
    }
}

internal static class LegacyLighting
{
    private static bool applied;
    internal static readonly List<Material> SkyMaterials = new List<Material>();
    internal static string ActiveState { get; private set; } = string.Empty;
    internal const int LightmapIndex = 31;
    internal static Texture2D ActiveLightmapTexture { get; private set; }
    private static Texture2D activeDecodedLightmap;
    internal static Texture2DArray ActiveLightmapArray { get; private set; }
    private static Texture2D neutralLightmapDir;
    private static readonly Dictionary<string, Texture2D> historicalMaps = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2D> decodedHistoricalMaps = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Texture2DArray> historicalMapArrays = new Dictionary<string, Texture2DArray>(StringComparer.OrdinalIgnoreCase);

    private static float nextLightingCheckTime;
    private static string lastModernState;
    private static int lastTimeIndex = -1;
    private static int lastWeather = -1;

    internal static void Apply()
    {
        if (applied) return;
        applied = true;
        ActiveState = "noon";

        ApplyHistoricalLightmap("noon");
        Debug.Log("[GorillaOGV2][LIGHTING] Restored August 2023 material lightmaps without changing ambient, exposure, cameras, or directional lights");
    }

    internal static Texture2D GetNeutralLightmapDir()
    {
        if (neutralLightmapDir == null)
        {
            neutralLightmapDir = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Color neutral = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            neutralLightmapDir.SetPixels(new[] { neutral, neutral, neutral, neutral });
            neutralLightmapDir.Apply();
            neutralLightmapDir.wrapMode = TextureWrapMode.Clamp;
        }
        return neutralLightmapDir;
    }

    internal static Texture2D GetHistoricalLightmap(string key)
    {
        if (string.IsNullOrEmpty(key)) key = "noon";
        if (historicalMaps.TryGetValue(key, out Texture2D tex) && tex != null)
        {
            return tex;
        }

        string lightmapDir = Path.Combine(Paths.PluginPath, "GorillaOGV2", "historical-world", "lightmaps");
        string file = Path.Combine(lightmapDir, $"lightmap-{key}.png");
        if (!File.Exists(file))
        {
            file = Path.Combine(lightmapDir, "lightmap-noon.png");
        }

        if (File.Exists(file))
        {
            byte[] bytes = File.ReadAllBytes(file);
            // Load as linear — RGBM lightmap data is already in linear HDR space.
            // If loaded as sRGB, Unity would apply an extra gamma-to-linear conversion
            // on top of the RGBM decode, further brightening everything.
            tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            tex.LoadImage(bytes, false);
            tex.name = $"August 2023 Lightmap | {key}";
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            historicalMaps[key] = tex;
            return tex;
        }

        Debug.LogError($"[GorillaOGV2][LIGHTING] Failed to locate historical lightmap file for '{key}' at {file}");
        return null;
    }

    private static Texture2D GetDecodedHistoricalLightmap(string key)
    {
        if (decodedHistoricalMaps.TryGetValue(key, out Texture2D decoded) && decoded != null) return decoded;
        Texture2D rgbm = GetHistoricalLightmap(key);
        if (rgbm == null) return null;
        Color[] pixels = rgbm.GetPixels();
        for (int i = 0; i < pixels.Length; i++)
        {
            Color p = pixels[i];
            float multiplier = p.a * 5f;
            pixels[i] = new Color(p.r * multiplier, p.g * multiplier, p.b * multiplier, 1f);
        }
        decoded = new Texture2D(rgbm.width, rgbm.height, TextureFormat.RGBAHalf, false, true)
        {
            name = $"August 2023 Decoded Lightmap | {key}",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        decoded.SetPixels(pixels);
        // Keep CPU data until the matching Texture2DArray slice is populated.
        decoded.Apply(false, false);
        decodedHistoricalMaps[key] = decoded;
        return decoded;
    }

    private static Texture2DArray GetHistoricalLightmapArray(string key)
    {
        if (historicalMapArrays.TryGetValue(key, out Texture2DArray array) && array != null) return array;
        Texture2D decoded = GetDecodedHistoricalLightmap(key);
        if (decoded == null) return null;
        array = new Texture2DArray(decoded.width, decoded.height, 1, TextureFormat.RGBAHalf, false, true)
        {
            name = $"August 2023 Lightmap Array | {key}",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        array.SetPixels(decoded.GetPixels(), 0);
        array.Apply(false, true);
        historicalMapArrays[key] = array;
        return array;
    }

    internal static void EnsureLightmapSlot()
    {
        if (ActiveLightmapTexture == null)
        {
            ActiveLightmapTexture = GetHistoricalLightmap(string.IsNullOrEmpty(ActiveState) ? "noon" : ActiveState);
        }
        if (ActiveLightmapTexture == null) return;

        LightmapData[] maps = LightmapSettings.lightmaps ?? new LightmapData[0];
        if (maps.Length <= LightmapIndex)
        {
            Array.Resize(ref maps, LightmapIndex + 1);
        }

        for (int i = 0; i <= LightmapIndex; i++)
        {
            if (maps[i] == null) maps[i] = new LightmapData();
        }

        bool changed = false;
        if (maps[LightmapIndex].lightmapColor != ActiveLightmapTexture)
        {
            maps[LightmapIndex].lightmapColor = ActiveLightmapTexture;
            changed = true;
        }
        // The 2023 scene used non-directional RGBM lightmaps. Supplying a fabricated
        // directional map selects a different shader variant and corrupts the bake.
        if (maps[LightmapIndex].lightmapDir != null)
        {
            maps[LightmapIndex].lightmapDir = null;
            changed = true;
        }

        if (changed || LightmapSettings.lightmaps == null || LightmapSettings.lightmaps.Length <= LightmapIndex)
        {
            LightmapSettings.lightmaps = maps;
        }
    }

    private static float nextSlotCheck;

    internal static void MaintainAmbientLighting()
    {
        if (!applied) return;
        // LightmapSettings.lightmaps allocates a fresh array (and LightmapData objects) on every
        // read; polling it per frame was the main source of GC stalls. Once a second is plenty.
        if (Time.unscaledTime >= nextSlotCheck)
        {
            nextSlotCheck = Time.unscaledTime + 1f;
            LightmapData[] maps = LightmapSettings.lightmaps;
            if (maps == null || maps.Length <= LightmapIndex || maps[LightmapIndex] == null ||
                maps[LightmapIndex].lightmapColor != ActiveLightmapTexture)
            {
                EnsureLightmapSlot();
            }
        }
        // BetterDayNightManager owns the same global names and updates them during
        // transitions. Rebind after its Update so historical materials never sample
        // a modern map or an interpolated endpoint from another atlas layout.
        if (activeDecodedLightmap != null)
        {
            Shader.SetGlobalTexture("_GlobalDayNightLightmap1", activeDecodedLightmap);
            Shader.SetGlobalTexture("_GlobalDayNightLightmap2", activeDecodedLightmap);
            Shader.SetGlobalFloat("_GlobalDayNightLerpValue", 0f);
        }
        if (ActiveLightmapArray != null) Shader.SetGlobalTexture("_DayNightLightmapArray", ActiveLightmapArray);
        // BetterDayNightManager owns ambient probes and the directional light.  Those
        // globals also light players and modern functional objects, so changing them
        // here made the whole game unnaturally dark and physically shaded.
    }

    private static void ApplyHistoricalLightmap(string key)
    {
        ActiveLightmapTexture = GetHistoricalLightmap(key);
        EnsureLightmapSlot();
        // The original environment shader is still shipped by the modern game.
        // Feed both of its historical day/night samplers the selected 2023 map;
        // BetterDayNightManager may change the global lerp, but identical endpoints
        // keep this pass stable until true two-map blending is reconstructed.
        Texture2D decoded = GetDecodedHistoricalLightmap(key);
        if (decoded != null)
        {
            activeDecodedLightmap = decoded;
            ActiveLightmapArray = GetHistoricalLightmapArray(key);
            Shader.SetGlobalTexture("_GlobalDayNightLightmap1", decoded);
            Shader.SetGlobalTexture("_GlobalDayNightLightmap2", decoded);
            Shader.SetGlobalFloat("_GlobalDayNightLerpValue", 0f);
            if (ActiveLightmapArray != null) Shader.SetGlobalTexture("_DayNightLightmapArray", ActiveLightmapArray);
            Debug.Log($"[GorillaOGV2][LIGHTING] Bound decoded linear RGBAHalf historical lightmap '{key}' ({decoded.width}x{decoded.height}, RGBM range 5.0)");
        }
        bool isNight   = key.Contains("night");
        bool isSunset  = key.Contains("sunset");
        bool isSunrise = key.Contains("sunrise");
        bool isRain    = key.Contains("rain");
        Color skyTint;

        if (isNight)
        {
            skyTint       = new Color(0.20f, 0.25f, 0.40f, 1f);
        }
        else if (isSunset)
        {
            skyTint       = new Color(0.95f, 0.65f, 0.50f, 1f);
        }
        else if (isSunrise)
        {
            skyTint       = new Color(0.95f, 0.78f, 0.62f, 1f);
        }
        else if (isRain)
        {
            skyTint       = new Color(0.65f, 0.68f, 0.72f, 1f);
        }
        else
        {
            skyTint       = Color.white;
        }

        for (int i = 0; i < SkyMaterials.Count; i++)
        {
            Material sm = SkyMaterials[i];
            if (sm != null)
            {
                sm.color = skyTint;
                if (sm.HasProperty("_BaseColor")) sm.SetColor("_BaseColor", skyTint);
                if (sm.HasProperty("_Color"))     sm.SetColor("_Color",     skyTint);
            }
        }

    }

    internal static void UpdateFromDayNight()
    {
        if (!applied) Apply();
        if (lastModernState != null && Time.time < nextLightingCheckTime) return;
        nextLightingCheckTime = Time.time + 1.0f;

        BetterDayNightManager manager = BetterDayNightManager.instance;
        string modernState = manager != null ? manager.currentTimeOfDay : "noon";
        int timeIndex = manager != null ? manager.currentTimeIndex : 0;
        int weather = manager != null ? (int)manager.CurrentWeather() : 0;

        if (modernState == lastModernState && timeIndex == lastTimeIndex && weather == lastWeather)
        {
            return;
        }
        lastModernState = modernState;
        lastTimeIndex = timeIndex;
        lastWeather = weather;

        string key = SelectHistoricalState(modernState, timeIndex, (BetterDayNightManager.WeatherType)weather);
        if (ActiveState == key) return;

        ActiveState = key;
        ApplyHistoricalLightmap(key);
        Debug.Log($"[GorillaOGV2][LIGHTING] Transitioned: modern='{modernState}' weather={weather} index={timeIndex} -> August2023='{key}'");
    }

    private static string SelectHistoricalState(string state, int index, BetterDayNightManager.WeatherType weather)
    {
        string value = (state ?? string.Empty).ToLowerInvariant();
        bool isNight = value.Contains("night") || value.Contains("sunset") || value.Contains("dusk") || index == 8 || index == 9;

        if (weather == BetterDayNightManager.WeatherType.Raining || value.Contains("rain"))
        {
            return isNight ? "rainnight" : "rainday";
        }

        if (value.Contains("sunrise") || value.Contains("dawn")) return "sunrise";
        if (value.Contains("10am") || value.Contains("morning")) return "10am";
        if (value.Contains("3pm") || value.Contains("afternoon")) return "3pm";
        if (value.Contains("sunset") || value.Contains("dusk")) return "sunset";
        if (value.Contains("night")) return "night";
        if (value.Contains("noon") || value.Contains("day")) return "noon";

        string[] ordered = { "sunrise", "sunrise", "10am", "10am", "noon", "3pm", "3pm", "sunset", "night", "night" };
        return ordered[Mathf.Abs(index) % ordered.Length];
    }
}

[HarmonyPatch(typeof(BetterDayNightManager), "PopulateAllLightmaps", new Type[] { typeof(int), typeof(int) })]
internal static class BetterDayNightManagerPopulateAllLightmapsPatch
{
    private static void Postfix()
    {
        LegacyLighting.EnsureLightmapSlot();
    }
}

[HarmonyPatch(typeof(BetterDayNightManager), "PopulateAllLightmaps", new Type[0])]
internal static class BetterDayNightManagerPopulateAllLightmapsNoArgsPatch
{
    private static void Postfix()
    {
        LegacyLighting.EnsureLightmapSlot();
    }
}

internal sealed class HistoricalWorldController : MonoBehaviour
{
    [Flags]
    private enum HistoricalMaterialFlags : byte
    {
        LightmapDayCycle = 1, Cutout = 2, Transparent = 4, Unlit = 8,
        Water = 16, Sky = 32
    }

    private sealed class HistoricalMaterialRecord
    {
        internal string Name, ShaderName;
        internal Texture2D Texture;
        internal Vector2 Scale, Offset;
        internal Color Color;
        internal HistoricalMaterialFlags Flags;
        internal float Cutoff;
        internal int RenderQueue;
        internal readonly Dictionary<string, float> Floats = new Dictionary<string, float>();
        internal readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>();
        internal readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, GameObject> zoneRoots = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HistoricalMaterialRecord> materialRecords = new Dictionary<string, HistoricalMaterialRecord>();
    private readonly Dictionary<string, Material> materials = new Dictionary<string, Material>();
    private readonly HashSet<Renderer> suppressedRenderers = new HashSet<Renderer>();
    private readonly HashSet<Collider> suppressedColliders = new HashSet<Collider>();
    private readonly List<string> suppressionRecords = new List<string>();
    private bool suppressionLogDirty;
    private bool zoneStateDirty;
    private bool loaded;
    private bool zoneCallbacksRegistered;
    private string loadedPackagePath = string.Empty;
    private long loadedPackageBytes;
    private string loadedPackageVersion = string.Empty;
    private long loadDurationMs;
    internal static HistoricalWorldController Instance { get; private set; }

    private int batchCountTotal;
    private int colliderCountTotal;
    private int materialCountTotal;

    private void Awake()
    {
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Start()
    {
        string package = Path.Combine(Paths.PluginPath, "GorillaOGV2", "historical-world", "historical-world.bin");
        loadedPackagePath = package;
        if (!File.Exists(package))
        {
            Debug.LogError($"[GorillaOGV2][WORLD FATAL] Complete historical world package is missing: {package}");
            return;
        }

        try
        {
            Stopwatch sw = Stopwatch.StartNew();
            LoadSynchronous(package);
            sw.Stop();
            loadDurationMs = sw.ElapsedMilliseconds;
            loaded = true;
            Debug.Log($"[GorillaOGV2][WORLD] Successfully loaded August 2023 historical world in {loadDurationMs} ms!");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GorillaOGV2][WORLD FATAL] Exception while loading historical world package: {ex}");
            return;
        }

        RegisterZoneCallbacks();
        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.sceneUnloaded += OnSceneUnloaded;

        ApplyZoneState(force: true);
        if (Plugin.DebugBuild) PerformAudit("0s_initial_load");
        StartCoroutine(RestoreSpawnLocation());
        StartCoroutine(InitializeStumpEnvironment());
        gameObject.AddComponent<HistoricalCityUi>().Initialize(this);
        if (Plugin.DebugBuild && Plugin.ZoneTour.Value) StartCoroutine(ZoneTour());
    }

    // Debug: every component-bearing object near a point, so 2026 counterparts of 2023 furniture can be named.
    private static void DumpNear(string tag, Vector3 center, float radius)
    {
        int n = 0;
        foreach (Transform t in FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (t == null || Vector3.Distance(t.position, center) > radius || IsOurs(t) || t.GetComponentInParent<VRRig>() != null) continue;
            Component[] comps = t.GetComponents<Component>();
            if (comps.Length <= 1) continue;
            string types = string.Join("/", Array.ConvertAll(comps, c => c == null ? "null" : c.GetType().Name));
            if (types == "Transform" || types == "RectTransform") continue;
            Renderer r = t.GetComponent<Renderer>();
            Debug.Log($"[GorillaOGV2][DUMP:{tag}] {HierarchyPath(t, null)} active={t.gameObject.activeInHierarchy} pos={t.position:F3} rot={t.rotation:F4} scale={t.lossyScale:F3} comps={types}{(r != null ? " renderer=" + r.enabled : "")}");
            if (++n > 400) break;
        }
    }

    private static void CaptureAround(string stem, Vector3 target, float distance)
    {
        string[] names = { "n", "e", "s", "w" };
        Vector3[] dirs = { Vector3.forward, Vector3.right, Vector3.back, Vector3.left };
        for (int i = 0; i < 4; i++) HistoricalStumpEnvironment.CaptureViewpoint($"{stem}-{names[i]}.png", target + dirs[i] * distance + Vector3.up * 0.3f, target, 500f);
    }

    // Verification only: drive the same ZoneManagement.SetActiveZones call the modern
    // tunnel triggers make, stand at the 2023 spawn, and photograph the result.
    private IEnumerator ZoneTour()
    {
        yield return new WaitForSeconds(20f);
        (GTZone zone, Vector3 spawn, float yaw)[] stops =
        {
            (GTZone.canyon, new Vector3(-85.42f, 10.53f, -108.35f), -157f),
            (GTZone.city, new Vector3(-55.38f, 17f, -104.51f), 127f),
            (GTZone.mall, new Vector3(-55.38f, 17f, -104.51f), 127f),      // user route: city -> mall -> mountain
            (GTZone.mountain, new Vector3(-19.008f, 18.661f, -100.46f), 81f),
            (GTZone.cave, new Vector3(-61.07f, -16.24f, -30.63f), -90f),
            (GTZone.forest, new Vector3(-64f, 12.534f, -83.014f), -90f),
        };
        // Where the modern zone triggers sit, so 2023 tunnels can be checked against them.
        FieldInfo zonesField = typeof(GorillaSetZoneTrigger).GetField("zones", BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (GorillaSetZoneTrigger trigger in FindObjectsByType<GorillaSetZoneTrigger>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            GTZone[] zs = zonesField?.GetValue(trigger) as GTZone[];
            Collider col = trigger.GetComponent<Collider>();
            Debug.Log($"[GorillaOGV2][TRIGGERS] {HierarchyPath(trigger.transform, null)} zones={(zs == null ? "?" : string.Join("+", zs))} active={trigger.gameObject.activeInHierarchy} bounds={(col != null ? col.bounds.center.ToString("F2") + "±" + col.bounds.extents.ToString("F2") : trigger.transform.position.ToString("F2"))}");
        }
        foreach (var stop in stops)
        {
            Debug.Log($"[GorillaOGV2][TOUR] entering {stop.zone}");
            ZoneManagement.SetActiveZones(new[] { stop.zone });
            yield return new WaitForSeconds(1f);
            GorillaLocomotion.GTPlayer player = GorillaLocomotion.GTPlayer.Instance;
            if (player != null)
            {
                Transform cam = player.mainCamera != null ? player.mainCamera.transform : player.headCollider.transform;
                player.Turn(Mathf.DeltaAngle(cam.eulerAngles.y, stop.yaw));
                player.TeleportTo(stop.spawn, player.transform.rotation, keepVelocity: false, center: true);
            }
            yield return new WaitForSeconds(6f);
            ApplyZoneState();
            string name = stop.zone.ToString();
            for (int i = 0; i < 4; i++)
            {
                Vector3 dir = Quaternion.Euler(10f, stop.yaw + 90f * i, 0f) * Vector3.forward;
                Vector3 eye = stop.spawn + Vector3.up * 0.4f;
                HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-view{i}.png", eye, eye + dir, 500f);
            }
            // Forest<->canyon tunnel (2023 'canyon entrance', baked into TreeRoom) from both mouths and inside.
            HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-tunnel-canyonside.png", new Vector3(-88f, 11f, -110f), new Vector3(-78f, 12f, -98f), 500f);
            HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-tunnel-inside.png", new Vector3(-77f, 12f, -97f), new Vector3(-88f, 10f, -110f), 500f);
            HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-tunnel-forestside.png", new Vector3(-66f, 13f, -88f), new Vector3(-78f, 12f, -98f), 500f);
            // City info hut (2023 'Info page 1/2' at x≈-51, z≈-100) and the city→mountain hallway wall screen.
            HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-infohut.png", new Vector3(-53.5f, 16.9f, -103.2f), new Vector3(-51.2f, 16.5f, -99.9f), 500f);
            HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-wardrobe.png", new Vector3(-55.6f, 16.9f, -100.6f), new Vector3(-54.6f, 16.3f, -97.7f), 500f);
            HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-wardrobe-close.png", new Vector3(-54.9f, 16.7f, -99.0f), new Vector3(-54.6f, 16.25f, -97.7f), 500f);
            HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-mountainhall.png", new Vector3(-29f, 18.2f, -101.5f), new Vector3(-25.4f, 17.8f, -103.7f), 500f);
            // 2023 store: checkout counter, ATM, stand shelves, fitting-room mirror, city scoreboard.
            if (stop.zone == GTZone.mountain)
            {
                DumpNear("mountain-ui", new Vector3(-26.5f, 17.8f, -95.3f), 4f);
                // 2023 Mountain/UI: monitor (-27.78,17.55,-95.14), keys around (-27.1,17.85,-95.1), heads (-24.7,17.6,-94.8).
                HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-ui-computer.png", new Vector3(-26.3f, 18.4f, -96.3f), new Vector3(-27.4f, 17.8f, -95.2f), 500f);
                HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-ui-wardrobe.png", new Vector3(-25.6f, 17.9f, -95.9f), new Vector3(-24.7f, 17.6f, -94.8f), 500f);
                HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-ui-modes.png", new Vector3(-26.2f, 18.1f, -95.6f), new Vector3(-27.5f, 17.85f, -96.95f), 500f);
            }
            if (stop.zone == GTZone.city)
            {
                HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-checkout.png", new Vector3(-60.4f, 17.4f, -109.2f), new Vector3(-62.6f, 16.6f, -111.0f), 500f);
                HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-atm.png", new Vector3(-58.2f, 17.1f, -105.5f), new Vector3(-59.7f, 16.6f, -107.1f), 500f);
                HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-shelf.png", new Vector3(-67.7f, 17.2f, -121.5f), new Vector3(-67.7f, 16.4f, -124.5f), 500f);
                HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-mirror.png", new Vector3(-50.5f, 17.0f, -116.1f), new Vector3(-49.3f, 16.6f, -118.9f), 500f);
                HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-fruithut.png", new Vector3(-47.2f, 22.3f, -101.5f), new Vector3(-47.5f, 21.6f, -98.7f), 500f);
                Vector3 banana = new Vector3(-47.5028f, 21.6854f, -98.6783f);
                HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-stand-front.png", banana + new Vector3(0.724f, -0.401f, 0.561f).normalized * 1.0f + Vector3.up * 0.3f, banana, 500f);
                HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-stand-back.png", banana - new Vector3(0.724f, -0.401f, 0.561f).normalized * 1.0f + Vector3.up * 0.3f, banana, 500f);
                Vector3 cart = new Vector3(-62.6598f, 16.6667f, -111.3364f);
                CaptureAround($"zone-{name}-cart", cart, 1.2f);
                HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-cart-close.png", cart + new Vector3(0.964f, 0.265f, 0.015f) * 0.45f, cart, 500f);
                foreach (string ln in new[] { "CheckoutCartButtonText01", "ButtonText_FruitHut", "SlotPrice_MainAisle_Headphones" })
                {
                    if (!HistoricalCityUi.TextsByName.TryGetValue(ln, out TMPro.TextMeshPro lt) || lt == null) continue;
                    HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-label-{ln}.png", lt.transform.position - lt.transform.forward * 0.15f, lt.transform.position, 500f);
                }
                Vector3 fruitLabel = new Vector3(-47.456f, 21.704f, -100.258f);
                HistoricalStumpEnvironment.CaptureViewpoint($"zone-{name}-fruit-close.png", fruitLabel + new Vector3(0.724f, 0.1f, 0.561f).normalized * 0.5f, fruitLabel, 500f);
                CaptureAround($"zone-{name}-scoreboard", new Vector3(-60.5f, 16.6f, -106.75f), 2.2f);
            }
            if (stop.zone == GTZone.forest)
            {
                GetComponent<HistoricalStumpEnvironment>()?.CaptureStumpSet("-return");
                for (int c = 0; c < 4; c++) Debug.Log($"[GorillaOGV2][WARDROBE] 2023 category {c} ({new[] { "HATS", "FACE", "BADGES", "HOLDABLES" }[c]}): {HistoricalWardrobe.Items(c).Count} owned items, page {HistoricalWardrobe.Pages[c]}");
                CaptureAround($"zone-{name}-scoreboard", new Vector3(-61.08f, 4.3f, -60.74f), 2.5f);
            }
            yield return new WaitForSeconds(2f);
        }
        Debug.Log("[GorillaOGV2][TOUR] complete");
    }

    private IEnumerator InitializeStumpEnvironment()
    {
        Transform treeRoom = null;
        for (int i = 0; i < 60; i++)
        {
            treeRoom = FindModernTreeRoom();
            if (treeRoom != null && treeRoom.Find("TreeRoomInteractables") != null) break;
            yield return new WaitForSeconds(0.25f);
        }

        if (treeRoom != null)
        {
            if (GetComponent<HistoricalStumpEnvironment>() == null)
            {
                gameObject.AddComponent<HistoricalStumpEnvironment>().Initialize(treeRoom, this);
            }
        }
        else
        {
            Debug.LogError("[GorillaOGV2][STUMP] TreeRoom not found during stump environment initialization.");
        }
    }

    private IEnumerator RestoreSpawnLocation()
    {
        Vector3 oldSpawnPos = new Vector3(-64.0f, 12.534f, -83.014f);
        const float oldSpawnYaw = -90f;

        for (int i = 0; i < 40; i++)
        {
            if (GorillaLocomotion.GTPlayer.Instance != null) break;
            yield return new WaitForSeconds(0.1f);
        }

        if (GorillaLocomotion.GTPlayer.Instance != null)
        {
            GorillaLocomotion.GTPlayer player = GorillaLocomotion.GTPlayer.Instance;
            // Let XR tracking, first-time-user setup, and scene spawn systems
            // finish first. Re-assert at measured intervals because current
            // builds can perform a later startup teleport.
            float[] applyAt = { 2f, 8f, 15f };
            float elapsed = 0f;
            foreach (float deadline in applyAt)
            {
                yield return new WaitForSeconds(deadline - elapsed);
                elapsed = deadline;

                Transform cameraTransform = player.mainCamera != null ? player.mainCamera.transform : player.headCollider.transform;
                float yawDelta = Mathf.DeltaAngle(cameraTransform.eulerAngles.y, oldSpawnYaw);
                player.Turn(yawDelta);
                // center=true applies the XR-origin offset so the tracked
                // head/camera, rather than merely the locomotion root, lands
                // on the serialized August 2023 spawn Transform.
                player.TeleportTo(oldSpawnPos, player.transform.rotation, keepVelocity: false, center: true);
                yield return new WaitForFixedUpdate();

                Transform head = player.headCollider != null ? player.headCollider.transform : cameraTransform;
                float headError = Vector3.Distance(head.position, oldSpawnPos);
                float yawError = Mathf.Abs(Mathf.DeltaAngle(cameraTransform.eulerAngles.y, oldSpawnYaw));
                Debug.Log($"[GorillaOGV2][SPAWN] {deadline:F0}s verified 2023 Tree House spawn: " +
                          $"root={player.transform.position:F4} head={head.position:F4} camera={cameraTransform.position:F4} " +
                          $"cameraYaw={cameraTransform.eulerAngles.y:F2} headError={headError:F4}m yawError={yawError:F2}deg");

                if (headError > 0.05f)
                {
                    Vector3 correction = oldSpawnPos - head.position;
                    player.TeleportTo(player.transform.position + correction, player.transform.rotation, keepVelocity: false, center: false);
                    yield return new WaitForFixedUpdate();
                    Debug.Log($"[GorillaOGV2][SPAWN] Applied tracked-head correction {correction:F4}; " +
                              $"finalRoot={player.transform.position:F4} finalHead={head.position:F4} " +
                              $"finalError={Vector3.Distance(head.position, oldSpawnPos):F4}m");
                }
            }
        }
    }

    private void RegisterZoneCallbacks()
    {
        if (zoneCallbacksRegistered) return;
        ZoneManagement.OnZoneChange += OnZoneChange;
        if (ZoneManagement.instance != null)
        {
            ZoneManagement.instance.onZoneChanged += OnZoneChangedAction;
            ZoneManagement.instance.OnSceneLoadsCompleted += OnSceneLoadsCompletedAction;
            zoneCallbacksRegistered = true;
        }
    }

    private void OnDestroy()
    {
        ZoneManagement.OnZoneChange -= OnZoneChange;
        if (ZoneManagement.instance != null)
        {
            ZoneManagement.instance.onZoneChanged -= OnZoneChangedAction;
            ZoneManagement.instance.OnSceneLoadsCompleted -= OnSceneLoadsCompletedAction;
        }
        SceneManager.sceneLoaded -= OnSceneLoaded;
        SceneManager.sceneUnloaded -= OnSceneUnloaded;
    }

    private void OnZoneChange(ZoneData[] _) => RequestApplyZoneState("OnZoneChange");
    private void OnZoneChangedAction() => RequestApplyZoneState("onZoneChanged");
    private void OnSceneLoadsCompletedAction() => RequestApplyZoneState("OnSceneLoadsCompleted");
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => RequestApplyZoneState("sceneLoaded " + scene.name);
    private void OnSceneUnloaded(Scene scene) => RequestApplyZoneState("sceneUnloaded " + scene.name);

    private string zoneRequestReason;
    private void RequestApplyZoneState(string reason)
    {
        zoneStateDirty = true;
        zoneRequestReason = reason;
    }

    // The modern game raises zone events for every trigger touch, even when nothing changed;
    // a full re-scan costs ~250 ms, so only re-apply when the zone/scene set really differs.
    private string lastZoneSignature;
    private string ZoneSignature()
    {
        ZoneManagement zm = ZoneManagement.instance;
        StringBuilder sb = new StringBuilder();
        if (zm != null && zm.activeZones != null) foreach (GTZone z in zm.activeZones) sb.Append(z).Append(',');
        sb.Append('|');
        for (int i = 0; i < SceneManager.sceneCount; i++) { Scene sc = SceneManager.GetSceneAt(i); if (sc.isLoaded) sb.Append(sc.name).Append(','); }
        return sb.ToString();
    }

    private float nextMaintain;

    private void LateUpdate()
    {
        LegacyLighting.MaintainAmbientLighting();
        if (Time.unscaledTime >= nextMaintain)
        {
            nextMaintain = Time.unscaledTime + 5f;
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            if (loaded) SuppressLateSpawns();
            long lateMs = sw.ElapsedMilliseconds;
            foreach (Renderer r in suppressedRenderers) if (r != null && r.enabled) r.enabled = false;
            foreach (Collider c in suppressedColliders) if (c != null && c.enabled) c.enabled = false;
            foreach (GameObject g in hiddenObjects) if (g != null && g.activeSelf) g.SetActive(false);
            foreach (Behaviour b in suppressedBehaviours) if (b != null && b.enabled) b.enabled = false;
            if (Plugin.DebugBuild && sw.ElapsedMilliseconds > 3) Debug.Log($"[GorillaOGV2][PERF] maintenance lateSpawns={lateMs} ms total={sw.ElapsedMilliseconds} ms tracked={suppressedRenderers.Count}/{suppressedColliders.Count}/{hiddenObjects.Count}");
        }
        if (zoneStateDirty)
        {
            zoneStateDirty = false;
            ApplyZoneState();
        }
    }

    private void LoadSynchronous(string path)
    {
        FileInfo fi = new FileInfo(path);
        loadedPackageBytes = fi.Length;
        Debug.Log($"[GorillaOGV2][WORLD] Opening package: {path} ({loadedPackageBytes} bytes)");

        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new BinaryReader(stream);

        byte[] magicBytes = reader.ReadBytes(9);
        loadedPackageVersion = Encoding.ASCII.GetString(magicBytes);
        if (loadedPackageVersion != "GOGWORLD\u0004")
        {
            throw new InvalidDataException($"Invalid historical world package magic: '{loadedPackageVersion}'");
        }

        string textureRoot = Path.Combine(Path.GetDirectoryName(path), "textures");
        materialCountTotal = reader.ReadInt32();
        Debug.Log($"[GorillaOGV2][WORLD] Parsing {materialCountTotal} materials...");
        for (int i = 0; i < materialCountTotal; i++)
        {
            string key = ReadString(reader), name = ReadString(reader), texture = ReadString(reader);
            Vector2 scale = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Vector2 offset = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            Color color = new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            byte filterByte = reader.ReadByte();
            byte wrapByte = reader.ReadByte();
            FilterMode filterMode = (FilterMode)filterByte;
            TextureWrapMode wrapMode = (TextureWrapMode)wrapByte;
            string shaderName = ReadString(reader);
            HistoricalMaterialFlags flags = (HistoricalMaterialFlags)reader.ReadByte();
            float cutoff = reader.ReadSingle();
            int renderQueue = reader.ReadInt32();
            Texture2D image = null;

            if (!string.IsNullOrEmpty(texture))
            {
                string texturePath = Path.Combine(textureRoot, texture);
                if (File.Exists(texturePath))
                {
                    image = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    image.LoadImage(File.ReadAllBytes(texturePath), false);
                    image.name = name + " atlas";
                    image.filterMode = filterMode;
                    image.wrapMode = wrapMode;
                    image.anisoLevel = (filterMode == FilterMode.Point) ? 0 : 1;
                }
            }
            HistoricalMaterialRecord record = new HistoricalMaterialRecord
            {
                Name = name, ShaderName = shaderName, Texture = image, Scale = scale,
                Offset = offset, Color = color, Flags = flags, Cutoff = cutoff,
                RenderQueue = renderQueue
            };
            int floatCount = reader.ReadInt32();
            for (int n = 0; n < floatCount; n++) record.Floats[ReadString(reader)] = reader.ReadSingle();
            int colorCount = reader.ReadInt32();
            for (int n = 0; n < colorCount; n++)
                record.Colors[ReadString(reader)] = new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            int keywordCount = reader.ReadInt32();
            for (int n = 0; n < keywordCount; n++) record.Keywords.Add(ReadString(reader));
            materialRecords[key] = record;
        }

        LegacyLighting.Apply();

        batchCountTotal = reader.ReadInt32();
        Debug.Log($"[GorillaOGV2][WORLD] Parsing {batchCountTotal} render batches...");
        for (int i = 0; i < batchCountTotal; i++)
        {
            string zone = ReadString(reader), materialKey = ReadString(reader);
            int layer = reader.ReadInt32(), oldLightmap = reader.ReadInt32();
            string sources = ReadString(reader);

            Mesh mesh = new Mesh { name = $"August 2023 {zone} batch {i}", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = ReadVector3(reader);
            mesh.normals = ReadVector3(reader);
            mesh.uv = ReadVector2(reader);
            mesh.uv2 = ReadVector2(reader);
            mesh.triangles = ReadIndices(reader);
            mesh.RecalculateBounds();

            bool hasLightmap = oldLightmap != 65535;
            Material mat = CreateHistoricalMaterial(materialKey, hasLightmap);
            bool isSkyBatch = materialRecords.TryGetValue(materialKey, out HistoricalMaterialRecord record) &&
                              (record.Flags & HistoricalMaterialFlags.Sky) != 0;
            if (isSkyBatch)
            {
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 50000f);
            }

            GameObject go = new GameObject($"August 2023 Visual | {zone} | {i}") { layer = layer };
            GameObject zoneRoot = GetZoneRoot(zone);
            go.transform.SetParent(zoneRoot.transform, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = mat;
            if (isSkyBatch)
            {
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }
            else if (oldLightmap != 65535)
            {
                renderer.lightmapIndex = LegacyLighting.LightmapIndex;
                renderer.lightmapScaleOffset = new Vector4(1f, 1f, 0f, 0f);
            }
            renderer.gameObject.AddComponent<HistoricalSourcePaths>().Value = sources;
        }

        Debug.Log($"[GorillaOGV2][RENDER] HistoricalDiagnosticMode={Mathf.Clamp(Plugin.RenderingDiagnosticMode.Value, 1, 4)}; " +
                  $"projectColorSpace={QualitySettings.activeColorSpace}; lightmaps=raw RGBM linear, UV0=albedo, UV2=pre-scaled; " +
                  $"material records={materialRecords.Count}, variants={materials.Count}");

        colliderCountTotal = reader.ReadInt32();
        Debug.Log($"[GorillaOGV2][WORLD] Parsing {colliderCountTotal} colliders...");
        for (int i = 0; i < colliderCountTotal; i++)
        {
            string zone = ReadString(reader), source = ReadString(reader);
            int layer = reader.ReadInt32();
            bool trigger = reader.ReadBoolean();
            byte type = reader.ReadByte();
            Matrix4x4 matrix = ReadMatrix(reader);

            PhysicsMaterial physics = null;
            if (reader.ReadBoolean())
            {
                physics = new PhysicsMaterial("August 2023 physics")
                {
                    dynamicFriction = reader.ReadSingle(),
                    staticFriction = reader.ReadSingle(),
                    bounciness = reader.ReadSingle(),
                    frictionCombine = (PhysicsMaterialCombine)reader.ReadInt32(),
                    bounceCombine = (PhysicsMaterialCombine)reader.ReadInt32()
                };
            }

            byte interactionFlags = reader.ReadByte();
            int surfaceIndex = 0; float surfaceVelocity = 1f, surfaceMaxVelocity = 1f; bool surfaceTap = false;
            bool snapX = false, snapY = false, snapZ = false; float snapDistance = 0.05f;
            if ((interactionFlags & 1) != 0)
            {
                surfaceIndex = reader.ReadInt32();
                surfaceVelocity = reader.ReadSingle();
                surfaceMaxVelocity = reader.ReadSingle();
                surfaceTap = reader.ReadBoolean();
            }
            if ((interactionFlags & 2) != 0)
            {
                snapX = reader.ReadUInt32() != 0;
                snapY = reader.ReadUInt32() != 0;
                snapZ = reader.ReadUInt32() != 0;
                snapDistance = reader.ReadSingle();
            }
            string matName = (interactionFlags & 4) != 0 ? ReadString(reader) : null;

            GameObject go = new GameObject($"August 2023 Collider | {source}") { layer = layer };
            GameObject zoneRoot = GetZoneRoot(zone);
            go.transform.SetParent(zoneRoot.transform, false);
            SetMatrix(go.transform, matrix);
            Collider collider;
            if (type == 0)
            {
                bool convex = reader.ReadBoolean();
                Mesh mesh = new Mesh { name = source + " collision", indexFormat = IndexFormat.UInt32 };
                mesh.vertices = ReadVector3(reader);
                mesh.triangles = ReadIndices(reader);
                mesh.RecalculateBounds();
                MeshCollider c = go.AddComponent<MeshCollider>();
                c.sharedMesh = mesh;
                if (convex && mesh.vertexCount <= 255) c.convex = true;
                collider = c;
            }
            else if (type == 1)
            {
                BoxCollider c = go.AddComponent<BoxCollider>();
                c.center = ReadVector3Value(reader);
                c.size = ReadVector3Value(reader);
                collider = c;
            }
            else if (type == 2)
            {
                SphereCollider c = go.AddComponent<SphereCollider>();
                c.center = ReadVector3Value(reader);
                c.radius = reader.ReadSingle();
                collider = c;
            }
            else
            {
                CapsuleCollider c = go.AddComponent<CapsuleCollider>();
                c.center = ReadVector3Value(reader);
                c.radius = reader.ReadSingle();
                c.height = reader.ReadSingle();
                c.direction = reader.ReadInt32();
                collider = c;
            }

            if (collider is MeshCollider mc && trigger)
            {
                if (mc.sharedMesh != null && mc.sharedMesh.vertexCount <= 255)
                {
                    mc.convex = true;
                    mc.isTrigger = true;
                }
                else
                {
                    mc.isTrigger = false;
                }
            }
            else
            {
                collider.isTrigger = trigger;
            }
            collider.sharedMaterial = physics;
            if ((interactionFlags & 1) != 0)
            {
                GorillaSurfaceOverride surface = go.AddComponent<GorillaSurfaceOverride>();
                surface.overrideIndex = surfaceIndex;
                surface.extraVelMultiplier = surfaceVelocity;
                surface.extraVelMaxMultiplier = surfaceMaxVelocity;
                surface.sendOnTapEvent = surfaceTap;
            }
            else if (matName != null && !trigger) pendingSurfaceNames.Add((go, matName));
            if ((interactionFlags & 2) != 0)
            {
                var climb = go.AddComponent<GorillaLocomotion.Climbing.GorillaClimbable>();
                climb.snapX = snapX;
                climb.snapY = snapY;
                climb.snapZ = snapZ;
                climb.maxDistanceSnap = snapDistance;
                climb.colliderCache = collider;
            }
        }
    }

    private Material CreateHistoricalMaterial(string key, bool hasLightmap)
    {
        int mode = Mathf.Clamp(Plugin.RenderingDiagnosticMode.Value, 1, 4);
        string cacheKey = key + "|" + hasLightmap + "|" + mode;
        if (materials.TryGetValue(cacheKey, out Material cached)) return cached;
        if (!materialRecords.TryGetValue(key, out HistoricalMaterialRecord source)) return null;

        bool sky = (source.Flags & HistoricalMaterialFlags.Sky) != 0;
        bool cutout = (source.Flags & HistoricalMaterialFlags.Cutout) != 0;
        bool transparent = (source.Flags & HistoricalMaterialFlags.Transparent) != 0;
        bool originallyUnlit = (source.Flags & HistoricalMaterialFlags.Unlit) != 0;
        bool baked = hasLightmap && !sky && !transparent && !originallyUnlit;

        Shader shader;
        if (mode == 1 || sky || originallyUnlit)
            shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
        else if (mode == 2 || mode == 3 || (mode == 4 && baked))
            shader = Shader.Find("GorillaTag/UberShader") ?? Shader.Find("Gorilla/CutoffLerpLightmap");
        else
            shader = Shader.Find("Universal Render Pipeline/Simple Lit") ?? Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");

        Material material = new Material(shader) { name = $"August 2023 [{mode}] | {source.Name}" };
        Texture texture = source.Texture;
        Color tint = source.Color;
        if (mode == 2 && baked)
        {
            texture = Texture2D.whiteTexture;
            tint = Color.white;
        }
        LegacyMaterial.Bind(material, texture, tint);
        if (mode == 4) ApplySerializedMaterialProperties(material, source);
        material.mainTextureScale = source.Scale;
        material.mainTextureOffset = source.Offset;
        if (material.HasProperty("_BaseMap_ST")) material.SetVector("_BaseMap_ST", new Vector4(source.Scale.x, source.Scale.y, source.Offset.x, source.Offset.y));
        if (material.HasProperty("_MainTex_ST")) material.SetVector("_MainTex_ST", new Vector4(source.Scale.x, source.Scale.y, source.Offset.x, source.Offset.y));
        if (baked && mode != 1)
        {
            if (material.HasProperty("_UseDayNightLightmap")) material.SetFloat("_UseDayNightLightmap", 1f);
            if (material.HasProperty("_DayNightLightmapArray_ST")) material.SetVector("_DayNightLightmapArray_ST", new Vector4(1f, 1f, 0f, 0f));
            if (material.HasProperty("_DayNightLightmapArray_AtlasSlice")) material.SetFloat("_DayNightLightmapArray_AtlasSlice", 0f);
            if (material.HasProperty("_USE_TEX_ARRAY_ATLAS")) material.SetFloat("_USE_TEX_ARRAY_ATLAS", 0f);
            if (material.HasProperty("_UseVertexColor")) material.SetFloat("_UseVertexColor", 0f);
            // UberShader controls these branches with local keywords. Property values
            // alone leave its flat-color variant active, which discards the atlas.
            material.EnableKeyword("_USE_TEXTURE");
            material.EnableKeyword("_UV_SOURCE__UV0");
            material.EnableKeyword("_USE_DAY_NIGHT_LIGHTMAP");
            material.DisableKeyword("_USE_TEX_ARRAY_ATLAS");
        }

        if (sky)
        {
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            material.renderQueue = 1000;
            LegacyLighting.SkyMaterials.Add(material);
        }
        else if (transparent)
        {
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_Blend")) material.SetFloat("_Blend", 0f);
            material.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.renderQueue = source.RenderQueue >= 3000 ? source.RenderQueue : (int)RenderQueue.Transparent;
        }
        else if (cutout)
        {
            if (material.HasProperty("_AlphaClip")) material.SetFloat("_AlphaClip", 1f);
            if (material.HasProperty("_Cutoff")) material.SetFloat("_Cutoff", source.Cutoff);
            material.EnableKeyword("_ALPHATEST_ON");
            material.renderQueue = source.RenderQueue >= 2450 ? source.RenderQueue : (int)RenderQueue.AlphaTest;
        }
        else if (baked && material.HasProperty("_Cutoff"))
        {
            material.SetFloat("_Cutoff", 0f);
        }

        if (baked && mode != 1)
        {
            material.EnableKeyword("LIGHTMAP_ON");
            material.DisableKeyword("DIRLIGHTMAP_COMBINED");
        }
        materials[cacheKey] = material;
        return material;
    }

    private static void ApplySerializedMaterialProperties(Material material, HistoricalMaterialRecord source)
    {
        foreach (var pair in source.Floats)
        {
            if (material.HasProperty(pair.Key)) material.SetFloat(pair.Key, pair.Value);
        }
        foreach (var pair in source.Colors)
        {
            if (material.HasProperty(pair.Key)) material.SetColor(pair.Key, pair.Value);
        }

        // Unity's Standard shader called this value Glossiness.  URP and the current
        // Gorilla Uber shader call the same perceptual parameter Smoothness.
        if (source.Floats.TryGetValue("_Glossiness", out float glossiness) && material.HasProperty("_Smoothness"))
            material.SetFloat("_Smoothness", glossiness);
        if (source.Colors.TryGetValue("_SpecColor", out Color specular) && material.HasProperty("_SpecularColor"))
            material.SetColor("_SpecularColor", specular);

        bool specularOff = source.Keywords.Contains("_SPECULARHIGHLIGHTS_OFF") ||
                           (source.Floats.TryGetValue("_SpecularHighlights", out float highlights) && highlights < 0.5f);
        bool reflectionsOff = source.Keywords.Contains("_GLOSSYREFLECTIONS_OFF") ||
                              (source.Floats.TryGetValue("_GlossyReflections", out float reflections) && reflections < 0.5f);
        if (material.HasProperty("_SpecularHighlights")) material.SetFloat("_SpecularHighlights", specularOff ? 0f : 1f);
        if (material.HasProperty("_EnvironmentReflections")) material.SetFloat("_EnvironmentReflections", reflectionsOff ? 0f : 1f);
        if (material.HasProperty("_UseSpecHighlight")) material.SetFloat("_UseSpecHighlight", specularOff ? 0f : 1f);
        if (specularOff)
        {
            material.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            material.DisableKeyword("_SPECULAR_HIGHLIGHT");
        }
        else
        {
            material.DisableKeyword("_SPECULARHIGHLIGHTS_OFF");
            material.EnableKeyword("_SPECULAR_HIGHLIGHT");
        }
        if (reflectionsOff) material.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
        else material.DisableKeyword("_ENVIRONMENTREFLECTIONS_OFF");

        if (source.Colors.TryGetValue("_EmissionColor", out Color emission))
        {
            bool emits = emission.maxColorComponent > 0.0001f;
            if (material.HasProperty("_EmissionColor")) material.SetColor("_EmissionColor", emission);
            if (emits) material.EnableKeyword("_EMISSION");
            else material.DisableKeyword("_EMISSION");
        }
    }

    internal GameObject ZoneRoot(string zone) => GetZoneRoot(zone);

    private GameObject GetZoneRoot(string zone)
    {
        if (zoneRoots.TryGetValue(zone, out GameObject root) && root != null)
        {
            return root;
        }
        root = new GameObject("GorillaOGV2 August 2023 Zone | " + zone);
        root.SetActive(false);
        DontDestroyOnLoad(root);
        zoneRoots[zone] = root;
        return root;
    }

    // 2023 tap/slide sounds: package collider objects have no Renderer, so GTPlayer.GetSlidePercentage
    // cannot read a material name; give them the 2023 material's materialData index instead.
    private readonly List<(GameObject go, string mat)> pendingSurfaceNames = new List<(GameObject, string)>();
    private void ResolveSurfaceNames()
    {
        GorillaLocomotion.GTPlayer player = GorillaLocomotion.GTPlayer.Instance;
        if (player == null || player.materialData == null || player.materialData.Count == 0) return;
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < player.materialData.Count; i++) if (player.materialData[i].matName != null && !index.ContainsKey(player.materialData[i].matName)) index[player.materialData[i].matName] = i;
        int applied = 0; var missing = new HashSet<string>();
        foreach (var (go, mat) in pendingSurfaceNames)
        {
            if (go == null) continue;
            if (!index.TryGetValue(mat, out int i)) { missing.Add(mat); continue; }
            if (i == 0) continue; // default material: identical to the no-override path
            GorillaSurfaceOverride surface = go.AddComponent<GorillaSurfaceOverride>();
            surface.overrideIndex = i;
            surface.slidePercentageOverride = -1f;
            applied++;
        }
        Debug.Log($"[GorillaOGV2][SURFACE] applied 2023 material sounds to {applied}/{pendingSurfaceNames.Count} colliders; unmatched materials: {string.Join(", ", missing)}");
        pendingSurfaceNames.Clear();
    }

    private void Update()
    {
        if (!loaded) return;
        if (!zoneCallbacksRegistered) RegisterZoneCallbacks();
        if (pendingSurfaceNames.Count > 0) ResolveSurfaceNames();
        LegacyLighting.UpdateFromDayNight();
        if (Plugin.DebugBuild) PerfSample();
    }

    // Debug builds only: names every frame over 50 ms with the GC generation-0 delta, and
    // summarises fps / worst frame / collections every 10 s so hitches are provable from Player.log.
    private float perfWindowStart, perfWorst, nextTouchProbe; private int perfFrames, perfGcAtWindow, perfLastGc;
    private readonly HashSet<string> touchedLogged = new HashSet<string>();
    private void PerfSample()
    {
        float dt = Time.unscaledDeltaTime; int gc = GC.CollectionCount(0);
        perfFrames++; if (dt > perfWorst) perfWorst = dt;
        if (dt > 0.05f) Debug.Log($"[GorillaOGV2][PERF] hitch {dt * 1000f:F0} ms at t={Time.unscaledTime:F1} gc0Delta={gc - perfLastGc}");
        perfLastGc = gc;
        if (Time.unscaledTime >= nextTouchProbe)
        {
            // Names every solid collider within 1.5 m of the head so "the leaves have colliders" style
            // reports can be traced to the exact object (2023 package or modern) from Player.log.
            nextTouchProbe = Time.unscaledTime + 1f;
            Transform head = GorillaTagger.Instance != null && GorillaTagger.Instance.headCollider != null ? GorillaTagger.Instance.headCollider.transform : null;
            if (head != null)
            {
                foreach (Collider c in Physics.OverlapSphere(head.position, 1.5f, ~0, QueryTriggerInteraction.Ignore))
                {
                    if (c == null || c.GetComponentInParent<VRRig>() != null) continue;
                    string n = c.name.ToLowerInvariant();
                    MeshCollider mc = c as MeshCollider;
                    string meshName = mc != null && mc.sharedMesh != null ? mc.sharedMesh.name.ToLowerInvariant() : "";
                    if (!(n.Contains("leaf") || n.Contains("leaves") || meshName.Contains("leaf") || meshName.Contains("leaves"))) continue;
                    string key = HierarchyPath(c.transform, null);
                    if (touchedLogged.Add(key)) Debug.Log($"[GorillaOGV2][TOUCH] leaf collider near player: {key} <{c.GetType().Name}> mesh='{meshName}' layer={c.gameObject.layer}");
                }
            }
        }
        if (Time.unscaledTime - perfWindowStart >= 10f)
        {
            if (perfWindowStart > 0f)
                Debug.Log($"[GorillaOGV2][PERF] window t={Time.unscaledTime:F0} avgFps={perfFrames / (Time.unscaledTime - perfWindowStart):F1} worst={perfWorst * 1000f:F0} ms gc0={gc - perfGcAtWindow} totalMem={GC.GetTotalMemory(false) / 1048576} MB");
            perfWindowStart = Time.unscaledTime; perfFrames = 0; perfWorst = 0f; perfGcAtWindow = gc;
        }
    }

    internal Material GetHistoricalMaterial(string materialKey, bool hasLightmap)
    {
        return CreateHistoricalMaterial(materialKey, hasLightmap);
    }

    private static Transform FindModernTreeRoom()
    {
        GorillaComputerTerminal[] terminals = FindObjectsByType<GorillaComputerTerminal>(FindObjectsSortMode.None);
        foreach (GorillaComputerTerminal terminal in terminals)
        {
            Transform cursor = terminal.transform;
            while (cursor != null)
            {
                if (cursor.name == "TreeRoom") return cursor;
                cursor = cursor.parent;
            }
        }
        GameObject localObjects = GameObject.Find("Environment Objects/LocalObjects_Prefab") ?? GameObject.Find("LocalObjects_Prefab");
        return localObjects != null ? localObjects.transform.Find("TreeRoom") : null;
    }

    private void ApplyZoneState(bool force = false)
    {
        if (!loaded) return;
        string signature = ZoneSignature();
        if (!force && signature == lastZoneSignature)
        {
            if (Plugin.DebugBuild) Debug.Log($"[GorillaOGV2][ZONE] unchanged ({zoneRequestReason}) -> skipped");
            return;
        }
        lastZoneSignature = signature;
        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        HashSet<string> active = ActiveHistoricalRoots();
        foreach (var pair in zoneRoots)
        {
            if (pair.Value == null) continue;
            bool shouldBeActive = active.Contains(pair.Key);
            if (pair.Value.activeSelf != shouldBeActive)
            {
                pair.Value.SetActive(shouldBeActive);
            }
        }
        SuppressModernVisuals();
        HistoricalStumpEnvironment.Instance?.ReclaimAdopted();
        HistoricalWardrobe.NotifyChanged();
        HistoricalScoreboards.Restyle();
        long suppressMs = sw.ElapsedMilliseconds;
        LogZoneDiagnostics(active);
        if (Plugin.DebugBuild) Debug.Log($"[GorillaOGV2][PERF] ApplyZoneState ({zoneRequestReason}) suppress={suppressMs} ms total={sw.ElapsedMilliseconds} ms");
        if (followUp != null) StopCoroutine(followUp);
        followUp = StartCoroutine(SuppressFollowUp());
    }

    private Coroutine followUp;

    // ponytail: fixed re-scan schedule after a zone change catches late spawns; hook the
    // spawners instead if something still leaks after 12 s.
    private IEnumerator SuppressFollowUp()
    {
        float last = 0f;
        foreach (float at in new[] { 1f, 3f, 6f, 12f })
        {
            yield return new WaitForSeconds(at - last);
            last = at;
            SuppressModernVisuals();
            HistoricalScoreboards.Restyle();
        }
        LogZoneDiagnostics(ActiveHistoricalRoots());
    }

    // Names every enabled modern MeshRenderer group left after suppression so leaks are
    // located from the log rather than guessed at.
    private void LogZoneDiagnostics(HashSet<string> activeRoots)
    {
        if (!Plugin.DebugBuild) return;
        ZoneManagement zm = ZoneManagement.instance;
        string zones = zm == null ? "none" : string.Join(",", zm.activeZones);
        List<string> scenes = new List<string>();
        for (int i = 0; i < SceneManager.sceneCount; i++) scenes.Add(SceneManager.GetSceneAt(i).name);
        List<string> ours = new List<string>();
        foreach (var pair in zoneRoots) ours.Add(pair.Key + (pair.Value != null && pair.Value.activeSelf ? "=on" : "=off"));
        Debug.Log($"[GorillaOGV2][ZONE] modern={zones} scenes={string.Join(",", scenes)} roots2023={string.Join(",", ours)}");

        Dictionary<string, List<string>> groups = new Dictionary<string, List<string>>();
        foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (r == null || !r.enabled || !r.gameObject.activeInHierarchy || r.GetType().Name is "ParticleSystemRenderer" or "TrailRenderer" or "LineRenderer") continue;
            Transform root = r.transform.root;
            if (root.name.StartsWith("GorillaOGV2") || IsPlayerHierarchy(r.transform) || r.GetComponentInParent<VRRig>() != null) continue;
            Transform top = r.transform;
            while (top.parent != null && top.parent != root) top = top.parent;
            string key = r.gameObject.scene.name + " | " + root.name + " | " + top.name;
            if (!groups.TryGetValue(key, out List<string> list)) groups[key] = list = new List<string>();
            list.Add(HierarchyPath(r.transform, root) + " <" + r.GetType().Name + ">");
        }
        List<KeyValuePair<string, List<string>>> ordered = new List<KeyValuePair<string, List<string>>>(groups);
        ordered.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
        StringBuilder leaks = new StringBuilder();
        leaks.AppendLine($"=== {Time.realtimeSinceStartup:F1}s modern={zones}");
        foreach (var g in ordered)
        {
            Debug.Log($"[GorillaOGV2][ZONE] enabled modern renderers {g.Value.Count,5}  {g.Key}  e.g. {string.Join(" ; ", g.Value.GetRange(0, Math.Min(3, g.Value.Count)))}");
            foreach (string path in g.Value) leaks.AppendLine(g.Key + "\t" + path);
        }
        // Modern solid colliders the player can still touch (leaf/branch colliders etc.).
        Dictionary<string, int> colGroups = new Dictionary<string, int>();
        List<string> colExamples = new List<string>();
        foreach (Collider c in FindObjectsByType<Collider>(FindObjectsSortMode.None))
        {
            if (c == null || !c.enabled || c.isTrigger || !c.gameObject.activeInHierarchy) continue;
            Transform root = c.transform.root;
            if (root.name.StartsWith("GorillaOGV2") || IsOurs(c.transform) || IsPlayerHierarchy(c.transform) || c.GetComponentInParent<VRRig>() != null) continue;
            // all layers: the player body collides with more than Default/GorillaBoundary
            Transform top = c.transform;
            while (top.parent != null && top.parent != root) top = top.parent;
            string key = c.gameObject.scene.name + " | " + root.name + " | " + top.name;
            colGroups[key] = colGroups.TryGetValue(key, out int n) ? n + 1 : 1;
            string path = HierarchyPath(c.transform, root);
            if (colExamples.Count < 400) colExamples.Add(key + "\t" + path + " <" + c.GetType().Name + "> layer=" + c.gameObject.layer + " why=" + KeepReason(c.transform));
        }
        foreach (var g in colGroups) Debug.Log($"[GorillaOGV2][ZONE] enabled modern colliders {g.Value,5}  {g.Key}");
        // Every visible piece of text that is not ours, with its content: the fastest way to name a stray 2026 sign.
        leaks.AppendLine("--- texts");
        foreach (TMPro.TMP_Text t in FindObjectsByType<TMPro.TMP_Text>(FindObjectsSortMode.None))
        {
            Renderer tr = t.GetComponent<Renderer>();
            if (t == null || !t.gameObject.activeInHierarchy || !t.enabled || (tr != null && !tr.enabled) || IsOurs(t.transform) || t.GetComponentInParent<VRRig>() != null) continue;
            if (string.IsNullOrWhiteSpace(t.text)) continue;
            leaks.AppendLine("TMP\t" + HierarchyPath(t.transform, null) + "\t" + t.text.Replace('\n', ' ').Substring(0, Math.Min(60, t.text.Length)));
        }
        foreach (UnityEngine.UI.Text t in FindObjectsByType<UnityEngine.UI.Text>(FindObjectsSortMode.None))
        {
            Canvas cv = t.GetComponentInParent<Canvas>();
            if (t == null || !t.gameObject.activeInHierarchy || !t.enabled || cv == null || !cv.enabled || IsOurs(t.transform) || t.GetComponentInParent<VRRig>() != null) continue;
            if (string.IsNullOrWhiteSpace(t.text)) continue;
            leaks.AppendLine("UIText\t" + HierarchyPath(t.transform, null) + "\t" + t.text.Replace('\n', ' ').Substring(0, Math.Min(60, t.text.Length)));
        }
        foreach (TextMesh t in FindObjectsByType<TextMesh>(FindObjectsSortMode.None))
        {
            Renderer tr = t.GetComponent<Renderer>();
            if (t == null || !t.gameObject.activeInHierarchy || tr == null || !tr.enabled || IsOurs(t.transform) || string.IsNullOrWhiteSpace(t.text)) continue;
            leaks.AppendLine("TextMesh\t" + HierarchyPath(t.transform, null) + "\t" + t.text.Replace('\n', ' ').Substring(0, Math.Min(60, t.text.Length)));
        }
        leaks.AppendLine("--- colliders");
        foreach (string e in colExamples) leaks.AppendLine(e);
        try { File.AppendAllText(Path.Combine(Paths.PluginPath, "GorillaOGV2", "zone-leaks.log"), leaks.ToString()); } catch { }
    }

    // Serialized August 2023 ZoneManagement_Prefab (level0 path 252985): zone -> LocalObjects roots.
    private static readonly Dictionary<GTZone, string[]> Roots2023 = new Dictionary<GTZone, string[]>
    {
        { GTZone.forest, new[] { "forest", "treeRoom", "forestToCave", "forestToCanyon", "forestToBeach", "standardSky", "cityToSkyJungle" } },
        { GTZone.city, new[] { "city", "treeRoom", "cityToBasement", "cityToMountain" } },
        { GTZone.basement, new[] { "basement", "cityToBasement" } },
        { GTZone.canyon, new[] { "canyon", "treeRoom", "forestToCanyon", "standardSky" } },
        { GTZone.beach, new[] { "beach", "forestToBeach" } },
        { GTZone.mountain, new[] { "mountain", "cityToMountain", "standardSky" } },
        { GTZone.skyJungle, new[] { "skyJungle", "treeRoom", "cityToSkyJungle" } },
        { GTZone.cave, new[] { "cave", "forestToCave", "standardSky", "treeRoom" } },
        { GTZone.cityWithSkyJungle, new[] { "city", "treeRoom", "cityToBasement", "cityToMountain", "cityToSkyJungle" } },
    };

    // Modern-only zone ids folded onto their 2023 entries.
    private static IEnumerable<GTZone> Equivalent2023Zones(GTZone zone)
    {
        switch (zone)
        {
            case GTZone.forestWithCity: yield return GTZone.forest; yield return GTZone.city; break;
            case GTZone.cityNoBuildings: yield return GTZone.city; break;
            default: yield return zone; break;
        }
    }

    private static HashSet<string> ActiveHistoricalRoots()
    {
        HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ZoneManagement zm = ZoneManagement.instance;
        if (zm == null) return roots;
        foreach (GTZone active in zm.activeZones)
            foreach (GTZone zone in Equivalent2023Zones(active))
                if (Roots2023.TryGetValue(zone, out string[] names)) roots.UnionWith(names);
        return roots;
    }

    private static bool IsZoneOrCompositeActive(GTZone checkZone)
    {
        ZoneManagement zm = ZoneManagement.instance;
        if (zm == null) return false;
        foreach (GTZone active in zm.activeZones)
            foreach (GTZone zone in Equivalent2023Zones(active))
                if (zone == checkZone) return true;
        return false;
    }

    private void SuppressModernVisuals()
    {
        ZoneManagement zm = ZoneManagement.instance;
        if (zm == null) return;

        FieldInfo field = typeof(ZoneManagement).GetField("zones", BindingFlags.Instance | BindingFlags.NonPublic);
        ZoneData[] zones = field?.GetValue(zm) as ZoneData[];
        if (zones != null)
        {
            foreach (ZoneData zone in zones)
            {
                if (zone == null || !HasHistoricalEquivalent(zone.zone)) continue;

                // 1. Process rootGameObjects serialized in ZoneData
                if (zone.rootGameObjects != null)
                {
                    foreach (GameObject root in zone.rootGameObjects)
                    {
                        if (root != null) SuppressHierarchy(root, zone.zone);
                    }
                }

                // 2. Process additively loaded scenes matching this zone
                if (!string.IsNullOrEmpty(zone.sceneName))
                {
                    Scene scene = SceneManager.GetSceneByName(zone.sceneName);
                    Debug.Log($"[GorillaOGV2][ZONE] scene check zone={zone.zone} scene='{zone.sceneName}' valid={scene.IsValid()} loaded={scene.isLoaded} roots={(scene.IsValid() ? scene.rootCount : -1)}");
                    if (scene.isLoaded && scene.name != "GorillaTag")
                    {
                        foreach (GameObject root in scene.GetRootGameObjects())
                        {
                            if (root != null) SuppressHierarchy(root, zone.zone, strict: true);
                        }
                    }
                }
            }
        }

        // 2b. Modern-only City furniture that lives outside the City scene.
        if (IsZoneOrCompositeActive(GTZone.city))
        {
            foreach (string path in new[] { "Networking Scripts/BundleManager/CityBundles", "Environment Objects/05Maze_PersistentObjects/GhostReactorElevatorManager" })
            {
                GameObject extra = GameObject.Find(path);
                if (extra != null) SuppressHierarchy(extra, GTZone.city, strict: true);
            }
        }

        // 3. Every LocalObjects_Prefab child is 2023-replaced geometry (world UI is kept by IsWorldUiNode).
        GameObject localObjects = GameObject.Find("Environment Objects/LocalObjects_Prefab") ?? GameObject.Find("LocalObjects_Prefab");
        if (localObjects != null)
        {
            Transform lt = localObjects.transform;
            for (int i = 0; i < lt.childCount; i++) SuppressHierarchy(lt.GetChild(i).gameObject, GTZone.forest);
        }
        SuppressLateSpawns(full: true);
    }

    // Modern objects that spawn or re-enable on their own schedule (critters, store bundle stands,
    // the 2026 satellite-wardrobe UI). Cheap enough to run from the 5 s maintenance tick.
    private static readonly string[] CritterNames = { "PlumpBeetle", "CaveBat", "Cave Bat", "Floating Bug", "BeaconTheBug", "Critters", "Critter" };
    private static readonly string[] WeatherNames = { "Weather", "weather", "rain", "Rain", "snow", "Snow" };
    private int lastBundleChildCount = -1;
    private void SuppressLateSpawns(bool full = false)
    {
        foreach (SkinnedMeshRenderer r in FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None))
        {
            if (!r.enabled || IsOurs(r.transform) || IsPlayerHierarchy(r.transform) || r.GetComponentInParent<VRRig>() != null) continue;
            if (!NameChainContains(r.transform, CritterNames)) continue;
            r.enabled = false; suppressedRenderers.Add(r);
        }
        if (full)
        {
            // Static hierarchies: walked once per zone change, the maintenance tick only re-disables tracked renderers.
            GameObject critters = GameObject.Find("Environment Objects/05Maze_PersistentObjects/CrittersManager");
            if (critters != null) SuppressHierarchy(critters, GTZone.forest, strict: true);
            GameObject satUi = GameObject.Find("Environment Objects/LocalObjects_Prefab/TreeRoom/TreeRoomInteractables/UI/SatelliteWardrobe/UI");
            if (satUi != null) SuppressHierarchy(satUi, GTZone.forest, strict: true);
            // 2026 GTv screens and GhostReactor elevator keep playing (and sounding) behind the 2023 city: no 2023 counterpart.
            foreach (UnityEngine.Video.VideoPlayer vp in FindObjectsByType<UnityEngine.Video.VideoPlayer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (IsOurs(vp.transform) || vp.GetComponentInParent<VRRig>() != null) continue;
                if (vp.enabled) { vp.Stop(); vp.enabled = false; suppressedBehaviours.Add(vp); }
                foreach (AudioSource a in vp.GetComponentsInChildren<AudioSource>(true)) if (a.enabled) { a.Stop(); a.enabled = false; suppressedBehaviours.Add(a); }
            }
            foreach (string path in new[] { "Environment Objects/05Maze_PersistentObjects/GhostReactorElevatorManager", "GhostReactorElevatorManager", "CityElevator" })
            {
                GameObject obj = GameObject.Find(path);
                if (obj != null)
                {
                    foreach (AudioSource a in obj.GetComponentsInChildren<AudioSource>(true))
                    {
                        if (a.enabled) { a.Stop(); a.enabled = false; suppressedBehaviours.Add(a); }
                    }
                    foreach (UnityEngine.Video.VideoPlayer vp in obj.GetComponentsInChildren<UnityEngine.Video.VideoPlayer>(true))
                    {
                        if (vp.enabled) { vp.Stop(); vp.enabled = false; suppressedBehaviours.Add(vp); }
                    }
                }
            }
            lastBundleChildCount = -1;
        }
        // ReparentOnAwake dumps modern map UI (city lobby selector, MOTD copies…) directly under TreeRoom
        // whenever a map loads; everything there except TreeRoomInteractables is 2026 text.
        Transform treeRoom = FindModernTreeRoom();
        if (treeRoom != null)
        {
            for (int i = 0; i < treeRoom.childCount; i++)
            {
                Transform child = treeRoom.GetChild(i);
                if (child.name == "TreeRoomInteractables" || IsOurs(child)) continue;
                foreach (TMPro.TMP_Text t in child.GetComponentsInChildren<TMPro.TMP_Text>(true))
                {
                    if (t.enabled) { t.enabled = false; suppressedBehaviours.Add(t); }
                    Renderer tr = t.GetComponent<Renderer>();
                    if (tr != null && tr.enabled) { tr.enabled = false; suppressedRenderers.Add(tr); }
                }
            }
        }
        if (IsZoneOrCompositeActive(GTZone.city))
        {
            // Store bundle stands are instantiated asynchronously; rescan only when something new arrived.
            GameObject bundles = GameObject.Find("Networking Scripts/BundleManager/CityBundles");
            int count = bundles != null ? bundles.GetComponentsInChildren<Renderer>(true).Length : -1;
            if (bundles != null && count != lastBundleChildCount)
            {
                lastBundleChildCount = count;
                SuppressHierarchy(bundles, GTZone.city, strict: true);
            }
        }
    }

    private static bool NameChainContains(Transform t, string[] needles)
    {
        for (Transform c = t; c != null; c = c.parent)
            foreach (string n in needles) if (c.name.IndexOf(n, StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    private static bool IsPlayerHierarchy(Transform t)
    {
        if (t == null) return false;
        string n = t.name.ToLowerInvariant();
        if (n.Contains("player") || n.Contains("rig") || n.Contains("gorilla") || n.Contains("photon") || n.Contains("network"))
        {
            if (t.GetComponent<VRRig>() != null || 
                t.GetComponent<GorillaLocomotion.GTPlayer>() != null || 
                t.GetComponentInParent<VRRig>() != null || 
                t.GetComponentInParent<GorillaLocomotion.GTPlayer>() != null)
            {
                return true;
            }
        }
        if (t.GetComponent<VRRig>() != null || 
            t.GetComponent<GorillaLocomotion.GTPlayer>() != null || 
            t.GetComponent<GorillaBodyRenderer>() != null ||
            t.GetComponent<TMPro.TMP_Text>() != null)
        {
            return true;
        }
        return false;
    }

    // Roots are rescanned on every apply (no instance-id cache): modern maps spawn
    // renderers after load (MeshCombinerParent, store displays), so a one-shot scan leaks.
    // strict = additively loaded map scene (City, Canyon2, ...): everything but text, canvases
    // and rope stand-ins is 2023-replaced geometry.
    private void SuppressHierarchy(GameObject root, GTZone zone, bool strict = false)
    {
        if (root == null || IsPlayerHierarchy(root.transform)) return;
        int before = suppressedRenderers.Count;
        SuppressHierarchyRecursive(root.transform, root.transform, zone, strict);
        if (suppressedRenderers.Count != before || (Plugin.DebugBuild && strict)) Debug.Log($"[GorillaOGV2][ZONE] scan strict={strict} ui={IsWorldUiNode(root.transform)} special={HasSpecialGameplayBehaviourNode(root.transform)} root='{root.name}' active={root.activeInHierarchy} keep={IsStrictKeepNode(root.transform)} comps={string.Join("/", Array.ConvertAll(root.GetComponents<Component>(), c => c == null ? "null" : c.GetType().Name))} newlySuppressed={suppressedRenderers.Count - before}");
    }

    private readonly HashSet<GameObject> hiddenObjects = new HashSet<GameObject>();
    private readonly HashSet<Behaviour> suppressedBehaviours = new HashSet<Behaviour>();

    // Modern objects the stump/city code adopts (terminal texts, key caps, MOTD/rules TMPs) were
    // often suppressed by the initial scan while still under TreeRoom; forget and re-enable them.
    internal void Reclaim(Transform root)
    {
        if (root == null) return;
        int n = 0;
        foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true)) if (suppressedRenderers.Remove(r)) { r.enabled = true; n++; }
        foreach (Collider c in root.GetComponentsInChildren<Collider>(true)) if (suppressedColliders.Remove(c)) { c.enabled = true; n++; }
        foreach (Behaviour b in root.GetComponentsInChildren<Behaviour>(true)) if (suppressedBehaviours.Remove(b)) { b.enabled = true; n++; }
        foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) if (hiddenObjects.Remove(t.gameObject)) { t.gameObject.SetActive(true); n++; }
        if (n > 0) Debug.Log($"[GorillaOGV2][ZONE] reclaimed {n} suppressed components under '{root.name}'");
    }

    private void SuppressHierarchyRecursive(Transform current, Transform root, GTZone zone, bool strict)
    {
        if (current == null || IsOurs(current)) return;
        // Modern map-scene text (store signs, FLAMETHROWER, friend booth, VIM …) is 2026 content;
        // the 2023 labels are rebuilt by HistoricalCityUi.
        if (strict && current.GetComponent<TMPro.TMP_Text>() != null && current.GetComponentInParent<VRRig>() == null && current.GetComponentInParent<GorillaScoreBoard>() == null)
        {
            Renderer tr = current.GetComponent<Renderer>();
            if (tr != null && tr.enabled) { tr.enabled = false; suppressedRenderers.Add(tr); }
            TMPro.TMP_Text tmpText = current.GetComponent<TMPro.TMP_Text>();
            if (tmpText.enabled) { tmpText.enabled = false; suppressedBehaviours.Add(tmpText); } // a later SetText would re-enable the mesh renderer
            return;
        }
        // Legacy UI (Canvas + Text/Image on CanvasRenderers) is invisible to Renderer scans: the 2026
        // "ONLY FOR SUBSCRIBERS" / VIM signs in the City are canvases. Scoreboards keep theirs.
        if (strict && current.GetComponentInParent<VRRig>() == null && current.GetComponentInParent<GorillaScoreBoard>() == null)
        {
            Canvas canvas = current.GetComponent<Canvas>();
            if (canvas != null && canvas.enabled) { canvas.enabled = false; suppressedBehaviours.Add(canvas); }
        }
        if (IsPlayerHierarchy(current)) return;
        if (current.name.StartsWith("BoundaryStoneSet"))
        {
            if (current.gameObject.activeSelf) current.gameObject.SetActive(false);
            hiddenObjects.Add(current.gameObject);
            return;
        }
        if (strict ? (IsStrictKeepNode(current) || current.GetComponent<GorillaScoreBoard>() != null) : (IsWorldUiNode(current) || HasSpecialGameplayBehaviourNode(current)))
        {
            // Replaced modern boards (CodeOfConduct_Group, MOTD, etc.) have physical solid colliders that block movement if kept
            if (current.name.Contains("CodeOfConduct") || current.name.Contains("MOTD") || current.name.Contains("COC"))
            {
                foreach (Collider c in current.GetComponents<Collider>())
                {
                    if (c != null && !c.isTrigger && c.enabled)
                    {
                        c.enabled = false;
                        suppressedColliders.Add(c);
                    }
                }
            }
            return; // scoreboards in map scenes stay whole; HistoricalScoreboards restyles them
        }

        Renderer r = current.GetComponent<Renderer>();
        // 2023 had the same weather systems (WeatherDayNight rain/snow, mountain snow): keep their particles.
        if (r is ParticleSystemRenderer && NameChainContains(current, WeatherNames)) r = null;
        if (r != null && r.enabled)
        {
            r.enabled = false;
            if (suppressedRenderers.Add(r))
            {
                suppressionRecords.Add($"renderer\t{zone}\t{HierarchyPath(current, root)}\t{r.GetType().Name}");
                suppressionLogDirty = true;
            }
        }

        // GetComponents: huts, brackets and store furniture carry several colliders on one object.
        foreach (Collider c in current.GetComponents<Collider>())
        {
            if (c == null || c.isTrigger || !c.enabled) continue;
            c.enabled = false;
            if (suppressedColliders.Add(c))
            {
                suppressionRecords.Add($"collider\t{zone}\t{HierarchyPath(current, root)}\t{c.GetType().Name}");
                suppressionLogDirty = true;
            }
        }

        int count = current.childCount;
        for (int i = 0; i < count; i++)
        {
            SuppressHierarchyRecursive(current.GetChild(i), root, zone, strict);
        }
    }

    // Debug: first ancestor rule that would have stopped a strict scan from reaching this node.
    private static string KeepReason(Transform t)
    {
        for (Transform a = t; a != null; a = a.parent)
        {
            if (IsOurs(a)) return "ours@" + a.name;
            if (a.GetComponent<TMPro.TMP_Text>() != null) return "tmp@" + a.name;
            if (IsPlayerHierarchy(a)) return "player@" + a.name;
            if (IsStrictKeepNode(a)) return "keep@" + a.name;
            if (a.GetComponent<GorillaScoreBoard>() != null) return "board@" + a.name;
            if (IsWorldUiNode(a)) return "ui@" + a.name;
            if (HasSpecialGameplayBehaviourNode(a)) return "special@" + a.name;
        }
        return "none";
    }

    // Objects this plugin created inside modern hierarchies (stump props, un-baked computer visuals, UI).
    private static bool IsOurs(Transform t)
    {
        if (t == null) return false;
        for (Transform c = t; c != null; c = c.parent)
        {
            string n = c.name;
            if (n.StartsWith("August 2023") || n.StartsWith("GorillaOGV2") || n == "2026 Visual" || n == "Historical UI" || n == "Tree Room Texts")
                return true;
        }
        return false;
    }

    private static bool IsStrictKeepNode(Transform t)
    {
        if (t.GetComponent<GorillaComputerTerminal>() != null) return true;
        for (Transform c = t; c != null; c = c.parent)
        {
            string cn = c.name;
            if (cn == "goodigloo" || cn == "PhysicalComputer (2)" || cn == "keyboard" || cn == "monitor (1)" || cn.StartsWith("wardrobe"))
                return true;
        }
        foreach (Component c in t.GetComponents<Component>())
        {
            if (c == null) continue;
            string n = c.GetType().Name;
            if (n.Contains("Rope")) return true; // 2023 canyon had the same 28 RopeSwings; modern ropes stand in
        }
        return false;
    }

    private static bool HasHistoricalEquivalent(GTZone zone) =>
        zone == GTZone.forest ||
        zone == GTZone.city ||
        zone == GTZone.basement ||
        zone == GTZone.canyon ||
        zone == GTZone.beach ||
        zone == GTZone.mountain ||
        zone == GTZone.skyJungle ||
        zone == GTZone.cave ||
        zone == GTZone.cityWithSkyJungle ||
        zone == GTZone.forestWithCity;

    private static bool HasSpecialGameplayBehaviourNode(Transform t)
    {
        MonoBehaviour[] scripts = t.GetComponents<MonoBehaviour>();
        for (int i = 0; i < scripts.Length; i++)
        {
            MonoBehaviour component = scripts[i];
            if (component == null) continue;
            string n = component.GetType().FullName ?? component.GetType().Name;
            // Physical stand-ins only. Trigger/teleport nodes are not skipped: the modern
            // boundary-stone and mall-tunnel geometry hides behind them.
            if (n.Contains("Rope") || n.Contains("Zipline") || n.Contains("Water") ||
                n.Contains("Bounce") || n.Contains("Geyser") || n.Contains("Swim") ||
                n.Contains("Current") || n.Contains("Wind"))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsWorldUiNode(Transform t)
    {
        string n = t.name;
        if (n.Length > 0 && (n[0] == 'U' || n[0] == 'F' || n[0] == 'L' || n[0] == 'M' || n[0] == 'T' || n[0] == 'B' || n[0] == 'R' || n[0] == 'C'))
        {
            if (n.StartsWith("Uncover") || n.StartsWith("Leaves") || n.StartsWith("Trunk") || n.StartsWith("Branch") || n.StartsWith("Rock") || n.StartsWith("B_") || n.StartsWith("C_") || n.StartsWith("mesh_"))
            {
                return false;
            }
        }
        string nl = n.ToLowerInvariant();
        if (nl.Contains("canvas") ||
            nl.Contains("keyboard") ||
            nl.Contains("motd") || nl.Contains("message of the day") ||
            nl.Contains("codeofconduct") || nl.Contains("code of conduct") ||
            nl.Contains("welcome") || nl.Contains("wardrobe") ||
            nl.Contains("cosmetic interface") || nl.Contains("computer terminal") ||
            nl.Contains("beachcomputer") || nl.Contains("scoreboard") ||
            nl.Contains("turnspeed") ||
            t.GetComponent<GorillaComputerTerminal>() != null ||
            t.GetComponent<GorillaNetworking.GorillaComputer>() != null)
        {
            return true;
        }

        // A Canvas component alone is not UI: GMMToCity and City_Pretty are whole map roots with one.
        return false;
    }

    internal void PerformAudit(string phase)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"==================== [GorillaOGV2 RUNTIME AUDIT: {phase}] ====================");

        // 1. Controller status
        sb.AppendLine($"1. Controller Alive: {isActiveAndEnabled} (InstanceID: {GetInstanceID()}, GameObject: {gameObject.name})");

        // 2. Package status
        sb.AppendLine($"2. Package Loaded: {loaded} (Path: '{loadedPackagePath}', Bytes: {loadedPackageBytes}, Version: '{loadedPackageVersion}', LoadTimeMs: {loadDurationMs})");

        // 3. Historical roots created
        var rootList = zoneRoots.Select(p => $"{p.Key}:(active={p.Value.activeSelf},children={p.Value.transform.childCount})");
        sb.AppendLine($"3. Historical Roots Created: {zoneRoots.Count} total: [{string.Join(", ", rootList)}]");

        // 4. Current active modern zones
        List<string> modernZones = new List<string>();
        if (ZoneManagement.instance != null)
        {
            foreach (var z in ZoneManagement.instance.activeZones) modernZones.Add(z.ToString());
        }
        sb.AppendLine($"4. Current Active Modern Zones: [{string.Join(", ", modernZones)}]");

        // 5. Historical zones active
        var activeHistZones = zoneRoots.Where(p => p.Value != null && p.Value.activeSelf).Select(p => p.Key).ToList();
        sb.AppendLine($"5. Historical Zones Active: [{string.Join(", ", activeHistZones)}]");

        // 6. Historical renderers enabled
        int histRenderersEnabled = 0;
        int histRenderersValidMesh = 0;
        int histRenderersValidMat = 0;
        Dictionary<string, int> histRenderersPerZone = new Dictionary<string, int>();
        foreach (var pair in zoneRoots)
        {
            if (pair.Value == null || !pair.Value.activeSelf) continue;
            int countInZone = 0;
            foreach (var r in pair.Value.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled) continue;
                histRenderersEnabled++;
                countInZone++;
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) histRenderersValidMesh++;
                if (r.sharedMaterial != null) histRenderersValidMat++;
            }
            histRenderersPerZone[pair.Key] = countInZone;
        }
        var zoneBreakdown = histRenderersPerZone.Select(p => $"{p.Key}={p.Value}");
        sb.AppendLine($"6. Historical Renderers Enabled: {histRenderersEnabled} (ValidMesh: {histRenderersValidMesh}, ValidMat: {histRenderersValidMat}) [{string.Join(", ", zoneBreakdown)}]");

        // 7. Historical colliders enabled
        int histCollidersEnabled = 0;
        int histSurfaceCount = 0;
        int histClimbCount = 0;
        foreach (var pair in zoneRoots)
        {
            if (pair.Value == null || !pair.Value.activeSelf) continue;
            foreach (var c in pair.Value.GetComponentsInChildren<Collider>(false))
            {
                if (!c.enabled || c.isTrigger) continue;
                histCollidersEnabled++;
                if (c.GetComponent<GorillaSurfaceOverride>() != null) histSurfaceCount++;
                if (c.GetComponent<GorillaLocomotion.Climbing.GorillaClimbable>() != null) histClimbCount++;
            }
        }
        sb.AppendLine($"7. Historical Colliders Enabled: {histCollidersEnabled} (SurfaceOverrides: {histSurfaceCount}, Climbables: {histClimbCount})");

        // 8. Modern renderers still enabled
        List<string> lingeringRenderers = new List<string>();
        foreach (Renderer r in suppressedRenderers)
        {
            if (r != null && r.enabled && r.gameObject.activeInHierarchy)
            {
                if (lingeringRenderers.Count < 20) lingeringRenderers.Add(HierarchyPath(r.transform, null));
            }
        }
        sb.AppendLine($"8. Modern Environment Renderers Still Enabled: {lingeringRenderers.Count} (Total Tracked Suppressed: {suppressedRenderers.Count})");
        if (lingeringRenderers.Count > 0)
        {
            sb.AppendLine($"   Lingering sample: {string.Join(" | ", lingeringRenderers)}");
        }

        // 9. Modern colliders still enabled
        List<string> lingeringColliders = new List<string>();
        foreach (Collider c in suppressedColliders)
        {
            if (c != null && c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy)
            {
                if (lingeringColliders.Count < 20) lingeringColliders.Add(HierarchyPath(c.transform, null));
            }
        }
        sb.AppendLine($"9. Modern Environment Colliders Still Enabled: {lingeringColliders.Count} (Total Tracked Suppressed: {suppressedColliders.Count})");

        // 10. Modern World UI status
        var terminals = FindObjectsByType<GorillaComputerTerminal>(FindObjectsSortMode.None);
        int enabledTerminals = terminals.Count(t => t.isActiveAndEnabled);
        sb.AppendLine($"10. Modern World UI Status: TerminalsTotal={terminals.Length}, TerminalsEnabled={enabledTerminals}");

        // 11. Historical player status
        var players = FindObjectsByType<LegacyPlayerController>(FindObjectsSortMode.None);
        sb.AppendLine($"11. Historical Player Controller Count: {players.Length}");

        // 12. Historical lighting status
        sb.AppendLine($"12. Historical Lighting: State='{LegacyLighting.ActiveState}', Slot={LegacyLighting.LightmapIndex}");
        sb.AppendLine("=============================================================================");

        string report = sb.ToString();
        Debug.Log(report);

        try
        {
            string auditFile = Path.Combine(Paths.PluginPath, "GorillaOGV2", "runtime-audit.log");
            File.AppendAllText(auditFile, report + "\n");

            if (suppressionLogDirty)
            {
                suppressionLogDirty = false;
                string[] snapshot = suppressionRecords.ToArray();
                string tsvFile = Path.Combine(Paths.PluginPath, "GorillaOGV2", "modern-environment-suppression.tsv");
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(tsvFile));
                        File.WriteAllLines(tsvFile, snapshot);
                    }
                    catch { }
                });
            }
        }
        catch { }
    }

    private static string HierarchyPath(Transform item, Transform root)
    {
        if (item == null) return string.Empty;
        string path = item.name;
        while (item.parent != null && item != root)
        {
            item = item.parent;
            path = item.name + "/" + path;
        }
        return path;
    }

    private static string ReadString(BinaryReader r)
    {
        int count = r.ReadInt32();
        byte[] bytes = r.ReadBytes(count);
        return Encoding.UTF8.GetString(bytes);
    }

    private static Vector3 ReadVector3Value(BinaryReader r) => new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
    private static Vector3[] ReadVector3(BinaryReader r)
    {
        int n = r.ReadInt32();
        Vector3[] a = new Vector3[n];
        for (int i = 0; i < n; i++) a[i] = ReadVector3Value(r);
        return a;
    }
    private static Vector2[] ReadVector2(BinaryReader r)
    {
        int n = r.ReadInt32();
        Vector2[] a = new Vector2[n];
        for (int i = 0; i < n; i++) a[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
        return a;
    }
    private static int[] ReadIndices(BinaryReader r)
    {
        int n = r.ReadInt32();
        int[] a = new int[n];
        for (int i = 0; i < n; i++) a[i] = r.ReadInt32();
        return a;
    }
    private static Matrix4x4 ReadMatrix(BinaryReader r)
    {
        Matrix4x4 m = new Matrix4x4();
        for (int c = 0; c < 4; c++)
            for (int row = 0; row < 4; row++)
                m[row, c] = r.ReadSingle();
        return m;
    }
    private static void SetMatrix(Transform t, Matrix4x4 m)
    {
        t.position = m.GetColumn(3);
        Vector3 scale = new Vector3(m.GetColumn(0).magnitude, m.GetColumn(1).magnitude, m.GetColumn(2).magnitude);
        if (scale.x > 0.0001f && scale.y > 0.0001f && scale.z > 0.0001f)
        {
            t.localScale = scale;
            Vector3 forward = m.GetColumn(2) / scale.z;
            Vector3 upwards = m.GetColumn(1) / scale.y;
            if (forward.sqrMagnitude > 0.001f && upwards.sqrMagnitude > 0.001f)
            {
                t.rotation = Quaternion.LookRotation(forward, upwards);
                return;
            }
        }
        t.rotation = m.rotation;
    }
}

internal sealed class HistoricalSourcePaths : MonoBehaviour
{
    internal string Value;
}
