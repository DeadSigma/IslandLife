using HarmonyLib;
using HMLLibrary;
using FMODUnity;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using Steamworks;
using UnityEngine;

public class IslandLife : Mod
{
    private Harmony harmony;

    public void Awake()
    {
        harmony = new Harmony("el.islandlife");
        harmony.PatchAll();
        Debug.Log("[IslandLife] Loaded");
    }

    public void OnModUnload()
    {
        IslandLifeStorage.SaveCurrentWorld();

        if (harmony != null)
            harmony.UnpatchAll(harmony.Id);

        Debug.Log("[IslandLife] Unloaded");
    }
}

public class IslandBuildRoot : MonoBehaviour
{
    public Landmark Landmark;
    public int TerrainLayer;
}

public class IslandBlockTag : MonoBehaviour
{
    public static readonly HashSet<IslandBlockTag> ActiveTags = new HashSet<IslandBlockTag>();

    public IslandBuildRoot Root;
    public string RecordId;
    public GameObject[] CollisionProxies;

    private void OnEnable() { ActiveTags.Add(this); }
    private void OnDisable() { ActiveTags.Remove(this); }
    private void OnDestroy() { ActiveTags.Remove(this); }
}

public class IslandCollisionProxy : MonoBehaviour
{
    public IslandBuildRoot Root;
    public Block SourceBlock;
}

[HarmonyPatch(typeof(BlockCreator), "Update")]
public static class BlockCreator_Update_IslandLife
{
    private static readonly FieldInfo QuadAtCursorField = AccessTools.Field(typeof(BlockCreator), "quadAtCursor");
    private static readonly FieldInfo QuadSurfaceField = AccessTools.Field(typeof(BlockCreator), "quadSurface");
    private static readonly FieldInfo QuadHitField = AccessTools.Field(typeof(BlockCreator), "quadHit");
    private static readonly FieldInfo SelectedBuildablePrefabField = AccessTools.Field(typeof(BlockCreator), "selectedBuildablePrefab");
    private static readonly FieldInfo ColliderPrefabEnablerField = AccessTools.Field(typeof(BlockCreator), "colliderPrefabEnabler");
    private static readonly FieldInfo EventRefPlaceBlockField = AccessTools.Field(typeof(BlockCreator), "eventRef_placeBlock");
    private static readonly FieldInfo EventRefCreateBlockField = AccessTools.Field(typeof(BlockCreator), "eventRef_createBlock");

    private static readonly MethodInfo HandleRotationMethod = AccessTools.Method(typeof(BlockCreator), "HandleRotationOfSelectedBlock");
    private static readonly MethodInfo HandleMirroredMethod = AccessTools.Method(typeof(BlockCreator), "HandleMirroredVersion");
    private static readonly MethodInfo SetGhostPositionMethod = AccessTools.Method(typeof(BlockCreator), "SetGhostBlockPositionAndRotation");

    private const float GroundClearance = 0.001f;
    private const float MinGroundNormalY = 0.55f;

    [HarmonyPrefix]
    public static bool Prefix(BlockCreator __instance, Item_Base ___selectedBuildableItem, Network_Player ___playerNetwork)
    {
        if (__instance == null || ___playerNetwork == null || !___playerNetwork.IsLocalPlayer || ___selectedBuildableItem == null)
            return true;

        if (CanvasHelper.ActiveMenu != MenuType.None || MyInput.GetButtonDown("RMB") || MyInput.GetButtonUp("RMB"))
        {
            RestoreGhostToRaftPivot(__instance);
            return true;
        }

        ColliderPrefabEnabler colliderEnabler = ColliderPrefabEnablerField.GetValue(__instance) as ColliderPrefabEnabler;
        if (colliderEnabler != null)
            colliderEnabler.ShowCollider();

        if (__instance.selectedBlock == null || SelectedBuildablePrefabField.GetValue(__instance) as Block == null)
        {
            QuadSurfaceField.SetValue(__instance, null);
            __instance.SetBlockTypeToBuild(___selectedBuildableItem.UniqueName);
        }

        Vector3 snapPosition;
        Quaternion snapRotation;
        IslandBuildRoot snapRoot;

        if (TryGetGridSnap(
            ___playerNetwork,
            ___selectedBuildableItem,
            out snapPosition,
            out snapRotation,
            out snapRoot))
        {
            HandleGridSnap(
                __instance,
                ___playerNetwork,
                ___selectedBuildableItem,
                snapRoot,
                snapPosition,
                snapRotation);

            return false;
        }

        RaycastHit quadHit;
        BlockQuad quad;
        IslandBuildRoot quadRoot;

        if (TryGetIslandQuad(___playerNetwork, ___selectedBuildableItem, out quadHit, out quad, out quadRoot))
        {
            HandleIslandQuad(__instance, ___playerNetwork, ___selectedBuildableItem, quadHit, quad, quadRoot);
            return false;
        }

        if (TryGetGridEdgeSnap(
            ___playerNetwork,
            ___selectedBuildableItem,
            out snapPosition,
            out snapRotation,
            out snapRoot))
        {
            HandleGridSnap(
                __instance,
                ___playerNetwork,
                ___selectedBuildableItem,
                snapRoot,
                snapPosition,
                snapRotation);

            return false;
        }

        if (!CanStartIslandBuild(___selectedBuildableItem))
        {
            RestoreGhostToRaftPivot(__instance);
            return true;
        }

        RaycastHit terrainHit;
        Landmark landmark;

        if (!TryGetIslandTerrain(___playerNetwork, out terrainHit, out landmark))
        {
            RestoreGhostToRaftPivot(__instance);
            return true;
        }

        HandleTerrainBlock(__instance, ___playerNetwork, ___selectedBuildableItem, terrainHit, landmark);
        return false;
    }

    private static Block EnsureGhost(BlockCreator creator, Item_Base item, DPS dpsType, out Block selectedPrefab)
    {
        selectedPrefab = null;
        if (creator == null || item == null || item.settings_buildable == null)
            return null;

        Block requiredPrefab = item.settings_buildable.GetBlockPrefab(dpsType);
        if (requiredPrefab == null)
            return null;

        selectedPrefab = SelectedBuildablePrefabField.GetValue(creator) as Block;

        if (creator.selectedBlock == null || selectedPrefab == null ||
            creator.selectedBlock.dpsType != requiredPrefab.dpsType)
        {
            creator.SetBlockTypeToBuild(requiredPrefab.buildableItem.UniqueName);
            selectedPrefab = SelectedBuildablePrefabField.GetValue(creator) as Block;
        }

        Block ghost = creator.selectedBlock;
        if (ghost != null)
            DetachGhostFromRaftPivot(ghost);

        return ghost;
    }

    private static Block ApplyRotationAndMirror(BlockCreator creator, out Block selectedPrefab)
    {
        HandleIslandRotation(creator);
        HandleMirroredMethod.Invoke(creator, null);

        Block ghost = creator.selectedBlock;
        selectedPrefab = SelectedBuildablePrefabField.GetValue(creator) as Block;

        if (ghost != null)
            DetachGhostFromRaftPivot(ghost);

        return ghost;
    }

    private static Block PrepareGhost(BlockCreator creator, Item_Base item, DPS dpsType, out Block selectedPrefab)
    {
        Block ghost = EnsureGhost(creator, item, dpsType, out selectedPrefab);
        return ghost == null ? null : ApplyRotationAndMirror(creator, out selectedPrefab);
    }

    private static void HandleIslandRotation(BlockCreator creator)
    {
        Block ghost = creator != null ? creator.selectedBlock : null;
        if (ghost == null)
            return;

        Item_Base item = ghost.buildableItem;
        bool freeRotation = item != null &&
                            (Block.IsBlockIndexFoundation(item.UniqueIndex) ||
                             Block.IsBlockIndexFloor(item.UniqueIndex) ||
                             Block.IsBlockIndexRaisedFloor(item.UniqueIndex));

        if (!freeRotation)
        {
            HandleRotationMethod.Invoke(creator, null);
            return;
        }

        bool oldValue = ghost.canRotateFreely;
        ghost.canRotateFreely = true;

        try
        {
            HandleRotationMethod.Invoke(creator, null);
        }
        finally
        {
            if (creator.selectedBlock == ghost)
                ghost.canRotateFreely = oldValue;
        }
    }

    private static void DetachGhostFromRaftPivot(Block ghost)
    {
        if (ghost != null && ghost.transform.parent != null)
            ghost.transform.SetParent(null, true);
    }

    private static void RestoreGhostToRaftPivot(BlockCreator creator)
    {
        if (creator == null || creator.selectedBlock == null)
            return;

        GameManager gameManager = SingletonGeneric<GameManager>.Singleton;
        if (gameManager == null || gameManager.lockedPivot == null ||
            creator.selectedBlock.transform.parent == gameManager.lockedPivot)
            return;

        creator.selectedBlock.transform.SetParent(gameManager.lockedPivot, true);
    }

