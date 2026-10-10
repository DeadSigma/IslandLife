using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

public static class IslandPipeGrid
{
    private const float HorizontalStep = 1.5f;
    private const float VerticalStep = 1.21f;
    private const float PipePivotHeight = 0.14f;
    private const float AimRadius = 0.90f;
    private const float OccupiedTolerance = 0.12f;

    private static readonly FieldInfo PipeTileField = AccessTools.Field(typeof(Pipe), "bitmaskTile");
    private static readonly FieldInfo PipeConnectionsField = AccessTools.Field(typeof(Block_Pipe), "pipeBitmaskConnections");

    private static readonly Vector3[] Directions =
    {
        Vector3.forward, Vector3.back, Vector3.left,
        Vector3.right, Vector3.up, Vector3.down
    };

    private struct PipePort
    {
        public Pipe Pipe;
        public BitmaskTile Tile;
        public IslandBuildRoot Root;
        public Vector3 Position;
    }

    public static bool IsIslandPipe(Pipe pipe)
    {
        return pipe != null && pipe.GetComponentInParent<IslandBlockTag>() != null;
    }

    public static void RegisterIslandPipe(Pipe pipe)
    {
        pipe.snappedBuildingPosition = RoundPosition(GetLogicalPortPosition(pipe));
        PipeGroupManager.TrackPipe(pipe.snappedBuildingPosition, pipe);
        pipe.gameObject.SetActiveSafe(true);
        pipe.MergeIntoNearbyPipeGroup(pipe.GetNearbyPipes());
    }

    public static bool TryGetIslandNearbyPipes(Pipe source, out List<Pipe> nearby)
    {
        nearby = null;
        if (!IsIslandPipe(source))
            return false;

        IslandBlockTag owner = source.GetComponentInParent<IslandBlockTag>();
        if (owner == null || owner.Root == null)
            return false;

        nearby = new List<Pipe>();
        BitmaskTile sourceTile = GetTile(source);
        BitmaskTile[] allowedTiles = sourceTile != null ? sourceTile.GetNeighbours() : null;

        Vector3[] directionOrder =
        {
            Vector3.forward, Vector3.left, Vector3.right,
            Vector3.back, Vector3.up, Vector3.down
        };

        for (int i = 0; i < directionOrder.Length; i++)
        {
            Vector3 dir = directionOrder[i];
            if (!AllowsDirection(sourceTile, source, dir))
                continue;

            Vector3 expected = GetLogicalPortPosition(source) + dir * (i < 4 ? HorizontalStep : VerticalStep);

            foreach (IslandBlockTag tag in IslandBlockTag.ActiveTags)
            {
                if (tag == null || tag.Root != owner.Root)
                    continue;

                Block block = tag.GetComponent<Block>();
                if (block == null || !block.gameObject.activeInHierarchy ||
                    (block.transform.position - expected).sqrMagnitude > 25f)
                    continue;

                Pipe[] pipes = block.GetComponentsInChildren<Pipe>(true);
                for (int n = 0; n < pipes.Length; n++)
                {
                    Pipe other = pipes[n];
                    if (other == null || other == source || other.isAboutToBeDestroyed ||
                        (GetLogicalPortPosition(other) - expected).sqrMagnitude > 0.01f)
                        continue;

                    BitmaskTile otherTile = GetTile(other);
                    if (sourceTile != null && otherTile != null &&
                        (sourceTile.BitmaskType != otherTile.BitmaskType ||
                         allowedTiles == null || allowedTiles[i] != otherTile))
                        continue;

                    if (!AllowsDirection(otherTile, other, -dir))
                        continue;

                    if (!nearby.Contains(other))
                        nearby.Add(other);
                    break;
                }
            }
        }

        if (nearby.Count == 0)
            nearby = null;

        return true;
    }

