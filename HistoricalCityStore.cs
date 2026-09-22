using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using CosmeticRoom;
using GorillaNetworking;
using TMPro;
using UnityEngine;

namespace GorillaOGV2;

/// <summary>
/// August 2023 City store: 95 cosmetic stands, the self-checkout (12 cart slots, 2 purchase buttons,
/// live head model), the fitting room (12 try-on slots) and the ATM, rebuilt at their serialized
/// 2023 poses from legacy-city-store.tsv and driven by the modern CosmeticsController. An item is
/// purchasable exactly when the live PlayFab catalog still sells it (price present), which is how
/// the modern game gates rotating vs retired cosmetics; retired items keep their 2023 stand but no
/// price and an inert button.
/// </summary>
internal sealed class HistoricalCityStore : MonoBehaviour
{
    private HistoricalWorldController world;
    private readonly List<CosmeticStand> stands = new List<CosmeticStand>();
    private readonly Dictionary<CosmeticStand, TextMeshPro> priceLabels = new Dictionary<CosmeticStand, TextMeshPro>();
    private Material pressedMat, unpressedMat;

    internal void Initialize(HistoricalWorldController w) { world = w; StartCoroutine(Build()); }

    private IEnumerator Build()
    {
        while (CosmeticsController.instance == null || !HistoricalCityUi.Built) yield return new WaitForSeconds(0.5f);
        try { BuildNow(); }
        catch (Exception ex) { Debug.LogError("[GorillaOGV2][STORE] build failed: " + ex); yield break; }
        // Catalog prices arrive asynchronously from PlayFab; bind stands once they exist.
        while (!CosmeticsController.instance.v2_isCosmeticPlayFabCatalogDataLoaded) yield return new WaitForSeconds(1f);
        ResolveStands();
    }

    private static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

    private void BuildNow()
    {
        Transform cityRoot = world.ZoneRoot("city").transform;
        unpressedMat = world.GetHistoricalMaterial("sharedassets0.assets:254", false);
        pressedMat = world.GetHistoricalMaterial("sharedassets0.assets:256", false);
        if (pressedMat == null && unpressedMat != null)
        {
            // 2023 material 256 "pressed": untextured, _Color (1,0,0). Nothing baked in the world uses it.
            pressedMat = new Material(unpressedMat) { name = "August 2023 | pressed", mainTexture = null, color = Color.red };
            if (pressedMat.HasProperty("_BaseColor")) pressedMat.SetColor("_BaseColor", Color.red);
            if (pressedMat.HasProperty("_BaseMap")) pressedMat.SetTexture("_BaseMap", null);
        }

        Dictionary<string, (Mesh mesh, int mat)> meshes = new Dictionary<string, (Mesh, int)>();
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GorillaOGV2.legacy-city-store.bin"))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            reader.ReadBytes(9); uint count = reader.ReadUInt32();
            for (int i = 0; i < count; i++)
            {
                string rel = HistoricalStumpEnvironment.ReadString(reader);
                for (int k = 0; k < 10; k++) reader.ReadSingle();
                reader.ReadBoolean();
                Mesh mesh = HistoricalStumpEnvironment.ReadMesh(reader, out _);
                uint matCount = reader.ReadUInt32(); int mat = 254;
                for (int m = 0; m < matCount; m++) { int id = reader.ReadInt32(); if (m == 0) mat = id; }
                reader.ReadInt32(); for (int k = 0; k < 4; k++) reader.ReadSingle();
                meshes[rel] = (mesh, mat);
            }
        }