    internal static bool CanStartIslandBuild(Item_Base item)
    {
        if (item == null || item.settings_buildable == null)
            return false;

        Block prefab = item.settings_buildable.GetBlockPrefab(DPS.Default);
        return prefab != null && !IsRaftNavigationBlock(item, prefab);
    }

    private static bool IsRaftNavigationBlock(Item_Base item, Block prefab)
    {
        if (item == null || prefab == null)
            return false;

        if (prefab.GetComponentInChildren<Sail>(true) != null ||
            prefab.GetComponentInChildren<MotorWheel>(true) != null ||
            prefab.GetComponentInChildren<SteeringWheel>(true) != null ||
            prefab.GetComponentInChildren<Anchor_Stationary>(true) != null)
            return true;

        string normalized = (item.UniqueName ?? string.Empty)
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .ToLowerInvariant();

        string[] blockedNames =
        {
            "engine", "motor", "anchor", "sail", "steeringwheel",
            "engineswitch", "enginecontrol", "enginecontrols", "rudder"
        };

        for (int i = 0; i < blockedNames.Length; i++)
            if (normalized.Contains(blockedNames[i]))
                return true;

        return false;
    }

    private static int GetGridKind(Item_Base item)
    {
        if (item == null)
            return 0;

        int index = item.UniqueIndex;

        if (Block.IsBlockIndexFoundation(index) &&
            !Block.IsBlockIndexFoundationTriangular(index))
            return 1;

        if (Block.IsBlockIndexFloor(index))
            return 2;

        if (Block.IsBlockIndexRaisedFloor(index))
            return 3;

        return 0;
    }

    private static bool TryGetGridSnap(
        Network_Player player,
        Item_Base item,
        out Vector3 position,
        out Quaternion rotation,
        out IslandBuildRoot root)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        root = null;

        int gridKind = GetGridKind(item);

        if (player == null ||
            player.CameraTransform == null ||
            gridKind == 0)
        {
            return false;
        }

        Ray ray = new Ray(
            player.CameraTransform.position,
            player.CameraTransform.forward.normalized
        );

        float bestScore = float.MaxValue;

        foreach (IslandBlockTag tag in IslandBlockTag.ActiveTags)
        {
            if (tag == null || tag.Root == null)
                continue;

            Block source = tag.GetComponent<Block>();

            if (source == null ||
                source.buildableItem == null ||
                GetGridKind(source.buildableItem) != gridKind)
            {
                continue;
            }

            Vector3 up = source.transform.up.normalized;
            Vector3 right = source.transform.right.normalized;
            Vector3 forward = source.transform.forward.normalized;

            Plane plane = new Plane(
                up,
                source.transform.position
            );

            float rayDistance;

            if (!plane.Raycast(ray, out rayDistance) ||
                rayDistance <= 0f ||
                rayDistance > Player.UseDistance * 2f)
            {
                continue;
            }

            Vector3 cursorPoint =
                ray.GetPoint(rayDistance);

            TestGridCandidate(
                source,
                source.transform.position + right * BlockCreator.BlockSize,
                cursorPoint,
                right,
                forward,
                tag.Root,
                ref bestScore,
                ref position,
                ref rotation,
                ref root
            );

            TestGridCandidate(
                source,
                source.transform.position - right * BlockCreator.BlockSize,
                cursorPoint,
                right,
                forward,
                tag.Root,
                ref bestScore,
                ref position,
                ref rotation,
                ref root
            );

            TestGridCandidate(
                source,
                source.transform.position + forward * BlockCreator.BlockSize,
                cursorPoint,
                right,
                forward,
                tag.Root,
                ref bestScore,
                ref position,
                ref rotation,
                ref root
            );

            TestGridCandidate(
                source,
                source.transform.position - forward * BlockCreator.BlockSize,
                cursorPoint,
                right,
                forward,
                tag.Root,
                ref bestScore,
                ref position,
                ref rotation,
                ref root
            );
        }

