using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using EFT.Visual;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace EFTNewCamera;

internal static class BodyVisibility
{
    internal static bool ShowHead;
    internal static bool ShowHeadwear = true;
    internal static bool ShowFaceCover = true;
    internal static bool ShowEyewear = true;

    private readonly struct RendererState
    {
        internal readonly Renderer Renderer;
        internal readonly bool Enabled;
        internal readonly ShadowCastingMode Shadows;

        internal RendererState(Renderer renderer)
        {
            Renderer = renderer;
            Enabled = renderer.enabled;
            Shadows = renderer.shadowCastingMode;
        }
    }

    private static readonly Dictionary<int, RendererState> OriginalRenderers = new();
    private static readonly Dictionary<int, Renderer[]> EquipmentRenderers = new();
    private static Player? _trackedPlayer;
    private static int _nextCachePruneFrame;
    private static bool _installed;

    internal static void Install()
    {
        if (_installed)
            return;

        Harmony harmony = new Harmony(NewCameraPlugin.PluginGuid + ".body");
        MethodInfo? updateRenders = AccessTools.Method(typeof(PlayerBody), nameof(PlayerBody.UpdatePlayerRenders));
        MethodInfo? updatePostfix = typeof(BodyVisibility).GetMethod(nameof(UpdatePlayerRendersPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (updateRenders != null && updatePostfix != null)
            harmony.Patch(updateRenders, postfix: new HarmonyMethod(updatePostfix) { priority = Priority.Last });

        MethodInfo? parentModel = AccessTools.Method(typeof(PlayerBody.SlotView), "CreateAndParent", new[] { typeof(PlayerBody), typeof(GameObject) });
        MethodInfo? parentPostfix = typeof(BodyVisibility).GetMethod(nameof(ParentEquipmentModelPostfix), BindingFlags.NonPublic | BindingFlags.Static);
        if (parentModel != null && parentPostfix != null)
            harmony.Patch(parentModel, postfix: new HarmonyMethod(parentPostfix) { priority = Priority.Last });

        _installed = true;
    }

    internal static void Apply(Player player)
    {
        if (!NewCameraSwitch.Enabled)
            return;

        PlayerBody? body = player.PlayerBody;
        if (body == null || !player.IsYourPlayer)
            return;

        if (!ReferenceEquals(_trackedPlayer, player))
        {
            Restore();
            _trackedPlayer = player;
        }

        Prune();
        SetHead(body, ShowHead);
        foreach (EquipmentSlot slot in PlayerBody.SlotNames)
        {
            if (body.SlotViews.TryGetByKey(slot, out PlayerBody.SlotView? view) && view != null)
                ApplySlot(player, view);
        }
    }

    internal static void Restore()
    {
        foreach (RendererState state in OriginalRenderers.Values)
        {
            if (state.Renderer == null)
                continue;

            state.Renderer.enabled = state.Enabled;
            state.Renderer.shadowCastingMode = state.Shadows;
        }

        OriginalRenderers.Clear();
        EquipmentRenderers.Clear();
        DeviceRenderers.Clear();
        _trackedPlayer = null;
        _nextCachePruneFrame = 0;
    }

    private static void UpdatePlayerRendersPostfix(PlayerBody __instance)
    {
        if (!NewCameraSwitch.Enabled)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || !ReferenceEquals(player.PlayerBody, __instance))
            return;

        Apply(player);
    }

    private static void ParentEquipmentModelPostfix(PlayerBody.SlotView __instance)
    {
        if (!NewCameraSwitch.Enabled)
            return;

        Player? player = Singleton<GameWorld>.Instance?.MainPlayer;
        if (player == null || __instance._playerBody == null || !ReferenceEquals(player.PlayerBody, __instance._playerBody))
            return;

        ApplySlot(player, __instance);
    }

    private static void SetHead(PlayerBody body, bool visible)
    {
        if (!body.BodySkins.TryGetValue(EBodyModelPart.Head, out LoddedSkin? skin) || skin == null)
            return;

        IEnumerable<Renderer> renderers = skin.GetRenderers();
        Snapshot(renderers);
        if (!skin.gameObject.activeSelf)
            skin.gameObject.SetActive(true);

        skin.EnableRenderers(true);
        skin.SetShadowCastingMode(visible ? ShadowCastingMode.On : ShadowCastingMode.ShadowsOnly);
        if (visible)
        {
            foreach (Renderer renderer in renderers)
            {
                if (renderer is SkinnedMeshRenderer skinned)
                    skinned.updateWhenOffscreen = true;
            }
        }

        int playerLayer = LayerMask.NameToLayer("Player");
        Camera? camera = Camera.main;
        bool cameraSeesPlayer = playerLayer < 0 || camera == null || (camera.cullingMask & (1 << playerLayer)) != 0;
        if (visible && !cameraSeesPlayer)
            skin.SetLayer(0);
        else if (!visible && playerLayer >= 0)
            skin.SetLayer(playerLayer);
    }

    private static void ApplySlot(Player player, PlayerBody.SlotView slotView)
    {
        if (slotView.EquipmentSlot != EquipmentSlot.Headwear
            && slotView.EquipmentSlot != EquipmentSlot.FaceCover
            && slotView.EquipmentSlot != EquipmentSlot.Eyewear)
            return;

        Snapshot(slotView.Renderers);
        Renderer[] renderers = SlotRenderers(slotView);
        Snapshot(renderers);
        if (!ShownSlot(slotView.EquipmentSlot))
        {
            Hide(slotView.Renderers);
            Hide(renderers);
            return;
        }

        Show(slotView.Renderers);
        Show(renderers);
        HideActiveDevices(player, slotView);
    }

    private static bool ShownSlot(EquipmentSlot slot)
    {
        switch (slot)
        {
            case EquipmentSlot.Headwear:
                return ShowHeadwear;
            case EquipmentSlot.FaceCover:
                return ShowFaceCover;
            case EquipmentSlot.Eyewear:
                return ShowEyewear;
            default:
                return true;
        }
    }

    private static Renderer[] SlotRenderers(PlayerBody.SlotView slotView)
    {
        if (slotView.Model == null)
            return Array.Empty<Renderer>();

        int modelId = slotView.Model.GetInstanceID();
        if (!EquipmentRenderers.TryGetValue(modelId, out Renderer[] renderers) || Destroyed(renderers))
        {
            renderers = slotView.Model.GetComponentsInChildren<Renderer>(true);
            EquipmentRenderers[modelId] = renderers;
        }

        return renderers;
    }

    private static readonly FieldInfo? CameraManagerInstance = AccessTools.Field(typeof(CameraManager), "instance");
    private static Type? _nightRendererType;
    private static PropertyInfo? _nightEnabled;
    private static PropertyInfo? _opticReady;
    private static MethodInfo? _usesCustomPipeline;
    private static bool _nightResolved;
    private static readonly object?[] PipelineArgument = new object?[1];

    // A mounted night or thermal device stays visible until its own filter is actually on.
    private static void HideActiveDevices(Player player, PlayerBody.SlotView slotView)
    {
        GameObject? model = slotView.Model;
        CameraManager? camera = CameraManagerInstance?.GetValue(null) as CameraManager;
        if (model == null || camera == null)
            return;

        bool nightOn = NightFilterVisible(camera.NightVision);
        bool thermalOn = camera.ThermalVision != null && camera.ThermalVision.On;
        if (!nightOn && !thermalOn)
            return;

        // This runs every frame. The search walks the whole model and the inventory, so its result is kept
        // until the filter state or the model changes.
        int modelId = model.GetInstanceID();
        if (!DeviceRenderers.TryGetValue(modelId, out DeviceHide cached) || cached.Night != nightOn
            || cached.Thermal != thermalOn || Destroyed(cached.Renderers))
        {
            cached = new DeviceHide { Night = nightOn, Thermal = thermalOn, Renderers = FindDevices(player, slotView, model, nightOn, thermalOn) };
            DeviceRenderers[modelId] = cached;
        }

        Hide(cached.Renderers);
    }

    private struct DeviceHide
    {
        internal bool Night;
        internal bool Thermal;
        internal Renderer[] Renderers;
    }

    private static readonly Dictionary<int, DeviceHide> DeviceRenderers = new();

    private static Renderer[] FindDevices(Player player, PlayerBody.SlotView slotView, GameObject model, bool nightOn, bool thermalOn)
    {
        List<Renderer> found = new();
        Transform root = model.transform;
        if (nightOn)
        {
            foreach (NightVisionDevice device in model.GetComponentsInChildren<NightVisionDevice>(true))
                HideBranch(device.transform, root, found);
        }

        if (thermalOn)
        {
            foreach (ThermalVisionDevice device in model.GetComponentsInChildren<ThermalVisionDevice>(true))
                HideBranch(device.transform, root, found);
        }

        Item? worn = player.InventoryController?.Inventory?.Equipment?.GetSlot(slotView.EquipmentSlot)?.ContainedItem;
        if (worn != null)
        {
            List<Item> carriers = new();
            CollectCarriers(worn, worn, nightOn, thermalOn, carriers);
            foreach (Item carrier in carriers)
                HideNamed(root, PrefabKey(carrier), found);
        }

        Snapshot(found);
        return found.ToArray();
    }

    private static bool NightFilterVisible(BSG.CameraEffects.NightVision? nightVision)
    {
        if (nightVision == null)
            return false;

        if (!UsesNightPipeline(nightVision))
            return nightVision.On;

        EnsureNightTypes();
        if (_nightRendererType == null)
            return false;

        Component renderer = nightVision.GetComponent(_nightRendererType);
        if (renderer is not Behaviour behaviour || !behaviour.isActiveAndEnabled)
            return false;

        if (_nightEnabled?.GetValue(renderer) is not true)
            return false;

        return _opticReady == null || _opticReady.GetValue(renderer) is true;
    }

    private static bool UsesNightPipeline(BSG.CameraEffects.NightVision nightVision)
    {
        EnsureNightTypes();
        if (_usesCustomPipeline == null)
            return false;
        PipelineArgument[0] = nightVision;
        return _usesCustomPipeline.Invoke(null, PipelineArgument) is true;
    }

    private static void EnsureNightTypes()
    {
        if (_nightResolved)
            return;

        _nightResolved = true;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(assembly.GetName().Name, "BorkelRNVG", StringComparison.Ordinal))
                continue;

            _nightRendererType = assembly.GetType("BorkelRNVG.Controllers.RealisticNightVisionRenderer");
            Type? helper = assembly.GetType("BorkelRNVG.Helpers.NvgHelper");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            _nightEnabled = _nightRendererType?.GetProperty("NightVisionEnabled", flags);
            _opticReady = _nightRendererType?.GetProperty("OpticReady", flags);
            _usesCustomPipeline = helper?.GetMethod("UsesCustomPipeline", BindingFlags.Public | BindingFlags.Static);
            return;
        }
    }

    private static void CollectCarriers(Item worn, Item item, bool nightOn, bool thermalOn, List<Item> carriers)
    {
        bool nightDevice = item.GetItemComponent<NightVisionComponent>() != null;
        bool thermalDevice = item.GetItemComponent<ThermalVisionComponent>() != null;
        if ((nightOn && nightDevice) || (thermalOn && thermalDevice))
        {
            carriers.Add(item);
            Item? parent = item.Parent?.Container?.ParentItem;
            while (parent != null && !ReferenceEquals(parent, worn))
            {
                carriers.Add(parent);
                parent = parent.Parent?.Container?.ParentItem;
            }
        }

        if (item is not CompoundItem compound)
            return;

        foreach (Slot slot in compound.AllSlots)
        {
            Item? child = slot.ContainedItem;
            if (child != null)
                CollectCarriers(worn, child, nightOn, thermalOn, carriers);
        }
    }

    private static string PrefabKey(Item item)
    {
        ResourceKey? prefab = item.Prefab;
        if (prefab == null || prefab.IsEmpty())
            return string.Empty;

        string? path = prefab.path;
        if (path == null || path.Length == 0)
            return string.Empty;

        string? file = prefab.FileName;
        return file ?? string.Empty;
    }

    private static void HideNamed(Transform root, string prefabKey, List<Renderer> found)
    {
        if (prefabKey.Length == 0)
            return;

        foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
        {
            if (transform.name.IndexOf(prefabKey, StringComparison.OrdinalIgnoreCase) >= 0)
                HideBranch(transform, root, found);
        }
    }

    private static void HideBranch(Transform device, Transform root, List<Renderer> found)
    {
        Transform top = device;
        Transform? cursor = device;
        while (cursor.parent != null && cursor.parent != root)
        {
            cursor = cursor.parent;
            top = cursor;
        }

        found.AddRange(top == root
            ? device.GetComponentsInChildren<Renderer>(true)
            : top.GetComponentsInChildren<Renderer>(true));
    }

    private static void Prune()
    {
        if (Time.frameCount < _nextCachePruneFrame)
            return;

        _nextCachePruneFrame = Time.frameCount + 600;
        List<int>? dead = null;
        foreach (KeyValuePair<int, RendererState> pair in OriginalRenderers)
        {
            if (pair.Value.Renderer != null)
                continue;

            dead ??= new List<int>();
            dead.Add(pair.Key);
        }

        if (dead != null)
        {
            foreach (int key in dead)
                OriginalRenderers.Remove(key);
        }

        dead = null;
        foreach (KeyValuePair<int, Renderer[]> pair in EquipmentRenderers)
        {
            if (!Destroyed(pair.Value))
                continue;

            dead ??= new List<int>();
            dead.Add(pair.Key);
        }

        if (dead != null)
        {
            foreach (int key in dead)
                EquipmentRenderers.Remove(key);
        }

        dead = null;
        foreach (KeyValuePair<int, DeviceHide> pair in DeviceRenderers)
        {
            if (!Destroyed(pair.Value.Renderers))
                continue;

            dead ??= new List<int>();
            dead.Add(pair.Key);
        }

        if (dead == null)
            return;

        foreach (int key in dead)
            DeviceRenderers.Remove(key);
    }

    private static bool Destroyed(Renderer[] renderers)
    {
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
                return true;
        }

        return false;
    }

    private static void Snapshot(IEnumerable<Renderer> renderers)
    {
        foreach (Renderer renderer in renderers)
        {
            if (renderer != null && !OriginalRenderers.ContainsKey(renderer.GetInstanceID()))
                OriginalRenderers.Add(renderer.GetInstanceID(), new RendererState(renderer));
        }
    }

    private static void Show(IEnumerable<Renderer> renderers)
    {
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
                continue;

            renderer.enabled = true;
            if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
                renderer.shadowCastingMode = ShadowCastingMode.On;
        }
    }

    private static void Hide(IEnumerable<Renderer> renderers)
    {
        foreach (Renderer renderer in renderers)
        {
            if (renderer == null)
                continue;

            renderer.enabled = true;
            renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
        }
    }
}