        string[] lines;
        using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("GorillaOGV2.legacy-city-store.tsv"))
        using (StreamReader reader = new StreamReader(stream)) lines = reader.ReadToEnd().Split('\n');

        List<CheckoutCartButton> cartButtons = new List<CheckoutCartButton>();
        List<FittingRoomButton> fittingButtons = new List<FittingRoomButton>();
        PurchaseItemButton leftPurchase = null, rightPurchase = null;
        HeadModel checkoutHead = null;
        HistoricalAtm atm = new GameObject("August 2023 Store | ATM").AddComponent<HistoricalAtm>();
        atm.transform.SetParent(cityRoot, false);

        foreach (string line in lines)
        {
            if (line.Length == 0) continue;
            string[] f = line.Split('\t');
            if (f[0] == "head")
            {
                GameObject headGo = new GameObject("August 2023 Store | Checkout HeadModel");
                headGo.transform.SetParent(cityRoot, false);
                headGo.transform.position = new Vector3(F(f[3]), F(f[4]), F(f[5]));
                headGo.transform.rotation = new Quaternion(F(f[6]), F(f[7]), F(f[8]), F(f[9]));
                headGo.transform.localScale = new Vector3(F(f[10]), F(f[11]), F(f[12]));
                // The 2023 head mesh is baked into the world package; HeadModel only needs a renderer to toggle.
                headGo.AddComponent<MeshFilter>(); headGo.AddComponent<MeshRenderer>().enabled = false;
                checkoutHead = headGo.AddComponent<HeadModel>();
                continue;
            }
            if (f[0] != "button") continue;
            string kind = f[1], name = f[2], extra = f[3];
            GameObject go = new GameObject($"August 2023 Store | {kind} {name}") { layer = 18 };
            go.transform.SetParent(cityRoot, false);
            go.transform.position = new Vector3(F(f[5]), F(f[6]), F(f[7]));
            go.transform.rotation = new Quaternion(F(f[8]), F(f[9]), F(f[10]), F(f[11]));
            go.transform.localScale = new Vector3(F(f[12]), F(f[13]), F(f[14]));
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.center = new Vector3(F(f[15]), F(f[16]), F(f[17])); box.size = new Vector3(F(f[18]), F(f[19]), F(f[20])); box.isTrigger = true;

            MeshRenderer buttonRenderer = BuildButtonVisual(go.transform, f[21], meshes,
                new Vector3(F(f[22]), F(f[23]), F(f[24])), new Quaternion(F(f[25]), F(f[26]), F(f[27]), F(f[28])), new Vector3(F(f[29]), F(f[30]), F(f[31])));
            TextMeshPro label = HistoricalCityUi.TextsById.TryGetValue(long.Parse(f[32]), out TextMeshPro t1) ? t1 : null;
            TextMeshPro price = HistoricalCityUi.TextsById.TryGetValue(long.Parse(f[33]), out TextMeshPro t2) ? t2 : null;
            // The 2023 labels sit exactly on the button face; the live face is 3 % larger, so lift them 3 mm outward.
            foreach (TextMeshPro lbl in new[] { label, price })
            {
                if (lbl == null) continue;
                Vector3 d = lbl.transform.position - go.transform.position;
                if (d.sqrMagnitude < 0.01f && d.sqrMagnitude > 1e-8f) lbl.transform.position += d.normalized * 0.003f;
            }

            GorillaPressableButton button;
            switch (kind)
            {
                case "CosmeticStand":
                {
                    CosmeticStand stand = go.AddComponent<CosmeticStand>();
                    stand.thisCosmeticName = extra; stand.offText = "ADD TO\nCART"; stand.onText = "REMOVE\nFROM CART";
                    stands.Add(stand); priceLabels[stand] = price; button = stand;
                    break;
                }
                case "CheckoutCartButton":
                {
                    CheckoutCartButton b = go.AddComponent<CheckoutCartButton>();
                    b.offText = "SELECT"; b.onText = "DESELECT"; b.noCosmeticText = ""; cartButtons.Add(b); button = b;
                    GameObject spriteGo = new GameObject("CosmeticSprite");
                    spriteGo.transform.SetParent(go.transform, false);
                    spriteGo.transform.localPosition = new Vector3(0f, 0.05f, 0f);
                    spriteGo.transform.localScale = Vector3.one * 0.15f;
                    SpriteRenderer sr = spriteGo.AddComponent<SpriteRenderer>();
                    typeof(CheckoutCartButton).GetField("currentCosmeticSprite", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(b, sr);
                    break;
                }
                case "PurchaseItemButton":
                {
                    PurchaseItemButton b = go.AddComponent<PurchaseItemButton>();
                    b.buttonSide = extra; b.offText = "-"; b.onText = "-";
                    if (extra == "left") leftPurchase = b; else rightPurchase = b; button = b;
                    break;
                }
                case "FittingRoomButton":
                {
                    FittingRoomButton b = go.AddComponent<FittingRoomButton>();
                    b.offText = "TRY ON"; b.onText = "TAKE OFF"; b.noCosmeticText = "N/A"; fittingButtons.Add(b); button = b;
                    GameObject spriteGo = new GameObject("CosmeticSprite");
                    spriteGo.transform.SetParent(go.transform, false);
                    spriteGo.transform.localPosition = new Vector3(0f, 0.05f, 0f);
                    spriteGo.transform.localScale = Vector3.one * 0.15f;
                    SpriteRenderer sr = spriteGo.AddComponent<SpriteRenderer>();
                    typeof(FittingRoomButton).GetField("currentCosmeticSprite", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(b, sr);
                    break;
                }
                default: // PurchaseCurrencyButton one..four
                {
                    HistoricalAtmButton b = go.AddComponent<HistoricalAtmButton>();
                    b.size = extra; b.atm = atm; button = b;
                    break;
                }
            }
            button.buttonRenderer = buttonRenderer; button.pressedMaterial = pressedMat; button.unpressedMaterial = unpressedMat;
            button.myTmpText = label; button.debounceTime = 0.25f;
            button.UpdateColor();
        }

        // Self checkout + fitting room registered through the modern classes (OnEnable/OnDisable follow the city root).
        GameObject checkoutGo = new GameObject("August 2023 Store | Checkout"); checkoutGo.SetActive(false); checkoutGo.transform.SetParent(cityRoot, false);
        ItemCheckout checkout = checkoutGo.AddComponent<ItemCheckout>();
        checkout.checkoutCartButtons = cartButtons.ToArray(); checkout.leftPurchaseButton = leftPurchase; checkout.rightPurchaseButton = rightPurchase;
        checkout.checkoutHeadModel = checkoutHead; checkout.addOnEnable = true;
        checkout.purchaseTextTMP = HistoricalCityUi.TextsByName.TryGetValue("CheckoutRegister_PurchaseText", out TextMeshPro pt) ? pt : null;
        checkoutGo.SetActive(true);

        GameObject fittingGo = new GameObject("August 2023 Store | Fitting Room"); fittingGo.SetActive(false); fittingGo.transform.SetParent(cityRoot, false);
        FittingRoom fitting = fittingGo.AddComponent<FittingRoom>();
        fitting.fittingRoomButtons = fittingButtons.ToArray(); fitting.addOnEnable = true;
        fittingGo.SetActive(true);

        atm.Bind(HistoricalCityUi.TextsByName.TryGetValue("Atm Screen", out TextMeshPro sc) ? sc : null,
                 HistoricalCityUi.TextsByName.TryGetValue("Atm Buttons Text", out TextMeshPro bt) ? bt : null,
                 HistoricalCityUi.TextsByName.TryGetValue("Currency Board Text", out TextMeshPro cb) ? cb : null,
                 HistoricalCityUi.TextsByName.TryGetValue("Daily Rocks Text", out TextMeshPro dr) ? dr : null);

        GameObject mirrorGo = new GameObject("August 2023 Store | Mirror"); mirrorGo.transform.SetParent(cityRoot, false);
        mirrorGo.AddComponent<HistoricalCityMirror>().Build(world);

        if (Plugin.DebugBuild)
        {
            Debug.Log($"[GorillaOGV2][STORE] materials pressed={(pressedMat != null ? pressedMat.name : "null")} unpressed={(unpressedMat != null ? unpressedMat.name : "null")} meshes={meshes.Count}");
            foreach (CheckoutCartButton b in cartButtons.GetRange(0, Math.Min(2, cartButtons.Count)))
            {
                MeshRenderer r = b.buttonRenderer; MeshFilter mf = r != null ? r.GetComponent<MeshFilter>() : null;
                Debug.Log($"[GorillaOGV2][STORE] cart button '{b.name}' pos={b.transform.position:F4} visual pos={(r != null ? r.transform.position.ToString("F4") : "-")} lossy={(r != null ? r.transform.lossyScale.ToString("F4") : "-")} mesh={(mf != null && mf.sharedMesh != null ? mf.sharedMesh.vertexCount.ToString() : "null")} bounds={(r != null ? r.bounds.size.ToString("F4") : "-")} mat={(r != null && r.sharedMaterial != null ? r.sharedMaterial.name : "null")} enabled={(r != null && r.enabled)}");
            }
        }
        Debug.Log($"[GorillaOGV2][STORE] built {stands.Count} stands, {cartButtons.Count} cart slots, {fittingButtons.Count} fitting slots, purchase L/R={(leftPurchase != null)}/{(rightPurchase != null)}, head={(checkoutHead != null)}, purchaseText={(checkout.purchaseTextTMP != null)}");
    }

    // The 2023 button face is also baked into the static world package; the live copy sits 3% larger
    // around its own centre so the pressed/unpressed material always wins the depth test.
    private MeshRenderer BuildButtonVisual(Transform parent, string meshKey, Dictionary<string, (Mesh mesh, int mat)> meshes, Vector3 pos, Quaternion rot, Vector3 scale)
    {
        GameObject vis;
        Mesh mesh;
        int mat = 254;
        if (meshKey == "cube")
        {
            vis = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Destroy(vis.GetComponent<Collider>());
            mesh = vis.GetComponent<MeshFilter>().sharedMesh;
        }
        else
        {
            vis = new GameObject("visual");
            if (!meshes.TryGetValue(meshKey, out (Mesh mesh, int mat) rec)) { vis.AddComponent<MeshFilter>(); return vis.AddComponent<MeshRenderer>(); }
            mesh = rec.mesh; mat = rec.mat;
            vis.AddComponent<MeshFilter>().sharedMesh = mesh;
            vis.AddComponent<MeshRenderer>();
        }
        vis.name = "August 2023 Store Button Face";
        vis.layer = 18;
        // World pose first, then parent (keeping world) so the button's own scale is not applied twice.
        vis.transform.position = pos; vis.transform.rotation = rot; vis.transform.localScale = scale;
        Vector3 center = vis.transform.TransformPoint(mesh.bounds.center);
        vis.transform.localScale = scale * 1.03f;
        vis.transform.position = center + (pos - center) * 1.03f;
        vis.transform.SetParent(parent, true);
        MeshRenderer mr = vis.GetComponent<MeshRenderer>();
        mr.sharedMaterial = unpressedMat ?? world.GetHistoricalMaterial($"sharedassets0.assets:{mat}", false);
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return mr;
    }

    private void ResolveStands()
    {
        CosmeticsController c = CosmeticsController.instance;
        int sold = 0; List<string> retired = new List<string>();
        foreach (CosmeticStand stand in stands)
        {
            string n = stand.thisCosmeticName;
            int index = c.allCosmetics.FindIndex(x => n == x.displayName || n == x.overrideDisplayName || n == x.itemName);
            CosmeticsController.CosmeticItem item = index >= 0 ? c.allCosmetics[index] : c.nullItem;
            bool forSale = index >= 0 && item.canTryOn && item.cost > 0;
            TextMeshPro price = priceLabels[stand];
            if (forSale)
            {
                stand.thisCosmeticItem = item;
                if (price != null) price.text = item.itemCategory.ToString().ToUpper() + " " + item.cost; // 2023 CosmeticStand.InitializeCosmetic
                stand.enabled = true; stand.UpdateColor(); sold++;
            }
            else
            {
                stand.thisCosmeticItem = c.nullItem;
                if (price != null) price.text = "";
                if (stand.myTmpText != null) stand.myTmpText.text = "";
                stand.enabled = false; // retired / limited item: the modern catalog no longer sells it
                retired.Add(n);
            }
        }
        if (Plugin.DebugBuild)
        {
            foreach (string n in new[] { "CheckoutCartButtonText01", "ButtonText_FruitHut", "SlotPrice_FruitHut", "Atm Buttons Text" })
            {
                if (!HistoricalCityUi.TextsByName.TryGetValue(n, out TextMeshPro t) || t == null) { Debug.Log($"[GorillaOGV2][STORE] label {n}: missing"); continue; }
                Renderer r = t.GetComponent<Renderer>();
                t.ForceMeshUpdate();
                Debug.Log($"[GorillaOGV2][STORE] label {n}: text='{t.text.Replace("\n", "|")}' pos={t.transform.position:F4} fwd={t.transform.forward:F3} lossy={t.transform.lossyScale:F6} rect={t.rectTransform.sizeDelta} font={t.fontSize:F1} active={t.isActiveAndEnabled} rEnabled={(r != null && r.enabled)} bounds={t.bounds.size:F4} color={t.color} mat={(t.fontSharedMaterial != null ? t.fontSharedMaterial.name : "null")}");
            }
        }
        Debug.Log($"[GorillaOGV2][STORE] catalog bound: {sold} stands for sale, {retired.Count} retired: {string.Join(", ", retired)}");
    }
}