        return root != null;
    }

    private static void TestGridCandidate(
        Block source,
        Vector3 candidate,
        Vector3 cursorPoint,
        Vector3 right,
        Vector3 forward,
        IslandBuildRoot candidateRoot,
        ref float bestScore,
        ref Vector3 bestPosition,
        ref Quaternion bestRotation,
        ref IslandBuildRoot bestRoot)
    {
        Vector3 delta =
            cursorPoint - candidate;

        float localX =
            Mathf.Abs(
                Vector3.Dot(delta, right)
            );

        float localZ =
            Mathf.Abs(
                Vector3.Dot(delta, forward)
            );

        if (localX > BlockCreator.HalfBlockSize ||
            localZ > BlockCreator.HalfBlockSize)
        {
            return;
        }

        float score =
            delta.sqrMagnitude;

        if (score >= bestScore)
            return;

        bestScore = score;
        bestPosition = candidate;
        bestRotation = source.transform.rotation;
        bestRoot = candidateRoot;
    }

    private static bool TryGetIslandQuad(
        Network_Player player, Item_Base item, out RaycastHit hit, out BlockQuad quad, out IslandBuildRoot root)
    {
        hit = default(RaycastHit);
        quad = null;
        root = null;

        if (player == null || player.CameraTransform == null)
            return false;

        RaycastHit[] hits = Physics.RaycastAll(
            player.CameraTransform.position,
            player.CameraTransform.forward,
            Player.UseDistance * 2f,
            LayerMasks.MASK_BuildQuad,
            QueryTriggerInteraction.UseGlobal);

        float bestDistance = float.MaxValue;

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit current = hits[i];
            if (current.distance >= bestDistance || current.transform == null)
                continue;

            BlockQuad currentQuad = current.transform.GetComponent<BlockQuad>() ??
                                    current.transform.GetComponentInParent<BlockQuad>();

            if (currentQuad == null || currentQuad.ParentBlock == null)
                continue;

            IslandBlockTag tag = currentQuad.ParentBlock.GetComponent<IslandBlockTag>();
            if (tag == null || tag.Root == null || !currentQuad.AcceptsBlock(item, current.normal))
                continue;

            bestDistance = current.distance;
            hit = current;
            quad = currentQuad;
            root = tag.Root;
        }

        return quad != null;
    }

    private static bool TryGetGridEdgeSnap(
        Network_Player player,
        Item_Base item,
        out Vector3 position,
        out Quaternion rotation,
        out IslandBuildRoot root)
    {
        position = Vector3.zero;
        rotation = Quaternion.identity;
        root = null;

        int gridKind = GetGridKind(item);

        if (player == null ||
            player.CameraTransform == null ||
            gridKind == 0)
        {
            return false;
        }

        RaycastHit[] hits = Physics.RaycastAll(
            player.CameraTransform.position,
            player.CameraTransform.forward,
            Player.UseDistance * 2f,
            LayerMasks.MASK_BuildQuad,
            QueryTriggerInteraction.UseGlobal
        );

        float bestDistance =
            float.MaxValue;

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit = hits[i];

            if (hit.distance >= bestDistance ||
                hit.transform == null)
            {
                continue;
            }

            BlockQuad currentQuad =
                hit.transform.GetComponent<BlockQuad>() ??
                hit.transform.GetComponentInParent<BlockQuad>();

            Block source =
                currentQuad != null
                    ? currentQuad.ParentBlock
                    : null;

            if (source == null ||
                source.buildableItem == null ||
                GetGridKind(source.buildableItem) != gridKind)
            {
                continue;
            }

            IslandBlockTag tag =
                source.GetComponent<IslandBlockTag>();

            if (tag == null ||
                tag.Root == null)
            {
                continue;
            }

            Vector3 up =
                source.transform.up.normalized;

            Vector3 delta =
                currentQuad.transform.position -
                source.transform.position;

            float vertical =
                Vector3.Dot(delta, up);

            Vector3 planar =
                delta - up * vertical;

            if (Mathf.Abs(vertical) > 0.25f ||
                planar.sqrMagnitude < 0.25f)
            {
                continue;
            }

            position =
                source.transform.position +
                planar.normalized *
                BlockCreator.BlockSize;

            rotation =
                source.transform.rotation;

            root =
                tag.Root;

            bestDistance =
                hit.distance;
        }

        return root != null;
    }

    private static bool TryGetIslandTerrain(Network_Player player, out RaycastHit hit, out Landmark landmark)
    {
        hit = default(RaycastHit);
        landmark = null;

        if (player == null || player.CameraTransform == null)
            return false;

        RaycastHit[] hits = Physics.RaycastAll(
            player.CameraTransform.position,
            player.CameraTransform.forward,
            Player.UseDistance * 2f,
            ~0,
            QueryTriggerInteraction.Ignore);

        float bestDistance = float.MaxValue;
        float minNormalY = IsTerrainAlignHeld() ? 0.05f : MinGroundNormalY;

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit current = hits[i];
            if (current.distance >= bestDistance || current.collider == null || current.normal.y < minNormalY)
                continue;

            if (current.collider.GetComponent<IslandCollisionProxy>() != null ||
                current.collider.GetComponentInParent<IslandCollisionProxy>() != null ||
                current.collider.GetComponentInParent<Block>() != null)
                continue;

            Landmark currentLandmark = current.collider.GetComponentInParent<Landmark>();
            if (currentLandmark == null)
                continue;

            bestDistance = current.distance;
            hit = current;
            landmark = currentLandmark;
        }

        return landmark != null;
    }

    private static void HandleIslandQuad(
        BlockCreator creator, Network_Player player, Item_Base item,
        RaycastHit hit, BlockQuad quad, IslandBuildRoot root)
    {
        BlockSurface surface = quad.GetSurfaceFromNormal(hit.normal);
        if (surface == null)
        {
            creator.SetGhostBlockVisibility(false);
            return;
        }

        Block selectedPrefab;
        Block ghost = EnsureGhost(creator, item, surface.dpsType, out selectedPrefab);
        if (ghost == null)
            return;

        QuadAtCursorField.SetValue(creator, quad);
        QuadSurfaceField.SetValue(creator, surface);
        QuadHitField.SetValue(creator, hit);

        ghost = ApplyRotationAndMirror(creator, out selectedPrefab);
        if (ghost == null)
            return;

        SetGhostPositionMethod.Invoke(creator, null);
        PlaceGhost(creator, player, ghost, root, surface.dpsType);
    }

    private static void HandleGridSnap(
        BlockCreator creator,
        Network_Player player,
        Item_Base item,
        IslandBuildRoot root,
        Vector3 position,
        Quaternion sourceRotation)
    {
        Block selectedPrefab;

        Block ghost =
            PrepareGhost(
                creator,
                item,
                DPS.Default,
                out selectedPrefab
            );

        if (ghost == null ||
            selectedPrefab == null)
        {
            return;
        }

        ghost.transform.position =
            position;

        Vector3 sourceUp =
            sourceRotation * Vector3.up;

        float yawDelta =
            Mathf.DeltaAngle(
                sourceRotation.eulerAngles.y,
                selectedPrefab.currentRotationY
            );

        ghost.transform.rotation =
            Quaternion.AngleAxis(
                yawDelta,
                sourceUp
            ) *
            sourceRotation;

        PlaceGhost(
            creator,
            player,
            ghost,
            root,
            DPS.Default
        );
    }

    private static void PlaceGhost(
        BlockCreator creator, Network_Player player, Block ghost,
        IslandBuildRoot root, DPS dpsType)
    {
        creator.SetGhostBlockVisibility(true);

        bool canBuild = CanBuildOnIsland(creator, ghost, root);
        SetGhostMaterial(ghost, canBuild);

        if (!canBuild || !MyInput.GetButtonDown("LMB"))
            return;

        IslandLifeNetwork.RequestPlace(
            creator,
            player,
            ghost.buildableItem,
            dpsType,
            root,
            ghost.transform.position,
            ghost.transform.rotation);
    }

    private static bool CanBuildOnIsland(BlockCreator creator, Block ghost, IslandBuildRoot root)
    {
        if (creator == null || ghost == null || root == null || !creator.HasEnoughResourcesToBuild(ghost))
            return false;

        IslandBlockTag[] tags = root.GetComponentsInChildren<IslandBlockTag>(true);

        for (int i = 0; i < tags.Length; i++)
        {
            IslandBlockTag tag = tags[i];
            Block placed = tag != null ? tag.GetComponent<Block>() : null;

            if (placed == null || placed.buildableItem == null || ghost.buildableItem == null ||
                placed.buildableItem.UniqueIndex != ghost.buildableItem.UniqueIndex ||
                Vector3.Distance(placed.transform.position, ghost.transform.position) > 0.05f)
                continue;

            bool gridBlock = Block.IsBlockIndexFoundation(ghost.buildableItem.UniqueIndex) ||
                             Block.IsBlockIndexFloor(ghost.buildableItem.UniqueIndex) ||
                             Block.IsBlockIndexRaisedFloor(ghost.buildableItem.UniqueIndex);

            if (gridBlock || Quaternion.Angle(placed.transform.rotation, ghost.transform.rotation) <= 2f)
                return false;
        }

        return true;
    }

    private static void HandleTerrainBlock(
        BlockCreator creator, Network_Player player, Item_Base item,
        RaycastHit hit, Landmark landmark)
    {
        Block selectedPrefab;
        Block ghost = PrepareGhost(creator, item, DPS.Default, out selectedPrefab);
        if (ghost == null || selectedPrefab == null)
            return;

        Vector3 baseEuler = selectedPrefab.transform.eulerAngles;
        Quaternion baseRotation = Quaternion.Euler(baseEuler.x, selectedPrefab.currentRotationY, baseEuler.z);

        bool alignToTerrain = IsTerrainAlignHeld();
        Vector3 supportNormal = alignToTerrain ? hit.normal.normalized : Vector3.up;

        if (alignToTerrain)
        {
            Vector3 baseUp = baseRotation * Vector3.up;
            ghost.transform.rotation = Quaternion.FromToRotation(baseUp, supportNormal) * baseRotation;
        }
        else
        {
            ghost.transform.rotation = baseRotation;
        }

        ghost.transform.position = hit.point;

        if (item.settings_buildable.Placeable)
        {
            ghost.transform.position = hit.point +
                                       GetPlaceablePivotOffset(ghost, supportNormal) +
                                       supportNormal * GroundClearance;
        }
        else
        {
            float bottomOffset = GetBottomOffsetFromPivot(ghost, supportNormal);
            ghost.transform.position = hit.point + supportNormal * (bottomOffset + GroundClearance);
        }

        creator.SetGhostBlockVisibility(true);
        bool canBuild = creator.HasEnoughResourcesToBuild(ghost);
        SetGhostMaterial(ghost, canBuild);

        if (!canBuild || !MyInput.GetButtonDown("LMB"))
            return;

        IslandBuildRoot root = GetOrCreateRoot(
            landmark,
            ghost.transform.position,
            ghost.transform.rotation,
            hit.collider.gameObject.layer);

        IslandLifeNetwork.RequestPlace(
            creator,
            player,
            item,
            DPS.Default,
            root,
            ghost.transform.position,
            ghost.transform.rotation);
    }

    private static bool IsTerrainAlignHeld()
    {
        return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
    }

    private static Vector3 GetPlaceablePivotOffset(Block block, Vector3 supportNormal)
    {
        if (block == null || block.pivotOffset == Vector3.zero)
            return Vector3.zero;

        if (supportNormal.sqrMagnitude < 0.001f)
            supportNormal = Vector3.up;

        supportNormal.Normalize();

        Vector3 right = Vector3.ProjectOnPlane(block.transform.right, supportNormal);
        if (right.sqrMagnitude < 0.001f)
            right = Vector3.ProjectOnPlane(block.transform.forward, supportNormal);

        right.Normalize();

        Vector3 forward = Vector3.Cross(supportNormal, right).normalized;
        if (Vector3.Dot(forward, block.transform.forward) < 0f)
            forward *= -1f;

        Vector3 offset = block.pivotOffset;
        return right * offset.x + supportNormal * offset.y + forward * offset.z;
    }

    private static float GetBottomOffsetFromPivot(Block block, Vector3 supportNormal)
    {
        if (block == null)
            return 0f;

        if (supportNormal.sqrMagnitude < 0.001f)
            supportNormal = Vector3.up;

        supportNormal.Normalize();

        bool found = false;
        float minProjection = 0f;
        Vector3 pivot = block.transform.position;

        if (block.blockColliders != null)
        {
            for (int i = 0; i < block.blockColliders.Length; i++)
            {
                BoxCollider collider = block.blockColliders[i];
                if (collider == null)
                    continue;

                AddBoundsProjection(
                    new Bounds(collider.center, collider.size),
                    collider.transform,
                    pivot,
                    supportNormal,
                    ref found,
                    ref minProjection);
            }
        }

        if (found)
            return Mathf.Max(0f, -minProjection);

        MeshFilter[] meshFilters = block.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter filter = meshFilters[i];
            if (filter != null && filter.sharedMesh != null)
                AddBoundsProjection(filter.sharedMesh.bounds, filter.transform, pivot, supportNormal, ref found, ref minProjection);
        }

        SkinnedMeshRenderer[] skinned = block.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinned.Length; i++)
        {
            SkinnedMeshRenderer renderer = skinned[i];
            if (renderer != null)
                AddBoundsProjection(renderer.localBounds, renderer.transform, pivot, supportNormal, ref found, ref minProjection);
        }

        return found ? Mathf.Max(0f, -minProjection) : 0f;
    }

    private static void AddBoundsProjection(
        Bounds bounds, Transform source, Vector3 pivot, Vector3 supportNormal,
        ref bool found, ref float minProjection)
    {
        if (source == null)
            return;

        Vector3 center = bounds.center;
        Vector3 extents = bounds.extents;

        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 localPoint = center + Vector3.Scale(extents, new Vector3(x, y, z));
                    Vector3 worldPoint = source.TransformPoint(localPoint);
                    float projection = Vector3.Dot(worldPoint - pivot, supportNormal);

                    if (!found || projection < minProjection)
                    {
                        minProjection = projection;
                        found = true;
                    }
                }
    }

    internal static IslandBuildRoot GetOrCreateRoot(
        Landmark landmark, Vector3 worldPosition, Quaternion worldRotation, int terrainLayer)
    {
        IslandBuildRoot[] roots = landmark.GetComponentsInChildren<IslandBuildRoot>(true);
        if (roots != null && roots.Length > 0)
            return roots[0];

        GameObject rootObject = new GameObject("IslandBuildRoot");
        rootObject.transform.SetParent(landmark.transform, true);
        rootObject.transform.SetPositionAndRotation(
            worldPosition,
            Quaternion.Euler(0f, worldRotation.eulerAngles.y, 0f));

        IslandBuildRoot root = rootObject.AddComponent<IslandBuildRoot>();
        root.Landmark = landmark;
        root.TerrainLayer = terrainLayer;
        return root;
    }

    internal static Block CreateIslandBlock(
        BlockCreator creator,
        Network_Player player,
        Item_Base item,
        DPS dpsType,
        IslandBuildRoot root,
        Vector3 worldPosition,
        Quaternion worldRotation,
        bool consumeResources = true,
        string recordId = null,
        bool registerRecord = true,
        int restoredHealth = -1,
        int restoredMaxHealth = -1,
        bool restoredReinforced = false,
        bool playPlacementSound = true,
        int hotbarIndexOverride = int.MinValue,
        uint blockObjectIndex = 0U,
        uint networkedObjectIndex = 0U,
        uint networkedBehaviourIndex = 0U)
    {
        if (creator == null || player == null || item == null || root == null)
            return null;

        Block prefab = item.settings_buildable.GetBlockPrefab(dpsType);
        if (prefab == null)
            return null;

        if (player.IsLocalPlayer)
            creator.SetGhostBlockVisibility(false);

        Block block = UnityEngine.Object.Instantiate(
            prefab,
            worldPosition,
            worldRotation,
            root.transform
        );

        block.OnStartingPlacement();

        IslandBlockTag tag =
            block.gameObject.AddComponent<IslandBlockTag>();

        tag.Root = root;
        tag.RecordId =
            string.IsNullOrEmpty(recordId)
                ? Guid.NewGuid().ToString("N")
                : recordId;

        BoxCollider[] physicalColliders =
            GetPhysicalColliders(block);

        block.blockColliders =
            new BoxCollider[0];

        if (blockObjectIndex == 0U)
            blockObjectIndex = SaveAndLoad.GetUniqueObjectIndex();

        block.ObjectIndex =
            blockObjectIndex;

        if (block.networkedBehaviour != null)
        {
            if (networkedObjectIndex == 0U)
                networkedObjectIndex = SaveAndLoad.GetUniqueObjectIndex();

            if (networkedBehaviourIndex == 0U)
                networkedBehaviourIndex = NetworkUpdateManager.GetUniqueBehaviourIndex();

            block.networkedBehaviour.ObjectIndex =
                networkedObjectIndex;

            block.networkedBehaviour.BehaviourIndex =
                networkedBehaviourIndex;

            NetworkUpdateManager.AddBehaviour(
                block.networkedBehaviour
            );
        }

        block.OnFinishedPlacement();

        if (playPlacementSound)
            PlayPlacementSound(creator, item, block.transform.position);

        if (restoredMaxHealth >= 0)
            block.MaxHealth = restoredMaxHealth;

        if (restoredHealth >= 0)
            block.SetHealth(restoredHealth);

        if (restoredReinforced)
            block.Reinforced = true;

        tag.CollisionProxies =
            CreateStaticCollisionProxies(
                physicalColliders,
                root
            );

        for (int i = 0; i < physicalColliders.Length; i++)
        {
            BoxCollider collider =
                physicalColliders[i];

            if (collider == null)
                continue;

            collider.isTrigger = true;
            collider.enabled = true;
        }

        if (block.occupyingComponent != null)
            block.occupyingComponent.RestoreToDefaultMaterial();

        int hotbarIndex =
            hotbarIndexOverride != int.MinValue
                ? hotbarIndexOverride
                : player.Inventory.hotbar.GetSelectedSlotIndex();

        if (consumeResources && player.IsLocalPlayer)
        {
            if (item.settings_buildable.Placeable)
            {
                if (hotbarIndex >= 0)
                {
                    Slot slot =
                        player.Inventory.GetSlot(hotbarIndex);

                    if (slot != null && !slot.IsEmpty)
                        slot.RemoveItem(1);
                }
            }
            else if (!Cheat.UseGodMode)
            {
                player.Inventory.RemoveCostMultiple(
                    item.settings_recipe.NewCost,
                    false
                );
            }
        }

        if (registerRecord && Raft_Network.IsHost)
            IslandLifeStorage.RegisterBlock(block, tag);

        if (consumeResources && player.IsLocalPlayer)
        {
            if (item.settings_buildable.Placeable)
            {
                Slot slot =
                    hotbarIndex >= 0
                        ? player.Inventory.GetSlot(hotbarIndex)
                        : null;

                if (slot == null || slot.IsEmpty)
                {
                    creator.selectedBlock = null;
                    player.Inventory.hotbar.ReselectCurrentSlot();
                }
                else
                {
                    creator.SetBlockTypeToBuild(
                        item.UniqueName
                    );
                }
            }
            else
            {
                creator.SetBlockTypeToBuild(
                    item.UniqueName
                );
            }
        }

        return block;
    }

    private static void PlayPlacementSound(
        BlockCreator creator,
        Item_Base item,
        Vector3 position)
    {
        if (creator == null ||
            item == null ||
            item.settings_buildable == null)
        {
            return;
        }

        bool placeable =
            item.settings_buildable.Placeable;

        FieldInfo field =
            placeable
                ? EventRefPlaceBlockField
                : EventRefCreateBlockField;

        string eventRef =
            field != null
                ? field.GetValue(creator) as string
                : null;

        if (!placeable &&
            string.IsNullOrEmpty(eventRef) &&
            EventRefPlaceBlockField != null)
        {
            eventRef =
                EventRefPlaceBlockField.GetValue(creator)
                    as string;
        }

        if (!string.IsNullOrEmpty(eventRef))
        {
            RuntimeManager.PlayOneShot(
                eventRef,
                position
            );
        }
    }

    private static BoxCollider[] GetPhysicalColliders(Block block)
    {
        if (block == null || block.blockColliders == null || block.blockColliders.Length == 0)
            return new BoxCollider[0];

        List<BoxCollider> result = new List<BoxCollider>();

        for (int i = 0; i < block.blockColliders.Length; i++)
        {
            BoxCollider collider = block.blockColliders[i];
            if (collider == null)
                continue;

            if (collider.GetComponent<BlockQuad>() != null ||
                collider.GetComponentInParent<BlockQuad>() != null)
                continue;

            result.Add(collider);
        }

        return result.ToArray();
    }

    private static GameObject[] CreateStaticCollisionProxies(BoxCollider[] sourceColliders, IslandBuildRoot root)
    {
        if (sourceColliders == null || root == null)
            return new GameObject[0];

        List<GameObject> proxies = new List<GameObject>();

        for (int i = 0; i < sourceColliders.Length; i++)
        {
            BoxCollider source = sourceColliders[i];
            if (source == null)
                continue;

            GameObject proxy = new GameObject("IslandStaticCollision");
            proxy.layer = root.TerrainLayer;
            proxy.transform.SetParent(root.transform, false);
            proxy.transform.position = source.transform.position;
            proxy.transform.rotation = source.transform.rotation;
            proxy.transform.localScale = RelativeScale(source.transform.lossyScale, root.transform.lossyScale);

            IslandCollisionProxy marker = proxy.AddComponent<IslandCollisionProxy>();
            marker.Root = root;
            marker.SourceBlock = source.GetComponentInParent<Block>();

            BoxCollider collider = proxy.AddComponent<BoxCollider>();
            collider.center = source.center;
            collider.size = source.size;
            collider.sharedMaterial = source.sharedMaterial;

            proxies.Add(proxy);
        }

        return proxies.ToArray();
    }

    private static Vector3 RelativeScale(Vector3 value, Vector3 parent)
    {
        return new Vector3(SafeDivide(value.x, parent.x), SafeDivide(value.y, parent.y), SafeDivide(value.z, parent.z));
    }

    private static float SafeDivide(float value, float divisor)
    {
        return Mathf.Abs(divisor) < 0.0001f ? value : value / divisor;
    }

    private static void SetGhostMaterial(Block ghost, bool canBuild)
    {
        if (ghost == null || ghost.occupyingComponent == null)
            return;

        GameManager gameManager = SingletonGeneric<GameManager>.Singleton;
        if (gameManager == null)
            return;

        Material material = canBuild ? gameManager.ghostMaterialGreen : gameManager.ghostMaterialRed;
        if (material != null)
            ghost.occupyingComponent.SetNewMaterial(material);
    }
}

