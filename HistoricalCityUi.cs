using System;
using System.IO;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace GorillaOGV2;

/// <summary>Static August 2023 City labels (info hut pages, floor signs, ATM, wall screens),
/// rebuilt from the serialized legacy UI.Text records in legacy-city-texts.json and parented
/// to the 2023 zone roots so they appear and vanish exactly with their 2023 root.</summary>
internal sealed class HistoricalCityUi : MonoBehaviour
{
    /// <summary>Rebuilt labels keyed by the 2023 level0 path id of their UI.Text (store buttons bind to these).</summary>
    internal static readonly System.Collections.Generic.Dictionary<long, TextMeshPro> TextsById = new System.Collections.Generic.Dictionary<long, TextMeshPro>();
    internal static readonly System.Collections.Generic.Dictionary<string, TextMeshPro> TextsByName = new System.Collections.Generic.Dictionary<string, TextMeshPro>();
    internal static bool Built;

    internal void Initialize(HistoricalWorldController world) => StartCoroutine(Build(world));

    private System.Collections.IEnumerator Build(HistoricalWorldController world)
    {
        // After the Stump has initialised: the Utopium TMP font is resolved from loaded assets.
        yield return new WaitForSeconds(3f);
        try { BuildNow(world); Built = true; }
        catch (Exception ex) { Debug.LogError("[GorillaOGV2][CITY] failed: " + ex); }
        try { gameObject.AddComponent<HistoricalCityStore>().Initialize(world); }
        catch (Exception ex) { Debug.LogError("[GorillaOGV2][STORE] failed: " + ex); }
        // The modern City scene (and its SatelliteWardrobeOutside) only exists once the player enters
        // the city; poll until the 2023 stand can be assembled from it.
        while (true)
        {
            HistoricalStumpEnvironment stump = GetComponent<HistoricalStumpEnvironment>();
            if (stump != null && stump.TryBuildCityWardrobe()) break;
            yield return new WaitForSeconds(1f);
        }
    }

    private void BuildNow(HistoricalWorldController world)
    {
        HistoricalStumpEnvironment.ResolveUtopiumFont();
        string[] lines;
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GorillaOGV2.legacy-city-texts.tsv"))
        using (StreamReader reader = new StreamReader(stream))
        {
            lines = reader.ReadToEnd().Split('\n');
        }
        int made = 0;
        foreach (string line in lines)
        {
            if (line.Length == 0) continue;
            string[] f = line.Split('\t');
            float N(int i) => float.Parse(f[3 + i], System.Globalization.CultureInfo.InvariantCulture);
            string name = f[0], path = f[1], align = f[2];
            string text = f[f.Length - 1].Replace("\\n", "\n").Replace("\\\\", "\\");
            string zone = path.StartsWith("CityToMountain") ? "cityToMountain" : path.StartsWith("CityToBasement") ? "cityToBasement" : "city";
            GameObject go = new GameObject("August 2023 City Text | " + name);
            go.transform.SetParent(world.ZoneRoot(zone).transform, false);
            go.transform.position = new Vector3(N(0), N(1), N(2));
            go.transform.rotation = new Quaternion(N(3), N(4), N(5), N(6));
            go.transform.localScale = new Vector3(N(7), N(8), N(9));
            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            tmp.text = text;
            HistoricalStumpEnvironment.ApplyLegacyUtopiumMetrics(tmp, Alignment(align), N(16),
                new Vector2(N(10), N(11)), new Color(N(12), N(13), N(14), N(15)));
            tmp.overflowMode = TextOverflowModes.Overflow;
            if (long.TryParse(f[f.Length - 2], out long pathId)) TextsById[pathId] = tmp;
            TextsByName[name] = tmp;
            made++;
        }
        Debug.Log($"[GorillaOGV2][CITY] rebuilt {made} August 2023 City labels");
    }

    private static TextAlignmentOptions Alignment(string legacy) => legacy switch
    {
        "UpperLeft" => TextAlignmentOptions.TopLeft, "UpperCenter" => TextAlignmentOptions.Top, "UpperRight" => TextAlignmentOptions.TopRight,
        "MiddleLeft" => TextAlignmentOptions.Left, "MiddleRight" => TextAlignmentOptions.Right,
        "LowerLeft" => TextAlignmentOptions.BottomLeft, "LowerCenter" => TextAlignmentOptions.Bottom, "LowerRight" => TextAlignmentOptions.BottomRight,
        _ => TextAlignmentOptions.Center,
    };
}
