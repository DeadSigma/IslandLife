using HarmonyLib;
using System.Reflection;
using UnityEngine;

public static class IslandAntennaHeight
{
    private static readonly FieldInfo RequiredDistanceField =
        AccessTools.Field(typeof(Reciever), "requiredAntennaDistance");

    private static readonly FieldInfo AntennaSpacingField =
        AccessTools.Field(typeof(Reciever), "minDistanceBetweenAntennas");

    private static readonly FieldInfo AntennaPanelsField =
        AccessTools.Field(typeof(Reciever), "antennaPanels");

    public static bool IsIslandReceiver(Reciever receiver)
    {
        IslandBlockTag tag = FindTag(receiver);
        return tag != null && tag.Root != null && tag.Root.Landmark != null;
    }

    public static void RefreshIslandSignal(Reciever receiver)
    {
        if (receiver == null || receiver.antennas == null || receiver.antennas.Length == 0)
            return;

        IslandBlockTag receiverTag = FindTag(receiver);
        if (receiverTag == null || receiverTag.Root == null || receiverTag.Root.Landmark == null)
            return;

        if (RequiredDistanceField == null || AntennaSpacingField == null)
            return;

        object rangeValue = RequiredDistanceField.GetValue(receiver);
        object spacingValue = AntennaSpacingField.GetValue(receiver);
        if (!(rangeValue is Interval_Float) || !(spacingValue is float))
            return;

        Interval_Float range = (Interval_Float)rangeValue;
        float spacing = (float)spacingValue;
        Reciever_Antenna[] antennas = receiver.antennas;
        Landmark island = receiverTag.Root.Landmark;
        bool updated = false;

        for (int i = 0; i < antennas.Length; i++)
        {
            Reciever_Antenna antenna = antennas[i];
            if (antenna == null || !IsOnIsland(antenna, island))
                continue;

            updated = true;

            // Для острова проверяется горизонтальная дистанция без перепада высоты
            float distance = HorizontalDistance(receiver.transform.position, antenna.transform.position);
            antenna.toCloseToReciever = distance < range.minValue;
            antenna.toFarAwayFromReciever = distance > range.maxValue;
            antenna.onSameAltitudeAsReciever = true;

            bool tooClose = false;
            for (int j = 0; j < antennas.Length; j++)
            {
                Reciever_Antenna other = antennas[j];
                if (i == j || other == null)
                    continue;

                if (HorizontalDistance(antenna.transform.position, other.transform.position) < spacing)
                {
                    tooClose = true;
                    break;
                }
            }

            antenna.toCloseToOtherAntenna = tooClose;
        }

        if (!updated)
            return;

        int valid = 0;
        for (int i = 0; i < antennas.Length; i++)
        {
            Reciever_Antenna antenna = antennas[i];
            if (antenna != null && antenna.HasValidConnection)
                valid++;
        }

        receiver.hasSignal = valid == antennas.Length || Cheat.ForceRecieverToggle;

        // Ваниль обновляет панель до завершающего пересчёта
        AntennaPanel[] panels = AntennaPanelsField != null
            ? AntennaPanelsField.GetValue(receiver) as AntennaPanel[]
            : null;

        if (panels != null)
        {
            for (int i = 0; i < panels.Length && i < antennas.Length; i++)
            {
                if (panels[i] != null)
                    panels[i].RefreshUI(antennas[i]);
            }
        }
    }

    private static bool IsOnIsland(Reciever_Antenna antenna, Landmark island)
    {
        IslandBlockTag tag = FindTag(antenna);
        return tag != null && tag.Root != null && tag.Root.Landmark == island;
    }

    private static IslandBlockTag FindTag(Component component)
    {
        if (component == null)
            return null;

        IslandBlockTag tag = component.GetComponentInParent<IslandBlockTag>();
        if (tag == null)
            tag = component.GetComponentInChildren<IslandBlockTag>();

        return tag;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float x = a.x - b.x;
        float z = a.z - b.z;
        return Mathf.Sqrt(x * x + z * z);
    }
}

[HarmonyPatch(typeof(Reciever), "CheckSignal")]
public static class Reciever_CheckSignal_IslandHeight
{
    [HarmonyPostfix]
    public static void Postfix(Reciever __instance)
    {
        IslandAntennaHeight.RefreshIslandSignal(__instance);
    }
}

[HarmonyPatch(typeof(Reciever), "get_HasCorrectAltitude")]
public static class Reciever_HasCorrectAltitude_IslandHeight
{
    [HarmonyPostfix]
    public static void Postfix(Reciever __instance, ref bool __result)
    {
        if (IslandAntennaHeight.IsIslandReceiver(__instance))
            __result = true;
    }
}
