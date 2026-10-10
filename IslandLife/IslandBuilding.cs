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

[HarmonyPatch(typeof(BlockCreator), "Update")]
public static class BlockCreator_Update_IslandLife
{
    private static readonly FieldInfo QuadAtCursorField = AccessTools.Field(typeof(BlockCreator), "quadAtCursor");
    private static readonly FieldInfo QuadSurfaceField = AccessTools.Field(typeof(BlockCreator), "quadSurface");
    private static readonly FieldInfo QuadHitField = AccessTools.Field(typeof(BlockCreator), "quadHit");
    private static readonly FieldInfo SelectedBuildablePrefabField = AccessTools.Field(typeof(BlockCreator), "selectedBuildablePrefab");
    private static readonly FieldInfo ColliderPrefabEnablerField = AccessTools.Field(typeof(BlockCreator), "colliderPrefabEnabler");
    private static readonly FieldInfo CurrentPipeColliderField = AccessTools.Field(typeof(Block_Pipe), "currentPipeCollider");

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

        IslandGridMode.HandleInput();

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

        if (IsGridSnapActive() && IslandPipeGrid.IsPipeBuild(__instance.selectedBlock))
        {
            Block selectedPrefab;
            Block pipeGhost = PrepareGhost(__instance, ___selectedBuildableItem, DPS.Default, out selectedPrefab);
            if (pipeGhost != null && selectedPrefab != null)
            {
                pipeGhost.transform.rotation = IslandPipeGrid.GetGridRotation(selectedPrefab);
                if (IslandPipeGrid.TryGetSnap(___playerNetwork, pipeGhost, out snapPosition, out snapRoot))
                {
                    IslandGridMode.MarkIslandBuildActive();
                    pipeGhost.transform.position = snapPosition;
                    PlaceGhost(__instance, ___playerNetwork, pipeGhost, snapRoot, DPS.Default);
                    return false;
                }
            }
        }