public static class IslandLifeCollisionCleanup
{
    public static void Cleanup(IslandBlockTag tag)
    {
        if (tag == null || tag.CollisionProxies == null)
            return;

        for (int i = 0; i < tag.CollisionProxies.Length; i++)
        {
            GameObject proxy = tag.CollisionProxies[i];
            if (proxy == null)
                continue;

            Collider collider = proxy.GetComponent<Collider>();
            if (collider != null)
                collider.enabled = false;

            UnityEngine.Object.Destroy(proxy);
        }

        tag.CollisionProxies = new GameObject[0];
    }
}

public static class IslandLifeNetwork
{
    private const int IslandMarker = 1 << 30;
    private const int SlotBits = 5;
    private const int SlotMask = (1 << SlotBits) - 1;

    public static void RequestPlace(
        BlockCreator creator,
        Network_Player player,
        Item_Base item,
        DPS dpsType,
        IslandBuildRoot root,
        Vector3 worldPosition,
        Quaternion worldRotation)
    {
        if (creator == null ||
            player == null ||
            item == null ||
            root == null ||
            root.Landmark == null)
        {
            return;
        }

        int hotbarIndex =
            player.Inventory.hotbar.GetSelectedSlotIndex();

        int encoded =
            EncodeContext(
                root.Landmark,
                hotbarIndex
            );

        if (encoded == 0)
            return;

        Vector3 localPosition =
            root.Landmark.transform.InverseTransformPoint(
                worldPosition
            );

        Quaternion localRotation =
            Quaternion.Inverse(
                root.Landmark.transform.rotation
            ) *
            worldRotation;

        if (Raft_Network.IsHost)
        {
            Block block =
                CreateAuthoritative(
                    creator,
                    player,
                    item,
                    dpsType,
                    root.Landmark,
                    localPosition,
                    localRotation,
                    hotbarIndex,
                    true
                );

            if (block == null)
                return;

            Message_BlockCreator_PlaceBlock message =
                CreatePlaceMessage(
                    creator,
                    item,
                    dpsType,
                    encoded,
                    localPosition,
                    localRotation,
                    block
                );

            player.Network.RPC(
                message,
                Target.Other,
                EP2PSend.k_EP2PSendReliable,
                NetworkChannel.Channel_Game
            );

            return;
        }

        Message_BlockCreator_PlaceBlock request =
            new Message_BlockCreator_PlaceBlock(
                Messages.BlockCreator_PlaceBlock,
                creator,
                item.UniqueIndex,
                0U,
                0U,
                0U,
                localPosition,
                localRotation.eulerAngles,
                encoded,
                dpsType
            );

        player.SendP2P(
            request,
            EP2PSend.k_EP2PSendReliable,
            NetworkChannel.Channel_Game
        );

        creator.SetGhostBlockVisibility(false);
    }