    public static bool TryGetIslandNeighbours(BitmaskTile source, out BitmaskTile[] neighbours)
    {
        neighbours = null;
        if (source == null)
            return false;

        IslandBlockTag owner = source.GetComponentInParent<IslandBlockTag>();
        if (owner == null || owner.Root == null)
            return false;

        neighbours = new BitmaskTile[6];
        Vector3[] directionOrder =
        {
            Vector3.forward, Vector3.left, Vector3.right,
            Vector3.back, Vector3.up, Vector3.down
        };

        for (int i = 0; i < directionOrder.Length; i++)
        {
            Vector3 dir = directionOrder[i];
            if (!AllowsDirection(source, FindOwnerPipe(source), dir))
                continue;

            Vector3 expected = GetLogicalTilePosition(source) + dir * (i < 4 ? HorizontalStep : VerticalStep);

            foreach (IslandBlockTag tag in IslandBlockTag.ActiveTags)
            {
                if (tag == null || tag.Root != owner.Root)
                    continue;

                Block block = tag.GetComponent<Block>();
                if (block == null || !block.gameObject.activeInHierarchy)
                    continue;

                if ((block.transform.position - expected).sqrMagnitude > 25f)
                    continue;

                BitmaskTile[] tiles = block.GetComponentsInChildren<BitmaskTile>(true);
                for (int t = 0; t < tiles.Length; t++)
                {
                    BitmaskTile other = tiles[t];
                    if (other == null || other == source ||
                        other.BitmaskType != source.BitmaskType ||
                        (GetLogicalTilePosition(other) - expected).sqrMagnitude > 0.01f ||
                        !AllowsDirection(other, FindOwnerPipe(other), -dir))
                        continue;

                    neighbours[i] = other;
                    break;
                }

                if (neighbours[i] != null)
                    break;
            }
        }

        return true;
    }

    private static Vector3 RoundPosition(Vector3 position)
    {
        return new Vector3(
            Mathf.Round(position.x * 1000f) / 1000f,
            Mathf.Round(position.y * 1000f) / 1000f,
            Mathf.Round(position.z * 1000f) / 1000f);
    }

    public static bool IsPipeBuild(Block block)
    {
        return block != null && block.GetComponentInChildren<Pipe>(true) != null;
    }

    public static Quaternion GetGridRotation(Block selectedPrefab)
    {
        if (selectedPrefab == null)
            return Quaternion.identity;

        Vector3 euler = selectedPrefab.transform.eulerAngles;
        euler.y = Mathf.Round(selectedPrefab.currentRotationY / 90f) * 90f;
        return Quaternion.Euler(euler);
    }

