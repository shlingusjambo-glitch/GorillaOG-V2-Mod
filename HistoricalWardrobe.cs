using System;
using System.Collections.Generic;
using System.Reflection;
using GorillaNetworking;
using TMPro;
using UnityEngine;

namespace GorillaOGV2;

/// <summary>
/// August 2023 wardrobe model: four categories (HATS / FACE / BADGES / HOLDABLES), three items per
/// page, one shared page state for every wardrobe in the world. Modern categories that did not exist
/// in 2023 are folded into the 2023 slot they would have shipped under: shirts, chest and back items
/// under BADGES; throwables and arm items under HOLDABLES. Fur, pants and tag effects have no 2023
/// counterpart and are not listed. Wardrobes are NOT registered with CosmeticsController, whose
/// UpdateWardrobeModelsAndButtons would overwrite the heads with the modern 11-category lists.
/// </summary>
internal static class HistoricalWardrobe
{
    internal static int Category;
    internal static readonly int[] Pages = new int[4];
    internal static event Action Changed;
    private static readonly List<CosmeticsController.CosmeticItem> scratch = new List<CosmeticsController.CosmeticItem>(256);

    internal static List<CosmeticsController.CosmeticItem> Items(int category)
    {
        scratch.Clear();
        CosmeticsController c = CosmeticsController.instance;
        if (c == null) return scratch;
        switch (category)
        {
            case 0: scratch.AddRange(c.unlockedHats); break;
            case 1: scratch.AddRange(c.unlockedFaces); break;
            case 2: scratch.AddRange(c.unlockedBadges); scratch.AddRange(c.unlockedShirts); scratch.AddRange(c.unlockedChests); scratch.AddRange(c.unlockedBacks); break;
            default: scratch.AddRange(c.unlockedPaws); scratch.AddRange(c.unlockedThrowables); scratch.AddRange(c.unlockedArms); break;
        }
        scratch.RemoveAll(it => !HistoricalCosmeticsGate.IsAllowed(it.itemName));
        return scratch;
    }

    internal static void Press(string function)
    {
        int count = Items(Category).Count;
        int lastPage = Math.Max(0, (count - 1) / 3);
        switch (function)
        {
            case "left": Pages[Category] = Pages[Category] <= 0 ? lastPage : Pages[Category] - 1; break;
            case "right": Pages[Category] = Pages[Category] >= lastPage ? 0 : Pages[Category] + 1; break;
            case "hat": Category = 0; break;
            case "face": Category = 1; break;
            case "badge": Category = 2; break;
            case "hand": Category = 3; break;
        }
        Changed?.Invoke();
    }

    internal static void NotifyChanged() => Changed?.Invoke();
}

/// <summary>One physical wardrobe (Stump or City): three item buttons + heads, the worn doll, labels.</summary>
internal sealed class HistoricalWardrobeView : MonoBehaviour
{
    internal HistoricalWardrobeItemButton[] itemButtons;
    internal HeadModel selfDoll;
    private bool subscribed;

    private void OnEnable()
    {
        if (!subscribed)
        {
            HistoricalWardrobe.Changed += Refresh;
            if (CosmeticsController.instance != null) CosmeticsController.instance.OnCosmeticsUpdated += Refresh;
            subscribed = true;
        }
        Refresh();
    }

    private void OnDestroy()
    {
        HistoricalWardrobe.Changed -= Refresh;
        if (CosmeticsController.instance != null) CosmeticsController.instance.OnCosmeticsUpdated -= Refresh;
    }

    internal void Refresh()
    {
        CosmeticsController c = CosmeticsController.instance;
        if (c == null || itemButtons == null) return;
        List<CosmeticsController.CosmeticItem> items = HistoricalWardrobe.Items(HistoricalWardrobe.Category);
        int page = HistoricalWardrobe.Pages[HistoricalWardrobe.Category];
        for (int i = 0; i < itemButtons.Length; i++)
        {
            HistoricalWardrobeItemButton b = itemButtons[i];
            if (b == null) continue;
            int index = page * 3 + i;
            CosmeticsController.CosmeticItem item = index < items.Count ? items[index] : c.nullItem;
            bool changed = b.currentCosmeticItem.itemName != item.itemName;
            b.currentCosmeticItem = item;
            if (b.controlledModel != null)
            {
                if (string.IsNullOrEmpty(item.itemName) || item.itemName == "null" || item.isNullItem)
                    b.controlledModel.SetCosmeticActive("NOTHING");
                else
                    b.controlledModel.SetCosmeticActive(item.itemName);
            }
            b.RefreshLabelAndState();
        }
        if (selfDoll != null)
        {
            string[] wornNames = new string[c.currentWornSet.items.Length];
            for (int j = 0; j < wornNames.Length; j++)
            {
                var it = c.currentWornSet.items[j];
                wornNames[j] = (!it.isNullItem && !string.IsNullOrEmpty(it.itemName) && it.itemName != "null") ? it.itemName : "NOTHING";
            }
            selfDoll.SetCosmeticActiveArray(wornNames, c.currentWornSet.ToOnRightSideArray());
        }
    }
}