/// <summary>2023 ATM: four buttons, the 2023 screen/button texts and state machine; the actual Steam
/// purchase and balance come from the modern CosmeticsController / ATM_Manager.</summary>
internal sealed class HistoricalAtm : MonoBehaviour
{
    private enum Stage { Unavailable, Begin, Menu, Balance, Choose, Confirm, Purchasing, Success, Failure, Locked }
    private Stage stage = Stage.Begin;
    private TextMeshPro screen, buttons, currencyBoard, daily;
    private int rocks; private float cost; private float nextBoard;

    internal void Bind(TextMeshPro screenText, TextMeshPro buttonsText, TextMeshPro board, TextMeshPro dailyText)
    {
        screen = screenText; buttons = buttonsText; currencyBoard = board; daily = dailyText;
        SwitchTo(Stage.Begin);
    }

    internal void Press(string size)
    {
        CosmeticsController c = CosmeticsController.instance;
        switch (stage)
        {
            case Stage.Begin: SwitchTo(Stage.Menu); break;
            case Stage.Menu:
                if (size == "one") SwitchTo(Stage.Balance); else if (size == "two") SwitchTo(Stage.Choose); else if (size == "four") SwitchTo(Stage.Begin);
                break;
            case Stage.Balance: if (size == "four") SwitchTo(Stage.Menu); break;
            case Stage.Choose:
                if (size == "one") Choose(1000, 4.99f, "1000SHINYROCKS");
                else if (size == "two") Choose(2200, 9.99f, "2200SHINYROCKS");
                else if (size == "three") Choose(5000, 19.99f, "5000SHINYROCKS");
                else if (size == "four") SwitchTo(Stage.Menu);
                break;
            case Stage.Confirm:
                if (size == "one" && c != null && ATM_Manager.instance != null)
                {
                    c.itemToPurchase = rocks + "SHINYROCKS"; c.buyingBundle = false;
                    ATM_Manager.instance.numShinyRocksToBuy = rocks; ATM_Manager.instance.shinyRocksCost = cost;
                    ATM_Manager.instance.SwitchToStage(ATM_Manager.ATMStages.Purchasing);
                    c.SteamPurchase();
                    SwitchTo(Stage.Purchasing);
                }
                else if (size == "four") SwitchTo(Stage.Choose);
                break;
            case Stage.Unavailable: case Stage.Purchasing: break;
            default: SwitchTo(Stage.Menu); break;
        }
    }

