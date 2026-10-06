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
    internal static bool ShowBody;
    internal static bool ShowFeet;
    internal static bool HideHeadwear;
    internal static bool HideFaceCover;
    internal static bool HideEyewear;

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
    private static bool? _originalSimplifiedSkeleton;
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

        MethodInfo? localPlayerCreate = AccessTools.Method(typeof(LocalPlayer), "Create");
        MethodInfo? localPlayerPrefix = typeof(BodyVisibility).GetMethod(nameof(LocalPlayerCreatePrefix), BindingFlags.NonPublic | BindingFlags.Static);
        if (localPlayerCreate != null && localPlayerPrefix != null)
            harmony.Patch(localPlayerCreate, prefix: new HarmonyMethod(localPlayerPrefix));

        _installed = true;
    }

    internal static void Apply(Player player)
    {
        if (!NewCameraSwitch.Enabled)
            return;

        PlayerBody? body = player.PlayerBody;
        if (body == null || !player.IsYourPlayer)
            return;

        Track(player);
        if (player.UsedSimplifiedSkeleton)
            player.UsedSimplifiedSkeleton = false;

        ApplyRenderers(player, body);
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
        if (_trackedPlayer != null && _originalSimplifiedSkeleton.HasValue)
            _trackedPlayer.UsedSimplifiedSkeleton = _originalSimplifiedSkeleton.Value;

        _trackedPlayer = null;
        _originalSimplifiedSkeleton = null;
        _nextCachePruneFrame = 0;
    }

    private static void LocalPlayerCreatePrefix(Profile profile, bool isYourPlayer)
    {
        if (!NewCameraSwitch.Enabled || !isYourPlayer || profile.Info?.Settings == null)
            return;

        profile.Info.Settings.UseSimpleAnimator = false;
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

        ApplySlot(__instance);
    }

    private static void Track(Player player)
    {
        if (ReferenceEquals(_trackedPlayer, player))
            return;

        Restore();
        _trackedPlayer = player;
        _originalSimplifiedSkeleton = player.UsedSimplifiedSkeleton;
    }

    private static void ApplyRenderers(Player player, PlayerBody body)
    {
        Prune();
        SetPart(body, EBodyModelPart.Head, ShowHead);
        SetPart(body, EBodyModelPart.Body, ShowBody);
        SetPart(body, EBodyModelPart.Feet, ShowFeet);
        foreach (EquipmentSlot slot in PlayerBody.SlotNames)
        {
            if (body.SlotViews.TryGetByKey(slot, out PlayerBody.SlotView? view) && view != null)
                ApplySlot(view);
        }

        ApplyHands(player, body);
    }

    private static void SetPart(PlayerBody body, EBodyModelPart part, bool visible)
    {
        if (!body.BodySkins.TryGetValue(part, out LoddedSkin? skin) || skin == null)
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

    private static void ApplySlot(PlayerBody.SlotView slotView)
    {
        Snapshot(slotView.Renderers);
        Renderer[] renderers = SlotRenderers(slotView);
        Snapshot(renderers);
        if (HiddenSlot(slotView.EquipmentSlot))
        {
            Hide(slotView.Renderers);
            Hide(renderers);
            return;
        }

        Show(slotView.Renderers);
        Show(renderers);
        if (slotView.EquipmentSlot == EquipmentSlot.Headwear)
            HideActiveHeadDevices(slotView);
    }

    private static bool HiddenSlot(EquipmentSlot slot)
    {
        switch (slot)
        {
            case EquipmentSlot.Headwear:
                return HideHeadwear;
            case EquipmentSlot.FaceCover:
                return HideFaceCover;
            case EquipmentSlot.Eyewear:
                return HideEyewear;
            default:
                return false;
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

    private static void HideActiveHeadDevices(PlayerBody.SlotView slotView)
    {
        GameObject? model = slotView.Model;
        CameraManager? camera = CameraManagerInstance?.GetValue(null) as CameraManager;
        if (model == null || camera == null)
            return;

        bool nightOn = NightFilterVisible(camera.NightVision);
        bool thermalOn = camera.ThermalVision != null && camera.ThermalVision.On;
        if (!nightOn && !thermalOn)
            return;

        Transform helmet = model.transform;
        if (nightOn)
        {
            foreach (NightVisionDevice device in model.GetComponentsInChildren<NightVisionDevice>(true))
                HideBranch(device.transform, helmet);
        }

        if (thermalOn)
        {
            foreach (ThermalVisionDevice device in model.GetComponentsInChildren<ThermalVisionDevice>(true))
                HideBranch(device.transform, helmet);
        }

        Item? headwear = Singleton<GameWorld>.Instance?.MainPlayer?.InventoryController?.Inventory?.Equipment
            ?.GetSlot(EquipmentSlot.Headwear)?.ContainedItem;
        if (headwear == null)
            return;

        List<Item> carriers = new();
        CollectCarriers(headwear, headwear, nightOn, thermalOn, carriers);
        foreach (Item carrier in carriers)
            HideNamed(helmet, PrefabKey(carrier));
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
        return _usesCustomPipeline?.Invoke(null, new object[] { nightVision }) is true;
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

    private static void CollectCarriers(Item headwear, Item item, bool nightOn, bool thermalOn, List<Item> carriers)
    {
        bool nightDevice = item.GetItemComponent<NightVisionComponent>() != null;
        bool thermalDevice = item.GetItemComponent<ThermalVisionComponent>() != null;
        if ((nightOn && nightDevice) || (thermalOn && thermalDevice))
        {
            carriers.Add(item);
            Item? parent = item.Parent?.Container?.ParentItem;
            while (parent != null && !ReferenceEquals(parent, headwear))
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
                CollectCarriers(headwear, child, nightOn, thermalOn, carriers);
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

    private static void HideNamed(Transform helmet, string prefabKey)
    {
        if (prefabKey.Length == 0)
            return;

        foreach (Transform transform in helmet.GetComponentsInChildren<Transform>(true))
        {
            if (transform.name.IndexOf(prefabKey, StringComparison.OrdinalIgnoreCase) >= 0)
                HideBranch(transform, helmet);
        }
    }

    private static void HideBranch(Transform device, Transform helmet)
    {
        Transform top = device;
        Transform? cursor = device;
        while (cursor.parent != null && cursor.parent != helmet)
        {
            cursor = cursor.parent;
            top = cursor;
        }

        Hide(top == helmet
            ? device.GetComponentsInChildren<Renderer>(true)
            : top.GetComponentsInChildren<Renderer>(true));
    }

    private static void ApplyHands(Player player, PlayerBody body)
    {
        if (!body.BodySkins.TryGetValue(EBodyModelPart.Hands, out LoddedSkin? hands) || hands == null)
            return;

        IEnumerable<Renderer> renderers = hands.GetRenderers();
        Snapshot(renderers);
        string typeName = player.HandsController?.GetType().Name ?? string.Empty;
        bool melee = typeName.IndexOf("Knife", StringComparison.Ordinal) >= 0 || typeName.IndexOf("Melee", StringComparison.Ordinal) >= 0;
        if (player.FirstPersonPointOfView && (player.HasFirearmInHands() || melee))
        {
            hands.EnableRenderers(false);
            hands.SetShadowCastingMode(ShadowCastingMode.ShadowsOnly);
        }
        else
        {
            RestoreRenderers(renderers);
        }
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

        if (dead == null)
            return;

        foreach (int key in dead)
            EquipmentRenderers.Remove(key);
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

    private static void RestoreRenderers(IEnumerable<Renderer> renderers)
    {
        foreach (Renderer renderer in renderers)
        {
            if (renderer != null && OriginalRenderers.TryGetValue(renderer.GetInstanceID(), out RendererState state))
            {
                renderer.enabled = state.Enabled;
                renderer.shadowCastingMode = state.Shadows;
            }
        }
    }
}
