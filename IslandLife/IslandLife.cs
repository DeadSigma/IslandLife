using HarmonyLib;
using HMLLibrary;
using FMODUnity;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
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
        if (harmony != null)
        {
            harmony.UnpatchAll(harmony.Id);
        }

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
    public static readonly HashSet<IslandBlockTag> ActiveTags =
        new HashSet<IslandBlockTag>();

    public IslandBuildRoot Root;
    public string RecordId;
    public Collider[] OriginalPhysicalColliders;
    public GameObject[] CollisionProxies;

    private void OnEnable()
    {
        ActiveTags.Add(this);
    }

    private void OnDisable()
    {
        ActiveTags.Remove(this);
    }

    private void OnDestroy()
    {
        ActiveTags.Remove(this);
    }
}

public class IslandCollisionProxy : MonoBehaviour
{
    public IslandBuildRoot Root;
    public Block SourceBlock;
}

[HarmonyPatch(typeof(BlockCreator), "Update")]
public static class BlockCreator_Update_IslandLife
{
    private static readonly FieldInfo QuadAtCursorField =
        AccessTools.Field(typeof(BlockCreator), "quadAtCursor");

    private static readonly FieldInfo QuadSurfaceField =
        AccessTools.Field(typeof(BlockCreator), "quadSurface");

    private static readonly FieldInfo QuadHitField =
        AccessTools.Field(typeof(BlockCreator), "quadHit");

    private static readonly FieldInfo SelectedBuildablePrefabField =
        AccessTools.Field(typeof(BlockCreator), "selectedBuildablePrefab");

    private static readonly FieldInfo ColliderPrefabEnablerField =
        AccessTools.Field(typeof(BlockCreator), "colliderPrefabEnabler");

    private static readonly FieldInfo EventRefPlaceBlockField =
        AccessTools.Field(typeof(BlockCreator), "eventRef_placeBlock");

    private static readonly FieldInfo EventRefCreateBlockField =
        AccessTools.Field(typeof(BlockCreator), "eventRef_createBlock");

    private static readonly MethodInfo HandleRotationMethod =
        AccessTools.Method(typeof(BlockCreator), "HandleRotationOfSelectedBlock");

    private static readonly MethodInfo HandleMirroredMethod =
        AccessTools.Method(typeof(BlockCreator), "HandleMirroredVersion");

    private static readonly MethodInfo SetGhostPositionMethod =
        AccessTools.Method(typeof(BlockCreator), "SetGhostBlockPositionAndRotation");

    private const float GroundClearance = 0.001f;
    private const float MinGroundNormalY = 0.55f;

    [HarmonyPrefix]
    public static bool Prefix(
        BlockCreator __instance,
        Item_Base ___selectedBuildableItem,
        Network_Player ___playerNetwork)
    {
        if (__instance == null ||
            ___playerNetwork == null ||
            !___playerNetwork.IsLocalPlayer ||
            ___selectedBuildableItem == null)
        {
            return true;
        }

        if (!Raft_Network.IsHost)
        {
            return true;
        }

        if (CanvasHelper.ActiveMenu != MenuType.None ||
            MyInput.GetButtonDown("RMB") ||
            MyInput.GetButtonUp("RMB"))
        {
            RestoreGhostToRaftPivot(__instance);
            return true;
        }

        ColliderPrefabEnabler colliderEnabler =
            ColliderPrefabEnablerField.GetValue(__instance)
                as ColliderPrefabEnabler;

        if (colliderEnabler != null)
        {
            colliderEnabler.ShowCollider();
        }

        Block selectedPrefab =
            SelectedBuildablePrefabField.GetValue(__instance)
                as Block;

        if (__instance.selectedBlock == null ||
            selectedPrefab == null)
        {
            QuadSurfaceField.SetValue(
                __instance,
                null
            );

            __instance.SetBlockTypeToBuild(
                ___selectedBuildableItem.UniqueName
            );
        }

        Vector3 floorGridPosition;
        IslandBuildRoot floorGridRoot;

        if (TryGetFloorGridSnap(
            ___playerNetwork,
            ___selectedBuildableItem,
            out floorGridPosition,
            out floorGridRoot))
        {
            HandleFloorGridSnap(
                __instance,
                ___playerNetwork,
                ___selectedBuildableItem,
                floorGridRoot,
                floorGridPosition
            );

            return false;
        }

        RaycastHit quadHit;
        BlockQuad islandQuad;
        IslandBuildRoot islandRoot;

        if (TryGetIslandQuad(
            ___playerNetwork,
            ___selectedBuildableItem,
            out quadHit,
            out islandQuad,
            out islandRoot))
        {
            HandleIslandQuad(
                __instance,
                ___playerNetwork,
                ___selectedBuildableItem,
                quadHit,
                islandQuad,
                islandRoot
            );

            return false;
        }

        BlockQuad floorSnapQuad;
        IslandBuildRoot floorSnapRoot;

        if (TryGetFloorToFloorSnap(
            ___playerNetwork,
            ___selectedBuildableItem,
            out floorSnapQuad,
            out floorSnapRoot))
        {
            HandleFloorToFloor(
                __instance,
                ___playerNetwork,
                ___selectedBuildableItem,
                floorSnapRoot,
                floorSnapQuad
            );

            return false;
        }

        if (!CanStartIslandBuild(___selectedBuildableItem))
        {
            RestoreGhostToRaftPivot(__instance);
            return true;
        }

        RaycastHit terrainHit;
        Landmark landmark;

        if (!TryGetIslandTerrain(
            ___playerNetwork,
            out terrainHit,
            out landmark))
        {
            RestoreGhostToRaftPivot(__instance);
            return true;
        }

        HandleTerrainBlock(
            __instance,
            ___playerNetwork,
            ___selectedBuildableItem,
            terrainHit,
            landmark
        );

        return false;
    }

    private static void HandleIslandRotation(
        BlockCreator creator)
    {
        if (creator == null ||
            creator.selectedBlock == null)
        {
            return;
        }

        Block ghost =
            creator.selectedBlock;

        Item_Base item =
            ghost.buildableItem;

        bool freeRotation =
            item != null &&
            (Block.IsBlockIndexFoundation(item.UniqueIndex) ||
             Block.IsBlockIndexFloor(item.UniqueIndex) ||
             Block.IsBlockIndexRaisedFloor(item.UniqueIndex));

        if (!freeRotation)
        {
            HandleRotationMethod.Invoke(
                creator,
                null
            );

            return;
        }

        bool oldCanRotateFreely =
            ghost.canRotateFreely;

        ghost.canRotateFreely = true;

        try
        {
            HandleRotationMethod.Invoke(
                creator,
                null
            );
        }
        finally
        {
            if (creator.selectedBlock == ghost)
            {
                ghost.canRotateFreely =
                    oldCanRotateFreely;
            }
        }
    }

    private static void DetachGhostFromRaftPivot(
        Block ghost)
    {
        if (ghost == null ||
            ghost.transform.parent == null)
        {
            return;
        }

        ghost.transform.SetParent(
            null,
            true
        );
    }