    private void Choose(int amount, float price, string id) { rocks = amount; cost = price; SwitchTo(Stage.Confirm); }

    // Texts verbatim from the August 2023 CosmeticsController.SwitchToStage.
    private void SwitchTo(Stage s)
    {
        stage = s;
        int balance = CosmeticsController.instance != null ? CosmeticsController.instance.currencyBalance : 0;
        string a = "", b = "";
        switch (s)
        {
            case Stage.Unavailable: a = "ATM NOT AVAILABLE! PLEASE TRY AGAIN LATER!"; break;
            case Stage.Begin: a = "WELCOME! PRESS ANY BUTTON TO BEGIN."; b = "\n\n\n\n\n\n\n\n\nBEGIN   -->"; break;
            case Stage.Menu: a = "CHECK YOUR BALANCE OR PURCHASE MORE SHINY ROCKS."; b = "BALANCE-- >\n\n\nPURCHASE-->\n\n\n\n\n\nBACK    -->"; break;
            case Stage.Balance: a = "CURRENT BALANCE:\n\n" + balance; b = "\n\n\n\n\n\n\n\n\nBACK    -->"; break;
            case Stage.Choose: a = "CHOOSE AN AMOUNT OF SHINY ROCKS TO PURCHASE."; b = "$4.99 FOR -->\n1000\n\n$9.99 FOR -->\n2200\n\n$19.99 FOR-->\n5000\n\nBACK -->"; break;
            case Stage.Confirm: a = "YOU HAVE CHOSEN TO PURCHASE " + rocks + " SHINY ROCKS FOR $" + cost + ". CONFIRM TO LAUNCH A STEAM WINDOW TO COMPLETE YOUR PURCHASE."; b = "CONFIRM -->\n\n\n\n\n\n\n\n\nBACK    -->"; break;
            case Stage.Purchasing: a = "PURCHASING IN STEAM..."; break;
            case Stage.Success: a = "SUCCESS! NEW SHINY ROCKS BALANCE: " + (balance + rocks); b = "\n\n\n\n\n\n\n\n\nRETURN  -->"; break;
            case Stage.Failure: a = "PURCHASE CANCELED. NO FUNDS WERE SPENT."; b = "\n\n\n\n\n\n\n\n\nRETURN  -->"; break;
            case Stage.Locked: a = "UNABLE TO PURCHASE AT THIS TIME. PLEASE RESTART THE GAME OR TRY AGAIN LATER."; b = "\n\n\n\n\n\n\n\n\nRETURN  -->"; break;
        }
        if (screen != null) screen.text = a;
        if (buttons != null) buttons.text = b;
    }

