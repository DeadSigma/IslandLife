using HarmonyLib;

[HarmonyPatch(typeof(Block_Pipe), "OnBitmaskTileChange")]
public static class Block_Pipe_OnBitmaskTileChange_IslandLife
{
    [HarmonyPostfix]
    public static void Postfix(Block_Pipe __instance)
    {
        BlockCreator_Update_IslandLife.RefreshLatePipeColliders(__instance);
    }
}
