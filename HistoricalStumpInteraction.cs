using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using GorillaLocomotion;
using GorillaNetworking;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

namespace GorillaOGV2;

/// <summary>
/// Authoritative restoration of the August 11, 2023 Stump Interaction Environment.
/// Reconstructs the complete 2023 UI hierarchy under Historical UI, positions all
/// physical boards and UI elements with serialized local transforms, eliminates
/// placeholder geometry, and audits world matrices at 5s, 15s, and 30s.
/// </summary>
internal sealed class HistoricalStumpEnvironment : MonoBehaviour
{
    internal static HistoricalStumpEnvironment Instance { get; private set; }

    private bool initialized;
    private Transform treeRoom;
    private HistoricalWorldController worldController;

    private Transform historicalUiRoot;
    private Transform computerTransform;
    private Transform motdHeadingTransform;
    private Transform motdBodyTransform;
    private Transform cocHeadingTransform;
    private Transform cocBodyTransform;

    private Transform wardrobeTransform;
    private Transform headModelsTransform;
    private Transform headLeft;
    private Transform headCenter;
    private Transform headRight;
    private Transform headTop;

    private Transform selectorButtonsTransform;
    private Transform modeAnchorTransform;

    private Material buttonPressedMat;
    private Material buttonUnpressedMat;
    private Material plasticMat;
    private TMP_FontAsset defaultFont;
    private Material defaultFontMat;
    private Mesh historicalWardrobeHeadMesh;
    private Material historicalWardrobeHeadMaterial;

    private readonly List<HistoricalModeSelectButton> modeButtons = new List<HistoricalModeSelectButton>();
    private readonly List<HistoricalWardrobeItemButton> wardrobeItemButtons = new List<HistoricalWardrobeItemButton>();
    private readonly List<HistoricalWardrobeFunctionButton> wardrobeFunctionButtons = new List<HistoricalWardrobeFunctionButton>();
    private TMP_Text currentModeTextDisplay;

    // Live modern text sources (content only) and historical-layout displays.
    private TMP_Text motdLiveSource;
    private TMP_Text motdBodyLiveSource;
    private TMP_Text cocLiveSource;
    private TMP_Text cocBodyLiveSource;
    private TMP_Text motdDisplay;
    private TMP_Text motdBodyDisplay;
    private TMP_Text cocDisplay;
    private TMP_Text cocBodyDisplay;

    // Ground-truth runtime matrix of TreeRoomInteractables, captured before any
    // restoration work. Historical TreeRoomInteractables local == identity and
    // historical TreeRoom == modern TreeRoom (verified in old/new hierarchies),
    // so historical world = runtimeInteractablesWorld * historical-local-chain.
    private Matrix4x4 runtimeInteractablesWorld;

    internal static TMP_FontAsset utopiumFont;
    private Transform welcomeForestTransform;
    private Transform motdBoardTransform;
    private Transform cocBoardTransform;

    // Tracked objects for runtime audit
    private sealed class AuditTarget
    {
        public string Name;
        public Transform Target;
        public Vector3 ExpectedPos;
        public Quaternion ExpectedRot;
        public Vector3 ExpectedScale;
        public Transform ExpectedParent;
        public Vector3 InitLocalPos;
        public Quaternion InitLocalRot;
        public Vector3 InitLocalScale;
        public bool HasRect;
        public Vector2 InitAnchorMin;
        public Vector2 InitAnchorMax;
        public Vector2 InitPivot;
        public Vector2 InitSizeDelta;
        public Vector3 InitAnchoredPosition3D;
        public Vector2 InitOffsetMin;
        public Vector2 InitOffsetMax;
        // For mirrored nodes (negative scale) Unity's world-rotation
        // decomposition convention is ambiguous, so facing is checked via the
        // display-forward axis composed from the serialized chain instead.
        public Vector3? ExpectedForward;
    }
    private readonly List<AuditTarget> auditTargets = new List<AuditTarget>();

