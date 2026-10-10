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
        harmony = new Harmony("el_neuman.islandlife");
        harmony.PatchAll();
        Debug.Log("[IslandLife] Loaded");
    }

    public void OnGUI()
    {
        IslandGridMode.DrawHint();
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