        if (IsGridSnapActive())
        {
            if (TryGetGridSnap(
                ___playerNetwork,
                ___selectedBuildableItem,
                out snapPosition,
                out snapRotation,
                out snapRoot))
            {
                IslandGridMode.MarkIslandBuildActive();
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
                IslandGridMode.MarkIslandBuildActive();
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
                IslandGridMode.MarkIslandBuildActive();
                HandleGridSnap(
                    __instance,
                    ___playerNetwork,
                    ___selectedBuildableItem,
                    snapRoot,
                    snapPosition,
                    snapRotation);

                return false;
            }
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

        IslandGridMode.MarkIslandBuildActive();
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

    private static int lastRotationFrame = -1;
    private static Block lastRotatedGhost;

    private static Block ApplyRotationAndMirror(BlockCreator creator, out Block selectedPrefab)
    {
        Block current = creator.selectedBlock;
        if (current != null && (lastRotationFrame != Time.frameCount || lastRotatedGhost != current))
        {
            HandleIslandRotation(creator);
            HandleMirroredMethod.Invoke(creator, null);
            lastRotatedGhost = creator.selectedBlock;
            lastRotationFrame = Time.frameCount;
        }

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
                            item.settings_buildable != null &&
                            !item.settings_buildable.Placeable;

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

        bool alignToTerrain = IsTerrainAlignHeld();
        bool structuralBlock = item.settings_buildable != null && !item.settings_buildable.Placeable;

        Vector3 gridOrigin = Vector3.zero;
        Quaternion gridRotation = Quaternion.identity;
        bool useVirtualGrid = IslandGridMode.Enabled &&
                              !alignToTerrain &&
                              structuralBlock &&
                              TryGetVirtualGridFrame(landmark, hit.point, out gridOrigin, out gridRotation);

        Vector3 baseEuler = selectedPrefab.transform.eulerAngles;
        float yaw = selectedPrefab.currentRotationY;

        if (useVirtualGrid)
        {
            float gridYaw = gridRotation.eulerAngles.y;
            float relativeSteps = Mathf.Round(
                Mathf.DeltaAngle(gridYaw, yaw) / 90f);

            yaw = gridYaw + relativeSteps * 90f;
        }

        if (IslandGridMode.Enabled && !alignToTerrain && IslandPipeGrid.IsPipeBuild(ghost))
            yaw = Mathf.Round(yaw / 90f) * 90f;

        Quaternion baseRotation = Quaternion.Euler(baseEuler.x, yaw, baseEuler.z);
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

            if (useVirtualGrid)
                SnapStructuralGhostToGrid(ghost, gridOrigin, gridRotation);
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

    private static bool IsGridSnapActive()
    {
        return IslandGridMode.Enabled && !IsTerrainAlignHeld();
    }

    private static bool IsTerrainAlignHeld()
    {
        return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
    }

    private static bool TryGetVirtualGridFrame(
        Landmark landmark,
        Vector3 nearPoint,
        out Vector3 origin,
        out Quaternion rotation)
    {
        origin = Vector3.zero;
        rotation = Quaternion.identity;

        if (landmark == null)
            return false;

        IslandBlockTag[] tags = landmark.GetComponentsInChildren<IslandBlockTag>(true);
        Block anchor = null;
        float bestDistance = float.MaxValue;
        float maxDistance = Player.UseDistance * 2f;
        float maxDistanceSqr = maxDistance * maxDistance;

        for (int i = 0; i < tags.Length; i++)
        {
            IslandBlockTag tag = tags[i];
            Block candidate = tag != null ? tag.GetComponent<Block>() : null;

            if (candidate == null ||
                candidate.buildableItem == null ||
                candidate.buildableItem.settings_buildable == null ||
                candidate.buildableItem.settings_buildable.Placeable)
            {
                continue;
            }

            Vector3 delta = candidate.transform.position - nearPoint;
            delta.y = 0f;
            float distance = delta.sqrMagnitude;

            if (distance > maxDistanceSqr || distance >= bestDistance)
                continue;

            bestDistance = distance;
            anchor = candidate;
        }

        if (anchor == null)
            return false;

        rotation = Quaternion.Euler(0f, anchor.transform.eulerAngles.y, 0f);

        bool halfX;
        bool halfZ;
        GetGridPhase(anchor, rotation, out halfX, out halfZ);

        Vector3 right = rotation * Vector3.right;
        Vector3 forward = rotation * Vector3.forward;
        float bottomOffset = GetBottomOffsetFromPivot(anchor, Vector3.up);

        origin = anchor.transform.position -
                 Vector3.up * (bottomOffset + GroundClearance);

        if (halfX)
            origin -= right * BlockCreator.HalfBlockSize;

        if (halfZ)
            origin -= forward * BlockCreator.HalfBlockSize;

        return true;
    }

    private static void SnapStructuralGhostToGrid(
        Block ghost,
        Vector3 gridOrigin,
        Quaternion gridRotation)
    {
        if (ghost == null)
            return;

        Vector3 right = gridRotation * Vector3.right;
        Vector3 forward = gridRotation * Vector3.forward;
        Vector3 up = Vector3.up;

        float bottomOffset = GetBottomOffsetFromPivot(ghost, up);
        Vector3 contactPoint = ghost.transform.position -
                               up * (bottomOffset + GroundClearance);

        Vector3 delta = contactPoint - gridOrigin;

        bool halfX;
        bool halfZ;
        GetGridPhase(ghost, gridRotation, out halfX, out halfZ);

        float phaseX = halfX ? BlockCreator.HalfBlockSize : 0f;
        float phaseZ = halfZ ? BlockCreator.HalfBlockSize : 0f;

        float x = SnapGridCoordinate(
            Vector3.Dot(delta, right),
            BlockCreator.BlockSize,
            phaseX);

        float z = SnapGridCoordinate(
            Vector3.Dot(delta, forward),
            BlockCreator.BlockSize,
            phaseZ);

        float y = Mathf.Round(
            Vector3.Dot(delta, up) / BlockCreator.BlockSize) *
            BlockCreator.BlockSize;

        Vector3 snappedContact = gridOrigin +
                                 right * x +
                                 forward * z +
                                 up * y;

        ghost.transform.position = snappedContact +
                                   up * (bottomOffset + GroundClearance);
    }

    private static float SnapGridCoordinate(float value, float step, float phase)
    {
        if (step <= 0.0001f)
            return value;

        return Mathf.Round((value - phase) / step) * step + phase;
    }

    private static void GetGridPhase(
        Block block,
        Quaternion gridRotation,
        out bool halfX,
        out bool halfZ)
    {
        halfX = false;
        halfZ = false;

        if (block == null)
            return;

        float spanX;
        float spanZ;

        if (!TryGetHorizontalSpan(
                block,
                gridRotation * Vector3.right,
                gridRotation * Vector3.forward,
                out spanX,
                out spanZ))
        {
            return;
        }

        float thinThreshold = BlockCreator.BlockSize * 0.45f;
        halfX = spanX < thinThreshold;
        halfZ = spanZ < thinThreshold;
    }

    private static bool TryGetHorizontalSpan(
        Block block,
        Vector3 axisX,
        Vector3 axisZ,
        out float spanX,
        out float spanZ)
    {
        spanX = 0f;
        spanZ = 0f;

        if (block == null)
            return false;

        bool found = false;
        float minX = 0f;
        float maxX = 0f;
        float minZ = 0f;
        float maxZ = 0f;

        MeshFilter[] meshFilters = block.GetComponentsInChildren<MeshFilter>(true);
        for (int i = 0; i < meshFilters.Length; i++)
        {
            MeshFilter filter = meshFilters[i];
            if (filter == null || filter.sharedMesh == null)
                continue;

            AddBoundsSpan(
                filter.sharedMesh.bounds,
                filter.transform,
                axisX,
                axisZ,
                ref found,
                ref minX,
                ref maxX,
                ref minZ,
                ref maxZ);
        }

        SkinnedMeshRenderer[] skinned = block.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        for (int i = 0; i < skinned.Length; i++)
        {
            SkinnedMeshRenderer renderer = skinned[i];
            if (renderer == null)
                continue;

            AddBoundsSpan(
                renderer.localBounds,
                renderer.transform,
                axisX,
                axisZ,
                ref found,
                ref minX,
                ref maxX,
                ref minZ,
                ref maxZ);
        }

        if (!found)
        {
            BoxCollider[] colliders = block.GetComponentsInChildren<BoxCollider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                BoxCollider collider = colliders[i];
                if (collider == null ||
                    collider.GetComponent<BlockQuad>() != null ||
                    collider.GetComponentInParent<BlockQuad>() != null)
                {
                    continue;
                }

                AddBoundsSpan(
                    new Bounds(collider.center, collider.size),
                    collider.transform,
                    axisX,
                    axisZ,
                    ref found,
                    ref minX,
                    ref maxX,
                    ref minZ,
                    ref maxZ);
            }
        }

        if (!found)
            return false;

        spanX = maxX - minX;
        spanZ = maxZ - minZ;
        return true;
    }

    private static void AddBoundsSpan(
        Bounds bounds,
        Transform source,
        Vector3 axisX,
        Vector3 axisZ,
        ref bool found,
        ref float minX,
        ref float maxX,
        ref float minZ,
        ref float maxZ)
    {
        if (source == null)
            return;

        Vector3 center = bounds.center;
        Vector3 extents = bounds.extents;

        for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
                for (int z = -1; z <= 1; z += 2)
                {
                    Vector3 localPoint = center +
                                         Vector3.Scale(
                                             extents,
                                             new Vector3(x, y, z));

                    Vector3 worldPoint = source.TransformPoint(localPoint);
                    float px = Vector3.Dot(worldPoint, axisX);
                    float pz = Vector3.Dot(worldPoint, axisZ);

                    if (!found)
                    {
                        minX = maxX = px;
                        minZ = maxZ = pz;
                        found = true;
                        continue;
                    }

                    minX = Mathf.Min(minX, px);
                    maxX = Mathf.Max(maxX, px);
                    minZ = Mathf.Min(minZ, pz);
                    maxZ = Mathf.Max(maxZ, pz);
                }
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

        BoxCollider[] proxyColliders;
        tag.CollisionProxies = CreateStaticCollisionProxies(
            physicalColliders,
            root,
            block,
            out proxyColliders);

        block.blockColliders = proxyColliders;

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

        EventReference eventRef =
            item.settings_buildable.Placeable
                ? creator.er_placeBlock
                : creator.er_createBlock;

        RuntimeManager.PlayOneShot(
            eventRef,
            position
        );
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
                collider.GetComponentInParent<BlockQuad>() != null ||
                collider.isTrigger)
                continue;

            result.Add(collider);
        }