    // Authoritative 2023 world transforms
    private static readonly Vector3 ComputerWorldPos = new Vector3(-69.0243838f, 11.5665524f, -83.1743308f);
    private static readonly Quaternion ComputerWorldRot = Quaternion.identity;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(this);
            return;
        }
        Instance = this;
    }

    internal void Initialize(Transform modernTreeRoom, HistoricalWorldController controller)
    {
        if (initialized) return;
        treeRoom = modernTreeRoom;
        worldController = controller;

        Transform interactables = treeRoom.Find("TreeRoomInteractables");
        if (interactables == null)
        {
            Debug.LogError("[GorillaOGV2][STUMP] TreeRoomInteractables not found!");
            return;
        }

        Debug.Log("[GorillaOGV2][STUMP] Initializing August 11, 2023 Stump Interaction Environment...");

        ResolveUtopiumFont();

        // Acquire materials
        plasticMat = worldController.GetHistoricalMaterial("sharedassets0.assets:254", false);
        GorillaPressableButton sampleBtn = interactables.GetComponentInChildren<GorillaPressableButton>(true);
        if (sampleBtn != null)
        {
            buttonUnpressedMat = sampleBtn.unpressedMaterial;
            buttonPressedMat = sampleBtn.pressedMaterial;
        }
        if (buttonUnpressedMat == null && plasticMat != null) buttonUnpressedMat = plasticMat;

        // Each section is isolated: a failure in one must never prevent the
        // audits and proof screenshots from running.
        RunSection("HistoricalUiRoot", () => SetupHistoricalUiRoot(interactables));
        RunSection("PhysicalComputer", () => SetupPhysicalComputer(interactables));
        RunSection("StumpProps", () => LoadEmbeddedStumpProps(interactables));
        RunSection("SuppressModern", () => SuppressModernConflictingObjects(interactables));
        RunSection("MotdAndRules", () => SetupMotdAndRules(interactables));
        RunSection("WelcomeScreens", () => SetupWelcomeScreens());
        RunSection("Wardrobe", () => SetupWardrobeInterface(interactables));
        RunSection("GameModeSelector", () => SetupGameModeSelector(interactables));

        // Facing proof runs after all placement sections complete.
        RunSection("SuppressOrphanTexts", () => SuppressModernOrphanTexts(modernTreeRoom, interactables));
        RunSection("ReclaimAdopted", () =>
        {
            worldController.Reclaim(historicalUiRoot);
            worldController.Reclaim(computerTransform);
        });
        RunSection("FacingChecks", () => LogFacingChecks());
        RunSection("ForeignTextAudit", () => LogForeignStumpTexts());

        // 8. Start Runtime Audits (at 5s, 15s, 30s) and Camera Screenshot Captures
        if (Plugin.DebugBuild)
        {
            StartCoroutine(RunRuntimeTransformAudits());
            StartCoroutine(CaptureProofScreenshots());
        }

        initialized = true;
        Debug.Log("[GorillaOGV2][STUMP] August 11, 2023 Stump Interaction Environment successfully initialized!");
    }

    internal void ReclaimAdopted()
    {
        if (worldController != null)
        {
            worldController.Reclaim(historicalUiRoot);
            worldController.Reclaim(computerTransform);
        }
    }

    private void RunSection(string name, System.Action section)
    {
        try
        {
            section();
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[GorillaOGV2][STUMP] Section {name} failed: {ex}");
        }
    }

    private void RegisterChildAudit(Transform parent, string childName, Vector3 expPos, Quaternion expRot, Vector3 expScale)
    {
        if (parent == null) return;
        Transform child = parent.Find(childName);
        if (child == null)
        {
            Debug.LogWarning($"[GorillaOGV2][STUMP] Audit child '{childName}' not found under '{parent.name}'.");
            return;
        }
        RegisterAuditTarget("Key_" + childName, child, expPos, expRot, expScale, parent);
    }

    /// <summary>Permanent facing proof: logs each text's display-forward axis
    /// against its board's display face so every run shows which side reads.</summary>
    /// <summary>Modern Stump UI texts carry ReparentOnAwakeWithRenderer and are
    /// moved directly under TreeRoom at load, so deactivating their original
    /// owners (SatelliteWardrobe, GameModeSelector, welcome board) never hid
    /// them. Every text still sitting directly under TreeRoom at this point is
    /// such an orphan: the ones this plugin keeps were re-parented already
    /// (MOTD/Rules under Historical UI, screen texts under monitor, key caps
    /// under the keyboard).</summary>
    private void SuppressModernOrphanTexts(Transform treeRoom, Transform interactables)
    {
        int hidden = 0;
        if (treeRoom != null)
        {
            for (int i = 0; i < treeRoom.childCount; i++)
            {
                Transform child = treeRoom.GetChild(i);
                if (child.GetComponent<TMP_Text>() == null) continue;
                foreach (Renderer r in child.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                hidden++;
            }
        }
        // Subscriber lobby sign and the FLOOD debug button: no 2023 counterpart.
        Transform joinSub = interactables.Find("UI/GameModeSelector_JoinPublicSub_Forest");
        if (joinSub != null) joinSub.gameObject.SetActive(false);
        Transform debugControls = treeRoom != null ? treeRoom.Find("DebugControls") : null;
        if (debugControls != null) debugControls.gameObject.SetActive(false);
        Debug.Log($"[GorillaOGV2][STUMP] Hid {hidden} orphaned modern TreeRoom texts; subscriber sign active={(joinSub != null && joinSub.gameObject.activeSelf)}.");
    }

    /// <summary>Lists every enabled text renderer near the Stump that this
    /// plugin did not create, so stray modern UI text is visible in the log.</summary>
    private void LogForeignStumpTexts()
    {
        Vector3 center = new Vector3(-67f, 12f, -83f);
        foreach (TMP_Text t in FindObjectsByType<TMP_Text>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (t == null || !t.isActiveAndEnabled || (t.GetComponent<Renderer>() is Renderer r && !r.enabled) || Vector3.Distance(t.transform.position, center) > 6f) continue;
            if (historicalUiRoot != null && t.transform.IsChildOf(historicalUiRoot)) continue;
            if (computerTransform != null && t.transform.IsChildOf(computerTransform)) continue;
            string path = t.name;
            for (Transform p = t.transform.parent; p != null; p = p.parent) path = p.name + "/" + path;
            Debug.Log($"[GorillaOGV2][STUMP][FOREIGN TEXT] {path} pos={t.transform.position:F2} scale={t.transform.lossyScale:F4} size={t.fontSize:F1} text='{Truncate(t.text, 50)}'");
        }
    }

    private void LogFacingChecks()
    {
        LogFacing("motdtext", motdBodyTransform, motdBoardTransform);
        LogFacing("COC Text", cocBodyTransform, cocBoardTransform);
        if (computerTransform != null)
        {
            Transform ui = computerTransform.Find("ComputerUI");
            Transform mon = ui != null ? ui.Find("monitor") : null;
            if (mon != null)
                Debug.Log($"[GorillaOGV2][STUMP][FACING] ComputerMonitor front={(mon.rotation * Vector3.forward).ToString("F3")} pos={mon.position.ToString("F3")}");
        }
    }

    private static void LogFacing(string name, Transform text, Transform board)
    {
        if (text == null || board == null) return;
        Vector3 textFront = text.rotation * Vector3.forward;
        Vector3 toText = (text.position - board.position).normalized;
        Debug.Log($"[GorillaOGV2][STUMP][FACING] {name} front={textFront.ToString("F3")} " +
            $"board->text={toText.ToString("F3")} pos={text.position.ToString("F3")}");
    }

    private void SetupHistoricalUiRoot(Transform interactables)
    {
        runtimeInteractablesWorld = interactables.localToWorldMatrix;

        GameObject uiGo = new GameObject("Historical UI");
        uiGo.transform.SetParent(interactables, false);
        // Authoritative 2023 TreeRoomInteractables/UI local transform
        // (analysis/inventory/old-stump-hierarchy.json, transform 250645).
        // Runtime TreeRoomInteractables world == historical world (identical
        // TreeRoom + identity Interactables in both eras), so the original
        // local transform is reused verbatim beneath the equivalent parent.
        uiGo.transform.localPosition = new Vector3(-18.932991f, 12.357571f, -3.518445f);
        uiGo.transform.localRotation = new Quaternion(3.216422e-08f, -5.893912e-08f, 0.9625188f, -0.2712153f);
        uiGo.transform.localScale = Vector3.one;

        historicalUiRoot = uiGo.transform;
        LogTransformRecord("UI (Historical UI root)", "TreeRoomInteractables",
            uiGo.transform.localPosition, uiGo.transform.localRotation, uiGo.transform.localScale,
            runtimeInteractablesWorld, interactables, interactables.localToWorldMatrix,
            uiGo.transform.localToWorldMatrix);
        Debug.Log($"[GorillaOGV2][STUMP] Historical UI root established at world pos {historicalUiRoot.position:F4}");
    }

    private static void DecomposeMatrix(Matrix4x4 m, out Vector3 pos, out Quaternion rot, out Vector3 scale)
    {
        pos = new Vector3(m.m03, m.m13, m.m23);
        Vector3 cx = new Vector3(m.m00, m.m10, m.m20);
        Vector3 cy = new Vector3(m.m01, m.m11, m.m21);
        Vector3 cz = new Vector3(m.m02, m.m12, m.m22);
        scale = new Vector3(cx.magnitude, cy.magnitude, cz.magnitude);
        Matrix4x4 n = m;
        if (scale.x > 1e-8f) { n.m00 /= scale.x; n.m10 /= scale.x; n.m20 /= scale.x; }
        if (scale.y > 1e-8f) { n.m01 /= scale.y; n.m11 /= scale.y; n.m21 /= scale.y; }
        if (scale.z > 1e-8f) { n.m02 /= scale.z; n.m12 /= scale.z; n.m22 /= scale.z; }
        rot = Quaternion.LookRotation(n.GetColumn(2), n.GetColumn(1));
    }

    private void LogTransformRecord(string histName, string histParent,
        Vector3 hPos, Quaternion hRot, Vector3 hScale,
        Matrix4x4 histParentWorld, Transform runtimeParent,
        Matrix4x4 runtimeParentWorld, Matrix4x4 resultWorld)
    {
        DecomposeMatrix(resultWorld, out Vector3 rp, out Quaternion rr, out Vector3 rs);
        Debug.Log($"[GorillaOGV2][STUMP][XFORM] {histName} | histParent={histParent} " +
            $"hLoc=({hPos.x:F4},{hPos.y:F4},{hPos.z:F4}) " +
            $"hRot=({hRot.x:F4},{hRot.y:F4},{hRot.z:F4},{hRot.w:F4}) " +
            $"hScale=({hScale.x:F5},{hScale.y:F5},{hScale.z:F5}) | " +
            $"rtParent={(runtimeParent != null ? runtimeParent.name : "None")} | " +
            $"result=({rp.x:F4},{rp.y:F4},{rp.z:F4})");
    }

    /// <summary>
    /// Places a restored object beneath a runtime parent. When the runtime
    /// parent world matches the historical parent world, the original
    /// historical local transform is applied directly. Otherwise the
    /// historical world matrix is converted into the correct local matrix for
    /// the actual runtime parent. Historical world coordinates are never
    /// copied into localPosition, and world-then-reparent (stays=false) is
    /// never used: exactly one correct conversion runs.
    /// </summary>
    private void PlaceHistorical(Transform child, Transform runtimeParent,
        Matrix4x4 histParentWorld, Vector3 hPos, Quaternion hRot, Vector3 hScale,
        string histName, string histParentName)
    {
        if (child == null || runtimeParent == null)
        {
            Debug.LogError($"[GorillaOGV2][STUMP][XFORM] {histName}: null child or parent, skipping placement.");
            return;
        }
        Matrix4x4 histWorld = histParentWorld * Matrix4x4.TRS(hPos, hRot, hScale);
        Matrix4x4 rtParentWorld = runtimeParent != null
            ? runtimeParent.localToWorldMatrix
            : Matrix4x4.identity;

        DecomposeMatrix(histParentWorld, out Vector3 hp, out Quaternion hr, out _);
        DecomposeMatrix(rtParentWorld, out Vector3 rp, out Quaternion rr, out _);
        bool parentMatches = Vector3.Distance(hp, rp) < 0.001f && Quaternion.Angle(hr, rr) < 0.01f;

        child.SetParent(runtimeParent, false);
        if (parentMatches)
        {
            child.localPosition = hPos;
            child.localRotation = hRot;
            child.localScale = hScale;
        }
        else
        {
            Debug.LogWarning($"[GorillaOGV2][STUMP][XFORM] {histName}: runtime parent world differs " +
                $"(dPos={Vector3.Distance(hp, rp):F4}m dRot={Quaternion.Angle(hr, rr):F3}d); converting world->local.");
            Matrix4x4 local = rtParentWorld.inverse * histWorld;
            DecomposeMatrix(local, out Vector3 lp, out Quaternion lr, out Vector3 ls);
            child.localPosition = lp;
            child.localRotation = lr;
            child.localScale = ls;
        }

        LogTransformRecord(histName, histParentName, hPos, hRot, hScale,
            histParentWorld, runtimeParent, rtParentWorld, child.localToWorldMatrix);
    }

    /// <summary>
    /// RectTransform-safe variant: layout fields (anchors/pivot/sizeDelta) are
    /// copied from the live modern counterpart FIRST, then the historical
    /// local TRS is applied LAST. anchoredPosition is never assigned after
    /// localPosition, which previously detached text from its board.
    /// </summary>
    private void PlaceHistoricalRect(Transform child, Transform runtimeParent,
        Matrix4x4 histParentWorld, Vector3 hPos, Quaternion hRot, Vector3 hScale,
        RectTransform layoutSource, string histName, string histParentName)
    {
        if (child == null) return;
        RectTransform rt = child.GetComponent<RectTransform>();
        if (rt != null && layoutSource != null)
        {
            rt.anchorMin = layoutSource.anchorMin;
            rt.anchorMax = layoutSource.anchorMax;
            rt.pivot = layoutSource.pivot;
            rt.sizeDelta = layoutSource.sizeDelta;
        }
        PlaceHistorical(child, runtimeParent, histParentWorld, hPos, hRot, hScale, histName, histParentName);
    }

    private void SetupPhysicalComputer(Transform interactables)
    {
        computerTransform = interactables.Find("GorillaComputerObject");
        if (computerTransform != null)
        {
            // Order matters: the 2026 monitor/key visuals are static-batched
            // in scene world space, so they are un-baked against the live
            // (original) transforms first, then the whole assembly moves.
            LoadModernComputerVisuals(computerTransform);
            ReclaimKeyboardLabels(computerTransform);

            computerTransform.position = ComputerWorldPos;
            computerTransform.rotation = ComputerWorldRot;
            computerTransform.localScale = Vector3.one;

            // NOTE: modern children live under ComputerUI (not the computer root).
            Transform computerUI = computerTransform.Find("ComputerUI");
            Transform keysParent = computerUI != null ? computerUI.Find("keyboard (1)/Buttons/Keys") : null;
            if (keysParent != null)
            {
                for (int i = 0; i < keysParent.childCount; i++)
                {
                    Transform key = keysParent.GetChild(i);
                    string kn = key.name.ToLowerInvariant();

                    if (kn == "rooms" || kn == "turn" || kn == "randomroom")
                    {
                        key.gameObject.SetActive(false);
                        continue;
                    }

                    BoxCollider col = key.GetComponent<BoxCollider>();
                    if (col != null)
                    {
                        col.enabled = true;
                        col.isTrigger = true;
                        key.gameObject.layer = 18; // GorillaInteractable
                    }

                    GorillaKeyboardButton btn = key.GetComponent<GorillaKeyboardButton>();
                    if (btn != null)
                    {
                        btn.enabled = true;
                    }
                }
            }

            TMP_Text sampleText = computerTransform.GetComponentInChildren<TMP_Text>(true);
            if (sampleText != null)
            {
                defaultFont = sampleText.font;
                defaultFontMat = sampleText.fontSharedMaterial;
            }

            // The live screen texts are whatever GorillaComputerTerminal
            // drives; any other TMP under the monitor is a decoy display.
            GorillaComputerTerminal terminal = computerTransform.GetComponentInChildren<GorillaComputerTerminal>(true);
            Transform modernMonitor = computerUI != null ? computerUI.Find("monitor") : null;
            if (terminal != null && modernMonitor != null)
            {
                foreach (TMP_Text stray in modernMonitor.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (stray == terminal.myScreenText || stray == terminal.myFunctionText) continue;
                    Debug.Log($"[GorillaOGV2][STUMP][SCREEN] Disabling non-terminal monitor text '{stray.name}' (text='{Truncate(stray.text, 40)}').");
                    stray.gameObject.SetActive(false);
                }
                // Serialized 2023 monitor/Data and monitor/FunctionSelect.
                RestoreComputerScreenRect(terminal.myScreenText, modernMonitor, "Data",
                    new Vector3(-0.05673f, -0.28490f, 0.51730f), new Vector2(197.1586f, 135.5958f));
                RestoreComputerScreenRect(terminal.myFunctionText, modernMonitor, "FunctionSelect",
                    new Vector3(0.24691f, -0.28498f, 0.51798f), new Vector2(46.0582f, 135.5958f));
            }
            else Debug.LogError($"[GorillaOGV2][STUMP][SCREEN] terminal={(terminal != null)} monitor={(modernMonitor != null)}; screen texts left as-is.");

            RegisterAuditTarget("Computer", computerTransform,
                new Vector3(-69.0244f, 11.5666f, -83.1743f),
                Quaternion.identity,
                Vector3.one,
                interactables);

            if (modernMonitor != null)
                RegisterAuditTarget("ComputerMonitor", modernMonitor,
                    new Vector3(-69.1389f, 11.4756f, -83.4713f),
                    new Quaternion(-0.5871f, 0.3940f, 0.3938f, 0.5873f),
                    Vector3.one, computerUI);
            Transform auditKeyboard = computerUI != null ? computerUI.Find("keyboard (1)") : null;
            if (auditKeyboard != null)
                RegisterAuditTarget("ComputerKeyboard", auditKeyboard,
                    new Vector3(-68.5918f, 11.6904f, -83.2177f),
                    new Quaternion(-0.5307f, 0.4672f, 0.4671f, 0.5309f),
                    Vector3.one, computerUI);
            if (keysParent != null)
                RegisterAuditTarget("ComputerKeys", keysParent,
                    new Vector3(-68.7623f, 11.7853f, -83.2077f),
                    new Quaternion(0.0083f, -0.0631f, -0.1304f, 0.9894f),
                    Vector3.one * 0.02f, keysParent.parent);
            // Serialized 2023 key worlds; the 2026 keys share the same
            // anchoredPosition layout so they must land here after the move.
            Quaternion keyWorldRot = new Quaternion(0.1368f, 0.6996f, -0.6996f, -0.0476f);
            Vector3 keyWorldScale = new Vector3(0.019f, 0.019f, 0.03f);
            RegisterChildAudit(keysParent, "m",
                new Vector3(-68.6573f, 11.7467f, -82.8935f), keyWorldRot, keyWorldScale);
            RegisterChildAudit(keysParent, "enterkeyforest",
                new Vector3(-68.7342f, 11.7598f, -82.6768f), keyWorldRot, keyWorldScale);
            RegisterChildAudit(keysParent, "1",
                new Vector3(-68.7629f, 11.7855f, -83.2094f), keyWorldRot, keyWorldScale);

            Debug.Log($"[GorillaOGV2][STUMP] Physical Computer aligned at {computerTransform.position:F4}");
        }

        Transform duplicateComputer = interactables.Find("UI/-- PhysicalComputer UI --");
        if (duplicateComputer != null) duplicateComputer.gameObject.SetActive(false);
    }

    // Modern key-cap labels (keyboard (1)/Buttons/Text children); their
    // ReparentOnAwakeWithRenderer moves them out to TreeRoom at load, so they
    // would stay behind when the computer moves.
    private static readonly HashSet<string> KeyLabelNames = new HashSet<string>(new[]
    {
        "downtext", "uptext", "option3", "option2", "option1", "enter", "delete",
        "m", "n", "b", "v", "c", "x", "z", "l", "k", "j", "h", "g", "f", "d", "s", "a",
        "p", "o", "i", "u", "y", "t", "r", "e", "w", "q", "0", "9", "8", "7", "6", "5", "4", "3", "2", "1",
    });

    private void ReclaimKeyboardLabels(Transform computer)
    {
        Transform keyboard = computer.Find("ComputerUI/keyboard (1)");
        Transform textRow = keyboard != null ? keyboard.Find("Buttons/Text") : null;
        if (textRow == null) { Debug.LogError("[GorillaOGV2][STUMP][COMPUTER] keyboard (1)/Buttons/Text not found."); return; }
        int reclaimed = 0;
        foreach (ReparentOnAwakeWithRenderer rep in FindObjectsByType<ReparentOnAwakeWithRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (!KeyLabelNames.Contains(rep.name) || rep.transform.IsChildOf(computer)) continue;
            if (rep.GetComponent<TMP_Text>() == null) continue;
            if (Vector3.Distance(rep.transform.position, keyboard.position) > 1.0f) continue;
            rep.enabled = false;
            rep.transform.SetParent(textRow, true);
            reclaimed++;
        }
        Debug.Log($"[GorillaOGV2][STUMP][COMPUTER] Reclaimed {reclaimed}/{KeyLabelNames.Count} key-cap labels back under keyboard (1)/Buttons/Text.");
    }

    private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…").Replace('\n', '|');

    /// <summary>Re-attaches the 2026 static-batched computer visuals (monitor
    /// screen, keys) to their own transforms so they follow the 2023 pose.
    /// Vertices arrive in scene world space and are un-baked against each
    /// object's live localToWorld, which must still be the original pose.</summary>
    private void LoadModernComputerVisuals(Transform computer)
    {
        Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GorillaOGV2.modern-computer.bin");
        if (stream == null)
        {
            Debug.LogError("[GorillaOGV2][STUMP][COMPUTER] Embedded resource GorillaOGV2.modern-computer.bin missing.");
            return;
        }
        GorillaComputerTerminal terminal = computer.GetComponentInChildren<GorillaComputerTerminal>(true);
        using BinaryReader reader = new BinaryReader(stream);
        reader.ReadBytes(8); reader.ReadByte();
        uint count = reader.ReadUInt32();
        int attached = 0;
        for (int i = 0; i < count; i++)
        {
            string path = ReadString(reader);
            reader.ReadBytes(10 * sizeof(float)); // serialized world TRS: unused, live transform is authoritative
            if (reader.ReadBoolean()) { ReadMatrix(reader); continue; }
            Mesh mesh = ReadMesh(reader, out _);
            uint matCount = reader.ReadUInt32();
            for (int m = 0; m < matCount; m++) reader.ReadInt32();
            reader.ReadInt32(); reader.ReadBytes(4 * sizeof(float)); // lightmap: copied from the live renderer

            Transform target = computer.Find(path);
            if (target == null)
            {
                Debug.LogWarning($"[GorillaOGV2][STUMP][COMPUTER] No live object for batched renderer '{path}'.");
                continue;
            }

            Matrix4x4 toLocal = target.worldToLocalMatrix;
            Matrix4x4 normalToLocal = target.localToWorldMatrix.transpose;
            Vector3[] v = mesh.vertices;
            for (int k = 0; k < v.Length; k++) v[k] = toLocal.MultiplyPoint3x4(v[k]);
            mesh.vertices = v;
            Vector3[] n = mesh.normals;
            for (int k = 0; k < n.Length; k++) n[k] = normalToLocal.MultiplyVector(n[k]).normalized;
            if (n.Length > 0) mesh.normals = n;
            Vector4[] t = mesh.tangents;
            for (int k = 0; k < t.Length; k++)
            {
                Vector3 d = toLocal.MultiplyVector(new Vector3(t[k].x, t[k].y, t[k].z)).normalized;
                t[k] = new Vector4(d.x, d.y, d.z, t[k].w);
            }
            if (t.Length > 0) mesh.tangents = t;
            mesh.RecalculateBounds();

            MeshRenderer batched = target.GetComponent<MeshRenderer>();
            GameObject visual = new GameObject("2026 Visual");
            visual.layer = target.gameObject.layer;
            visual.transform.SetParent(target, false);
            visual.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer mr = visual.AddComponent<MeshRenderer>();
            if (batched != null)
            {
                mr.sharedMaterials = batched.sharedMaterials;
                mr.lightmapIndex = batched.lightmapIndex;
                mr.lightmapScaleOffset = batched.lightmapScaleOffset;
                mr.shadowCastingMode = batched.shadowCastingMode;
                mr.receiveShadows = batched.receiveShadows;
                if (terminal != null && terminal.monitorMesh == batched) terminal.monitorMesh = mr;
                batched.enabled = false;
            }
            attached++;
        }
        Debug.Log($"[GorillaOGV2][STUMP][COMPUTER] Re-attached {attached}/{count} un-baked 2026 computer renderers to their live transforms.");
    }

    private void RestoreComputerScreenRect(TMP_Text liveText, Transform monitor, string name, Vector3 historicalLocal, Vector2 historicalRect)
    {
        if (liveText == null)
        {
            Debug.LogError($"[GorillaOGV2][STUMP][SCREEN] Terminal has no '{name}' text.");
            return;
        }
        Transform liveScreenText = liveText.transform;
        Debug.Log($"[GorillaOGV2][STUMP][SCREEN] {name}: terminal text '{liveScreenText.name}' from parent '{liveScreenText.parent?.name}' text='{Truncate(liveText.text, 60)}'.");
        DisableReparentOnAwake(liveScreenText);
        // August 2023 had Data/FunctionSelect directly under monitor.
        liveScreenText.SetParent(monitor, false);
        liveScreenText.localPosition = historicalLocal;
        liveScreenText.localRotation = new Quaternion(8.890359e-05f, -0.7384517f, -0.6743057f, -0.0010451f);
        liveScreenText.localScale = new Vector3(0.0025f, 0.0025f, 1f);
        liveText.rectTransform.anchorMin = liveText.rectTransform.anchorMax = liveText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
        ApplyLegacyUtopiumMetrics(liveText, TextAlignmentOptions.TopLeft, 1f, historicalRect, Color.white);
        liveText.gameObject.SetActive(true);
        liveText.enabled = true;
        RegisterAuditTarget("ComputerScreen_" + name, liveScreenText,
            liveScreenText.position, liveScreenText.rotation, liveScreenText.lossyScale, monitor);
    }

    private void LoadEmbeddedStumpProps(Transform interactables)
    {
        Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GorillaOGV2.legacy-stump-props.bin");
        if (stream == null)
        {
            Debug.LogError("[GorillaOGV2][STUMP] Failed to open embedded resource GorillaOGV2.legacy-stump-props.bin!");
            return;
        }

        using BinaryReader reader = new BinaryReader(stream);
        byte[] magic = reader.ReadBytes(8);
        string magicStr = Encoding.ASCII.GetString(magic);
        byte version = reader.ReadByte();
        if (magicStr != "GOG2PROP" || (version != 1 && version != 2))
        {
            Debug.LogError($"[GorillaOGV2][STUMP] Invalid props magic '{magicStr}' or version {version}");
            return;
        }

        uint count = reader.ReadUInt32();
        GameObject root = new GameObject("August 2023 Historical Stump Props");
        // Parented under the room with world-stays so the baked world
        // transforms below are unchanged, but props can never split from
        // the room if it is ever repositioned after init.
        root.transform.SetParent(interactables, true);
        root.transform.position = Vector3.zero;
        root.transform.rotation = Quaternion.identity;
        root.transform.localScale = Vector3.one;

        for (int i = 0; i < count; i++)
        {
            string path = ReadString(reader);
            Vector3 worldPos = Vector3.zero;
            Quaternion worldRot = Quaternion.identity;
            Vector3 worldScale = Vector3.one;

            if (version >= 2)
            {
                worldPos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                worldRot = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                worldScale = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }

            bool isPrimitive = reader.ReadBoolean();
            Matrix4x4 primMatrix = Matrix4x4.identity;
            string meshName = "";
            Mesh mesh = null;

            if (isPrimitive)
            {
                primMatrix = ReadMatrix(reader);
            }
            else
            {
                mesh = ReadMesh(reader, out meshName);
            }

            uint matCount = reader.ReadUInt32();
            int[] matIds = new int[matCount];
            for (int m = 0; m < matCount; m++) matIds[m] = reader.ReadInt32();
            int lightmapIndex = reader.ReadInt32();
            Vector4 lmTilingOffset = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

            // Instantiate only the static visual props needed by Stump interaction
            bool isStaticProp = path.Contains("Static[0]/wardrobe") ||
                                path.Contains("Static[0]/Current Mode") ||
                                path.Contains("Static[0]/Game Modes") ||
                                path.Contains("Static[0]/modeselectbox[0]/gamemodeselector") ||
                                path.Contains("Static[0]/modeselectbox[0]/selection cver") ||
                                path.Contains("Static[0]/wallmonitorlong") ||
                                path.Contains("StaticUnlit[0]/motdscreen") ||
                                path.Contains("Static[0]/code of conduct") ||
                                path.Contains("Static[0]/keyboard (1)");

            bool isHistoricalHead = path.EndsWith("/HeadModels[0]/Head Model Left[0]", StringComparison.Ordinal) ||
                                    path.EndsWith("/HeadModels[0]/Head Model Center[0]", StringComparison.Ordinal) ||
                                    path.EndsWith("/HeadModels[0]/Head Model Right[0]", StringComparison.Ordinal) ||
                                    path.EndsWith("/HeadModels[0]/Head Model Top[0]", StringComparison.Ordinal);
            if (isHistoricalHead && mesh != null)
            {
                historicalWardrobeHeadMesh = mesh;
                int headMaterialId = matIds.Length > 0 ? matIds[0] : 227;
                historicalWardrobeHeadMaterial = worldController.GetHistoricalMaterial($"sharedassets0.assets:{headMaterialId}", false);
                Debug.Log($"[GorillaOGV2][STUMP][WARDROBE] Loaded authoritative 2023 mannequin mesh '{mesh.name}' " +
                          $"({mesh.vertexCount} vertices), material {headMaterialId}.");
                continue;
            }

            if (isStaticProp && mesh != null)
            {
                string propLabel = path.Substring(path.LastIndexOf('/') + 1);
                GameObject go = new GameObject("August 2023 Prop | " + propLabel);
                go.transform.position = (version >= 2) ? worldPos : Vector3.zero;
                go.transform.rotation = (version >= 2) ? worldRot : Quaternion.identity;
                go.transform.localScale = (version >= 2) ? worldScale : Vector3.one;
                go.transform.SetParent(root.transform, true);

                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();

                int primaryMatId = matIds.Length > 0 ? matIds[0] : 259;
                string matKey = $"sharedassets0.assets:{primaryMatId}";
                bool hasLightmap = (lightmapIndex != 65535);
                Material mat = worldController.GetHistoricalMaterial(matKey, hasLightmap);
                mr.sharedMaterial = mat;

                if (hasLightmap)
                {
                    mr.lightmapIndex = LegacyLighting.LightmapIndex;
                    mr.lightmapScaleOffset = lmTilingOffset;
                }

                if (mr.sharedMaterial == null)
                {
                    Debug.LogError($"[GorillaOGV2][STUMP] Prop '{propLabel}' resolved a null material for key '{matKey}'; board may render incorrectly.");
                    if (plasticMat != null) mr.sharedMaterial = plasticMat;
                }

                MeshCollider mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                go.layer = (path.Contains("Current Mode") || path.Contains("Game Modes")) ? 18 : 9;

                if (path.Contains("motdscreen")) motdBoardTransform = go.transform;
                else if (path.Contains("code of conduct/board")) cocBoardTransform = go.transform;

                GorillaSurfaceOverride gso = go.AddComponent<GorillaSurfaceOverride>();
                gso.overrideIndex = 0;

                // Register audit target for physical boards
                if (path.Contains("wallmonitorlong"))
                {
                    RegisterAuditTarget("wallmonitorlong", go.transform,
                        (version >= 2) ? worldPos : new Vector3(-69.4376f, 11.8460f, -81.9510f),
                        (version >= 2) ? worldRot : new Quaternion(-0.0001f, -0.6372f, -0.0002f, 0.7707f),
                        (version >= 2) ? worldScale : Vector3.one, root.transform);
                }
                else if (path.Contains("motdscreen"))
                {
                    RegisterAuditTarget("motdscreen", go.transform,
                        (version >= 2) ? worldPos : new Vector3(-69.4376f, 11.8460f, -81.9510f),
                        (version >= 2) ? worldRot : new Quaternion(-0.0001f, -0.6372f, -0.0002f, 0.7707f),
                        (version >= 2) ? worldScale : Vector3.one, root.transform);
                }
                else if (path.Contains("code of conduct[0]/board") || path.Contains("code of conduct/board"))
                {
                    RegisterAuditTarget("code of conduct board", go.transform,
                        (version >= 2) ? worldPos : new Vector3(-65.3426f, 11.3225f, -80.9841f),
                        (version >= 2) ? worldRot : new Quaternion(0.2170f, 0.6730f, 0.6730f, -0.2170f),
                        (version >= 2) ? worldScale : new Vector3(0.4428f, 0.4428f, 0.4428f), root.transform);
                }
                else if (path.Contains("wardrobe"))
                {
                    RegisterAuditTarget("wardrobe", go.transform,
                        (version >= 2) ? worldPos : new Vector3(-65.4979f, 11.5154f, -84.1516f),
                        (version >= 2) ? worldRot : new Quaternion(-0.7069f, -0.0168f, -0.0168f, 0.7069f),
                        (version >= 2) ? worldScale : new Vector3(1.0f, 1.1753f, 1.0f), root.transform);
                }
            }
        }

        Debug.Log("[GorillaOGV2][STUMP] Loaded and instantiated 2023 historical stump props.");
    }

    // Modern boards the 2023 ones replace: hide the mesh and drop its colliders (the 2026
    // rules board otherwise stays touchable beside the 2023 one).
    private static void HideModern(Transform t)
    {
        foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
        foreach (Collider c in t.GetComponentsInChildren<Collider>(true)) if (!c.isTrigger) c.enabled = false;
    }

    private void SuppressModernConflictingObjects(Transform interactables)
    {
        // 1. Hide modern displaced wallmonitorlong and motdscreen
        Transform modernWallMonitor = interactables.Find("Static/wallmonitorlong");
        if (modernWallMonitor != null)
        {
            HideModern(modernWallMonitor);
        }

        Transform modernMotdScreen = interactables.Find("StaticUnlit/motdscreen");
        if (modernMotdScreen != null)
        {
            HideModern(modernMotdScreen);
        }

        // 2. Hide modern CodeOfConduct group renderers, including the modern
        // screen that otherwise sits exactly over the historical board
        Transform cocGroup = interactables.Find("UI/CodeOfConduct_Group");
        if (cocGroup != null)
        {
            Transform modernCoc = cocGroup.Find("Static/code of conduct");
            if (modernCoc != null)
            {
                HideModern(modernCoc);
            }
            Transform modernCocScreen = cocGroup.Find("StaticUnlit/screen");
            if (modernCocScreen != null)
            {
                HideModern(modernCocScreen);
            }
        }

        // 3. Hide modern GameModeSelector visuals
        Transform modernGms = interactables.Find("UI/GameModeSelector");
        if (modernGms != null)
        {
            Transform toggle = modernGms.Find("GameModeSelector_SuperToggleButton (prefab)");
            if (toggle != null) toggle.gameObject.SetActive(false);
            modernGms.gameObject.SetActive(false);
        }
    }

    private void SetupMotdAndRules(Transform interactables)
    {
        Matrix4x4 histUiWorld = historicalUiRoot.localToWorldMatrix;

        // Reuse the live modern TMP objects for content/functionality, but put
        // them in the exact 2023 RectTransform hierarchy. Do not create a
        // second competing text/layout system.
        Transform modernMotdHead = FindLiveTextTransform("motdHeadingText");
        Transform modernMotdBody = modernMotdHead != null ? modernMotdHead.Find("motdBodyText") : null;
        if (modernMotdBody == null) modernMotdBody = FindLiveTextTransform("motdBodyText");
        motdLiveSource = modernMotdHead != null ? modernMotdHead.GetComponent<TMP_Text>() : null;
        motdBodyLiveSource = modernMotdBody != null ? modernMotdBody.GetComponent<TMP_Text>() : null;
        motdDisplay = motdLiveSource;
        motdBodyDisplay = motdBodyLiveSource;
        motdHeadingTransform = modernMotdHead;
        motdBodyTransform = modernMotdBody;
        DisableReparentOnAwake(motdHeadingTransform);
        ApplyHistoricalRect(motdHeadingTransform, historicalUiRoot,
            new Vector3(-13.908953f, 25.070595f, 10.341937f),
            new Quaternion(-0.54492235f, 0.45044595f, 0.45066935f, -0.54502767f),
            Vector3.one * 0.007941466f, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f), new Vector2(189.89395f, 77.69061f));
        ApplyHistoricalRect(motdBodyTransform, motdHeadingTransform,
            new Vector3(-89.54713f, -36.98437f, -0.00064f),
            Quaternion.identity,
            Vector3.one * 0.5936f, new Vector2(0f, 1f), new Vector2(1f, 1f),
            Vector2.zero, new Vector2(120.91110f, 109.05612f));

        ApplyLegacyUtopiumMetrics(motdLiveSource, TextAlignmentOptions.Top, 1f,
            new Vector2(189.89395f, 77.69061f), Color.white);
        ApplyLegacyUtopiumMetrics(motdBodyLiveSource, TextAlignmentOptions.TopLeft, 1f,
            new Vector2(120.91110f, 109.05612f), Color.white);
        // 2023 legacy Text used vertical Truncate: live content longer than the
        // serialized board is clipped, never spilled below the screen.
        if (motdBodyLiveSource != null) motdBodyLiveSource.overflowMode = TextOverflowModes.Truncate;

        RegisterAuditTarget("motd", motdHeadingTransform,
            new Vector3(-69.1433f, 12.3935f, -82.0029f),
            new Quaternion(-0.0001f, -0.6372f, -0.0002f, 0.7707f),
            Vector3.one * 0.007941466f, historicalUiRoot);

        RegisterAuditTarget("motdtext", motdBodyTransform,
            new Vector3(-69.2771f, 12.0999f, -82.7014f),
            new Quaternion(-0.0001f, -0.6372f, -0.0002f, 0.7707f),
            Vector3.one * (0.007941466f * 0.5936f), motdHeadingTransform);

        Transform modernCocHead = FindLiveTextTransform("CodeOfConductHeadingText");
        Transform modernCocBody = null;
        if (modernCocHead != null)
            modernCocBody = modernCocHead.Find("COCBodyText_TitleData") ?? modernCocHead.Find("COC Text");
        if (modernCocBody == null) modernCocBody = FindLiveTextTransform("COCBodyText_TitleData");
        cocLiveSource = modernCocHead != null ? modernCocHead.GetComponent<TMP_Text>() : null;
        cocBodyLiveSource = modernCocBody != null ? modernCocBody.GetComponent<TMP_Text>() : null;

        cocDisplay = cocLiveSource;
        cocBodyDisplay = cocBodyLiveSource;
        cocHeadingTransform = modernCocHead;
        cocBodyTransform = modernCocBody;
        DisableReparentOnAwake(cocHeadingTransform);
        ApplyHistoricalRect(cocHeadingTransform, historicalUiRoot,
            new Vector3(-10.128456f, 24.064861f, 9.976578f),
            new Quaternion(0.7119657f, 0.230666f, 0.2050855f, 0.6307441f),
            Vector3.one * 0.007941466f, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(0.5f, 0.5f), new Vector2(123.19699f, 108.64099f));
        ApplyHistoricalRect(cocBodyTransform, cocHeadingTransform,
            new Vector3(-56.20084f, -71.07941f, 0.00024f),
            Quaternion.identity,
            Vector3.one * 0.5936f, new Vector2(0f, 1f), new Vector2(1f, 1f),
            Vector2.zero, new Vector2(68.87438f, 190f));

        ApplyLegacyUtopiumMetrics(cocLiveSource, TextAlignmentOptions.Top, 1f,
            new Vector2(123.19699f, 108.64099f), Color.white);
        ApplyLegacyUtopiumMetrics(cocBodyLiveSource, TextAlignmentOptions.TopLeft, 1f,
            new Vector2(68.87438f, 190f), Color.white);
        if (cocBodyLiveSource != null) cocBodyLiveSource.overflowMode = TextOverflowModes.Truncate;

        RegisterAuditTarget("CodeOfConduct", cocHeadingTransform,
            new Vector3(-65.3628f, 12.0281f, -80.9972f),
            new Quaternion(0.0574f, 0.3081f, -0.0181f, 0.9494f),
            Vector3.one * 0.007941466f, historicalUiRoot);

        RegisterAuditTarget("COC Text", cocBodyTransform,
            new Vector3(-65.7635f, 11.4673f, -80.7904f),
            new Quaternion(0.0574f, 0.3081f, -0.0181f, 0.9494f),
            Vector3.one * (0.007941466f * 0.5936f), cocHeadingTransform);

        Debug.Log("[GorillaOGV2][STUMP] MOTD and Rules hierarchy established directly under Historical UI.");
    }

    private void SetupWelcomeScreens()
    {
        if (historicalUiRoot == null) return;

        GameObject group = new GameObject("Tree Room Texts");
        group.transform.SetParent(historicalUiRoot, false);

        welcomeForestTransform = CreateWelcomeScreen(group.transform, "WallScreenForest",
            "WELCOME TO GORILLA TAG!\n\nHEAD OUTSIDE TO AUTOMATICALLY JOIN A PUBLIC GAME, OR USE THE TERMINAL TO JOIN A SPECIFIC ROOM OR ADJUST YOUR SETTINGS.",
            new Vector3(-12.448000f, 23.240000f, 10.181000f),
            new Quaternion(0.7021185f, -0.0838436f, -0.0838438f, 0.7021183f)).transform;
        CreateWelcomeScreen(group.transform, "WallScreenCave",
            "WELCOME TO GORILLA TAG!\n\nHEAD DOWN THE TUNNEL TO AUTOMATICALLY JOIN A PUBLIC GAME, OR USE THE TERMINAL TO JOIN A SPECIFIC ROOM OR ADJUST YOUR SETTINGS.",
            new Vector3(-11.676604f, 28.313410f, 9.935000f),
            new Quaternion(0.0294473f, -0.7064936f, -0.7064931f, 0.0294473f));
        CreateWelcomeScreen(group.transform, "WallScreenCity Front",
            "WELCOME TO GORILLA TAG!\n\nHEAD DOWN THIS HALLWAY TO AUTOMATICALLY JOIN A PUBLIC GAME, OR USE THE TERMINAL TO JOIN A SPECIFIC ROOM OR ADJUST YOUR SETTINGS.",
            new Vector3(-8.927885f, 31.000383f, 10.944999f),
            new Quaternion(0.4900174f, -0.5097871f, -0.5097868f, 0.4900178f));
        CreateWelcomeScreen(group.transform, "WallScreenCanyon",
            "WELCOME TO GORILLA TAG!\n\nHEAD DOWN THIS HALLWAY TO AUTOMATICALLY JOIN A PUBLIC GAME, OR USE THE TERMINAL TO JOIN A SPECIFIC ROOM OR ADJUST YOUR SETTINGS.",
            new Vector3(-14.398102f, 29.275732f, 9.935001f),
            new Quaternion(0.6001976f, -0.3738483f, -0.3738483f, 0.6001982f));
        CreateWelcomeScreen(group.transform, "WallScreenSkyJungle",
            "WELCOME TO GORILLA TAG!\n\nHEAD DOWN THIS HALLWAY TO AUTOMATICALLY JOIN A PUBLIC GAME, OR USE THE TERMINAL TO JOIN A SPECIFIC ROOM OR ADJUST YOUR SETTINGS.",
            new Vector3(-13.175907f, 40.127251f, 11.140994f),
            new Quaternion(0.7500103f, -0.6614263f, -0.0000005f, 0.0000015f));

        Debug.Log("[GorillaOGV2][STUMP][WELCOME] Reconstructed all five active August 2023 welcome displays from serialized scene text and transforms.");
    }

    private TMP_Text CreateWelcomeScreen(Transform parent, string name, string content, Vector3 localPos, Quaternion localRot)
    {
        GameObject go = new GameObject(name);
        TextMeshPro tmp = go.AddComponent<TextMeshPro>();
        tmp.text = content;
        RectTransform rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
        // Serialized 2023 GorillaLevelScreen Text: UpperLeft, 188.69x71.57, lineSpacing 1.
        ApplyLegacyUtopiumMetrics(tmp, TextAlignmentOptions.TopLeft, 1f,
            new Vector2(188.693573f, 71.567719f), Color.white);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localRotation = localRot;
        go.transform.localScale = Vector3.one * 0.004f;
        RegisterAuditTarget(name, go.transform, go.transform.position, go.transform.rotation,
            go.transform.lossyScale, parent);
        return tmp;
    }

    private static Transform FindLiveTextTransform(string objectName)
    {
        TMP_Text[] texts = FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None);
        foreach (TMP_Text text in texts)
        {
            if (text != null && text.name == objectName && text.gameObject.scene.IsValid())
            {
                Debug.Log($"[GorillaOGV2][STUMP][TEXT] Resolved {objectName} from runtime parent '{text.transform.parent?.name}'.");
                return text.transform;
            }
        }
        Debug.LogError($"[GorillaOGV2][STUMP][TEXT] Could not resolve live TMP object '{objectName}'.");
        return null;
    }

    private static void ApplyHistoricalRect(Transform child, Transform parent, Vector3 localPosition,
        Quaternion localRotation, Vector3 localScale, Vector2 anchorMin, Vector2 anchorMax,
        Vector2 pivot, Vector2 sizeDelta)
    {
        if (child == null || parent == null) return;
        child.SetParent(parent, false);
        RectTransform rect = child as RectTransform;
        if (rect != null)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.sizeDelta = sizeDelta;
        }
        // Historical local TRS is applied last. No anchoredPosition/offset
        // mutation follows it, so Unity cannot silently recompute placement.
        child.localPosition = localPosition;
        child.localRotation = localRotation;
        child.localScale = localScale;
    }

    private static void SyncLiveText(TMP_Text dest, TMP_Text src)
    {
        if (dest == null || src == null) return;
        if (dest.text != src.text) dest.text = src.text;
    }

    /// <summary>Resolves the Utopium brand font once; every historical text
    /// renderer gets it so restored UI matches the era typeface.</summary>
    internal static void ResolveUtopiumFont()
    {
        if (utopiumFont != null) return;
        foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
        {
            if (f != null && f.name.IndexOf("Utopium", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                utopiumFont = f;
                break;
            }
        }
        Debug.Log(utopiumFont != null
            ? $"[GorillaOGV2][STUMP] Utopium font resolved: {utopiumFont.name}"
            : "[GorillaOGV2][STUMP] Utopium font NOT found at runtime; keeping source fonts.");
    }

    internal static void ApplyUtopium(TMP_Text tmp)
    {
        if (tmp == null || utopiumFont == null) return;
        tmp.font = utopiumFont;
        if (utopiumFont.material != null) tmp.fontSharedMaterial = utopiumFont.material;
    }

    // Every 2023 stump label was a legacy UnityEngine.UI.Text using the bitmap
    // Utopium font at native size (fontSize 0): glyph advance 5 rect units,
    // line advance 12 units x lineSpacing. Source: sharedassets0 Font 2925
    // m_CharacterRects / m_LineSpacing, and each Text's raw m_FontData.
    private const float LegacyGlyphAdvance = 5f;
    private const float LegacyLineAdvance = 12f;
    private static readonly Color LegacyButtonTextColor = new Color(0.196f, 0.196f, 0.196f, 1f);

    /// <summary>Makes a TextMeshPro label reproduce the 2023 legacy Utopium
    /// metrics inside the serialized rect. Self-calibrating: measures the
    /// font's real advance / line height and solves for fontSize and
    /// lineSpacing, so it does not depend on TMP's internal unit constants.</summary>
    internal static void ApplyLegacyUtopiumMetrics(TMP_Text tmp, TextAlignmentOptions alignment, float lineSpacingMul, Vector2 rectSize, Color color)
    {
        if (tmp == null) return;
        ApplyUtopium(tmp);
        tmp.enableAutoSizing = false;
        tmp.fontStyle = FontStyles.Normal;
        tmp.richText = true;
        tmp.textWrappingMode = TextWrappingModes.Normal;
        tmp.overflowMode = TextOverflowModes.Overflow;
        tmp.alignment = alignment;
        tmp.color = color;
        tmp.characterSpacing = 0f;
        tmp.wordSpacing = 0f;
        tmp.paragraphSpacing = 0f;
        tmp.margin = Vector4.zero;
        tmp.rectTransform.sizeDelta = rectSize;

        string content = tmp.text;
        tmp.fontSize = 36f;
        float advance = 0f;
        for (int i = 0; i < 3; i++)
        {
            advance = tmp.GetPreferredValues("AA").x - tmp.GetPreferredValues("A").x;
            if (advance > 1e-4f) tmp.fontSize *= LegacyGlyphAdvance / advance;
        }

        float target = LegacyLineAdvance * lineSpacingMul;
        tmp.lineSpacing = 0f;
        float line0 = tmp.GetPreferredValues("A\nA").y - tmp.GetPreferredValues("A").y;
        tmp.lineSpacing = 10f;
        float line1 = tmp.GetPreferredValues("A\nA").y - tmp.GetPreferredValues("A").y;
        float slope = (line1 - line0) / 10f;
        tmp.lineSpacing = Mathf.Abs(slope) > 1e-6f ? (target - line0) / slope : 0f;
        float lineFinal = tmp.GetPreferredValues("A\nA").y - tmp.GetPreferredValues("A").y;

        tmp.text = content;
        tmp.ForceMeshUpdate(true, true);
        Debug.Log($"[GorillaOGV2][STUMP][TEXTMETRICS] '{tmp.name}': fontSize={tmp.fontSize:F2} advance={advance:F3} (target {LegacyGlyphAdvance}) " +
                  $"lineSpacing={tmp.lineSpacing:F2} lineAdvance={lineFinal:F3} (target {target:F2}) rect={rectSize} align={alignment}");
    }

    private void SetupWardrobeInterface(Transform interactables)
    {
        Transform satWardrobe = interactables.Find("UI/SatelliteWardrobe");
        if (satWardrobe == null)
        {
            Debug.LogError("[GorillaOGV2][STUMP] SatelliteWardrobe not found!");
            return;
        }

        // 1. Reconstruct Wardrobe UI root under Historical UI (transform 81421)
        GameObject wardrobeGo = new GameObject("Wardrobe");
        wardrobeTransform = wardrobeGo.transform;
        PlaceHistorical(wardrobeTransform, historicalUiRoot, historicalUiRoot.localToWorldMatrix,
            new Vector3(-9.636400f, 27.129906f, 9.374200f),
            new Quaternion(0.704697f, -0.058741f, -0.058739f, 0.704629f),
            Vector3.one, "Wardrobe", "UI");

        RegisterAuditTarget("Wardrobe", wardrobeTransform,
            new Vector3(-64.8708f, 11.4258f, -84.0622f),
            new Quaternion(0.0f, -0.0831f, 0.0f, 0.9965f),
            Vector3.one, historicalUiRoot);

        // 2. Reconstruct HeadModels parent under Wardrobe (plain Transform,
        // no renderers — verified in 2023 hierarchy)
        GameObject hmGo = new GameObject("HeadModels");
        headModelsTransform = hmGo.transform;
        PlaceHistorical(headModelsTransform, wardrobeTransform, wardrobeTransform.localToWorldMatrix,
            new Vector3(-0.6332966f, 0.08963989f, 0.01567543f),
            new Quaternion(-0.7058597f, 0.0419760f, 0.0419756f, 0.7058599f),
            Vector3.one, "HeadModels", "Wardrobe");

        RegisterAuditTarget("HeadModels", headModelsTransform,
            new Vector3(-65.4979f, 11.5154f, -84.1516f),
            new Quaternion(-0.7069f, -0.0168f, -0.0168f, 0.7069f),
            Vector3.one, wardrobeTransform);

        // 3. Reparent 4 Mannequin Heads from modern SatelliteWardrobe to HeadModels
        headLeft = satWardrobe.Find("CosmeticSlot/DisplayHead_1");
        headCenter = satWardrobe.Find("CosmeticSlot/DisplayHead_2");
        headRight = satWardrobe.Find("CosmeticSlot/DisplayHead_3");
        headTop = satWardrobe.Find("WornDisplay/SpinningObjects/DisplayHead_Equipped");

        // Disable spinning script on turntable parent
        Transform spinningObjs = satWardrobe.Find("WornDisplay/SpinningObjects");
        if (spinningObjs != null)
        {
            foreach (var mb in spinningObjs.GetComponents<MonoBehaviour>()) mb.enabled = false;
        }

        // Suppress unwanted modern elements
        Transform head4 = satWardrobe.Find("CosmeticSlot/DisplayHead_4");
        if (head4 != null) head4.gameObject.SetActive(false);
        Transform head5 = satWardrobe.Find("CosmeticSlot/DisplayHead_5");
        if (head5 != null) head5.gameObject.SetActive(false);
        Transform modernUi = satWardrobe.Find("UI");
        if (modernUi != null) modernUi.gameObject.SetActive(false);
        Transform colorPicker = satWardrobe.Find("PushSliderColorPicker");
        if (colorPicker != null) colorPicker.gameObject.SetActive(false);
        Transform cameraSpawner = satWardrobe.Find("LCKWallCameraSpawner");
        if (cameraSpawner != null) cameraSpawner.gameObject.SetActive(false);
        Transform lazySuzan = satWardrobe.Find("WornDisplay/LazySusanSpinner");
        if (lazySuzan != null) lazySuzan.gameObject.SetActive(false);

        // Suppress booth mesh renderers and extra colliders, preserving ProximityDetector
        foreach (var mr in satWardrobe.GetComponentsInChildren<MeshRenderer>(true))
        {
            Transform t = mr.transform;
            bool isHead = (headLeft != null && (t == headLeft || t.IsChildOf(headLeft))) ||
                          (headCenter != null && (t == headCenter || t.IsChildOf(headCenter))) ||
                          (headRight != null && (t == headRight || t.IsChildOf(headRight))) ||
                          (headTop != null && (t == headTop || t.IsChildOf(headTop)));
            if (!isHead) mr.enabled = false;
        }
        foreach (var col in satWardrobe.GetComponentsInChildren<Collider>(true))
        {
            Transform t = col.transform;
            bool isHead = (headLeft != null && (t == headLeft || t.IsChildOf(headLeft))) ||
                          (headCenter != null && (t == headCenter || t.IsChildOf(headCenter))) ||
                          (headRight != null && (t == headRight || t.IsChildOf(headRight))) ||
                          (headTop != null && (t == headTop || t.IsChildOf(headTop)));
            bool isProximity = col.name.IndexOf("Proximity", StringComparison.OrdinalIgnoreCase) >= 0 || col.GetComponent<CosmeticWardrobeProximityDetector>() != null;
            if (!isHead && !isProximity) col.enabled = false;
        }

        // Acquire the mannequin head mesh from DisplayHead_Equipped/Head Model.
        // 2023 mannequins used era defaultMat (serialized material 227: neutral
        // gray), never the player-tinted fur materials, so the era material is
        // resolved instead of reusing modern equipped materials.
        Mesh unbatchedMesh = historicalWardrobeHeadMesh;
        Material headMeshMaterial = historicalWardrobeHeadMaterial ?? worldController.GetHistoricalMaterial("sharedassets0.assets:227", false)
            ?? plasticMat ?? buttonUnpressedMat;

        // Reparent and set authoritative local transforms under HeadModels
        Quaternion headFaceRotation = Quaternion.Euler(0f, 180f, 0f);
        if (headLeft != null)
        {
            headLeft.name = "Head Model Left";
            PrepareHistoricalHead(headLeft);
            PlaceHistorical(headLeft, headModelsTransform, headModelsTransform.localToWorldMatrix,
                new Vector3(0.12819999f, 0.10599999f, 0.08659999f),
                headFaceRotation, Vector3.one * 0.25f, "Head Model Left", "HeadModels");
            ApplyUnbatchedHeadMesh(headLeft, unbatchedMesh, headMeshMaterial);

            RegisterAuditTarget("Head Model Left", headLeft,
                new Vector3(-65.3648f, 11.6020f, -84.2514f),
                new Quaternion(-0.7069f, -0.0168f, -0.0168f, 0.7069f) * headFaceRotation,
                Vector3.one * 0.25f, headModelsTransform);
        }

        if (headCenter != null)
        {
            headCenter.name = "Head Model Center";
            PrepareHistoricalHead(headCenter);
            PlaceHistorical(headCenter, headModelsTransform, headModelsTransform.localToWorldMatrix,
                new Vector3(0.00609999f, 0.10599999f, 0.08659887f),
                headFaceRotation, Vector3.one * 0.25f, "Head Model Center", "HeadModels");
            ApplyUnbatchedHeadMesh(headCenter, unbatchedMesh, headMeshMaterial);

            RegisterAuditTarget("Head Model Center", headCenter,
                new Vector3(-65.4868f, 11.6020f, -84.2572f),
                new Quaternion(-0.7069f, -0.0168f, -0.0168f, 0.7069f) * headFaceRotation,
                Vector3.one * 0.25f, headModelsTransform);
        }

        if (headRight != null)
        {
            headRight.name = "Head Model Right";
            PrepareHistoricalHead(headRight);
            PlaceHistorical(headRight, headModelsTransform, headModelsTransform.localToWorldMatrix,
                new Vector3(-0.11339999f, 0.10600485f, 0.08659887f),
                headFaceRotation, Vector3.one * 0.25f, "Head Model Right", "HeadModels");
            ApplyUnbatchedHeadMesh(headRight, unbatchedMesh, headMeshMaterial);

            RegisterAuditTarget("Head Model Right", headRight,
                new Vector3(-65.6062f, 11.6020f, -84.2629f),
                new Quaternion(-0.7069f, -0.0168f, -0.0168f, 0.7069f) * headFaceRotation,
                Vector3.one * 0.25f, headModelsTransform);
        }

        if (headTop != null)
        {
            headTop.name = "Head Model Top";
            PrepareHistoricalHead(headTop);
            PlaceHistorical(headTop, headModelsTransform, headModelsTransform.localToWorldMatrix,
                new Vector3(-0.00200000f, 0.31319999f, 0.27570000f),
                headFaceRotation, Vector3.one * 0.25f, "Head Model Top", "HeadModels");
            ApplyUnbatchedHeadMesh(headTop, unbatchedMesh, headMeshMaterial);

            RegisterAuditTarget("Head Model Top", headTop,
                new Vector3(-65.4851f, 11.7911f, -84.4645f),
                new Quaternion(-0.7069f, -0.0168f, -0.0168f, 0.7069f) * headFaceRotation,
                Vector3.one * 0.25f, headModelsTransform);
        }

        // 4. Reconstruct 9 Buttons under Wardrobe with serialized local transforms and materials
        // 3 Item Buttons
        HistoricalWardrobeItemButton item0 = CreateWardrobeItemButton(wardrobeTransform, 0,
            headLeft?.GetComponentInChildren<HeadModel>(),
            new Vector3(-0.5099596f, 0.1245939f, -0.0206538f),
            new Quaternion(-0.3376551f, 0.0558637f, 0.0200796f, 0.9393962f),
            Vector3.one * 0.083062f);

        HistoricalWardrobeItemButton item1 = CreateWardrobeItemButton(wardrobeTransform, 1,
            headCenter?.GetComponentInChildren<HeadModel>(),
            new Vector3(-0.6271285f, 0.1246364f, 0.0002047f),
            new Quaternion(-0.3414170f, 0.0557826f, 0.0203027f, 0.9380354f),
            Vector3.one * 0.083062f);

        HistoricalWardrobeItemButton item2 = CreateWardrobeItemButton(wardrobeTransform, 2,
            headRight?.GetComponentInChildren<HeadModel>(),
            new Vector3(-0.7413692f, 0.1246778f, 0.0204922f),
            new Quaternion(-0.3414171f, 0.0557824f, 0.0203026f, 0.9380355f),
            Vector3.one * 0.083062f);

        wardrobeItemButtons.Add(item0);
        wardrobeItemButtons.Add(item1);
        wardrobeItemButtons.Add(item2);

        // 2 Page Navigation Buttons
        CreateWardrobeFunctionButton(wardrobeTransform, "left", -1, "<--",
            new Vector3(-0.4104533f, 0.1245594f, -0.0380498f),
            new Quaternion(-0.3376550f, 0.0558637f, 0.0200794f, 0.9393961f),
            Vector3.one * 0.066450f,
            new Vector3(-0.4068251f, 0.1481667f, -0.0128259f),
            new Vector3(-0.001196f, 0.001196f, 0.000332f));

        CreateWardrobeFunctionButton(wardrobeTransform, "right", -1, "-->",
            new Vector3(-0.8624593f, 0.1237209f, 0.0421057f),
            new Quaternion(-0.3376550f, 0.0558637f, 0.0200796f, 0.9393961f),
            Vector3.one * 0.066450f,
            new Vector3(-0.8588448f, 0.1473312f, 0.0673455f),
            new Vector3(-0.001196f, 0.001196f, 0.000332f));

        // 4 Category Buttons
        Vector3 catScale = new Vector3(0.082060f, 0.083062f, 0.084065f);
        Vector3 catTextScale = new Vector3(-0.001477f, 0.001495f, 0.000420f);

        CreateWardrobeFunctionButton(wardrobeTransform, "hat", 0, "HATS",
            new Vector3(-0.1083622f, 0.3054609f, 0.0053206f),
            Quaternion.identity, catScale,
            new Vector3(-0.1076117f, 0.3079414f, 0.0491896f), catTextScale);

        CreateWardrobeFunctionButton(wardrobeTransform, "face", 1, "FACE",
            new Vector3(-0.2585511f, 0.3064129f, 0.0053209f),
            Quaternion.identity, catScale,
            new Vector3(-0.2578030f, 0.3088913f, 0.0492088f), catTextScale);

        CreateWardrobeFunctionButton(wardrobeTransform, "badge", 2, "BADGES",
            new Vector3(-0.1031034f, 0.1409598f, 0.0054130f),
            Quaternion.identity, catScale,
            new Vector3(-0.1023560f, 0.1434364f, 0.0492962f), catTextScale);

        CreateWardrobeFunctionButton(wardrobeTransform, "hand", 3, "HOLDABLES",
            new Vector3(-0.2638410f, 0.1354143f, 0.0056358f),
            Quaternion.identity, catScale,
            new Vector3(-0.2630920f, 0.1378937f, 0.0495258f), catTextScale);

        // 3 Item Button Text Labels (historical Wardrobe/Text local layout)
        Matrix4x4 histWardrobeWorld = wardrobeTransform.localToWorldMatrix;
        Vector3 itemTextScale = new Vector3(-0.001495f, 0.001495f, 0.000415f);
        item0.buttonLabel = CreateButtonText(wardrobeTransform, histWardrobeWorld, "Wardrobe",
            "Text_Item_0", "PUT ON",
            new Vector3(-0.5054379f, 0.1541023f, 0.0108736f),
            new Quaternion(-0.3376550f, 0.0558637f, 0.0200796f, 0.9393961f),
            itemTextScale);

        item1.buttonLabel = CreateButtonText(wardrobeTransform, histWardrobeWorld, "Wardrobe",
            "Text_Item_1", "PUT ON",
            new Vector3(-0.6226301f, 0.1544075f, 0.0315026f),
            new Quaternion(-0.3414170f, 0.0557826f, 0.0203027f, 0.9380355f),
            itemTextScale);

        item2.buttonLabel = CreateButtonText(wardrobeTransform, histWardrobeWorld, "Wardrobe",
            "Text_Item_2", "PUT ON",
            new Vector3(-0.7368641f, 0.1544495f, 0.0517936f),
            new Quaternion(-0.3414172f, 0.0557824f, 0.0203026f, 0.9380354f),
            itemTextScale);

        // 5. 2023 category/page model shared by every wardrobe (see HistoricalWardrobe).
        HistoricalWardrobeView view = wardrobeTransform.gameObject.AddComponent<HistoricalWardrobeView>();
        view.itemButtons = new[] { item0, item1, item2 };
        view.selfDoll = headTop?.GetComponentInChildren<HeadModel>();
        view.Refresh();

        Debug.Log("[GorillaOGV2][STUMP] Historical Wardrobe interface fully reconstructed with 4 mannequins and 9 buttons.");
    }

    /// <summary>
    /// Strips modern-only display children from a reparented mannequin head
    /// BEFORE placement: destroys the "Coming Soon" subtree (whose
    /// comingsoonsign quad is the large white display surface seen in the
    /// headset) and recreates the historical Hat/Badge/Face/Holdable cosmetic
    /// anchors that exist under every 2023 Head Model but are absent modernly.
    /// Never blanket-enables renderers: only the head's own mesh renderer is
    /// kept, and only with a non-null material.
    /// </summary>
    private void PrepareHistoricalHead(Transform head)
    {
        if (head == null) return;

        Transform headModelTr = head.Find("Head Model");
        if (headModelTr != null)
        {
            Transform comingSoon = headModelTr.Find("Coming Soon");
            if (comingSoon != null) Destroy(comingSoon.gameObject);
        }

        string[] anchors = { "Hat", "Badge", "Face", "Holdable" };
        foreach (string anchorName in anchors)
        {
            if (head.Find(anchorName) == null)
            {
                GameObject anchor = new GameObject(anchorName);
                anchor.transform.SetParent(head, false);
                anchor.transform.localPosition = Vector3.zero;
                anchor.transform.localRotation = Quaternion.identity;
                anchor.transform.localScale = Vector3.one;
            }
        }

        // Disable any renderer that has no mesh or no material so a default
        // white surface can never appear; also disable stray renderers that
        // are not the head mesh itself.
        Transform core = headModelTr ?? head;
        foreach (var childMr in head.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (childMr.transform != core) childMr.enabled = false;
        }
    }

    /// <summary>Records the head mesh's pre-reparent world orientation so the
    /// mesh keeps facing exactly as it did in the modern booth after the root
    /// moves to its 2023 parent frame.</summary>
    private static Quaternion? GetMeshWorldRotation(Transform head)
    {
        Transform m = head != null ? head.Find("Head Model") : null;
        if (m != null) return m.rotation;
        return null;
    }

    private static void RestoreMeshOrientation(Transform head, Quaternion? worldRot)
    {
        if (!worldRot.HasValue) return;
        Transform m = head != null ? head.Find("Head Model") : null;
        if (m != null) m.rotation = worldRot.Value;
    }

    private void ApplyUnbatchedHeadMesh(Transform head, Mesh mesh, Material material)
    {
        if (head == null) return;
        // In the 2023 scene the mesh is on the Head Model root itself. Keeping
        // it on a modern nested child applies an extra transform and shrinks it
        // to a colored speck, so suppress the modern nested renderer and place
        // the authoritative mesh on this exact serialized root.
        Transform modernNested = head.Find("Head Model");
        if (modernNested != null)
            foreach (Renderer nestedRenderer in modernNested.GetComponentsInChildren<Renderer>(true)) nestedRenderer.enabled = false;

        MeshFilter mf = head.GetComponent<MeshFilter>() ?? head.gameObject.AddComponent<MeshFilter>();
        if (mesh != null) mf.sharedMesh = mesh;

        MeshRenderer mr = head.GetComponent<MeshRenderer>() ?? head.gameObject.AddComponent<MeshRenderer>();
        if (material != null) mr.sharedMaterial = material;
        if (mr.sharedMaterial == null)
        {
            Material fallback = buttonUnpressedMat != null ? buttonUnpressedMat : plasticMat;
            if (fallback != null) mr.sharedMaterial = fallback;
            else { mr.enabled = false; return; }
        }
        if (mf.sharedMesh == null) { mr.enabled = false; return; }
        mr.enabled = true;

        HeadModel hm = head.GetComponent<HeadModel>() ?? head.GetComponentInChildren<HeadModel>(true);
        if (hm != null)
        {
            Debug.Log($"[GorillaOGV2][WARDROBE] head '{head.name}' HeadModel on '{hm.name}' local pos={hm.transform.localPosition:F4} rot={hm.transform.localRotation:F4} euler={hm.transform.localEulerAngles:F1} scale={hm.transform.localScale:F3} headRot={head.rotation:F4}");
            typeof(HeadModel).GetField("_mannequinRenderer", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(hm, mr);
        }
    }

    private HistoricalWardrobeItemButton CreateWardrobeItemButton(Transform parent, int slot, HeadModel model, Vector3 localPos, Quaternion localRot, Vector3 localScale)
    {
        GameObject btnGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        btnGo.name = "WardrobeItemButton_" + slot;
        PlaceHistorical(btnGo.transform, parent, parent.localToWorldMatrix,
            localPos, localRot, localScale, "WardrobeItemButton_" + slot, parent.name);
        btnGo.layer = 18; // GorillaInteractable

        BoxCollider col = btnGo.GetComponent<BoxCollider>();
        col.isTrigger = true;

        MeshRenderer mr = btnGo.GetComponent<MeshRenderer>();
        Material primaryMat = (buttonUnpressedMat != null) ? buttonUnpressedMat : plasticMat;
        if (primaryMat != null) mr.sharedMaterial = primaryMat;
        else mr.enabled = false; // never show a default white placeholder cube

        HistoricalWardrobeItemButton wb = btnGo.AddComponent<HistoricalWardrobeItemButton>();
        wb.slotIndex = slot;
        wb.controlledModel = model;
        wb.buttonRenderer = mr;
        wb.pressedMaterial = buttonPressedMat;
        wb.unpressedMaterial = (buttonUnpressedMat != null) ? buttonUnpressedMat : plasticMat;
        wb.debounceTime = 0.25f;

        Vector3 expectedWorldPos = (slot == 0) ? new Vector3(-65.3703f, 11.5504f, -84.1670f) :
                                   (slot == 1) ? new Vector3(-65.4893f, 11.5504f, -84.1659f) :
                                                 new Vector3(-65.6053f, 11.5504f, -84.1648f);

        RegisterAuditTarget("WardrobeItemButton_" + slot, btnGo.transform,
            expectedWorldPos,
            new Quaternion(-0.3381f, -0.0224f, -0.0080f, 0.9408f),
            Vector3.one * 0.0831f, parent);

        return wb;
    }

    private void CreateWardrobeFunctionButton(Transform parent, string function, int catIdx, string label, Vector3 localPos, Quaternion localRot, Vector3 localScale, Vector3 textPos, Vector3 textScale)
    {
        GameObject btnGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        btnGo.name = "WardrobeFunctionButton_" + function;
        PlaceHistorical(btnGo.transform, parent, parent.localToWorldMatrix,
            localPos, localRot, localScale, "WardrobeFunctionButton_" + function, parent.name);
        btnGo.layer = 18; // GorillaInteractable

        BoxCollider col = btnGo.GetComponent<BoxCollider>();
        col.isTrigger = true;

        MeshRenderer mr = btnGo.GetComponent<MeshRenderer>();
        Material primaryMat = (buttonUnpressedMat != null) ? buttonUnpressedMat : plasticMat;
        if (primaryMat != null) mr.sharedMaterial = primaryMat;
        else mr.enabled = false; // never show a default white placeholder cube

        HistoricalWardrobeFunctionButton fb = btnGo.AddComponent<HistoricalWardrobeFunctionButton>();
        fb.function = function;
        fb.categoryIndex = catIdx;
        fb.buttonRenderer = mr;
        fb.pressedMaterial = buttonPressedMat;
        fb.unpressedMaterial = (buttonUnpressedMat != null) ? buttonUnpressedMat : plasticMat;
        fb.debounceTime = 0.25f;
        wardrobeFunctionButtons.Add(fb);

        CreateButtonText(parent, parent.localToWorldMatrix, parent.name,
            "Text_Func_" + function, label, textPos, localRot, textScale);
    }

    private TMP_Text CreateButtonText(Transform parent, Matrix4x4 histParentWorld, string histParentName,
        string name, string content, Vector3 localPos, Quaternion localRot, Vector3 localScale)
    {
        GameObject textGo = new GameObject(name);
        TextMeshPro tmp = textGo.AddComponent<TextMeshPro>();
        tmp.text = content;
        if (defaultFont != null)
        {
            tmp.font = defaultFont;
            tmp.fontSharedMaterial = defaultFontMat;
        }

        // Layout first, historical local transform last; the negative x scale
        // on 2023 wardrobe text is intentional board mirroring — preserved.
        RectTransform rt = textGo.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        // Serialized 2023 wardrobe Text: MiddleCenter, 45.93x47.85, RGB 0.196.
        ApplyLegacyUtopiumMetrics(tmp, TextAlignmentOptions.Center, 1f,
            new Vector2(45.931427f, 47.848145f), LegacyButtonTextColor);
        PlaceHistorical(textGo.transform, parent, histParentWorld, localPos, localRot, localScale, name, histParentName);

        return tmp;
    }

    private void SetupGameModeSelector(Transform interactables)
    {
        // 1. Create Selector Buttons under Historical UI (transform 250598)
        GameObject sbGo = new GameObject("Selector Buttons");
        selectorButtonsTransform = sbGo.transform;
        PlaceHistorical(selectorButtonsTransform, historicalUiRoot, historicalUiRoot.localToWorldMatrix,
            new Vector3(-11.173457f, 25.576052f, 9.977058f),
            new Quaternion(0.5150675f, -0.5429628f, -0.4807047f, 0.4569683f),
            Vector3.one * 0.007941465f, "Selector Buttons", "UI");

        // 2. Create anchor under Selector Buttons (transform 250739)
        GameObject anchorGo = new GameObject("anchor");
        modeAnchorTransform = anchorGo.transform;
        PlaceHistorical(modeAnchorTransform, selectorButtonsTransform,
            selectorButtonsTransform.localToWorldMatrix,
            new Vector3(223.6818f, -15.32359f, 259.8955f),
            new Quaternion(0.7277672f, 0.4899634f, -0.1392595f, -0.4592358f),
            Vector3.one * 77.06387f, "anchor", "Selector Buttons");

        // 3. Create ENABLE FOR BETA under anchor (transform 250247)
        GameObject betaGo = new GameObject("ENABLE FOR BETA");
        PlaceHistorical(betaGo.transform, modeAnchorTransform, modeAnchorTransform.localToWorldMatrix,
            Vector3.zero, Quaternion.identity, Vector3.one, "ENABLE FOR BETA", "anchor");

        // 4. Create 4 Mode Buttons under ENABLE FOR BETA
        Quaternion modeBtnRot = new Quaternion(0f, 0f, -0.7071068f, 0.7071067f);
        Vector3 modeBtnScale = Vector3.one * 0.105611f;

        CreateHistoricalModeButton(betaGo.transform, "CASUAL",
            new Vector3(0.1416270f, 0.2900029f, 0.1999955f), modeBtnRot, modeBtnScale,
            new Vector3(-68.3635f, 11.8039f, -80.7650f));

        CreateHistoricalModeButton(betaGo.transform, "INFECTION",
            new Vector3(-0.0040000f, 0.2900000f, 0.2000000f), modeBtnRot, modeBtnScale,
            new Vector3(-68.3304f, 11.7321f, -80.8062f));

        CreateHistoricalModeButton(betaGo.transform, "HUNT",
            new Vector3(-0.1514080f, 0.2900029f, 0.2000055f), modeBtnRot, modeBtnScale,
            new Vector3(-68.2970f, 11.6594f, -80.8478f));

        CreateHistoricalModeButton(betaGo.transform, "BATTLE",
            new Vector3(-0.2880000f, 0.2900000f, 0.2000000f), modeBtnRot, modeBtnScale,
            new Vector3(-68.2659f, 11.5920f, -80.8864f));

        // 5. Texts under anchor
        Quaternion textRot = new Quaternion(0f, 0f, -0.7071071f, 0.7071065f);

        // Current Mode Text
        currentModeTextDisplay = CreateAnchorText(modeAnchorTransform, "Current Mode Text",
            "CURRENT MODE\nCASUAL",
            new Vector3(-0.5737648f, -0.0184708f, 0.2284066f), textRot,
            new Vector3(-0.0063427f, 0.0063427f, 0.0000666f),
            new Vector2(130.61294f, 27.285606f), 1f, TextAlignmentOptions.Center);

        RegisterAuditTarget("Current Mode Text", currentModeTextDisplay.transform,
            new Vector3(-68.3394f, 11.4614f, -81.0963f),
            new Quaternion(0.2940f, -0.3155f, 0.1035f, 0.8963f),
            new Vector3(0.0039f, 0.0039f, 0.0000f), modeAnchorTransform,
            new Vector3(0.505f, 0.592f, -0.628f));

        // Game Mode Title Text
        TMP_Text titleText = CreateAnchorText(modeAnchorTransform, "Game Mode Title Text",
            "GAME MODES",
            new Vector3(0.3009701f, -0.0469055f, 0.2280351f), textRot,
            new Vector3(-0.0145895f, 0.0145895f, 0.0001533f),
            new Vector2(58.731934f, 85.18015f), 1.6f, TextAlignmentOptions.Left);

        RegisterAuditTarget("Game Mode Title Text", titleText.transform,
            new Vector3(-68.5517f, 11.8926f, -80.8599f),
            new Quaternion(0.2940f, -0.3155f, 0.1035f, 0.8963f),
            new Vector3(0.0089f, 0.0089f, -0.0001f), modeAnchorTransform,
            new Vector3(0.505f, 0.592f, -0.628f));

        // Game Mode List Text
        CreateAnchorText(modeAnchorTransform, "Game Mode List Text ENABLE FOR BETA",
            "CASUAL\nINFECTION\nHUNT\nPAINTBRAWL",
            new Vector3(-0.0600061f, -0.1300125f, 0.2279992f), textRot,
            new Vector3(-0.0110812f, 0.0110812f, 0.0001164f),
            new Vector2(58.731934f, 85.18015f), 1.06f, TextAlignmentOptions.Left);

        Debug.Log("[GorillaOGV2][STUMP] Historical Game Mode selector reconstructed under Historical UI.");
    }

    private void CreateHistoricalModeButton(Transform parent, string mode, Vector3 localPos, Quaternion localRot, Vector3 localScale, Vector3 expectedWorldPos)
    {
        GameObject btnGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
        btnGo.name = "ModeButton_" + mode;
        PlaceHistorical(btnGo.transform, parent, parent.localToWorldMatrix,
            localPos, localRot, localScale, "ModeButton_" + mode, parent.name);
        btnGo.layer = 18; // GorillaInteractable

        BoxCollider col = btnGo.GetComponent<BoxCollider>();
        col.isTrigger = true;

        MeshRenderer mr = btnGo.GetComponent<MeshRenderer>();
        Material primaryMat = (buttonUnpressedMat != null) ? buttonUnpressedMat : plasticMat;
        if (primaryMat != null) mr.sharedMaterial = primaryMat;
        else mr.enabled = false; // never show a default white placeholder cube

        HistoricalModeSelectButton mb = btnGo.AddComponent<HistoricalModeSelectButton>();
        mb.modeName = mode;
        mb.buttonRenderer = mr;
        mb.pressedMaterial = buttonPressedMat;
        mb.unpressedMaterial = (buttonUnpressedMat != null) ? buttonUnpressedMat : plasticMat;
        mb.debounceTime = 0.25f;
        modeButtons.Add(mb);

        // 2023 "<Mode> Button Text" children are serialized inactive: no label.

        RegisterAuditTarget("ModeButton_" + mode, btnGo.transform,
            expectedWorldPos,
            new Quaternion(-0.1035f, 0.8963f, 0.2940f, 0.3155f),
            Vector3.one * 0.0646f, parent);
    }

    private TMP_Text CreateAnchorText(Transform parent, string name, string content, Vector3 localPos, Quaternion localRot, Vector3 localScale, Vector2 sizeDelta, float lineSpacingMul, TextAlignmentOptions alignment)
    {
        GameObject textGo = new GameObject(name);
        TextMeshPro tmp = textGo.AddComponent<TextMeshPro>();
        tmp.text = content;
        if (defaultFont != null)
        {
            tmp.font = defaultFont;
            tmp.fontSharedMaterial = defaultFontMat;
        }

        // Layout first, historical local transform last — assigning pivot or
        // anchoredPosition after localPosition would displace the text.
        RectTransform rt = textGo.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        ApplyLegacyUtopiumMetrics(tmp, alignment, lineSpacingMul, sizeDelta, Color.white);
        PlaceHistorical(textGo.transform, parent, parent.localToWorldMatrix,
            localPos, localRot, localScale, name, parent.name);

        return tmp;
    }

    private void RegisterAuditTarget(string name, Transform t, Vector3 expPos, Quaternion expRot, Vector3 expScale, Transform expParent, Vector3? expForward = null)
    {
        if (t == null) return;
        RectTransform rect = t as RectTransform;
        auditTargets.Add(new AuditTarget
        {
            Name = name,
            Target = t,
            ExpectedPos = expPos,
            ExpectedRot = expRot,
            ExpectedScale = expScale,
            ExpectedParent = expParent,
            InitLocalPos = t.localPosition,
            InitLocalRot = t.localRotation,
            InitLocalScale = t.localScale,
            HasRect = rect != null,
            InitAnchorMin = rect != null ? rect.anchorMin : Vector2.zero,
            InitAnchorMax = rect != null ? rect.anchorMax : Vector2.zero,
            InitPivot = rect != null ? rect.pivot : Vector2.zero,
            InitSizeDelta = rect != null ? rect.sizeDelta : Vector2.zero,
            InitAnchoredPosition3D = rect != null ? rect.anchoredPosition3D : Vector3.zero,
            InitOffsetMin = rect != null ? rect.offsetMin : Vector2.zero,
            InitOffsetMax = rect != null ? rect.offsetMax : Vector2.zero,
            ExpectedForward = expForward
        });
    }

    private IEnumerator RunRuntimeTransformAudits()
    {
        yield return new WaitForSeconds(5.0f);
        ExecuteTransformAudit(5);

        yield return new WaitForSeconds(10.0f); // 15s mark
        ExecuteTransformAudit(15);

        yield return new WaitForSeconds(15.0f); // 30s mark
        ExecuteTransformAudit(30);
    }

    private void ExecuteTransformAudit(int seconds)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"==========================================================================================");
        sb.AppendLine($"[GorillaOGV2][STUMP AUDIT] Runtime Transform Audit at {seconds}s post-initialization");
        sb.AppendLine($"==========================================================================================");
        sb.AppendLine(string.Format("{0,-24} | {1,-7} | {2,-7} | {3,-9} | {4,-12} | {5,-25} | {6} | {7}",
            "Object Name", "PosErr", "RotErr", "ScaleErr", "ParentMatch", "Actual World Pos", "Actual Parent", "MovedBy"));
        sb.AppendLine(new string('-', 130));

        int passCount = 0;
        int totalCount = auditTargets.Count;

        foreach (var entry in auditTargets)
        {
            if (entry.Target == null)
            {
                sb.AppendLine(string.Format("{0,-24} | DESTROYED/NULL", entry.Name));
                continue;
            }

            Vector3 actualPos = entry.Target.position;
            Quaternion actualRot = entry.Target.rotation;
            Vector3 actualScale = entry.Target.lossyScale;
            Transform actualParent = entry.Target.parent;

            float posErr = Vector3.Distance(actualPos, entry.ExpectedPos);
            // Sign-insensitive: mirrored chains (negative scale) flip the
            // quaternion sign with identical orientation; q and -q match.
            float rotErr;
            if (entry.ExpectedForward.HasValue)
            {
                rotErr = Vector3.Angle(actualRot * Vector3.forward, entry.ExpectedForward.Value);
            }
            else
            {
                Quaternion negExpected = new Quaternion(-entry.ExpectedRot.x, -entry.ExpectedRot.y, -entry.ExpectedRot.z, -entry.ExpectedRot.w);
                rotErr = Mathf.Min(Quaternion.Angle(actualRot, entry.ExpectedRot), Quaternion.Angle(actualRot, negExpected));
            }
            float scaleErr = Vector3.Distance(actualScale, entry.ExpectedScale);
            bool parentOk = (actualParent == entry.ExpectedParent);

            // Post-restoration drift watchdog: any parent or local-transform
            // change after registration means a modern script moved the object.
            bool parentMoved = actualParent != entry.ExpectedParent;
            bool localMoved = Vector3.Distance(entry.Target.localPosition, entry.InitLocalPos) > 0.0005f
                || Quaternion.Angle(entry.Target.localRotation, entry.InitLocalRot) > 0.05f
                || Vector3.Distance(entry.Target.localScale, entry.InitLocalScale) > 0.0005f;
            string movedBy = (!parentMoved && !localMoved) ? "-"
                : $"MODERN-SCRIPT? parentMoved={parentMoved} localMoved={localMoved}";
            if (parentMoved || localMoved)
                Debug.LogWarning($"[GorillaOGV2][STUMP AUDIT] {entry.Name} moved after restoration at {seconds}s: {movedBy} " +
                    $"parent={(actualParent != null ? actualParent.name : "None")}");

            // Scale tolerance is loose (2cm lossy): mirrored micro-scale text
            // nodes report an anomalous RectTransform z-lossy that is visually
            // irrelevant (glyph size depends on x/y only).
            if (posErr < 0.05f && rotErr < 2.0f && scaleErr < 0.02f && parentOk) passCount++;

            sb.AppendLine(string.Format("{0,-24} | {1,6:F3}m | {2,5:F1}d | {3,7:F4}m | {4,-12} | {5,-25} | {6} | {7}",
                entry.Name,
                posErr,
                rotErr,
                scaleErr,
                parentOk ? "OK" : "CHANGED!",
                $"{actualPos.x:F2},{actualPos.y:F2},{actualPos.z:F2}",
                actualParent != null ? actualParent.name : "None",
                movedBy));
        }

        sb.AppendLine(new string('-', 100));
        sb.AppendLine($"[GorillaOGV2][STUMP AUDIT] Summary: {passCount}/{totalCount} objects within tolerance at {seconds}s.");
        sb.AppendLine("--- Scoped placement evidence (initial restored state -> current state) ---");
        foreach (var entry in auditTargets)
        {
            if (entry.Target == null || !IsScopedPlacementTarget(entry.Name)) continue;
            Transform t = entry.Target;
            sb.AppendLine($"[{entry.Name}] parent='{t.parent?.name}' expectedParent='{entry.ExpectedParent?.name}'");
            sb.AppendLine($"  initialLocal pos={entry.InitLocalPos:F6} rot={entry.InitLocalRot:F6} scale={entry.InitLocalScale:F6}");
            sb.AppendLine($"  currentLocal pos={t.localPosition:F6} rot={t.localRotation:F6} scale={t.localScale:F6}");
            sb.AppendLine($"  localToWorld={FormatMatrix(t.localToWorldMatrix)}");
            if (entry.HasRect && t is RectTransform rect)
            {
                sb.AppendLine($"  initialRect anchorMin={entry.InitAnchorMin:F5} anchorMax={entry.InitAnchorMax:F5} " +
                              $"pivot={entry.InitPivot:F5} sizeDelta={entry.InitSizeDelta:F5} anchored3D={entry.InitAnchoredPosition3D:F5} " +
                              $"offsetMin={entry.InitOffsetMin:F5} offsetMax={entry.InitOffsetMax:F5}");
                sb.AppendLine($"  currentRect anchorMin={rect.anchorMin:F5} anchorMax={rect.anchorMax:F5} " +
                              $"pivot={rect.pivot:F5} sizeDelta={rect.sizeDelta:F5} anchored3D={rect.anchoredPosition3D:F5} " +
                              $"offsetMin={rect.offsetMin:F5} offsetMax={rect.offsetMax:F5}");
            }
            if (entry.Name.StartsWith("KeyVisual_", StringComparison.Ordinal))
            {
                Collider collider = t.parent != null ? t.parent.GetComponent<Collider>() : null;
                Renderer renderer = t.GetComponent<Renderer>();
                if (collider != null && renderer != null)
                    sb.AppendLine($"  bounds rendererCenter={renderer.bounds.center:F6} colliderCenter={collider.bounds.center:F6} " +
                                  $"delta={Vector3.Distance(renderer.bounds.center, collider.bounds.center):F6}m colliderObject='{collider.name}' rendererObject='{renderer.name}'");
            }
        }
        sb.AppendLine($"==========================================================================================");

        string logText = sb.ToString();
        Debug.Log(logText);

        try
        {
            string outDir = Path.Combine(Paths.PluginPath, "GorillaOGV2");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            string logPath = Path.Combine(outDir, "runtime-stump-transform-audit.log");
            File.AppendAllText(logPath, logText + "\n");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GorillaOGV2][STUMP AUDIT] Failed writing log: {ex}");
        }
    }

    private static bool IsScopedPlacementTarget(string name)
    {
        return name.StartsWith("ComputerScreen_", StringComparison.Ordinal)
            || name.StartsWith("KeyVisual_", StringComparison.Ordinal)
            || name == "Key_1" || name == "Key_m" || name == "Key_enterkeyforest"
            || name == "motd" || name == "motdtext"
            || name == "CodeOfConduct" || name == "COC Text";
    }

    private static string FormatMatrix(Matrix4x4 m)
    {
        return $"[{m.m00:F6},{m.m01:F6},{m.m02:F6},{m.m03:F6};" +
               $"{m.m10:F6},{m.m11:F6},{m.m12:F6},{m.m13:F6};" +
               $"{m.m20:F6},{m.m21:F6},{m.m22:F6},{m.m23:F6};" +
               $"{m.m30:F6},{m.m31:F6},{m.m32:F6},{m.m33:F6}]";
    }

    private IEnumerator CaptureProofScreenshots()
    {
        // Wait for geometry and lighting to settle, then again once the
        // network/computer state has had time to change text content.
        foreach (float delay in new[] { 5.5f, 10.5f })
        {
            yield return new WaitForSeconds(delay);
            CaptureStumpSet(delay > 6f ? "-16s" : "");
        }
    }

    internal void CaptureStumpSet(string suffix)
    {
        {
            CaptureViewpoint("stump-rules-proof" + suffix + ".png",
                new Vector3(-65.00f, 11.35f, -79.75f),
                new Vector3(-65.76f, 11.47f, -80.79f));

            CaptureViewpoint("stump-wardrobe-proof" + suffix + ".png",
                new Vector3(-65.5f, 11.65f, -83.1f),
                new Vector3(-65.5f, 11.58f, -84.2f));

            CaptureViewpoint("stump-gamemode-proof" + suffix + ".png",
                new Vector3(-67.73f, 12.17f, -81.85f),
                new Vector3(-68.35f, 11.70f, -80.85f));

            // The 2023 terminal lies flat (screen and keys face up); shoot it
            // from above-front so both render, plus a keyboard close-up.
            CaptureViewpoint("stump-computer-proof" + suffix + ".png",
                new Vector3(-68.50f, 12.70f, -82.60f),
                new Vector3(-68.95f, 11.60f, -83.35f));
            CaptureViewpoint("stump-keyboard-proof" + suffix + ".png",
                new Vector3(-68.25f, 12.25f, -82.65f),
                new Vector3(-68.66f, 11.75f, -82.95f));

            // Fixed legacy viewpoints can be occluded by the curved Stump, so
            // capture both sides along each text plane's own normal.
            CaptureTextPlaneSides("stump-motd-text" + suffix, motdHeadingTransform, 1.35f);
            CaptureTextPlaneSides("stump-rules-text" + suffix, cocHeadingTransform, 1.35f);
            CaptureTextPlaneSides("stump-welcome-text" + suffix, welcomeForestTransform, 1.2f);
            if (welcomeForestTransform != null)
            {
                Vector3 c = welcomeForestTransform.position, n = welcomeForestTransform.forward;
                CaptureViewpoint("stump-welcome-base" + suffix + ".png", c - n * 2.2f + Vector3.up * 0.2f, c - Vector3.up * 0.7f);
            }
        }
    }

    private static void CaptureTextPlaneSides(string stem, Transform textTransform, float distance)
    {
        if (textTransform == null) return;
        TMP_Text text = textTransform.GetComponent<TMP_Text>();
        if (text != null) text.ForceMeshUpdate(true, true);
        Renderer renderer = textTransform.GetComponent<Renderer>();
        Vector3 center = renderer != null ? renderer.bounds.center : textTransform.position;
        Vector3 normal = textTransform.forward.normalized;
        CaptureViewpoint(stem + "-plus.png", center + normal * distance, center);
        CaptureViewpoint(stem + "-minus.png", center - normal * distance, center);
    }

    internal static void CaptureViewpoint(string filename, Vector3 camPos, Vector3 lookAtPos, float farClip = 50f)
    {
        try
        {
            GameObject camGo = new GameObject("GorillaOGV2_StumpProofCam");
            Camera cam = camGo.AddComponent<Camera>();
            cam.cullingMask = ~0; // Render all layers
            cam.nearClipPlane = 0.05f;
            cam.farClipPlane = farClip;
            cam.fieldOfView = 65f;
            cam.clearFlags = CameraClearFlags.Skybox;

            camGo.transform.position = camPos;
            camGo.transform.LookAt(lookAtPos);

            RenderTexture rt = new RenderTexture(1024, 1024, 24);
            cam.targetTexture = rt;
            cam.Render();

            RenderTexture.active = rt;
            Texture2D img = new Texture2D(1024, 1024, TextureFormat.RGB24, false);
            img.ReadPixels(new Rect(0, 0, 1024, 1024), 0, 0);
            img.Apply();

            string outDir = Path.Combine(Paths.PluginPath, "GorillaOGV2");
            if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
            string outPath = Path.Combine(outDir, filename);
            File.WriteAllBytes(outPath, img.EncodeToPNG());
            Debug.Log($"[GorillaOGV2][STUMP] Saved proof screenshot: {outPath}");

            RenderTexture.active = null;
            cam.targetTexture = null;
            Destroy(rt);
            Destroy(img);
            Destroy(camGo);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[GorillaOGV2][STUMP] Failed capturing {filename}: {ex}");
        }
    }

    private static void DisableReparentOnAwake(Transform target)
    {
        if (target == null) return;
        MonoBehaviour[] scripts = target.GetComponents<MonoBehaviour>();
        for (int i = 0; i < scripts.Length; i++)
        {
            MonoBehaviour m = scripts[i];
            if (m != null && m.GetType().Name.Contains("ReparentOnAwake"))
            {
                m.enabled = false;
            }
        }
    }

    // ---------------------------------------------------------------------
    // August 2023 City cosmetic stand (LocalObjects/City/.../WardrobeAnchor/Wardrobe).
    // Same recipe as the Stump wardrobe: 2023 meshes for the stand, buttons and
    // mannequin heads; the modern SatelliteWardrobeOutside heads carry HeadModel so
    // CosmeticsController keeps dressing them. Built once the City scene is active.
    // ---------------------------------------------------------------------
    private bool cityWardrobeBuilt;

    internal bool TryBuildCityWardrobe()
    {
        if (cityWardrobeBuilt || !initialized) return cityWardrobeBuilt;
        GameObject hut = GameObject.Find("City_Pretty/CosmeticsRoomAnchor/outsidestores_prefab/Bottom Layer/OutsideBuildings/Wardrobe Hut/SatelliteWardrobeOutside");
        if (hut == null) return false;
        cityWardrobeBuilt = true;
        try { BuildCityWardrobe(hut.transform); }
        catch (Exception ex) { Debug.LogError("[GorillaOGV2][CITY] wardrobe failed: " + ex); }
        return true;
    }

    private void BuildCityWardrobe(Transform satWardrobe)
    {
        Transform cityRoot = worldController.ZoneRoot("city").transform;
        GameObject wardrobeGo = new GameObject("August 2023 City Wardrobe");
        Transform wardrobe = wardrobeGo.transform;
        wardrobe.SetParent(cityRoot, false);
        wardrobe.position = new Vector3(-54.643912f, 16.5174428f, -97.5304336f);
        wardrobe.rotation = new Quaternion(0f, -0.5225069f, 0f, 0.8526351f);

        // 2023 meshes.
        Mesh standMesh = null, buttonMesh = null, headMesh = null; int standMat = 193, buttonMat = 254, headMat = 227;
        int standLightmap = -1; Vector4 standLmst = Vector4.zero; Vector3 standPos = Vector3.zero, standScale = Vector3.one; Quaternion standRot = Quaternion.identity;
        var buttonPoses = new Dictionary<string, (Vector3 pos, Quaternion rot, Vector3 scale)>();
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GorillaOGV2.legacy-city-props.bin"))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            reader.ReadBytes(9); uint count = reader.ReadUInt32();
            for (int i = 0; i < count; i++)
            {
                string rel = ReadString(reader);
                Vector3 pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                Quaternion rot = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                Vector3 scale = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                reader.ReadBoolean();
                Mesh mesh = ReadMesh(reader, out _);
                uint matCount = reader.ReadUInt32(); int[] mats = new int[matCount];
                for (int m = 0; m < matCount; m++) mats[m] = reader.ReadInt32();
                int lm = reader.ReadInt32(); Vector4 lmst = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                if (rel.StartsWith("wardrobelarge")) { standMesh = mesh; standMat = mats[0]; standLightmap = lm; standLmst = lmst; standPos = pos; standRot = rot; standScale = scale; }
                else if (rel.StartsWith("Buttons[0]/")) { buttonMesh ??= mesh; buttonMat = mats[0]; buttonPoses[rel.Substring(11).Replace("[0]", "")] = (pos, rot, scale); }
                else if (rel.StartsWith("models")) { headMesh = mesh; headMat = mats[0]; }
            }
        }

        // Stand.
        GameObject stand = new GameObject("August 2023 Prop | wardrobelarge");
        stand.transform.SetParent(wardrobe, false);
        stand.transform.position = standPos; stand.transform.rotation = standRot; stand.transform.localScale = standScale;
        stand.AddComponent<MeshFilter>().sharedMesh = standMesh;
        MeshRenderer standMr = stand.AddComponent<MeshRenderer>();
        bool standLit = standLightmap != 65535 && standLightmap >= 0;
        standMr.sharedMaterial = worldController.GetHistoricalMaterial($"sharedassets0.assets:{standMat}", standLit);
        if (standLit) { standMr.lightmapIndex = LegacyLighting.LightmapIndex; standMr.lightmapScaleOffset = standLmst; }
        stand.AddComponent<MeshCollider>().sharedMesh = standMesh;
        stand.layer = 9;
        stand.AddComponent<GorillaSurfaceOverride>().overrideIndex = 0;

        // Mannequins: modern DisplayHeads at the four 2023 'Head Model Nonstatic' poses (scale 1).
        Material headMaterial = worldController.GetHistoricalMaterial($"sharedassets0.assets:{headMat}", false) ?? historicalWardrobeHeadMaterial;
        (string modern, Vector3 pos, Quaternion rot)[] heads =
        {
            ("CosmeticSlot/DisplayHead_1", new Vector3(-54.7105719f, 16.1504427f, -97.5335067f), new Quaternion(0.1594139f, 0.6889029f, 0.6889028f, -0.1594143f)),
            ("CosmeticSlot/DisplayHead_2", new Vector3(-54.2904056f, 16.1508419f, -97.7397272f), new Quaternion(0.1594139f, 0.6889029f, 0.6889028f, -0.1594143f)),
            ("CosmeticSlot/DisplayHead_3", new Vector3(-53.7953845f, 16.1508422f, -97.9840846f), new Quaternion(0.1594139f, 0.6889029f, 0.6889028f, -0.1594143f)),
            ("WornDisplay/SpinningObjects/DisplayHead_Equipped", new Vector3(-53.8460558f, 16.1384434f, -99.0065087f), new Quaternion(-0.6127792f, -0.3528478f, -0.3528475f, 0.6127795f)),
        };
        Transform spinning = satWardrobe.Find("WornDisplay/SpinningObjects");
        if (spinning != null) foreach (MonoBehaviour mb in spinning.GetComponents<MonoBehaviour>()) mb.enabled = false;
        foreach (string off in new[] { "CosmeticSlot/DisplayHead_4", "CosmeticSlot/DisplayHead_5", "UI", "PushSliderColorPicker", "LCKWallCameraSpawner", "WornDisplay/LazySusanSpinner" })
        {
            Transform t = satWardrobe.Find(off); if (t != null) t.gameObject.SetActive(false);
        }
        Transform[] headTransforms = new Transform[4];
        for (int i = 0; i < heads.Length; i++)
        {
            Transform head = satWardrobe.Find(heads[i].modern);
            if (head == null) { Debug.LogWarning($"[GorillaOGV2][CITY] modern {heads[i].modern} missing"); continue; }
            PrepareHistoricalHead(head);
            head.SetParent(wardrobe, true);
            head.position = heads[i].pos; head.rotation = heads[i].rot; head.localScale = Vector3.one;
            head.name = "Head Model Nonstatic " + i;
            ApplyUnbatchedHeadMesh(head, headMesh, headMaterial);
            headTransforms[i] = head;
        }
        foreach (MeshRenderer mr in satWardrobe.GetComponentsInChildren<MeshRenderer>(true)) mr.enabled = false;
        foreach (Collider col in satWardrobe.GetComponentsInChildren<Collider>(true))
        {
            bool proximity = col.name.IndexOf("Proximity", StringComparison.OrdinalIgnoreCase) >= 0 || col.GetComponent<CosmeticWardrobeProximityDetector>() != null;
            if (!proximity) col.enabled = false;
        }

        // Buttons (2023 meshes at their serialized poses) + labels (serialized Wardrobe_ButtonText_*).
        HistoricalWardrobeItemButton MakeItem(string key, int slot, Transform head)
        {
            var pose = buttonPoses[key];
            GameObject go = CityButtonObject("WardrobeItemButton_" + slot, buttonMesh, buttonMat, pose.pos, pose.rot, pose.scale, wardrobe);
            HistoricalWardrobeItemButton wb = go.AddComponent<HistoricalWardrobeItemButton>();
            wb.slotIndex = slot; wb.controlledModel = head != null ? head.GetComponentInChildren<HeadModel>() : null;
            wb.buttonRenderer = go.GetComponent<MeshRenderer>(); wb.pressedMaterial = buttonPressedMat;
            wb.unpressedMaterial = buttonUnpressedMat != null ? buttonUnpressedMat : plasticMat; wb.debounceTime = 0.25f;
            wardrobeItemButtons.Add(wb);
            return wb;
        }
        void MakeFunction(string key, string function, int cat)
        {
            var pose = buttonPoses[key];
            GameObject go = CityButtonObject("WardrobeFunctionButton_" + function, buttonMesh, buttonMat, pose.pos, pose.rot, pose.scale, wardrobe);
            HistoricalWardrobeFunctionButton fb = go.AddComponent<HistoricalWardrobeFunctionButton>();
            fb.function = function; fb.categoryIndex = cat; fb.buttonRenderer = go.GetComponent<MeshRenderer>();
            fb.pressedMaterial = buttonPressedMat; fb.unpressedMaterial = buttonUnpressedMat != null ? buttonUnpressedMat : plasticMat; fb.debounceTime = 0.25f;
            wardrobeFunctionButtons.Add(fb);
        }
        TMP_Text Label(string name, string text, Vector3 pos, Quaternion rot)
        {
            GameObject go = new GameObject("Wardrobe_ButtonText_" + name);
            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            tmp.text = text;
            ApplyLegacyUtopiumMetrics(tmp, TextAlignmentOptions.Center, 1f, new Vector2(45.931427f, 47.848145f), LegacyButtonTextColor);
            go.transform.SetParent(wardrobe, false);
            go.transform.position = pos; go.transform.rotation = rot; go.transform.localScale = new Vector3(0.001495f, 0.001495f, -0.000415f);
            return tmp;
        }
        Quaternion itemRot = new Quaternion(0.26802f, 0.22439f, -0.06434f, 0.9347f);
        HistoricalWardrobeItemButton i0 = MakeItem("WardrobeItemButton", 0, headTransforms[0]);
        HistoricalWardrobeItemButton i1 = MakeItem("WardrobeItemButton (1)", 1, headTransforms[1]);
        HistoricalWardrobeItemButton i2 = MakeItem("WardrobeItemButton (2)", 2, headTransforms[2]);
        i0.buttonLabel = Label("01", "PUT ON", new Vector3(-54.8099f, 16.2205f, -97.7202f), itemRot);
        i1.buttonLabel = Label("02", "PUT ON", new Vector3(-54.3591f, 16.2209f, -97.9499f), itemRot);
        i2.buttonLabel = Label("03", "PUT ON", new Vector3(-53.8833f, 16.2205f, -98.1923f), itemRot);
        MakeFunction("WardrobeLeftButton", "left", -1); Label("04", "<--", new Vector3(-55.1022f, 16.2209f, -97.5713f), itemRot);
        MakeFunction("WardrobeRightItem", "right", -1); Label("05", "-->", new Vector3(-54.9828f, 16.2209f, -97.6321f), itemRot);
        MakeFunction("WardobeHatButton", "hat", 0); Label("Hats", "HATS", new Vector3(-55.2647f, 16.5569f, -97.4188f), new Quaternion(0f, 0.23344f, 0f, 0.97237f));
        MakeFunction("WardrobeFaceButton", "face", 1); Label("Face", "FACE", new Vector3(-55.3803f, 16.4539f, -97.352f), new Quaternion(0f, 0.23805f, 0f, 0.97125f));
        MakeFunction("WardrobeBadgeButton", "badge", 2); Label("Badges", "BADGES", new Vector3(-55.252f, 16.3599f, -97.4151f), new Quaternion(0f, 0.23834f, 0f, 0.97118f));
        MakeFunction("WardrobeHoldableButton", "hand", 3); Label("Holdables", "HOLDABLES", new Vector3(-55.3839f, 16.2569f, -97.3457f), new Quaternion(0f, 0.23834f, 0f, 0.97118f));

        HistoricalWardrobeView view = wardrobeGo.AddComponent<HistoricalWardrobeView>();
        view.itemButtons = new[] { i0, i1, i2 };
        view.selfDoll = headTransforms[3] != null ? headTransforms[3].GetComponentInChildren<HeadModel>() : null;
        view.Refresh();
        Debug.Log("[GorillaOGV2][CITY] August 2023 City wardrobe built: stand, 4 mannequins, 9 buttons, 9 labels.");
    }

    private GameObject CityButtonObject(string name, Mesh mesh, int matId, Vector3 pos, Quaternion rot, Vector3 scale, Transform parent)
    {
        GameObject go = new GameObject(name) { layer = 18 };
        go.transform.SetParent(parent, false);
        go.transform.position = pos; go.transform.rotation = rot; go.transform.localScale = scale;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        MeshRenderer mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = buttonUnpressedMat != null ? buttonUnpressedMat : (worldController.GetHistoricalMaterial($"sharedassets0.assets:{matId}", false) ?? plasticMat);
        BoxCollider col = go.AddComponent<BoxCollider>();
        col.center = mesh.bounds.center; col.size = mesh.bounds.size; col.isTrigger = true;
        return go;
    }


    private string lastRawModeText;

    private void LateUpdate()
    {
        if (!initialized) return;

        // Keep computer aligned against any late room respawns. Thresholded so
        // per-frame forcing can never fight physics or modern scripts; any
        // correction above threshold is logged for the audit trail.
        if (computerTransform != null)
        {
            float rootDrift = Vector3.Distance(computerTransform.position, ComputerWorldPos);
            float rootTwist = Quaternion.Angle(computerTransform.rotation, ComputerWorldRot);
            float rootScale = Vector3.Distance(computerTransform.localScale, Vector3.one);
            if (rootDrift > 0.001f || rootTwist > 0.05f || rootScale > 0.001f)
            {
                Debug.Log($"[GorillaOGV2][STUMP] Correcting computer root drift " +
                    $"dPos={rootDrift:F4}m dRot={rootTwist:F3}d dScale={rootScale:F5}.");
                computerTransform.position = ComputerWorldPos;
                computerTransform.rotation = ComputerWorldRot;
                computerTransform.localScale = Vector3.one;
            }
        }

        // Live update Current Mode display text
        if (currentModeTextDisplay != null && GorillaComputer.instance != null && GorillaComputer.instance.currentGameModeText != null)
        {
            // 2023 serialized text: "CURRENT MODE\n-NOT IN ROOM-"; current builds drop the newline.
            string raw = GorillaComputer.instance.currentGameModeText.Value;
            if (!string.IsNullOrEmpty(raw) && !ReferenceEquals(raw, lastRawModeText))
            {
                lastRawModeText = raw;
                string liveText = raw.Replace("MODE-", "MODE\n-");
                if (currentModeTextDisplay.text != liveText) currentModeTextDisplay.text = liveText;
            }
        }

        // Live sync: historical-layout MOTD/Rules renderers mirror modern content.
        SyncLiveText(motdDisplay, motdLiveSource);
        SyncLiveText(motdBodyDisplay, motdBodyLiveSource);
        SyncLiveText(cocDisplay, cocLiveSource);
        SyncLiveText(cocBodyDisplay, cocBodyLiveSource);
    }

    internal static Mesh ReadMesh(BinaryReader reader, out string meshName)
    {
        meshName = ReadString(reader);
        Vector3[] verts = ReadVector3(reader);
        Vector3[] norms = ReadVector3(reader);
        Vector4[] tangs = ReadVector4(reader);
        Vector2[] uv0 = ReadVector2(reader);
        Vector2[] uv1 = ReadVector2(reader);
        uint submeshCount = reader.ReadUInt32();
        int[][] submeshIndices = new int[submeshCount][];
        for (int s = 0; s < submeshCount; s++)
        {
            uint idxCount = reader.ReadUInt32();
            submeshIndices[s] = new int[idxCount];
            for (int k = 0; k < idxCount; k++) submeshIndices[s][k] = reader.ReadInt32();
        }

        Mesh mesh = new Mesh { name = meshName, indexFormat = IndexFormat.UInt32 };
        mesh.vertices = verts;
        if (norms.Length > 0) mesh.normals = norms;
        if (tangs.Length > 0) mesh.tangents = tangs;
        if (uv0.Length > 0) mesh.uv = uv0;
        if (uv1.Length > 0) mesh.uv2 = uv1;
        mesh.subMeshCount = (int)submeshCount;
        for (int s = 0; s < submeshCount; s++) mesh.SetTriangles(submeshIndices[s], s);
        mesh.RecalculateBounds();
        return mesh;
    }

    internal static string ReadString(BinaryReader r)
    {
        int len = r.ReadInt32();
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    private static Vector3[] ReadVector3(BinaryReader r)
    {
        int count = r.ReadInt32();
        Vector3[] arr = new Vector3[count];
        for (int i = 0; i < count; i++) arr[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return arr;
    }

    private static Vector4[] ReadVector4(BinaryReader r)
    {
        int count = r.ReadInt32();
        Vector4[] arr = new Vector4[count];
        for (int i = 0; i < count; i++) arr[i] = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return arr;
    }

    private static Vector2[] ReadVector2(BinaryReader r)
    {
        int count = r.ReadInt32();
        Vector2[] arr = new Vector2[count];
        for (int i = 0; i < count; i++) arr[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
        return arr;
    }

    private static Matrix4x4 ReadMatrix(BinaryReader r)
    {
        Matrix4x4 m = default;
        for (int i = 0; i < 16; i++) m[i] = r.ReadSingle();
        return m;
    }
}

/// <summary>
/// Historical mode selection button forwarding presses to GorillaComputer.OnModeSelectButtonPress.
/// </summary>
internal sealed class HistoricalModeSelectButton : GorillaPressableButton
{
    public string modeName;

    public override void ButtonActivationWithHand(bool isLeftHand)
    {
        base.ButtonActivationWithHand(isLeftHand);
        if (GorillaComputer.instance != null)
        {
            GorillaComputer.instance.OnModeSelectButtonPress(modeName, isLeftHand);
        }
    }

    private void Update()
    {
        if (GorillaComputer.instance == null || GorillaComputer.instance.currentGameMode == null) return;
        bool active = string.Equals(GorillaComputer.instance.currentGameMode.Value, modeName, StringComparison.OrdinalIgnoreCase);
        if (isOn != active)
        {
            isOn = active;
            UpdateColor();
        }
    }
}

/// <summary>
/// Historical wardrobe item button equipped preview and toggle forwarding to CosmeticsController.
/// </summary>
internal sealed class HistoricalWardrobeItemButton : WardrobeItemButton
{
    public int slotIndex;
    public TMP_Text buttonLabel;

    public override void ButtonActivationWithHand(bool isLeftHand)
    {
        base.ButtonActivationWithHand(isLeftHand);
        if (CosmeticsController.instance != null && !currentCosmeticItem.isNullItem && currentCosmeticItem.itemName != "null" && !string.IsNullOrEmpty(currentCosmeticItem.itemName))
        {
            CosmeticsController.instance.PressWardrobeItemButton(currentCosmeticItem, isLeftHand, false);
            HistoricalWardrobe.NotifyChanged();
        }
    }

    public void RefreshLabelAndState()
    {
        if (CosmeticsController.instance == null) return;

        bool hasItem = !currentCosmeticItem.isNullItem && currentCosmeticItem.itemName != "null" && !string.IsNullOrEmpty(currentCosmeticItem.itemName);
        bool equipped = hasItem && CosmeticsController.instance.IsCosmeticEquipped(currentCosmeticItem);

        if (isOn != equipped)
        {
            isOn = equipped;
            UpdateColor();
        }

        if (buttonLabel != null)
        {
            if (equipped) buttonLabel.text = "TAKE OFF";
            else if (hasItem) buttonLabel.text = "PUT ON";
            else buttonLabel.text = "";
        }
    }
}

/// <summary>
/// Historical wardrobe function button handling page navigation and category switching.
/// </summary>
internal sealed class HistoricalWardrobeFunctionButton : WardrobeFunctionButton
{
    public int categoryIndex = -1; // 0=hat, 1=face, 2=badge, 3=hand

    // Bypasses WardrobeFunctionButton.ButtonActivation, which would drive the modern 11-category state.
    public override void ButtonActivation()
    {
        HistoricalWardrobe.Press(function);
        StartCoroutine(Flash());
    }

    public override void ButtonActivationWithHand(bool isLeftHand) => ButtonActivation();

    private System.Collections.IEnumerator Flash()
    {
        if (buttonRenderer != null) buttonRenderer.material = pressedMaterial;
        yield return new WaitForSeconds(buttonFadeTime);
        if (buttonRenderer != null) buttonRenderer.material = (categoryIndex >= 0 && HistoricalWardrobe.Category == categoryIndex) ? pressedMaterial : unpressedMaterial;
    }

    private void Update()
    {
        if (categoryIndex >= 0)
        {
            bool active = HistoricalWardrobe.Category == categoryIndex;
            if (isOn != active)
            {
                isOn = active;
                if (buttonRenderer != null) buttonRenderer.material = active ? pressedMaterial : unpressedMaterial;
            }
        }
    }
}