    private static void RestoreGhostToRaftPivot(
        BlockCreator creator)
    {
        if (creator == null ||
            creator.selectedBlock == null)
        {
            return;
        }

        GameManager gameManager =
            SingletonGeneric<GameManager>.Singleton;

        if (gameManager == null ||
            gameManager.lockedPivot == null ||
            creator.selectedBlock.transform.parent ==
                gameManager.lockedPivot)
        {
            return;
        }

        creator.selectedBlock.transform.SetParent(
            gameManager.lockedPivot,
            true
        );
    }

    private static bool CanStartIslandBuild(Item_Base item)
    {
        if (item == null ||
            item.settings_buildable == null)
        {
            return false;
        }

        Block prefab =
            item.settings_buildable.GetBlockPrefab(DPS.Default);

        if (prefab == null)
        {
            return false;
        }

        return !IsRaftNavigationBlock(
            item,
            prefab
        );
    }

    private static bool IsRaftNavigationBlock(
        Item_Base item,
        Block prefab)
    {
        if (item == null ||
            prefab == null)
        {
            return false;
        }

        if (prefab.GetComponentInChildren<Sail>(true) != null ||
            prefab.GetComponentInChildren<MotorWheel>(true) != null ||
            prefab.GetComponentInChildren<SteeringWheel>(true) != null ||
            prefab.GetComponentInChildren<Anchor_Stationary>(true) != null)
        {
            return true;
        }

        string name =
            item.UniqueName ?? string.Empty;

        string normalized =
            name.Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .ToLowerInvariant();

        string[] blockedNames =
        {
            "engine",
            "motor",
            "anchor",
            "sail",
            "steeringwheel",
            "engineswitch",
            "enginecontrol",
            "enginecontrols",
            "rudder"
        };

        for (int i = 0; i < blockedNames.Length; i++)
        {
            if (normalized.Contains(blockedNames[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetFloorGridSnap(
        Network_Player player,
        Item_Base item,
        out Vector3 position,
        out IslandBuildRoot root)
    {
        position = Vector3.zero;
        root = null;

        if (player == null ||
            player.CameraTransform == null ||
            item == null ||
            !Block.IsBlockIndexFloor(item.UniqueIndex))
        {
            return false;
        }

        Ray ray = new Ray(
            player.CameraTransform.position,
            player.CameraTransform.forward.normalized
        );

        float bestScore = float.MaxValue;
        bool found = false;

        IslandBlockTag[] tags =
            IslandBlockTag.ActiveTags.ToArray();

        for (int i = 0; i < tags.Length; i++)
        {
            IslandBlockTag tag = tags[i];

            if (tag == null ||
                tag.Root == null)
            {
                continue;
            }

            Block source =
                tag.GetComponent<Block>();

            if (source == null ||
                source.buildableItem == null ||
                !Block.IsBlockIndexFloor(
                    source.buildableItem.UniqueIndex))
            {
                continue;
            }

            float verticalDirection =
                ray.direction.y;

            if (Mathf.Abs(verticalDirection) < 0.001f)
            {
                continue;
            }

            float rayDistance =
                (source.transform.position.y - ray.origin.y) /
                verticalDirection;

            if (rayDistance <= 0f ||
                rayDistance > Player.UseDistance * 2f)
            {
                continue;
            }

            Vector3 cursorPoint =
                ray.GetPoint(rayDistance);

            Vector3 right =
                source.transform.right;

            Vector3 forward =
                source.transform.forward;

            right.y = 0f;
            forward.y = 0f;

            if (right.sqrMagnitude < 0.001f ||
                forward.sqrMagnitude < 0.001f)
            {
                continue;
            }

            right.Normalize();
            forward.Normalize();

            Vector3[] directions =
            {
                right,
                -right,
                forward,
                -forward
            };

            for (int directionIndex = 0;
                 directionIndex < directions.Length;
                 directionIndex++)
            {
                Vector3 candidate =
                    source.transform.position +
                    directions[directionIndex] *
                    BlockCreator.BlockSize;

                candidate.y =
                    source.transform.position.y;

                Vector3 delta =
                    cursorPoint - candidate;

                delta.y = 0f;

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
                    continue;
                }

                float score =
                    delta.sqrMagnitude;

                if (score >= bestScore)
                {
                    continue;
                }

                bestScore = score;
                position = candidate;
                root = tag.Root;
                found = true;
            }
        }

        return found;
    }

    private static bool TryGetIslandQuad(
        Network_Player player,
        Item_Base item,
        out RaycastHit hit,
        out BlockQuad quad,
        out IslandBuildRoot root)
    {
        hit = default(RaycastHit);
        quad = null;
        root = null;

        if (player.CameraTransform == null)
        {
            return false;
        }

        RaycastHit[] hits = Physics.RaycastAll(
            player.CameraTransform.position,
            player.CameraTransform.forward,
            Player.UseDistance * 2f,
            LayerMasks.MASK_BuildQuad,
            QueryTriggerInteraction.UseGlobal
        )
        .OrderBy(h => h.distance)
        .ToArray();

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit currentHit = hits[i];

            if (currentHit.transform == null)
            {
                continue;
            }

            BlockQuad currentQuad =
                currentHit.transform.GetComponent<BlockQuad>();

            if (currentQuad == null)
            {
                currentQuad =
                    currentHit.transform.GetComponentInParent<BlockQuad>();
            }

            if (currentQuad == null ||
                currentQuad.ParentBlock == null)
            {
                continue;
            }

            IslandBlockTag tag =
                currentQuad.ParentBlock.GetComponent<IslandBlockTag>();

            if (tag == null || tag.Root == null)
            {
                continue;
            }

            if (!currentQuad.AcceptsBlock(
                    item,
                    currentHit.normal))
            {
                continue;
            }

            hit = currentHit;
            quad = currentQuad;
            root = tag.Root;

            return true;
        }

        return false;
    }

    private static bool TryGetIslandTerrain(
        Network_Player player,
        out RaycastHit hit,
        out Landmark landmark)
    {
        hit = default(RaycastHit);
        landmark = null;

        if (player.CameraTransform == null)
        {
            return false;
        }

        RaycastHit[] hits = Physics.RaycastAll(
            player.CameraTransform.position,
            player.CameraTransform.forward,
            Player.UseDistance * 2f,
            ~0,
            QueryTriggerInteraction.Ignore
        )
        .OrderBy(h => h.distance)
        .ToArray();

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit currentHit = hits[i];

            if (currentHit.collider == null)
            {
                continue;
            }

            if (currentHit.collider.GetComponent<IslandCollisionProxy>() != null ||
                currentHit.collider.GetComponentInParent<IslandCollisionProxy>() != null)
            {
                continue;
            }

            float minNormalY =
                IsTerrainAlignHeld()
                    ? 0.05f
                    : MinGroundNormalY;

            if (currentHit.normal.y < minNormalY)
            {
                continue;
            }

            if (currentHit.collider.GetComponentInParent<Block>() != null)
            {
                continue;
            }

            Landmark currentLandmark =
                currentHit.collider.GetComponentInParent<Landmark>();

            if (currentLandmark == null)
            {
                continue;
            }

            hit = currentHit;
            landmark = currentLandmark;
            return true;
        }

        return false;
    }

    private static void HandleIslandQuad(
        BlockCreator creator,
        Network_Player player,
        Item_Base item,
        RaycastHit hit,
        BlockQuad quad,
        IslandBuildRoot root)
    {
        BlockSurface surface =
            quad.GetSurfaceFromNormal(hit.normal);

        if (surface == null)
        {
            creator.SetGhostBlockVisibility(false);
            return;
        }

        DPS dpsType =
            surface.dpsType;

        Block requiredPrefab =
            item.settings_buildable.GetBlockPrefab(dpsType);

        if (requiredPrefab == null)
        {
            creator.SetGhostBlockVisibility(false);
            return;
        }

        Block selectedPrefab =
            SelectedBuildablePrefabField.GetValue(creator) as Block;

        if (creator.selectedBlock == null ||
            selectedPrefab == null ||
            creator.selectedBlock.dpsType != requiredPrefab.dpsType)
        {
            creator.SetBlockTypeToBuild(
                requiredPrefab.buildableItem.UniqueName
            );

            selectedPrefab =
                SelectedBuildablePrefabField.GetValue(creator) as Block;
        }

        Block ghost = creator.selectedBlock;
        if (ghost == null || selectedPrefab == null)
        {
            return;
        }

        DetachGhostFromRaftPivot(ghost);

        QuadAtCursorField.SetValue(creator, quad);
        QuadSurfaceField.SetValue(creator, surface);
        QuadHitField.SetValue(creator, hit);

        HandleIslandRotation(creator);
        HandleMirroredMethod.Invoke(creator, null);

        ghost = creator.selectedBlock;
        if (ghost == null)
        {
            return;
        }

        DetachGhostFromRaftPivot(ghost);

        SetGhostPositionMethod.Invoke(creator, null);
        creator.SetGhostBlockVisibility(true);

        bool canBuild =
            CanBuildOnIsland(
                creator,
                ghost,
                root
            );

        SetGhostMaterial(
            ghost,
            canBuild
        );

        if (!canBuild ||
            !MyInput.GetButtonDown("LMB"))
        {
            return;
        }

        CreateIslandBlock(
            creator,
            player,
            ghost.buildableItem,
            dpsType,
            root,
            ghost.transform.position,
            ghost.transform.rotation
        );
    }

    private static bool CanBuildOnIsland(
        BlockCreator creator,
        Block ghost,
        IslandBuildRoot root)
    {
        if (creator == null ||
            ghost == null ||
            root == null)
        {
            return false;
        }

        if (!creator.HasEnoughResourcesToBuild(ghost))
        {
            return false;
        }

        IslandBlockTag[] tags =
            root.GetComponentsInChildren<IslandBlockTag>(true);

        for (int i = 0; i < tags.Length; i++)
        {
            IslandBlockTag tag = tags[i];

            if (tag == null)
            {
                continue;
            }

            Block placed =
                tag.GetComponent<Block>();

            if (placed == null ||
                placed.buildableItem == null ||
                ghost.buildableItem == null)
            {
                continue;
            }

            if (placed.buildableItem.UniqueIndex !=
                ghost.buildableItem.UniqueIndex)
            {
                continue;
            }

            if (Vector3.Distance(
                    placed.transform.position,
                    ghost.transform.position) > 0.05f)
            {
                continue;
            }

            bool gridBlock =
                Block.IsBlockIndexFoundation(
                    ghost.buildableItem.UniqueIndex) ||
                Block.IsBlockIndexFloor(
                    ghost.buildableItem.UniqueIndex) ||
                Block.IsBlockIndexRaisedFloor(
                    ghost.buildableItem.UniqueIndex);

            if (gridBlock ||
                Quaternion.Angle(
                    placed.transform.rotation,
                    ghost.transform.rotation) <= 2f)
            {
                return false;
            }
        }

        return true;
    }

    private static void HandleFloorGridSnap(
        BlockCreator creator,
        Network_Player player,
        Item_Base item,
        IslandBuildRoot root,
        Vector3 snapPosition)
    {
        if (creator == null ||
            player == null ||
            item == null ||
            root == null)
        {
            return;
        }

        Block prefab =
            item.settings_buildable.GetBlockPrefab(DPS.Default);

        if (prefab == null)
        {
            return;
        }

        Block selectedPrefab =
            SelectedBuildablePrefabField.GetValue(creator)
                as Block;

        if (creator.selectedBlock == null ||
            selectedPrefab == null ||
            creator.selectedBlock.dpsType != prefab.dpsType)
        {
            creator.SetBlockTypeToBuild(
                prefab.buildableItem.UniqueName
            );

            selectedPrefab =
                SelectedBuildablePrefabField.GetValue(creator)
                    as Block;
        }

        Block ghost =
            creator.selectedBlock;

        if (ghost == null ||
            selectedPrefab == null)
        {
            return;
        }

        DetachGhostFromRaftPivot(ghost);

        HandleIslandRotation(creator);

        HandleMirroredMethod.Invoke(
            creator,
            null
        );

        ghost =
            creator.selectedBlock;

        selectedPrefab =
            SelectedBuildablePrefabField.GetValue(creator)
                as Block;

        if (ghost == null ||
            selectedPrefab == null)
        {
            return;
        }

        DetachGhostFromRaftPivot(ghost);

        // Центр пола привязывается к выбранной пустой клетке
        ghost.transform.position =
            snapPosition;

        ghost.transform.rotation =
            Quaternion.Euler(
                0f,
                selectedPrefab.currentRotationY,
                0f
            );

        creator.SetGhostBlockVisibility(true);

        bool canBuild =
            CanBuildOnIsland(
                creator,
                ghost,
                root
            );

        SetGhostMaterial(
            ghost,
            canBuild
        );

        if (!canBuild ||
            !MyInput.GetButtonDown("LMB"))
        {
            return;
        }

        CreateIslandBlock(
            creator,
            player,
            ghost.buildableItem,
            DPS.Default,
            root,
            ghost.transform.position,
            ghost.transform.rotation
        );
    }

    private static bool TryGetFloorToFloorSnap(
        Network_Player player,
        Item_Base item,
        out BlockQuad quad,
        out IslandBuildRoot root)
    {
        quad = null;
        root = null;

        if (player == null ||
            player.CameraTransform == null ||
            item == null ||
            !Block.IsBlockIndexFloor(item.UniqueIndex))
        {
            return false;
        }

        RaycastHit[] hits = Physics.RaycastAll(
            player.CameraTransform.position,
            player.CameraTransform.forward,
            Player.UseDistance * 2f,
            LayerMasks.MASK_BuildQuad,
            QueryTriggerInteraction.UseGlobal
        )
        .OrderBy(h => h.distance)
        .ToArray();

        for (int i = 0; i < hits.Length; i++)
        {
            RaycastHit hit =
                hits[i];

            if (hit.transform == null)
            {
                continue;
            }

            BlockQuad currentQuad =
                hit.transform.GetComponent<BlockQuad>();

            if (currentQuad == null)
            {
                currentQuad =
                    hit.transform.GetComponentInParent<BlockQuad>();
            }

            if (currentQuad == null ||
                currentQuad.ParentBlock == null ||
                currentQuad.ParentBlock.buildableItem == null ||
                !Block.IsBlockIndexFloor(
                    currentQuad.ParentBlock.buildableItem.UniqueIndex))
            {
                continue;
            }

            IslandBlockTag tag =
                currentQuad.ParentBlock.GetComponent<IslandBlockTag>();

            if (tag == null ||
                tag.Root == null)
            {
                continue;
            }

            Vector3 delta =
                currentQuad.transform.position -
                currentQuad.ParentBlock.transform.position;

            Vector3 horizontal =
                new Vector3(
                    delta.x,
                    0f,
                    delta.z
                );

            // Берётся только соседняя клетка пола, не верхняя и не центральная точка
            if (Mathf.Abs(delta.y) > 0.25f ||
                horizontal.sqrMagnitude < 0.25f)
            {
                continue;
            }

            quad =
                currentQuad;

            root =
                tag.Root;

            return true;
        }

        return false;
    }

    private static void HandleFloorToFloor(
        BlockCreator creator,
        Network_Player player,
        Item_Base item,
        IslandBuildRoot root,
        BlockQuad snapQuad)
    {
        if (creator == null ||
            player == null ||
            item == null ||
            root == null ||
            snapQuad == null)
        {
            return;
        }

        Block prefab =
            item.settings_buildable.GetBlockPrefab(DPS.Default);

        if (prefab == null)
        {
            return;
        }

        Block selectedPrefab =
            SelectedBuildablePrefabField.GetValue(creator) as Block;

        if (creator.selectedBlock == null ||
            selectedPrefab == null ||
            creator.selectedBlock.dpsType != prefab.dpsType)
        {
            creator.SetBlockTypeToBuild(
                prefab.buildableItem.UniqueName
            );

            selectedPrefab =
                SelectedBuildablePrefabField.GetValue(creator) as Block;
        }

        Block ghost =
            creator.selectedBlock;

        if (ghost == null ||
            selectedPrefab == null)
        {
            return;
        }

        DetachGhostFromRaftPivot(ghost);

        HandleIslandRotation(creator);

        HandleMirroredMethod.Invoke(
            creator,
            null
        );

        ghost =
            creator.selectedBlock;

        selectedPrefab =
            SelectedBuildablePrefabField.GetValue(creator) as Block;

        if (ghost == null ||
            selectedPrefab == null)
        {
            return;
        }

        DetachGhostFromRaftPivot(ghost);

        Block sourceBlock =
            snapQuad.ParentBlock;

        if (sourceBlock == null)
        {
            creator.SetGhostBlockVisibility(false);
            return;
        }

        Vector3 direction =
            snapQuad.transform.position -
            sourceBlock.transform.position;

        direction.y = 0f;

        if (direction.sqrMagnitude < 0.001f)
        {
            creator.SetGhostBlockVisibility(false);
            return;
        }

        direction.Normalize();

        // BlockQuad пола расположен у края - центр следующей клетки смещается на полный шаг
        ghost.transform.position =
            sourceBlock.transform.position +
            direction * BlockCreator.BlockSize;

        ghost.transform.position =
            new Vector3(
                ghost.transform.position.x,
                sourceBlock.transform.position.y,
                ghost.transform.position.z
            );

        // Пол остаётся горизонтальным, меняется только его yaw
        ghost.transform.rotation =
            Quaternion.Euler(
                0f,
                selectedPrefab.currentRotationY,
                0f
            );

        creator.SetGhostBlockVisibility(true);

        bool canBuild =
            CanBuildOnIsland(
                creator,
                ghost,
                root
            );

        SetGhostMaterial(
            ghost,
            canBuild
        );

        if (!canBuild ||
            !MyInput.GetButtonDown("LMB"))
        {
            return;
        }

        CreateIslandBlock(
            creator,
            player,
            ghost.buildableItem,
            DPS.Default,
            root,
            ghost.transform.position,
            ghost.transform.rotation
        );
    }

    private static void HandleTerrainBlock(
        BlockCreator creator,
        Network_Player player,
        Item_Base item,
        RaycastHit hit,
        Landmark landmark)
    {
        Block selectedPrefab =
            SelectedBuildablePrefabField.GetValue(creator) as Block;

        if (creator.selectedBlock == null ||
            selectedPrefab == null)
        {
            creator.SetBlockTypeToBuild(item.UniqueName);

            selectedPrefab =
                SelectedBuildablePrefabField.GetValue(creator) as Block;
        }

        Block ghost = creator.selectedBlock;
        if (ghost == null || selectedPrefab == null)
        {
            return;
        }

        DetachGhostFromRaftPivot(ghost);

        HandleIslandRotation(creator);
        HandleMirroredMethod.Invoke(creator, null);

        ghost = creator.selectedBlock;
        selectedPrefab =
            SelectedBuildablePrefabField.GetValue(creator) as Block;

        if (ghost == null || selectedPrefab == null)
        {
            return;
        }

        DetachGhostFromRaftPivot(ghost);

        Vector3 baseEuler =
            selectedPrefab.transform.eulerAngles;

        Quaternion baseRotation =
            Quaternion.Euler(
                baseEuler.x,
                selectedPrefab.currentRotationY,
                baseEuler.z
            );

        bool alignToTerrain =
            IsTerrainAlignHeld();

        Vector3 supportNormal =
            alignToTerrain
                ? hit.normal.normalized
                : Vector3.up;

        if (alignToTerrain)
        {
            Vector3 baseUp =
                baseRotation * Vector3.up;

            Quaternion terrainRotation =
                Quaternion.FromToRotation(
                    baseUp,
                    supportNormal
                );

            ghost.transform.rotation =
                terrainRotation * baseRotation;
        }
        else
        {
            ghost.transform.rotation =
                baseRotation;
        }

        ghost.transform.position =
            hit.point;

        if (item.settings_buildable.Placeable)
        {
            // Для Placeable используется штатный offset prefab
            ghost.transform.position =
                hit.point +
                GetPlaceablePivotOffset(
                    ghost,
                    supportNormal
                ) +
                supportNormal * GroundClearance;
        }
        else
        {
            float bottomOffset =
                GetBottomOffsetFromPivot(
                    ghost,
                    supportNormal
                );

            ghost.transform.position =
                hit.point +
                supportNormal *
                (bottomOffset + GroundClearance);
        }

        creator.SetGhostBlockVisibility(true);

        // Земля острова специально соприкасается с первым блоком
        bool canBuild =
            creator.HasEnoughResourcesToBuild(ghost);

        SetGhostMaterial(ghost, canBuild);

        if (!canBuild || !MyInput.GetButtonDown("LMB"))
        {
            return;
        }

        IslandBuildRoot root =
            GetOrCreateRoot(
                landmark,
                ghost.transform.position,
                ghost.transform.rotation,
                hit.collider.gameObject.layer
            );

        CreateIslandBlock(
            creator,
            player,
            item,
            DPS.Default,
            root,
            ghost.transform.position,
            ghost.transform.rotation
        );
    }

    private static bool IsTerrainAlignHeld()
    {
        return Input.GetKey(KeyCode.LeftShift) ||
               Input.GetKey(KeyCode.RightShift);
    }

    private static Vector3 GetPlaceablePivotOffset(
        Block block,
        Vector3 supportNormal)
    {
        if (block == null ||
            block.pivotOffset == Vector3.zero)
        {
            return Vector3.zero;
        }

        if (supportNormal.sqrMagnitude < 0.001f)
        {
            supportNormal = Vector3.up;
        }

        supportNormal.Normalize();

        Vector3 right =
            Vector3.ProjectOnPlane(
                block.transform.right,
                supportNormal
            );

        if (right.sqrMagnitude < 0.001f)
        {
            right =
                Vector3.ProjectOnPlane(
                    block.transform.forward,
                    supportNormal
                );
        }

        right.Normalize();

        Vector3 forward =
            Vector3.Cross(
                supportNormal,
                right
            ).normalized;

        if (Vector3.Dot(
                forward,
                block.transform.forward) < 0f)
        {
            forward *= -1f;
        }

        Vector3 offset =
            block.pivotOffset;

        return right * offset.x +
               supportNormal * offset.y +
               forward * offset.z;
    }

    private static float GetBottomOffsetFromPivot(
        Block block,
        Vector3 supportNormal)
    {
        if (block == null)
        {
            return 0f;
        }

        if (supportNormal.sqrMagnitude < 0.001f)
        {
            supportNormal = Vector3.up;
        }

        supportNormal.Normalize();

        bool found = false;
        float minProjection = 0f;
        Vector3 pivot =
            block.transform.position;

        if (block.blockColliders != null)
        {
            for (int i = 0; i < block.blockColliders.Length; i++)
            {
                BoxCollider collider =
                    block.blockColliders[i];

                if (collider == null)
                {
                    continue;
                }

                AddBoxColliderProjection(
                    collider,
                    pivot,
                    supportNormal,
                    ref found,
                    ref minProjection
                );
            }
        }

        if (found)
        {
            return Mathf.Max(
                0f,
                -minProjection
            );
        }

        MeshFilter[] meshFilters =
            block.GetComponentsInChildren<MeshFilter>(true);

        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter filter =
                meshFilters[i];

            if (filter == null ||
                filter.sharedMesh == null)
            {
                continue;
            }

            AddBoundsProjection(
                filter.sharedMesh.bounds,
                filter.transform,
                pivot,
                supportNormal,
                ref found,
                ref minProjection
            );
        }

        SkinnedMeshRenderer[] skinnedRenderers =
            block.GetComponentsInChildren<SkinnedMeshRenderer>(true);

        for (int i = 0; i < skinnedRenderers.Length; i++)
        {
            SkinnedMeshRenderer renderer =
                skinnedRenderers[i];

            if (renderer == null)
            {
                continue;
            }

            AddBoundsProjection(
                renderer.localBounds,
                renderer.transform,
                pivot,
                supportNormal,
                ref found,
                ref minProjection
            );
        }

        if (!found)
        {
            return 0f;
        }

        return Mathf.Max(
            0f,
            -minProjection
        );
    }

    private static void AddBoxColliderProjection(
        BoxCollider collider,
        Vector3 pivot,
        Vector3 supportNormal,
        ref bool found,
        ref float minProjection)
    {
        Vector3 center =
            collider.center;

        Vector3 extents =
            collider.size * 0.5f;

        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 localPoint =
                        center +
                        Vector3.Scale(
                            extents,
                            new Vector3(x, y, z)
                        );

                    Vector3 worldPoint =
                        collider.transform.TransformPoint(
                            localPoint
                        );

                    float projection =
                        Vector3.Dot(
                            worldPoint - pivot,
                            supportNormal
                        );

                    if (!found ||
                        projection < minProjection)
                    {
                        minProjection =
                            projection;

                        found = true;
                    }
                }
            }
        }
    }

    private static void AddBoundsProjection(
        Bounds bounds,
        Transform source,
        Vector3 pivot,
        Vector3 supportNormal,
        ref bool found,
        ref float minProjection)
    {
        if (source == null)
        {
            return;
        }

        Vector3 center =
            bounds.center;

        Vector3 extents =
            bounds.extents;

        for (int x = -1; x <= 1; x += 2)
        {
            for (int y = -1; y <= 1; y += 2)
            {
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 localPoint =
                        center +
                        Vector3.Scale(
                            extents,
                            new Vector3(x, y, z)
                        );

                    Vector3 worldPoint =
                        source.TransformPoint(
                            localPoint
                        );

                    float projection =
                        Vector3.Dot(
                            worldPoint - pivot,
                            supportNormal
                        );

                    if (!found ||
                        projection < minProjection)
                    {
                        minProjection =
                            projection;

                        found = true;
                    }
                }
            }
        }
    }

    internal static IslandBuildRoot GetOrCreateRoot(
        Landmark landmark,
        Vector3 worldPosition,
        Quaternion worldRotation,
        int terrainLayer)
    {
        IslandBuildRoot[] roots =
            landmark.GetComponentsInChildren<IslandBuildRoot>(true);

        if (roots != null && roots.Length > 0)
        {
            return roots[0];
        }

        GameObject rootObject =
            new GameObject("IslandBuildRoot");

        rootObject.transform.SetParent(
            landmark.transform,
            true
        );

        rootObject.transform.SetPositionAndRotation(
            worldPosition,
            Quaternion.Euler(
                0f,
                worldRotation.eulerAngles.y,
                0f
            )
        );

        IslandBuildRoot root =
            rootObject.AddComponent<IslandBuildRoot>();

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
        bool restoredReinforced = false)
    {
        if (creator == null ||
            player == null ||
            item == null ||
            root == null)
        {
            return null;
        }

        Block prefab =
            item.settings_buildable.GetBlockPrefab(dpsType);

        if (prefab == null)
        {
            return null;
        }

        creator.SetGhostBlockVisibility(false);

        Block block = UnityEngine.Object.Instantiate<Block>(
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

        Collider[] physicalColliders =
            GetPhysicalColliders(block);

        tag.OriginalPhysicalColliders =
            physicalColliders;

        // Коллайдеры острова не передаются системе плота
        block.blockColliders = new BoxCollider[0];

        block.ObjectIndex =
            SaveAndLoad.GetUniqueObjectIndex();

        if (block.networkedBehaviour != null)
        {
            block.networkedBehaviour.ObjectIndex =
                SaveAndLoad.GetUniqueObjectIndex();

            block.networkedBehaviour.BehaviourIndex =
                NetworkUpdateManager.GetUniqueBehaviourIndex();

            NetworkUpdateManager.AddBehaviour(
                block.networkedBehaviour
            );
        }

        block.OnFinishedPlacement();

        if (consumeResources)
        {
            PlayPlacementSound(
                creator,
                item,
                block.transform.position
            );
        }

        if (restoredMaxHealth >= 0)
        {
            block.MaxHealth = restoredMaxHealth;
        }

        if (restoredHealth >= 0)
        {
            block.SetHealth(restoredHealth);
        }

        if (restoredReinforced)
        {
            block.Reinforced = true;
        }

        tag.CollisionProxies =
            CreateStaticCollisionProxies(
                physicalColliders,
                root
            );

        for (int i = 0; i < physicalColliders.Length; i++)
        {
            Collider collider = physicalColliders[i];

            if (collider != null)
            {
                // Коллайдер остаётся маркером для ColliderPrefabEnabler
                collider.isTrigger = true;
                collider.enabled = true;
            }
        }

        if (block.occupyingComponent != null)
        {
            block.occupyingComponent.RestoreToDefaultMaterial();
        }

        int hotbarIndex =
            player.Inventory.hotbar.GetSelectedSlotIndex();

        if (consumeResources)
        {
            if (item.settings_buildable.Placeable)
            {
                if (hotbarIndex >= 0)
                {
                    Slot slot =
                        player.Inventory.GetSlot(hotbarIndex);

                    if (slot != null &&
                        !slot.IsEmpty)
                    {
                        slot.RemoveItem(1);
                    }
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

        if (registerRecord)
        {
            IslandLifeStorage.RegisterBlock(
                block,
                tag
            );
        }

        Debug.Log(
            "[IslandLife] Блок установлен на острове"
        );

        if (item.settings_buildable.Placeable)
        {
            Slot slot =
                hotbarIndex >= 0
                    ? player.Inventory.GetSlot(hotbarIndex)
                    : null;

            if (slot == null ||
                slot.IsEmpty)
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

        return block;
    }

    private static void PlayPlacementSound(
        BlockCreator creator,
        Item_Base item,
        Vector3 position)
    {
        if (creator == null ||
            item == null)
        {
            return;
        }

        FieldInfo field =
            item.settings_buildable.Placeable
                ? EventRefPlaceBlockField
                : EventRefCreateBlockField;

        if (field == null)
        {
            return;
        }

        string eventRef =
            field.GetValue(creator) as string;

        if (string.IsNullOrEmpty(eventRef))
        {
            return;
        }

        RuntimeManager.PlayOneShot(
            eventRef,
            position
        );
    }

    private static Collider[] GetPhysicalColliders(Block block)
    {
        if (block == null ||
            block.blockColliders == null ||
            block.blockColliders.Length == 0)
        {
            return new Collider[0];
        }

        List<Collider> result =
            new List<Collider>();

        for (int i = 0; i < block.blockColliders.Length; i++)
        {
            BoxCollider collider =
                block.blockColliders[i];

            if (collider == null)
            {
                continue;
            }

            // Строительные точки остаются полностью ванильными
            if (collider.GetComponent<BlockQuad>() != null ||
                collider.GetComponentInParent<BlockQuad>() != null)
            {
                continue;
            }

            result.Add(collider);
        }

        return result.ToArray();
    }

    private static GameObject[] CreateStaticCollisionProxies(
        Collider[] sourceColliders,
        IslandBuildRoot root)
    {
        if (sourceColliders == null ||
            root == null)
        {
            return new GameObject[0];
        }

        List<GameObject> proxies =
            new List<GameObject>();

        for (int i = 0; i < sourceColliders.Length; i++)
        {
            Collider source = sourceColliders[i];

            if (source == null)
            {
                continue;
            }

            GameObject proxy =
                CreateCollisionProxy(source, root);

            if (proxy != null)
            {
                proxies.Add(proxy);
            }
        }

        return proxies.ToArray();
    }

    private static GameObject CreateCollisionProxy(
        Collider source,
        IslandBuildRoot root)
    {
        GameObject proxy =
            new GameObject("IslandStaticCollision");

        proxy.layer = root.TerrainLayer;

        IslandCollisionProxy marker =
            proxy.AddComponent<IslandCollisionProxy>();

        marker.Root = root;
        marker.SourceBlock =
            source.GetComponentInParent<Block>();

        proxy.transform.SetParent(
            root.transform,
            false
        );

        proxy.transform.position =
            source.transform.position;

        proxy.transform.rotation =
            source.transform.rotation;

        proxy.transform.localScale =
            GetRelativeScale(
                source.transform.lossyScale,
                root.transform.lossyScale
            );

        BoxCollider sourceBox =
            source as BoxCollider;

        if (sourceBox != null)
        {
            BoxCollider collider =
                proxy.AddComponent<BoxCollider>();

            collider.center = sourceBox.center;
            collider.size = sourceBox.size;
            collider.sharedMaterial =
                sourceBox.sharedMaterial;

            return proxy;
        }

        SphereCollider sourceSphere =
            source as SphereCollider;

        if (sourceSphere != null)
        {
            SphereCollider collider =
                proxy.AddComponent<SphereCollider>();

            collider.center = sourceSphere.center;
            collider.radius = sourceSphere.radius;
            collider.sharedMaterial =
                sourceSphere.sharedMaterial;

            return proxy;
        }

        CapsuleCollider sourceCapsule =
            source as CapsuleCollider;

        if (sourceCapsule != null)
        {
            CapsuleCollider collider =
                proxy.AddComponent<CapsuleCollider>();

            collider.center = sourceCapsule.center;
            collider.radius = sourceCapsule.radius;
            collider.height = sourceCapsule.height;
            collider.direction = sourceCapsule.direction;
            collider.sharedMaterial =
                sourceCapsule.sharedMaterial;

            return proxy;
        }

        MeshCollider sourceMesh =
            source as MeshCollider;

        if (sourceMesh != null)
        {
            MeshCollider collider =
                proxy.AddComponent<MeshCollider>();

            collider.sharedMesh =
                sourceMesh.sharedMesh;

            collider.convex =
                sourceMesh.convex;

            collider.sharedMaterial =
                sourceMesh.sharedMaterial;

            return proxy;
        }

        UnityEngine.Object.Destroy(proxy);
        return null;
    }

    private static Vector3 GetRelativeScale(
        Vector3 worldScale,
        Vector3 parentScale)
    {
        return new Vector3(
            SafeDivide(worldScale.x, parentScale.x),
            SafeDivide(worldScale.y, parentScale.y),
            SafeDivide(worldScale.z, parentScale.z)
        );
    }

    private static float SafeDivide(
        float value,
        float divisor)
    {
        if (Mathf.Abs(divisor) < 0.0001f)
        {
            return value;
        }

        return value / divisor;
    }

    private static void SetGhostMaterial(
        Block ghost,
        bool canBuild)
    {
        if (ghost == null ||
            ghost.occupyingComponent == null)
        {
            return;
        }

        GameManager gameManager =
            SingletonGeneric<GameManager>.Singleton;

        if (gameManager == null)
        {
            return;
        }

        Material material =
            canBuild
                ? gameManager.ghostMaterialGreen
                : gameManager.ghostMaterialRed;

        if (material != null)
        {
            ghost.occupyingComponent.SetNewMaterial(material);
        }
    }
}


[HarmonyPatch(typeof(ColliderPrefabEnabler), "AttachColliderPrefab")]
public static class ColliderPrefabEnabler_Attach_IslandLife
{
    [HarmonyPostfix]
    public static void Postfix(Block block)
    {
        if (block == null ||
            block.GetComponent<IslandBlockTag>() == null ||
            block.activeColliderPrefab == null)
        {
            return;
        }

        BlockQuad[] quads =
            block.activeColliderPrefab
                .GetComponentsInChildren<BlockQuad>(true);

        Debug.Log(
            "[IslandLife] Активные строительные точки: " +
            (quads != null ? quads.Length : 0)
        );
    }
}

public static class IslandLifeCollisionCleanup
{
    public static void Cleanup(
        IslandBlockTag tag)
    {
        if (tag == null ||
            tag.CollisionProxies == null)
        {
            return;
        }

        for (int i = 0; i < tag.CollisionProxies.Length; i++)
        {
            GameObject proxy =
                tag.CollisionProxies[i];

            if (proxy == null)
            {
                continue;
            }

            Collider[] colliders =
                proxy.GetComponentsInChildren<Collider>(true);

            for (int j = 0; j < colliders.Length; j++)
            {
                if (colliders[j] != null)
                {
                    colliders[j].enabled = false;
                }
            }

            UnityEngine.Object.Destroy(proxy);
        }

        tag.CollisionProxies =
            new GameObject[0];
    }
}

[HarmonyPatch(typeof(BlockCreator), "RemoveBlock")]
public static class BlockCreator_RemoveBlock_IslandLife
{
    [HarmonyPrefix]
    public static void Prefix(
        Block block,
        ref bool updateRaftBounds)
    {
        if (block == null)
        {
            return;
        }

        IslandBlockTag tag =
            block.GetComponent<IslandBlockTag>();

        if (tag == null)
        {
            return;
        }

        updateRaftBounds = false;

        IslandLifeCollisionCleanup.Cleanup(
            tag
        );

        IslandLifeStorage.RemoveRecord(
            tag.RecordId
        );
    }
}

[HarmonyPatch(typeof(Block), "OnDestroy")]
public static class Block_OnDestroy_IslandLife
{
    [HarmonyPrefix]
    public static void Prefix(Block __instance)
    {
        if (__instance == null)
        {
            return;
        }

        IslandBlockTag tag =
            __instance.GetComponent<IslandBlockTag>();

        if (tag != null)
        {
            IslandLifeCollisionCleanup.Cleanup(
                tag
            );
        }
    }
}

[Serializable]
public class IslandLifeSaveData
{
    public List<IslandBlockRecord> blocks =
        new List<IslandBlockRecord>();
}

[Serializable]
public class IslandBlockRecord
{
    public string id;
    public long landmarkIndex;
    public int itemIndex;
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
    private const string FileName =
        "IslandLife.dat";

    private static IslandLifeSaveData data =
        new IslandLifeSaveData();

    private static string loadedWorldKey =
        string.Empty;

    private static bool restoring;

    public static void RegisterBlock(
        Block block,
        IslandBlockTag tag)
    {
        if (restoring ||
            block == null ||
            tag == null ||
            tag.Root == null ||
            tag.Root.Landmark == null)
        {
            return;
        }

        EnsureLoaded();

        IslandBlockRecord record =
            FindRecord(tag.RecordId);

        if (record == null)
        {
            record =
                new IslandBlockRecord();

            record.id =
                tag.RecordId;

            data.blocks.Add(record);
        }

        FillRecord(
            record,
            block,
            tag
        );
    }

    public static void RemoveRecord(
        string recordId)
    {
        if (!Raft_Network.IsHost ||
            string.IsNullOrEmpty(recordId))
        {
            return;
        }

        EnsureLoaded();

        data.blocks.RemoveAll(
            r =>
                r != null &&
                r.id == recordId
        );
    }

    public static void SaveCurrentWorld()
    {
        if (!Raft_Network.IsHost)
        {
            return;
        }

        EnsureLoaded();
        CaptureActiveBlocks();

        string path =
            GetSavePath();

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        string directory =
            Path.GetDirectoryName(path);

        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        BinaryFormatter formatter =
            new BinaryFormatter();

        using (FileStream stream =
            File.Open(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None))
        {
            formatter.Serialize(
                stream,
                data
            );
        }

        Debug.Log(
            "[IslandLife] Постройки островов сохранены: " +
            data.blocks.Count
        );
    }

    public static void LoadCurrentWorld(
        bool force)
    {
        if (!Raft_Network.IsHost)
        {
            return;
        }

        string worldKey =
            GetWorldKey();

        if (!force &&
            loadedWorldKey == worldKey)
        {
            return;
        }

        loadedWorldKey =
            worldKey;

        data =
            new IslandLifeSaveData();

        string path =
            GetSavePath();

        if (!string.IsNullOrEmpty(path) &&
            File.Exists(path))
        {
            try
            {
                BinaryFormatter formatter =
                    new BinaryFormatter();

                using (FileStream stream =
                    File.Open(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read))
                {
                    IslandLifeSaveData loaded =
                        formatter.Deserialize(stream)
                            as IslandLifeSaveData;

                    if (loaded != null &&
                        loaded.blocks != null)
                    {
                        data = loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning(
                    "[IslandLife] Не удалось загрузить сохранение: " +
                    ex.Message
                );

                data =
                    new IslandLifeSaveData();
            }
        }

        Debug.Log(
            "[IslandLife] Постройки островов загружены: " +
            data.blocks.Count
        );
    }

    public static void RestoreAllActiveLandmarks()
    {
        if (!Raft_Network.IsHost)
        {
            return;
        }

        EnsureLoaded();

        Landmark[] landmarks =
            UnityEngine.Object.FindObjectsOfType<Landmark>();

        for (int i = 0; i < landmarks.Length; i++)
        {
            RestoreLandmark(
                landmarks[i]
            );
        }
    }

    public static void RestoreLandmark(
        Landmark landmark)
    {
        if (!Raft_Network.IsHost ||
            landmark == null)
        {
            return;
        }

        EnsureLoaded();

        List<IslandBlockRecord> records =
            data.blocks.FindAll(
                r =>
                    r != null &&
                    r.landmarkIndex ==
                    (long)landmark.uniqueLandmarkIndex
            );

        if (records.Count == 0)
        {
            return;
        }

        Raft_Network network =
            ComponentManager<Raft_Network>.Value;

        Network_Player player =
            network != null
                ? network.GetLocalPlayer()
                : null;

        if (player == null ||
            player.BlockCreator == null)
        {
            return;
        }

        HashSet<string> existing =
            new HashSet<string>();

        IslandBlockTag[] currentTags =
            landmark.GetComponentsInChildren<IslandBlockTag>(true);

        for (int i = 0; i < currentTags.Length; i++)
        {
            IslandBlockTag tag =
                currentTags[i];

            if (tag != null &&
                !string.IsNullOrEmpty(tag.RecordId))
            {
                existing.Add(
                    tag.RecordId
                );
            }
        }

        restoring = true;

        try
        {
            for (int i = 0; i < records.Count; i++)
            {
                IslandBlockRecord record =
                    records[i];

                if (record == null ||
                    existing.Contains(record.id))
                {
                    continue;
                }

                Item_Base item =
                    ItemManager.GetItemByIndex(
                        record.itemIndex
                    );

                if (item == null)
                {
                    continue;
                }

                Vector3 localPosition =
                    new Vector3(
                        record.px,
                        record.py,
                        record.pz
                    );

                Quaternion localRotation =
                    Quaternion.Euler(
                        record.rx,
                        record.ry,
                        record.rz
                    );

                Vector3 worldPosition =
                    landmark.transform.TransformPoint(
                        localPosition
                    );

                Quaternion worldRotation =
                    landmark.transform.rotation *
                    localRotation;

                IslandBuildRoot root =
                    BlockCreator_Update_IslandLife
                        .GetOrCreateRoot(
                            landmark,
                            worldPosition,
                            worldRotation,
                            record.terrainLayer
                        );

                BlockCreator_Update_IslandLife
                    .CreateIslandBlock(
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
                        record.reinforced
                    );
            }
        }
        finally
        {
            restoring = false;
        }
    }

    private static void EnsureLoaded()
    {
        string worldKey =
            GetWorldKey();

        if (loadedWorldKey != worldKey)
        {
            LoadCurrentWorld(true);
        }
    }

    private static void CaptureActiveBlocks()
    {
        IslandBlockTag[] tags =
            UnityEngine.Object.FindObjectsOfType<IslandBlockTag>();

        HashSet<string> activeIds =
            new HashSet<string>();

        HashSet<long> activeLandmarks =
            new HashSet<long>();

        Landmark[] landmarks =
            UnityEngine.Object.FindObjectsOfType<Landmark>();

        for (int i = 0; i < landmarks.Length; i++)
        {
            Landmark landmark =
                landmarks[i];

            if (landmark != null &&
                landmark.isSpawned)
            {
                activeLandmarks.Add(
                    (long)landmark.uniqueLandmarkIndex
                );
            }
        }

        for (int i = 0; i < tags.Length; i++)
        {
            IslandBlockTag tag =
                tags[i];

            if (tag == null ||
                tag.Root == null ||
                tag.Root.Landmark == null)
            {
                continue;
            }

            Block block =
                tag.GetComponent<Block>();

            if (block == null)
            {
                continue;
            }

            if (string.IsNullOrEmpty(tag.RecordId))
            {
                tag.RecordId =
                    Guid.NewGuid().ToString("N");
            }

            activeIds.Add(
                tag.RecordId
            );

            IslandBlockRecord record =
                FindRecord(tag.RecordId);

            if (record == null)
            {
                record =
                    new IslandBlockRecord();

                record.id =
                    tag.RecordId;

                data.blocks.Add(record);
            }

            FillRecord(
                record,
                block,
                tag
            );
        }

        data.blocks.RemoveAll(
            r =>
                r != null &&
                activeLandmarks.Contains(
                    r.landmarkIndex
                ) &&
                !activeIds.Contains(
                    r.id
                )
        );
    }

    private static void FillRecord(
        IslandBlockRecord record,
        Block block,
        IslandBlockTag tag)
    {
        Landmark landmark =
            tag.Root.Landmark;

        Vector3 localPosition =
            landmark.transform.InverseTransformPoint(
                block.transform.position
            );

        Quaternion localRotation =
            Quaternion.Inverse(
                landmark.transform.rotation
            ) *
            block.transform.rotation;

        Vector3 euler =
            localRotation.eulerAngles;

        record.landmarkIndex =
            (long)landmark.uniqueLandmarkIndex;

        record.itemIndex =
            block.buildableItem != null
                ? block.buildableItem.UniqueIndex
                : 0;

        record.dpsType =
            (int)block.dpsType;

        record.terrainLayer =
            tag.Root.TerrainLayer;

        record.px =
            localPosition.x;

        record.py =
            localPosition.y;

        record.pz =
            localPosition.z;

        record.rx =
            euler.x;

        record.ry =
            euler.y;

        record.rz =
            euler.z;

        record.health =
            block.Health;

        record.maxHealth =
            block.MaxHealth;

        record.reinforced =
            block.Reinforced;
    }

    private static IslandBlockRecord FindRecord(
        string id)
    {
        if (data == null ||
            data.blocks == null ||
            string.IsNullOrEmpty(id))
        {
            return null;
        }

        return data.blocks.Find(
            r =>
                r != null &&
                r.id == id
        );
    }

    private static string GetWorldKey()
    {
        return SaveAndLoad.CurrentGameFileName ??
               string.Empty;
    }

    private static string GetSavePath()
    {
        string worldKey =
            GetWorldKey();

        if (string.IsNullOrEmpty(worldKey) ||
            string.IsNullOrEmpty(SaveAndLoad.WorldPath))
        {
            return null;
        }

        return Path.Combine(
            SaveAndLoad.WorldPath,
            worldKey,
            FileName
        );
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
        IslandLifeStorage.RestoreLandmark(
            __instance
        );
    }
}

[HarmonyPatch(typeof(SaveAndLoad), "CreateRGDGame")]
public static class SaveAndLoad_CreateRGDGame_IslandLife
{
    [HarmonyPostfix]
    public static void Postfix(RGD_Game __result)
    {
        if (__result == null ||
            __result.behaviours == null)
        {
            return;
        }

        IslandBlockTag[] tags =
            UnityEngine.Object.FindObjectsOfType<IslandBlockTag>();

        if (tags == null || tags.Length == 0)
        {
            return;
        }

        HashSet<uint> objectIndexes =
            new HashSet<uint>();

        for (int i = 0; i < tags.Length; i++)
        {
            IslandBlockTag tag = tags[i];

            if (tag == null)
            {
                continue;
            }

            Block block = tag.GetComponent<Block>();

            if (block != null)
            {
                objectIndexes.Add(block.ObjectIndex);
            }
        }

        __result.behaviours.RemoveAll(
            rgd =>
            {
                RGD_Block blockData = rgd as RGD_Block;

                return blockData != null &&
                       objectIndexes.Contains(
                           blockData.BlockObjectIndex
                       );
            }
        );
    }
}