    public static bool TryHandleDeserialize(
        BlockCreator creator,
        Message_NetworkBehaviour message,
        Network_UserId remoteID,
        out bool result)
    {
        result = false;

        if (creator == null ||
            message == null)
        {
            return false;
        }

        if (message.Type == Messages.BlockCreator_PlaceBlock)
        {
            Message_BlockCreator_PlaceBlock place =
                message as Message_BlockCreator_PlaceBlock;

            long landmarkIndex;
            int hotbarIndex;

            if (place == null ||
                !TryDecodeContext(
                    place.hotSlotIndex,
                    out landmarkIndex,
                    out hotbarIndex))
            {
                return false;
            }

            result =
                HandlePlaceMessage(
                    creator,
                    place,
                    remoteID,
                    landmarkIndex,
                    hotbarIndex
                );

            return true;
        }

        if (message.Type == Messages.BlockCreator_RemoveBlock)
        {
            Message_BlockCreator_RemoveBlock remove =
                message as Message_BlockCreator_RemoveBlock;

            Block block =
                remove != null
                    ? FindIslandBlock(remove.blockObjectIndex)
                    : null;

            if (block == null)
                return false;

            Raft_Network network =
                ComponentManager<Raft_Network>.Value;

            Network_Player removingPlayer =
                network != null
                    ? network.GetPlayerFromID(remove.SteamID)
                    : null;

            BlockCreator.RemoveBlock(
                block,
                removingPlayer,
                false
            );

            result = true;
            return true;
        }

        if (message.Type == Messages.BlockCreator_UpdateHealth)
        {
            Message_BlockCreator_UpdateHealth health =
                message as Message_BlockCreator_UpdateHealth;

            Block block =
                health != null
                    ? FindIslandBlock(health.blockObjectIndex)
                    : null;

            if (block == null)
                return false;

            block.MaxHealth =
                health.newMaxHealth;

            block.SetHealth(
                health.newHealth
            );

            result = true;
            return true;
        }

        if (message.Type == Messages.BlockCreator_SetReinforced)
        {
            Message_BlockCreator_SetReinforced reinforced =
                message as Message_BlockCreator_SetReinforced;

            Block block =
                reinforced != null
                    ? FindIslandBlock(reinforced.blockObjectIndex)
                    : null;

            if (block == null)
                return false;

            Raft_Network network =
                ComponentManager<Raft_Network>.Value;

            Network_Player player =
                network != null
                    ? network.GetPlayerFromID(reinforced.SteamID)
                    : null;

            if (player == null ||
                player.HammerScript == null)
            {
                result = false;
                return true;
            }

            result =
                player.HammerScript.ReinforceBlock(
                    block
                );

            return true;
        }

        return false;
    }

    private static bool HandlePlaceMessage(
        BlockCreator creator,
        Message_BlockCreator_PlaceBlock message,
        Network_UserId remoteID,
        long landmarkIndex,
        int hotbarIndex)
    {
        Landmark landmark =
            FindLandmark(landmarkIndex);

        Item_Base item =
            ItemManager.GetItemByIndex(
                message.uniqueBlockIndex
            );

        Network_Player player =
            creator.GetPlayerNetwork();

        if (landmark == null ||
            item == null ||
            player == null)
        {
            return false;
        }

        Vector3 worldPosition =
            landmark.transform.TransformPoint(
                message.LocalPosition
            );

        Quaternion localRotation =
            Quaternion.Euler(
                message.LocalEuler
            );

        Quaternion worldRotation =
            landmark.transform.rotation *
            localRotation;

        if (Raft_Network.IsHost)
        {
            if (player.steamID != remoteID ||
                !BlockCreator_Update_IslandLife.CanStartIslandBuild(item) ||
                Vector3.Distance(
                    player.transform.position,
                    worldPosition) > Player.UseDistance * 3f)
            {
                return false;
            }

            Block prefab =
                item.settings_buildable.GetBlockPrefab(
                    message.dpsType
                );

            if (prefab == null ||
                !creator.HasEnoughResourcesToBuild(prefab))
            {
                return false;
            }

            if (item.settings_buildable.Placeable)
            {
                Slot slot =
                    hotbarIndex >= 0
                        ? player.Inventory.GetSlot(hotbarIndex)
                        : null;

                if (slot == null ||
                    slot.IsEmpty)
                {
                    return false;
                }
            }

            IslandBuildRoot root =
                BlockCreator_Update_IslandLife.GetOrCreateRoot(
                    landmark,
                    worldPosition,
                    worldRotation,
                    GetTerrainLayer(
                        landmark,
                        worldPosition
                    )
                );

            Block block =
                CreateAuthoritative(
                    creator,
                    player,
                    item,
                    message.dpsType,
                    landmark,
                    message.LocalPosition,
                    localRotation,
                    hotbarIndex,
                    false
                );

            if (block == null)
                return false;

            message.blockObjectIndex =
                block.ObjectIndex;

            if (block.networkedBehaviour != null)
            {
                message.networkedObjectIndex =
                    block.networkedBehaviour.ObjectIndex;

                message.networkedBehaviourIndex =
                    block.networkedBehaviour.BehaviourIndex;
            }

            return true;
        }

        if (message.blockObjectIndex == 0U ||
            FindIslandBlock(message.blockObjectIndex) != null)
        {
            return false;
        }

        IslandBuildRoot clientRoot =
            BlockCreator_Update_IslandLife.GetOrCreateRoot(
                landmark,
                worldPosition,
                worldRotation,
                GetTerrainLayer(
                    landmark,
                    worldPosition
                )
            );

        bool consume =
            player.IsLocalPlayer &&
            hotbarIndex >= 0;

        BlockCreator_Update_IslandLife.CreateIslandBlock(
            creator,
            player,
            item,
            message.dpsType,
            clientRoot,
            worldPosition,
            worldRotation,
            consume,
            "net-" + message.blockObjectIndex,
            false,
            -1,
            -1,
            false,
            true,
            hotbarIndex,
            message.blockObjectIndex,
            message.networkedObjectIndex,
            message.networkedBehaviourIndex
        );

        return false;
    }

