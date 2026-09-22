using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GorillaOGV2;

/// <summary>
/// Restyles the modern GorillaScoreBoard prefabs into the August 2023 board: root at the 2023
/// PersistentObjects_Prefab/GorillaUI/*ScoreboardAnchor pose (anchor scale x prefab 4.5), Board Text and
/// Button Text at the 2023 prefab offsets with Utopium metrics, lines at y = 35 - 14 i, 2026 extras
/// (room/weather controls, level, MMR) hidden. The modern component keeps driving names, mute and report.
/// </summary>
internal static class HistoricalScoreboards
{
    private struct Anchor { public Vector3 pos; public Quaternion rot; public float scale; public Anchor(float x, float y, float z, float qx, float qy, float qz, float qw, float s) { pos = new Vector3(x, y, z); rot = new Quaternion(qx, qy, qz, qw); scale = s; } }
    private static readonly Dictionary<string, Anchor> Anchors2023 = new Dictionary<string, Anchor>
    {
        { "forest", new Anchor(-61.0792f, 4.0639f, -60.7438f, 0.05634f, 0.26258f, -0.01536f, 0.96314f, 0.002f) },
        { "city", new Anchor(-60.508f, 16.3792f, -106.7507f, 0f, 0.96569f, 0f, -0.25969f, 0.001f) },
        { "cave", new Anchor(-65.298f, -26.6683f, -33.495f, 0.03546f, 0.92181f, -0.06082f, 0.3812f, 0.001f) },
        { "mountain", new Anchor(-22.7558f, 18.1362f, -101.7498f, -0.01553f, 0.96236f, -0.0563f, -0.26544f, 0.001f) },
        { "beach", new Anchor(26.1535f, 10.0282f, -0.149f, 0.06744f, 0.41306f, -0.02021f, 0.90798f, 0.001f) },
        { "canyon", new Anchor(-88.3575f, 10.2467f, -107.225f, 0.25846f, -0.33348f, 0.09093f, 0.90207f, 0.001f) },
        { "basement", new Anchor(-33.5587f, 14.9342f, -87.7685f, 0f, 0.26729f, 0f, 0.96362f, 0.001f) },
        { "skyjungle", new Anchor(-73.2775f, 162.9142f, -101.44f, 0.02855f, 0.86803f, -0.05095f, 0.49306f, 0.001f) },
    };
    private static readonly HashSet<GorillaScoreBoard> done = new HashSet<GorillaScoreBoard>();
    private static readonly Color ButtonGray = new Color(0.196f, 0.196f, 0.196f, 1f);

    internal static void Restyle()
    {
        foreach (GorillaScoreBoard board in Object.FindObjectsByType<GorillaScoreBoard>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (board == null || done.Contains(board)) continue;
            string key = ZoneKey(board.transform);
            if (key == null) continue;
            done.Add(board);
            string path = HierarchyName(board.transform);
            if (path.Contains("Arcade") || path.Contains("OldCave") || path.Contains("Physical"))
            {
                // 2026-only extra boards (arcade room, old-cave physical board) share the zone: one 2023 board per zone.
                board.gameObject.SetActive(false);
                Debug.Log($"[GorillaOGV2][SCOREBOARD] hidden 2026-only board '{path}'");
                continue;
            }
            try { Apply(board, Anchors2023[key]); Debug.Log($"[GorillaOGV2][SCOREBOARD] restyled '{HierarchyName(board.transform)}' as 2023 {key} board"); }
            catch (System.Exception ex) { Debug.LogError("[GorillaOGV2][SCOREBOARD] " + ex); }
        }
    }

    private static string ZoneKey(Transform t)
    {
        string scene = t.gameObject.scene.name;
        switch (scene)
        {
            case "City": return "city";
            case "Cave": return "cave";
            case "Mountain": return "mountain";
            case "Beach": return "beach";
            case "Canyon": return "canyon";
            case "Canyon2": return "canyon";
            case "Basement": return "basement";
            case "Skyjungle": return "skyjungle";
        }
        for (Transform c = t; c != null; c = c.parent)
        {
            if (c.name == "Forest" || c.name == "ForestScoreboardAnchor") return "forest";
            if (c.name.StartsWith("City") || c.name.Contains("Cosmetics")) return "city";
        }
        return null;
    }

    private static string HierarchyName(Transform t) => t.parent != null ? HierarchyName(t.parent) + "/" + t.name : t.name;