    public static bool TryGetSnap(
        Network_Player player,
        Block ghost,
        out Vector3 position,
        out IslandBuildRoot root)
    {
        position = Vector3.zero;
        root = null;

        if (player == null || player.CameraTransform == null || ghost == null)
            return false;

        Pipe[] ghostPipes = ghost.GetComponentsInChildren<Pipe>(true);
        if (ghostPipes.Length == 0)
            return false;

        Transform camera = player.CameraTransform;
        Vector3 origin = camera.position;
        Vector3 forward = camera.forward.normalized;
        float maxDistance = Player.UseDistance * 2f;
        float maxDistanceSqr = (maxDistance + HorizontalStep * 2f) * (maxDistance + HorizontalStep * 2f);
        List<PipePort> placed = new List<PipePort>();

        foreach (IslandBlockTag tag in IslandBlockTag.ActiveTags)
        {
            if (tag == null || tag.Root == null || tag.Root.Landmark == null)
                continue;

            Block block = tag.GetComponent<Block>();
            if (block == null || !block.gameObject.activeInHierarchy ||
                (block.transform.position - origin).sqrMagnitude > maxDistanceSqr)
                continue;

            Pipe[] sources = block.GetComponentsInChildren<Pipe>(true);
            bool hasSockets = false;
            for (int i = 0; i < sources.Length; i++)
                if (sources[i] is PipeSocket)
                    hasSockets = true;

            for (int i = 0; i < sources.Length; i++)
            {
                Pipe pipe = sources[i];
                if (pipe == null || pipe.isAboutToBeDestroyed ||
                    (hasSockets && !(pipe is PipeSocket)))
                    continue;

                BitmaskTile tile = GetTile(pipe);
                placed.Add(new PipePort
                {
                    Pipe = pipe,
                    Tile = tile,
                    Root = tag.Root,
                    Position = GetLogicalPortPosition(pipe)
                });
            }
        }

        float bestScore = float.MaxValue;
        for (int i = 0; i < placed.Count; i++)
        {
            PipePort source = placed[i];
            for (int d = 0; d < Directions.Length; d++)
            {
                Vector3 direction = Directions[d];
                if (!AllowsDirection(source.Tile, source.Pipe, direction))
                    continue;

                Vector3 target = source.Position + direction * (d < 4 ? HorizontalStep : VerticalStep);
                float along = Vector3.Dot(target - origin, forward);
                if (along < 0f || along > maxDistance)
                    continue;

                float distanceSqr = (target - (origin + forward * along)).sqrMagnitude;
                if (distanceSqr > AimRadius * AimRadius)
                    continue;

                bool occupied = false;
                for (int n = 0; n < placed.Count; n++)
                {
                    if (placed[n].Root == source.Root &&
                        (placed[n].Position - target).sqrMagnitude < OccupiedTolerance * OccupiedTolerance)
                    {
                        occupied = true;
                        break;
                    }
                }
                if (occupied)
                    continue;

                for (int g = 0; g < ghostPipes.Length; g++)
                {
                    Pipe receiver = ghostPipes[g];
                    if (receiver == null)
                        continue;

                    BitmaskTile receiverTile = GetTile(receiver);
                    if (source.Tile != null && receiverTile != null &&
                        source.Tile.BitmaskType != receiverTile.BitmaskType)
                        continue;

                    if (!AllowsDirection(receiverTile, receiver, -direction))
                        continue;

                    float score = distanceSqr + along * 0.0001f;
                    if (score >= bestScore)
                        continue;

                    bestScore = score;
                    Vector3 receiverPort = GetLogicalPortPosition(receiver);
                    position = target - (receiverPort - ghost.transform.position);
                    root = source.Root;
                }
            }
        }

        return root != null;
    }

    private static Pipe FindOwnerPipe(BitmaskTile tile)
    {
        if (tile == null)
            return null;

        Block block = tile.GetComponentInParent<Block>();
        if (block == null)
            return null;

        Pipe[] pipes = block.GetComponentsInChildren<Pipe>(true);
        for (int i = 0; i < pipes.Length; i++)
            if (GetTile(pipes[i]) == tile)
                return pipes[i];

        return null;
    }

    private static BitmaskTile GetTile(Pipe pipe)
    {
        if (pipe == null)
            return null;

        BitmaskTile tile = PipeTileField != null
            ? PipeTileField.GetValue(pipe) as BitmaskTile
            : null;
        if (tile != null)
            return tile;

        Block_Pipe block = pipe.GetComponentInParent<Block_Pipe>();
        if (block == null || PipeConnectionsField == null)
            return null;

        Block_PipeBitmask[] connections =
            PipeConnectionsField.GetValue(block) as Block_PipeBitmask[];
        if (connections == null)
            return null;

        for (int i = 0; i < connections.Length; i++)
            if (connections[i] != null && connections[i].pipe == pipe)
                return connections[i].bitmaskTile;

        return null;
    }

    private static Vector3 GetPortPosition(Pipe pipe)
    {
        BitmaskTile tile = GetTile(pipe);
        return tile != null ? tile.transform.position : pipe.transform.position;
    }