    private static Block CreateAuthoritative(
        BlockCreator creator,
        Network_Player player,
        Item_Base item,
        DPS dpsType,
        Landmark landmark,
        Vector3 localPosition,
        Quaternion localRotation,
        int hotbarIndex,
        bool consumeResources)
    {
        Vector3 worldPosition =
            landmark.transform.TransformPoint(
                localPosition
            );

        Quaternion worldRotation =
            landmark.transform.rotation *
            localRotation;

        IslandBuildRoot root =
            BlockCreator_Update_IslandLife.GetOrCreateRoot(
                landmark,
                worldPosition,
                worldRotation,
                GetTerrainLayer(
                    landmark,
                    worldPosition
                )
            );

        if (HasSameBlock(
            root,
            item,
            worldPosition))
        {
            return null;
        }

        return BlockCreator_Update_IslandLife.CreateIslandBlock(
            creator,
            player,
            item,
            dpsType,
            root,
            worldPosition,
            worldRotation,
            consumeResources,
            null,
            true,
            -1,
            -1,
            false,
            true,
            hotbarIndex
        );
    }

    private static Message_BlockCreator_PlaceBlock CreatePlaceMessage(
        BlockCreator creator,
        Item_Base item,
        DPS dpsType,
        int encodedContext,
        Vector3 localPosition,
        Quaternion localRotation,
        Block block)
    {
        return new Message_BlockCreator_PlaceBlock(
            Messages.BlockCreator_PlaceBlock,
            creator,
            item.UniqueIndex,
            block.ObjectIndex,
            block.networkedBehaviour != null
                ? block.networkedBehaviour.ObjectIndex
                : 0U,
            block.networkedBehaviour != null
                ? block.networkedBehaviour.BehaviourIndex
                : 0U,
            localPosition,
            localRotation.eulerAngles,
            encodedContext,
            dpsType
        );
    }

    public static void AppendWorldMessages(
        BlockCreator creator)
    {
        if (!Raft_Network.IsHost ||
            creator == null)
        {
            return;
        }

        Raft_Network network =
            ComponentManager<Raft_Network>.Value;

        Network_Player player =
            creator.GetPlayerNetwork();

        if (network == null ||
            player == null ||
            player.steamID != network.HostID)
        {
            return;
        }

        List<IslandBlockTag> tags =
            new List<IslandBlockTag>(
                IslandBlockTag.ActiveTags
            );

        for (int i = 0; i < tags.Count; i++)
        {
            IslandBlockTag tag =
                tags[i];

            Block block =
                tag != null
                    ? tag.GetComponent<Block>()
                    : null;

            if (block == null ||
                block.buildableItem == null ||
                tag.Root == null ||
                tag.Root.Landmark == null)
            {
                continue;
            }

            int encoded =
                EncodeContext(
                    tag.Root.Landmark,
                    -1
                );

            if (encoded == 0)
                continue;

            Vector3 localPosition =
                tag.Root.Landmark.transform.InverseTransformPoint(
                    block.transform.position
                );

            Quaternion localRotation =
                Quaternion.Inverse(
                    tag.Root.Landmark.transform.rotation
                ) *
                block.transform.rotation;

            Message_BlockCreator_PlaceBlock place =
                CreatePlaceMessage(
                    creator,
                    block.buildableItem,
                    block.dpsType,
                    encoded,
                    localPosition,
                    localRotation,
                    block
                );

            creator.MessagesStack.Add(
                place
            );

            if (block.Health != block.MaxHealth)
            {
                creator.MessagesStack.Add(
                    new Message_BlockCreator_UpdateHealth(
                        Messages.BlockCreator_UpdateHealth,
                        creator,
                        block.ObjectIndex,
                        block.Health,
                        block.MaxHealth
                    )
                );
            }
        }
    }

    public static void BroadcastExisting(
        Block block)
    {
        if (!Raft_Network.IsHost ||
            block == null)
        {
            return;
        }

        IslandBlockTag tag =
            block.GetComponent<IslandBlockTag>();

        if (tag == null ||
            tag.Root == null ||
            tag.Root.Landmark == null ||
            block.buildableItem == null)
        {
            return;
        }

        Raft_Network network =
            ComponentManager<Raft_Network>.Value;

        Network_Player host =
            network != null
                ? network.GetLocalPlayer()
                : null;

        if (network == null ||
            host == null ||
            host.BlockCreator == null)
        {
            return;
        }

        int encoded =
            EncodeContext(
                tag.Root.Landmark,
                -1
            );

        if (encoded == 0)
            return;

        Vector3 localPosition =
            tag.Root.Landmark.transform.InverseTransformPoint(
                block.transform.position
            );

        Quaternion localRotation =
            Quaternion.Inverse(
                tag.Root.Landmark.transform.rotation
            ) *
            block.transform.rotation;

        Message_BlockCreator_PlaceBlock message =
            CreatePlaceMessage(
                host.BlockCreator,
                block.buildableItem,
                block.dpsType,
                encoded,
                localPosition,
                localRotation,
                block
            );

        network.RPC(
            message,
            Target.Other,
            EP2PSend.k_EP2PSendReliable,
            NetworkChannel.Channel_Game
        );

        if (block.Health != block.MaxHealth)
        {
            network.RPC(
                new Message_BlockCreator_UpdateHealth(
                    Messages.BlockCreator_UpdateHealth,
                    host.BlockCreator,
                    block.ObjectIndex,
                    block.Health,
                    block.MaxHealth
                ),
                Target.Other,
                EP2PSend.k_EP2PSendReliable,
                NetworkChannel.Channel_Game
            );
        }
    }

    public static Block FindIslandBlock(
        uint objectIndex)
    {
        foreach (IslandBlockTag tag
                 in IslandBlockTag.ActiveTags)
        {
            if (tag == null)
                continue;

            Block block =
                tag.GetComponent<Block>();

            if (block != null &&
                block.ObjectIndex == objectIndex)
            {
                return block;
            }
        }

        return null;
    }

    private static bool HasSameBlock(
        IslandBuildRoot root,
        Item_Base item,
        Vector3 position)
    {
        if (root == null ||
            item == null)
        {
            return true;
        }

        IslandBlockTag[] tags =
            root.GetComponentsInChildren<IslandBlockTag>(
                true
            );

        for (int i = 0; i < tags.Length; i++)
        {
            Block block =
                tags[i] != null
                    ? tags[i].GetComponent<Block>()
                    : null;

            if (block == null ||
                block.buildableItem == null)
            {
                continue;
            }

            if (block.buildableItem.UniqueIndex ==
                    item.UniqueIndex &&
                Vector3.Distance(
                    block.transform.position,
                    position) <= 0.05f)
            {
                return true;
            }
        }

        return false;
    }

    private static Landmark FindLandmark(
        long uniqueIndex)
    {
        Landmark[] landmarks =
            UnityEngine.Object.FindObjectsOfType<Landmark>();

        for (int i = 0; i < landmarks.Length; i++)
        {
            Landmark landmark =
                landmarks[i];

            if (landmark != null &&
                (long)landmark.uniqueLandmarkIndex ==
                    uniqueIndex)
            {
                return landmark;
            }
        }

        return null;
    }

    private static int GetTerrainLayer(
        Landmark landmark,
        Vector3 position)
    {
        IslandBuildRoot[] roots =
            landmark.GetComponentsInChildren<IslandBuildRoot>(
                true
            );

        if (roots != null &&
            roots.Length > 0)
        {
            return roots[0].TerrainLayer;
        }

        Collider[] colliders =
            landmark.GetComponentsInChildren<Collider>(
                true
            );

        float bestDistance =
            float.MaxValue;

        int bestLayer =
            landmark.gameObject.layer;

        for (int i = 0; i < colliders.Length; i++)
        {
            Collider collider =
                colliders[i];

            if (collider == null ||
                collider.GetComponentInParent<Block>() != null)
            {
                continue;
            }

            float distance =
                Vector3.Distance(
                    collider.ClosestPoint(position),
                    position
                );

            if (distance >= bestDistance)
                continue;

            bestDistance = distance;
            bestLayer = collider.gameObject.layer;
        }

        return bestLayer;
    }