        return result.ToArray();
    }

    private static GameObject[] CreateStaticCollisionProxies(
        BoxCollider[] blockColliders,
        IslandBuildRoot root,
        Block block,
        out BoxCollider[] proxyColliders)
    {
        List<GameObject> proxies = new List<GameObject>();
        List<BoxCollider> boxes = new List<BoxCollider>();
        HashSet<Collider> processed = new HashSet<Collider>();

        proxyColliders = new BoxCollider[0];
        if (root == null || block == null)
            return proxies.ToArray();

        // Физические коллайдеры блока могут быть выключены во время OnFinishedPlacement
        if (blockColliders != null)
        {
            for (int i = 0; i < blockColliders.Length; i++)
            {
                BoxCollider source = blockColliders[i];
                if (source == null || !processed.Add(source))
                    continue;

                Collider physical = CreateTerrainCollider(source, root, block, proxies);
                BoxCollider box = physical as BoxCollider;
                if (box != null)
                    boxes.Add(box);
            }
        }

        // Многие предметы и лестницы используют отдельные Box/Mesh/Sphere/CapsuleCollider
        Collider[] allColliders = block.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < allColliders.Length; i++)
        {
            Collider source = allColliders[i];
            if (source == null || !processed.Add(source) ||
                !source.enabled || source.isTrigger ||
                source.GetComponentInParent<IslandCollisionProxy>() != null ||
                source.GetComponent<BlockQuad>() != null ||
                source.GetComponentInParent<BlockQuad>() != null)
                continue;

            CreateTerrainCollider(source, root, block, proxies);
        }

        proxyColliders = boxes.ToArray();
        return proxies.ToArray();
    }

    private static Collider CreateTerrainCollider(
        Collider source,
        IslandBuildRoot root,
        Block block,
        List<GameObject> proxies)
    {
        if (source == null || root == null || block == null ||
            source.GetComponentInParent<IslandCollisionProxy>() != null)
            return null;

        int terrainLayer = root.TerrainLayer;
        if (terrainLayer < 0 || terrainLayer > 31)
            return null;

        GameObject proxy = new GameObject("IslandStaticCollision");
        proxy.layer = terrainLayer;
        proxy.transform.SetParent(source.transform, false);
        proxy.transform.localPosition = Vector3.zero;
        proxy.transform.localRotation = Quaternion.identity;
        proxy.transform.localScale = Vector3.one;

        Collider target = null;
        BoxCollider sourceBox = source as BoxCollider;
        MeshCollider sourceMesh = source as MeshCollider;
        SphereCollider sourceSphere = source as SphereCollider;
        CapsuleCollider sourceCapsule = source as CapsuleCollider;

        if (sourceBox != null)
        {
            BoxCollider box = proxy.AddComponent<BoxCollider>();
            box.center = sourceBox.center;
            box.size = sourceBox.size;
            target = box;
        }
        else if (sourceMesh != null && sourceMesh.sharedMesh != null)
        {
            MeshCollider mesh = proxy.AddComponent<MeshCollider>();
            mesh.sharedMesh = sourceMesh.sharedMesh;
            mesh.convex = sourceMesh.convex;
            target = mesh;
        }
        else if (sourceSphere != null)
        {
            SphereCollider sphere = proxy.AddComponent<SphereCollider>();
            sphere.center = sourceSphere.center;
            sphere.radius = sourceSphere.radius;
            target = sphere;
        }
        else if (sourceCapsule != null)
        {
            CapsuleCollider capsule = proxy.AddComponent<CapsuleCollider>();
            capsule.center = sourceCapsule.center;
            capsule.radius = sourceCapsule.radius;
            capsule.height = sourceCapsule.height;
            capsule.direction = sourceCapsule.direction;
            target = capsule;
        }

        if (target == null)
        {
            UnityEngine.Object.Destroy(proxy);
            Debug.LogWarning("[IslandLife] Неподдерживаемый физический коллайдер: " +
                source.GetType().Name + " на " + block.name);
            return null;
        }

        IslandCollisionProxy marker = proxy.AddComponent<IslandCollisionProxy>();
        marker.Root = root;
        marker.SourceBlock = block;

        target.sharedMaterial = source.sharedMaterial;
        target.isTrigger = false;
        target.enabled = true;
        proxies.Add(proxy);

        // Оригинал сохраняется для лучей взаимодействия, но больше не является опорой игрока
        if (sourceMesh == null || sourceMesh.convex)
        {
            source.isTrigger = true;
            source.enabled = true;
        }
        else
        {
            // Неконвексный MeshCollider в Unity нельзя сделать триггером
            source.enabled = false;
            CreateMeshRaycastTrigger(sourceMesh, block);
        }

        return target;
    }

    private static void CreateMeshRaycastTrigger(MeshCollider source, Block block)
    {
        if (source == null || source.sharedMesh == null || block == null)
            return;

        Bounds meshBounds = source.sharedMesh.bounds;
        GameObject query = new GameObject("IslandMeshRaycast");
        query.layer = source.gameObject.layer;
        query.transform.SetParent(source.transform, false);
        query.transform.localPosition = Vector3.zero;
        query.transform.localRotation = Quaternion.identity;
        query.transform.localScale = Vector3.one;

        BoxCollider box = query.AddComponent<BoxCollider>();
        box.center = meshBounds.center;
        box.size = meshBounds.size;
        box.isTrigger = true;
    }

    internal static void RefreshLatePipeColliders(Block_Pipe pipeBlock)
    {
        if (pipeBlock == null)
            return;

        IslandBlockTag tag = pipeBlock.GetComponent<IslandBlockTag>();
        if (tag == null || tag.Root == null)
            return;

        GameObject container = CurrentPipeColliderField != null
            ? CurrentPipeColliderField.GetValue(pipeBlock) as GameObject
            : null;
        if (container == null)
            return;

        List<GameObject> newProxies = new List<GameObject>();
        Collider[] colliders = container.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            Collider source = colliders[i];
            if (source == null || !source.enabled || source.isTrigger ||
                source.GetComponentInParent<IslandCollisionProxy>() != null ||
                source.GetComponentInParent<BlockQuad>() != null)
                continue;

            CreateTerrainCollider(source, tag.Root, pipeBlock, newProxies);
        }

        if (newProxies.Count == 0)
            return;

        List<GameObject> all = tag.CollisionProxies != null
            ? new List<GameObject>(tag.CollisionProxies)
            : new List<GameObject>();
        all.RemoveAll(item => item == null);
        all.AddRange(newProxies);
        tag.CollisionProxies = all.ToArray();
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