    // Физическая высота трубы отделяется от координаты соединения
    private static Vector3 GetLogicalPortPosition(Pipe pipe)
    {
        Vector3 position = GetPortPosition(pipe);
        if (pipe != null && !(pipe is PipeSocket))
            position.y -= PipePivotHeight;
        return position;
    }

    private static Vector3 GetLogicalTilePosition(BitmaskTile tile)
    {
        Vector3 position = tile.transform.position;
        Pipe pipe = FindOwnerPipe(tile);
        if (pipe != null && !(pipe is PipeSocket))
            position.y -= PipePivotHeight;
        return position;
    }

    private static bool AllowsDirection(BitmaskTile tile, Pipe pipe, Vector3 direction)
    {
        if (tile == null)
        {
            if (pipe is PipeSocket)
                return Vector3.Dot(pipe.transform.forward.normalized, direction) > 0.98f;
            return true;
        }

        Vector3 axis;
        switch (tile.axisConnection)
        {
            case Axis.All:
                if (pipe is PipeSocket)
                    return Vector3.Dot(pipe.transform.forward.normalized, direction) > 0.98f;
                return true;
            case Axis.None: return false;
            case Axis.X: axis = tile.transform.right; break;
            case Axis.NX: axis = -tile.transform.right; break;
            case Axis.Y: axis = tile.transform.up; break;
            case Axis.NY: axis = -tile.transform.up; break;
            case Axis.Z: axis = tile.transform.forward; break;
            case Axis.NZ: axis = -tile.transform.forward; break;
            default: return false;
        }

        return Vector3.Dot(axis.normalized, direction) > 0.98f;
    }
}

[HarmonyPatch(typeof(Pipe), "OnPipePlaced")]
public static class Pipe_OnPipePlaced_IslandLife
{
    [HarmonyPrefix]
    public static bool Prefix(Pipe __instance)
    {
        if (!IslandPipeGrid.IsIslandPipe(__instance))
            return true;

        IslandPipeGrid.RegisterIslandPipe(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(BitmaskTile), "GetNeighbours")]
public static class BitmaskTile_GetNeighbours_IslandLife
{
    [HarmonyPrefix]
    public static bool Prefix(BitmaskTile __instance, ref BitmaskTile[] __result)
    {
        BitmaskTile[] neighbours;
        if (!IslandPipeGrid.TryGetIslandNeighbours(__instance, out neighbours))
            return true;

        __result = neighbours;
        return false;
    }
}

[HarmonyPatch(typeof(Pipe), "GetNearbyPipes")]
public static class Pipe_GetNearbyPipes_IslandLife
{
    [HarmonyPrefix]
    public static bool Prefix(Pipe __instance, ref List<Pipe> __result)
    {
        List<Pipe> nearby;
        if (!IslandPipeGrid.TryGetIslandNearbyPipes(__instance, out nearby))
            return true;

        __result = nearby;
        return false;
    }
}

[HarmonyPatch(typeof(PipeGroup), "IsStable")]
public static class PipeGroup_IsStable_IslandLife
{
    [HarmonyPrefix]
    public static bool Prefix(PipeGroup __instance, ref bool __result)
    {
        if (__instance == null)
            return true;

        List<Pipe> pipes = __instance.GetPipesCopy();
        if (pipes == null || pipes.Count == 0)
            return true;

        IslandBuildRoot root = null;
        for (int i = 0; i < pipes.Count; i++)
        {
            Pipe pipe = pipes[i];
            if (pipe == null || pipe.isAboutToBeDestroyed ||
                !pipe.gameObject.activeInHierarchy)
                return true;

            IslandBlockTag tag = pipe.GetComponentInParent<IslandBlockTag>();
            if (tag == null || tag.Root == null || tag.Root.Landmark == null)
                return true;

            if (root == null)
                root = tag.Root;
            else if (root != tag.Root)
                return true;
        }

        // Островная линия считается устойчивой независимо от коллайдеров плота
        __result = true;
        return false;
    }
}
