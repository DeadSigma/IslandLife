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