    private void Update()
    {
        if (stage == Stage.Purchasing && ATM_Manager.instance != null)
        {
            ATM_Manager.ATMStages m = ATM_Manager.instance.CurrentATMStage;
            if (m == ATM_Manager.ATMStages.Success) SwitchTo(Stage.Success);
            else if (m == ATM_Manager.ATMStages.Failure) SwitchTo(Stage.Failure);
        }
        if (Time.unscaledTime < nextBoard) return;
        nextBoard = Time.unscaledTime + 1f;
        CosmeticsController c = CosmeticsController.instance;
        if (c == null) return;
        // 2023 UpdateCurrencyBoard.
        if (daily != null) daily.text = !c.checkedDaily ? "CHECKING DAILY ROCKS..." : (c.gotMyDaily ? "SUCCESSFULLY GOT DAILY ROCKS!" : "WAITING TO GET DAILY ROCKS...");
        if (currencyBoard != null) currencyBoard.text = c.currencyBalance + "\n\n" + c.secondsUntilTomorrow / 3600 + " HR, " + c.secondsUntilTomorrow % 3600 / 60 + "MIN";
    }
}

internal sealed class HistoricalAtmButton : GorillaPressableButton
{
    public string size; public HistoricalAtm atm;
    public override void ButtonActivation()
    {
        base.ButtonActivation();
        atm?.Press(size);
        StartCoroutine(Flash());
    }
    private IEnumerator Flash()
    {
        if (buttonRenderer != null) buttonRenderer.sharedMaterial = pressedMaterial;
        yield return new WaitForSeconds(0.25f);
        if (buttonRenderer != null) buttonRenderer.sharedMaterial = unpressedMaterial;
    }
}
