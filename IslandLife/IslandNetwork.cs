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

    public static bool TryHandlePlayerDeserialize(
        Network_Player player,
        Message_NetworkBehaviour message,
        Network_UserId remoteID,
        out bool result)
    {
        result = false;

        if (player == null ||
            message == null ||
            message.Type != Messages.Axe_RemoveBlock)
        {
            return false;
        }

        Message_BlockCreator_RemoveBlock remove =
            message as Message_BlockCreator_RemoveBlock;

        if (remove == null)
            return false;

        Block block =
            FindIslandBlock(
                remove.blockObjectIndex
            );

        if (block == null)
            return false;

        if (Raft_Network.IsHost &&
            player.steamID != remoteID)
        {
            return true;
        }

        if (player.AxeScript == null)
            return true;

        result =
            player.AxeScript.DestroyBlock(
                block,
                false
            );

        return true;
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

[HarmonyPatch(typeof(Network_Player), "Deserialize")]
public static class Network_Player_Deserialize_IslandLife
{
    [HarmonyPrefix]
    public static bool Prefix(
        Network_Player __instance,
        Message_NetworkBehaviour msg,
        Network_UserId remoteID,
        ref bool __result)
    {
        bool handled =
            IslandLifeNetwork.TryHandlePlayerDeserialize(
                __instance,
                msg,
                remoteID,
                out __result
            );

        return !handled;
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