    private static int EncodeContext(
        Landmark landmark,
        int hotbarIndex)
    {
        if (landmark == null)
            return 0;

        long landmarkIndex =
            (long)landmark.uniqueLandmarkIndex;

        long maxLandmark =
            (IslandMarker - 1) >> SlotBits;

        if (landmarkIndex < 0 ||
            landmarkIndex > maxLandmark)
        {
            Debug.LogWarning(
                "[IslandLife] Индекс острова слишком большой для сетевого сообщения"
            );

            return 0;
        }

        int slotCode =
            hotbarIndex >= 0
                ? Mathf.Clamp(
                    hotbarIndex + 1,
                    1,
                    SlotMask
                )
                : 0;

        int raw =
            IslandMarker |
            ((int)landmarkIndex << SlotBits) |
            slotCode;

        return -raw;
    }

    private static bool TryDecodeContext(
        int encoded,
        out long landmarkIndex,
        out int hotbarIndex)
    {
        landmarkIndex = 0;
        hotbarIndex = -1;

        if (encoded >= 0 ||
            encoded == int.MinValue)
        {
            return false;
        }

        int raw =
            -encoded;

        if ((raw & IslandMarker) == 0)
            return false;

        landmarkIndex =
            (raw & (IslandMarker - 1)) >>
            SlotBits;

        int slotCode =
            raw & SlotMask;

        hotbarIndex =
            slotCode > 0
                ? slotCode - 1
                : -1;

        return true;
    }
}

[HarmonyPatch(typeof(BlockCreator), "Deserialize")]
public static class BlockCreator_Deserialize_IslandLife
{
    [HarmonyPrefix]
    public static bool Prefix(
        BlockCreator __instance,
        Message_NetworkBehaviour msg,
        Network_UserId remoteID,
        ref bool __result)
    {
        bool handled =
            IslandLifeNetwork.TryHandleDeserialize(
                __instance,
                msg,
                remoteID,
                out __result
            );

        return !handled;
    }
}

[HarmonyPatch(typeof(BlockCreator), "Serialize_Create")]
public static class BlockCreator_SerializeCreate_IslandLife
{
    [HarmonyPostfix]
    public static void Postfix(
        BlockCreator __instance)
    {
        IslandLifeNetwork.AppendWorldMessages(
            __instance
        );
    }
}

[HarmonyPatch(typeof(BlockCreator), "GetBlockByObjectIndex")]
public static class BlockCreator_GetBlockByObjectIndex_IslandLife
{
    [HarmonyPostfix]
    public static void Postfix(
        uint objectIndex,
        ref Block __result)
    {
        if (__result == null)
        {
            __result =
                IslandLifeNetwork.FindIslandBlock(
                    objectIndex
                );
        }
    }
}

[HarmonyPatch(typeof(BlockCreator), "RemoveBlock")]
public static class BlockCreator_RemoveBlock_IslandLife
{
    [HarmonyPrefix]
    public static void Prefix(Block block, ref bool updateRaftBounds)
    {
        IslandBlockTag tag = block != null ? block.GetComponent<IslandBlockTag>() : null;
        if (tag == null)
            return;

        updateRaftBounds = false;
        IslandLifeCollisionCleanup.Cleanup(tag);
        IslandLifeStorage.RemoveRecord(tag.RecordId);
    }
}

[HarmonyPatch(typeof(Block), "OnDestroy")]
public static class Block_OnDestroy_IslandLife
{
    [HarmonyPrefix]
    public static void Prefix(Block __instance)
    {
        IslandBlockTag tag = __instance != null ? __instance.GetComponent<IslandBlockTag>() : null;
        if (tag != null)
            IslandLifeCollisionCleanup.Cleanup(tag);
    }
}

[Serializable]
public class IslandLifeSaveData
{
    [OptionalField]
    public int version = 1;
    public List<IslandBlockRecord> blocks = new List<IslandBlockRecord>();
}

[Serializable]
public class IslandBlockRecord
{
    public string id;
    public long landmarkIndex;
    public int itemIndex;
    [OptionalField]
    public string itemName;
    public int dpsType;
    public int terrainLayer;

    public float px;
    public float py;
    public float pz;

    public float rx;
    public float ry;
    public float rz;

    public int health;
    public int maxHealth;
    public bool reinforced;
}

public static class IslandLifeStorage
{
    // Имя сохраняется между версиями
    private const string FileName = "IslandLife.dat";
    private const string LegacyFileName = "IslandBuilding.dat";

    private static IslandLifeSaveData data = new IslandLifeSaveData();
    private static string loadedWorldKey = string.Empty;
    private static bool restoring;
    private static bool storageHealthy = true;

    public static void RegisterBlock(Block block, IslandBlockTag tag)
    {
        if (restoring || block == null || tag == null || tag.Root == null || tag.Root.Landmark == null)
            return;

        EnsureLoaded();

        IslandBlockRecord record = FindRecord(tag.RecordId);
        if (record == null)
        {
            record = new IslandBlockRecord { id = tag.RecordId };
            data.blocks.Add(record);
        }

        FillRecord(record, block, tag);
    }

    public static void RemoveRecord(string recordId)
    {
        if (!Raft_Network.IsHost || string.IsNullOrEmpty(recordId))
            return;

        EnsureLoaded();
        data.blocks.RemoveAll(r => r != null && r.id == recordId);
    }

    public static void SaveCurrentWorld()
    {
        if (!Raft_Network.IsHost)
            return;

        EnsureLoaded();

        if (!storageHealthy)
        {
            Debug.LogWarning("[IslandLife] Сохранение пропущено - старый файл не удалось прочитать");
            return;
        }

        CaptureActiveBlocks();
        WriteData(GetSavePath(FileName), true);
    }

    public static void LoadCurrentWorld(bool force)
    {
        if (!Raft_Network.IsHost)
            return;

        string worldKey = GetWorldKey();
        if (!force && loadedWorldKey == worldKey)
            return;

        loadedWorldKey = worldKey;
        data = new IslandLifeSaveData();
        storageHealthy = true;

        string legacyPath = GetSavePath(LegacyFileName);
        string currentPath = GetSavePath(FileName);

        bool legacyExists = !string.IsNullOrEmpty(legacyPath) && File.Exists(legacyPath);

        bool loadedLegacy = legacyExists && MergeFile(legacyPath, false);
        bool loadedCurrent = MergeFile(currentPath, true);

        if (storageHealthy && (loadedLegacy || loadedCurrent))
        {
            data.version = 1;

            if (loadedLegacy)
            {
                bool migrated = WriteData(currentPath, true);

                if (migrated)
                    ArchiveLegacySave(legacyPath);
            }
        }

        Debug.Log("[IslandLife] Постройки островов загружены: " + data.blocks.Count);
    }

    public static void RestoreAllActiveLandmarks()
    {
        if (!Raft_Network.IsHost)
            return;

        EnsureLoaded();

        Landmark[] landmarks = UnityEngine.Object.FindObjectsOfType<Landmark>();
        for (int i = 0; i < landmarks.Length; i++)
            RestoreLandmark(landmarks[i]);
    }

    public static void RestoreLandmark(Landmark landmark)
    {
        if (!Raft_Network.IsHost || landmark == null)
            return;

        EnsureLoaded();

        Raft_Network network = ComponentManager<Raft_Network>.Value;
        Network_Player player = network != null ? network.GetLocalPlayer() : null;

        if (player == null || player.BlockCreator == null)
            return;

        HashSet<string> existing = new HashSet<string>();
        IslandBlockTag[] currentTags = landmark.GetComponentsInChildren<IslandBlockTag>(true);

        for (int i = 0; i < currentTags.Length; i++)
        {
            IslandBlockTag tag = currentTags[i];
            if (tag != null && !string.IsNullOrEmpty(tag.RecordId))
                existing.Add(tag.RecordId);
        }

        restoring = true;

        try
        {
            for (int i = 0; i < data.blocks.Count; i++)
            {
                IslandBlockRecord record = data.blocks[i];

                if (record == null ||
                    record.landmarkIndex != (long)landmark.uniqueLandmarkIndex ||
                    existing.Contains(record.id))
                    continue;

                Item_Base item = !string.IsNullOrEmpty(record.itemName)
                    ? ItemManager.GetItemByName(record.itemName)
                    : null;

                if (item == null)
                    item = ItemManager.GetItemByIndex(record.itemIndex);

                if (item == null)
                    continue;

                Vector3 localPosition = new Vector3(record.px, record.py, record.pz);
                Quaternion localRotation = Quaternion.Euler(record.rx, record.ry, record.rz);

                Vector3 worldPosition = landmark.transform.TransformPoint(localPosition);
                Quaternion worldRotation = landmark.transform.rotation * localRotation;

                IslandBuildRoot root = BlockCreator_Update_IslandLife.GetOrCreateRoot(
                    landmark,
                    worldPosition,
                    worldRotation,
                    record.terrainLayer);

                Block restored =
                    BlockCreator_Update_IslandLife.CreateIslandBlock(
                        player.BlockCreator,
                        player,
                        item,
                        (DPS)record.dpsType,
                        root,
                        worldPosition,
                        worldRotation,
                        false,
                        record.id,
                        false,
                        record.health,
                        record.maxHealth,
                        record.reinforced,
                        false
                    );

                if (restored != null)
                    IslandLifeNetwork.BroadcastExisting(restored);
            }
        }
        finally
        {
            restoring = false;
        }
    }