    private static void Apply(GorillaScoreBoard board, Anchor a)
    {
        Transform root = board.transform;
        root.position = a.pos; root.rotation = a.rot;
        Vector3 parentScale = root.parent != null ? root.parent.lossyScale : Vector3.one;
        float world = a.scale * 4.5f; // 2023 anchor scale x GorillaScoreBoard prefab root scale
        root.localScale = new Vector3(world / parentScale.x, world / parentScale.y, world / parentScale.z);

        board.startingYValue = 35; board.lineHeight = 14;
        Transform lineParent = board.linesParent != null ? board.linesParent.transform : root.Find("LeftPanel/LineParent");
        if (lineParent != null) { lineParent.SetParent(root, false); lineParent.localPosition = Vector3.zero; lineParent.localRotation = Quaternion.identity; lineParent.localScale = Vector3.one; }

        PlaceText(board.boardText, root, new Vector3(-22.9f, -36.6f, 0f), Vector3.one * 1.0782243f, new Vector2(186.24474f, 194.3533f), TextAlignmentOptions.TopLeft, 1.1f, Color.white);
        PlaceText(board.buttonText, root, new Vector3(43.2f, -76.52f, -6.3f), new Vector3(0.2222222f, 0.2222222f, 12f), new Vector2(356.69427f, 1013.2737f), TextAlignmentOptions.TopLeft, 5.25f, new Color(0.1698113f, 0.1698113f, 0.1698113f, 1f));
        if (board.notInRoomText != null)
        {
            // 2023 TreeRoom/UI/Scoreboard_OfflineText sat in the middle of the forest board; other boards had none.
            Transform t = board.notInRoomText.transform;
            if (a.pos == Anchors2023["forest"].pos)
            {
                t.SetParent(root, true);
                t.position = new Vector3(-61.296f, 3.6604f, -60.6696f); t.rotation = new Quaternion(0.05456f, 0.26269f, -0.01539f, 0.96321f);
                Vector3 ps = root.lossyScale; t.localScale = new Vector3(0.019351f / ps.x, 0.019351f / ps.y, 0.019351f / ps.z);
                HistoricalStumpEnvironment.ApplyLegacyUtopiumMetrics(board.notInRoomText, TextAlignmentOptions.Center, 1f, new Vector2(111.562f, 108.641f), Color.white);
            }
            else board.notInRoomText.gameObject.SetActive(false);
        }

        foreach (string extra in new[] { "Room Buttons", "RightPanel", "Weather Controls Status" })
        {
            Transform e = root.Find(extra); if (e != null) e.gameObject.SetActive(false);
        }
        if (board.roomControlsToggle != null) board.roomControlsToggle.SetActive(false);
        if (board.weatherControlsToggle != null) board.weatherControlsToggle.SetActive(false);
        if (board.rightPanel != null) board.rightPanel.SetActive(false);

        foreach (GorillaPlayerScoreboardLine line in root.GetComponentsInChildren<GorillaPlayerScoreboardLine>(true)) RestyleLine(line.transform);
    }

    private static void PlaceText(TextMeshPro tmp, Transform root, Vector3 localPos, Vector3 localScale, Vector2 rect, TextAlignmentOptions align, float lineSpacing, Color color)
    {
        if (tmp == null) return;
        tmp.transform.SetParent(root, false);
        tmp.transform.localPosition = localPos; tmp.transform.localRotation = Quaternion.identity; tmp.transform.localScale = localScale;
        HistoricalStumpEnvironment.ApplyLegacyUtopiumMetrics(tmp, align, lineSpacing, rect, color);
        tmp.overflowMode = TextOverflowModes.Overflow;
    }

    // 2023 GorillaPlayerScoreboardLine prefab (sharedassets0 6231) local layout.
    private static void RestyleLine(Transform line)
    {
        SetLocalX(line, "Color Swatch", -33.4f); SetLocalX(line, "gizmo-speaker", -14.6f);
        SetLocalX(line, "Mute Button", 6.6f); SetLocalX(line, "ReportButton", 47.48f);
        foreach (string hide in new[] { "Level", "MMR", "RoomControlButtons" }) { Transform h = line.Find(hide); if (h != null) h.gameObject.SetActive(false); }
        foreach (TextMeshPro tmp in line.GetComponentsInChildren<TextMeshPro>(true))
        {
            if (tmp.name == "Player Name") HistoricalStumpEnvironment.ApplyLegacyUtopiumMetrics(tmp, TextAlignmentOptions.Left, 1f, new Vector2(119.834f, 12.0016f), Color.white);
            else HistoricalStumpEnvironment.ApplyLegacyUtopiumMetrics(tmp, TextAlignmentOptions.Center, 1f, new Vector2(160f, 30f), ButtonGray);
        }
    }

    private static void SetLocalX(Transform line, string child, float x)
    {
        Transform c = line.Find(child); if (c == null) return;
        Vector3 p = c.localPosition; p.x = x; c.localPosition = p;
    }
}