    private static void EnsureLoaded()
    {
        if (loadedWorldKey != GetWorldKey())
            LoadCurrentWorld(true);
    }

    private static void CaptureActiveBlocks()
    {
        IslandBlockTag[] tags = UnityEngine.Object.FindObjectsOfType<IslandBlockTag>();

        for (int i = 0; i < tags.Length; i++)
        {
            IslandBlockTag tag = tags[i];

            if (tag == null || tag.Root == null || tag.Root.Landmark == null)
                continue;

            Block block = tag.GetComponent<Block>();
            if (block == null)
                continue;

            if (string.IsNullOrEmpty(tag.RecordId))
                tag.RecordId = Guid.NewGuid().ToString("N");

            IslandBlockRecord record = FindRecord(tag.RecordId);
            if (record == null)
            {
                record = new IslandBlockRecord { id = tag.RecordId };
                data.blocks.Add(record);
            }

            FillRecord(record, block, tag);
        }
    }

    private static void FillRecord(IslandBlockRecord record, Block block, IslandBlockTag tag)
    {
        Landmark landmark = tag.Root.Landmark;
        Vector3 localPosition = landmark.transform.InverseTransformPoint(block.transform.position);
        Quaternion localRotation = Quaternion.Inverse(landmark.transform.rotation) * block.transform.rotation;
        Vector3 euler = localRotation.eulerAngles;

        record.landmarkIndex = (long)landmark.uniqueLandmarkIndex;
        record.itemIndex = block.buildableItem != null ? block.buildableItem.UniqueIndex : 0;
        record.itemName = block.buildableItem != null ? block.buildableItem.UniqueName : null;
        record.dpsType = (int)block.dpsType;
        record.terrainLayer = tag.Root.TerrainLayer;

        record.px = localPosition.x;
        record.py = localPosition.y;
        record.pz = localPosition.z;

        record.rx = euler.x;
        record.ry = euler.y;
        record.rz = euler.z;

        record.health = block.Health;
        record.maxHealth = block.MaxHealth;
        record.reinforced = block.Reinforced;
    }

    private static IslandBlockRecord FindRecord(string id)
    {
        if (data == null || data.blocks == null || string.IsNullOrEmpty(id))
            return null;

        return data.blocks.Find(r => r != null && r.id == id);
    }

    private static bool MergeFile(string path, bool overwriteExisting)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;

        try
        {
            IslandLifeSaveData loaded = ReadData(path);
            if (loaded == null || loaded.blocks == null)
                return false;

            for (int i = 0; i < loaded.blocks.Count; i++)
            {
                IslandBlockRecord incoming = loaded.blocks[i];
                if (incoming == null || string.IsNullOrEmpty(incoming.id))
                    continue;

                IslandBlockRecord existing = FindRecord(incoming.id);

                if (existing == null)
                    data.blocks.Add(incoming);
                else if (overwriteExisting)
                    CopyRecord(incoming, existing);
            }

            return true;
        }
        catch (Exception ex)
        {
            storageHealthy = false;
            Debug.LogWarning("[IslandLife] Не удалось загрузить " + Path.GetFileName(path) + ": " + ex.Message);
            return false;
        }
    }

    private static IslandLifeSaveData ReadData(string path)
    {
        BinaryFormatter formatter = new BinaryFormatter();
        formatter.Binder = new LegacySaveBinder();

        using (FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            return formatter.Deserialize(stream) as IslandLifeSaveData;
    }

    private static bool WriteData(string path, bool makeBackup)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        try
        {
            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            string tempPath = path + ".tmp";
            BinaryFormatter formatter = new BinaryFormatter();

            using (FileStream stream = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                formatter.Serialize(stream, data);

            if (File.Exists(path))
            {
                if (makeBackup)
                    File.Copy(path, path + ".bak", true);

                File.Delete(path);
            }

            File.Move(tempPath, path);
            Debug.Log("[IslandLife] Постройки островов сохранены: " + data.blocks.Count);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[IslandLife] Не удалось сохранить постройки: " + ex.Message);
            return false;
        }
    }

    private static void ArchiveLegacySave(string path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        try
        {
            string backup = path + ".migrated.bak";

            if (File.Exists(backup))
                File.Delete(backup);

            File.Move(path, backup);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[IslandLife] Не удалось архивировать старое сохранение: " + ex.Message);
        }
    }

    private static void CopyRecord(IslandBlockRecord source, IslandBlockRecord target)
    {
        target.id = source.id;
        target.landmarkIndex = source.landmarkIndex;
        target.itemIndex = source.itemIndex;
        target.itemName = source.itemName;
        target.dpsType = source.dpsType;
        target.terrainLayer = source.terrainLayer;

        target.px = source.px;
        target.py = source.py;
        target.pz = source.pz;

        target.rx = source.rx;
        target.ry = source.ry;
        target.rz = source.rz;

        target.health = source.health;
        target.maxHealth = source.maxHealth;
        target.reinforced = source.reinforced;
    }

    private static string GetWorldKey()
    {
        return SaveAndLoad.CurrentGameFileName ?? string.Empty;
    }

    private static string GetSavePath(string fileName)
    {
        string worldKey = GetWorldKey();

        if (string.IsNullOrEmpty(worldKey) || string.IsNullOrEmpty(SaveAndLoad.WorldPath))
            return null;

        return Path.Combine(SaveAndLoad.WorldPath, worldKey, fileName);
    }

    private sealed class LegacySaveBinder : SerializationBinder
    {
        public override Type BindToType(string assemblyName, string typeName)
        {
            if (typeName == "IslandBuildingSaveData" || typeName == "IslandLifeSaveData" ||
                typeName.EndsWith(".IslandBuildingSaveData") || typeName.EndsWith(".IslandLifeSaveData"))
                return typeof(IslandLifeSaveData);

            if (typeName == "IslandBlockRecord" || typeName.EndsWith(".IslandBlockRecord"))
                return typeof(IslandBlockRecord);

            if (typeName.StartsWith("System.Collections.Generic.List`1") &&
                typeName.Contains("IslandBlockRecord"))
                return typeof(List<IslandBlockRecord>);

            Type resolved = Type.GetType(typeName + ", " + assemblyName, false) ?? Type.GetType(typeName, false);
            if (resolved != null)
                return resolved;

            throw new SerializationException("Не удалось восстановить тип " + typeName);
        }
    }
}

[HarmonyPatch(typeof(SaveAndLoad), "SaveGame")]
public static class SaveAndLoad_SaveGame_IslandLife
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        IslandLifeStorage.SaveCurrentWorld();
    }
}

[HarmonyPatch(typeof(SaveAndLoad), "RestoreRGDGame")]
public static class SaveAndLoad_RestoreRGDGame_IslandLife
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        IslandLifeStorage.LoadCurrentWorld(true);
        IslandLifeStorage.RestoreAllActiveLandmarks();
    }
}

[HarmonyPatch(typeof(Landmark), "OnSpawn")]
public static class Landmark_OnSpawn_IslandLife
{
    [HarmonyPostfix]
    public static void Postfix(Landmark __instance)
    {
        IslandLifeStorage.RestoreLandmark(__instance);
    }
}

[HarmonyPatch(typeof(SaveAndLoad), "CreateRGDGame")]
public static class SaveAndLoad_CreateRGDGame_IslandLife
{
    [HarmonyPostfix]
    public static void Postfix(RGD_Game __result)
    {
        if (__result == null || __result.behaviours == null)
            return;

        IslandBlockTag[] tags = UnityEngine.Object.FindObjectsOfType<IslandBlockTag>();
        if (tags == null || tags.Length == 0)
            return;

        HashSet<uint> objectIndexes = new HashSet<uint>();

        for (int i = 0; i < tags.Length; i++)
        {
            IslandBlockTag tag = tags[i];
            Block block = tag != null ? tag.GetComponent<Block>() : null;

            if (block != null)
                objectIndexes.Add(block.ObjectIndex);
        }

        __result.behaviours.RemoveAll(rgd =>
        {
            RGD_Block blockData = rgd as RGD_Block;
            return blockData != null && objectIndexes.Contains(blockData.BlockObjectIndex);
        });
    }
}
