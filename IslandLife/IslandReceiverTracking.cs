using HarmonyLib;
using HMLLibrary;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

[Serializable]
public sealed class IslandReceiverSnapshotMessage : Message
{
    public int Sequence;
    public int PartIndex;
    public int PartCount;
    public string Payload;

    public IslandReceiverSnapshotMessage(int sequence, int partIndex, int partCount, string payload)
        : base((Messages)29173)
    {
        Sequence = sequence;
        PartIndex = partIndex;
        PartCount = partCount;
        Payload = payload;
    }
}

[Serializable]
public sealed class IslandReceiverRenameMessage : Message
{
    public string Id;
    public string Name;

    public IslandReceiverRenameMessage(string id, string name)
        : base((Messages)29174)
    {
        Id = id;
        Name = name;
    }
}

public static class IslandReceiverTracking
{
    private const string FileName = "IslandReceiverBeacons.dat";
    private const int Channel = 173;
    private const int PacketType = 29173;
    private const int RenamePacketType = 29174;
    public const int MaxNameLength = 24;
    private const int EntriesPerPacket = 24;
    private const float ScanInterval = 0.75f;
    private const float BroadcastInterval = 3f;
    private const float ClientTimeout = 15f;

    private sealed class Beacon
    {
        public string Id;
        public long LandmarkId;
        public Vector3 Position;
        public bool Online;
        public bool IsRaft;
        public string Name;
        public uint NetworkBlockId;
    }

    private sealed class RadarState
    {
        public readonly Dictionary<string, Reciever_Dot> Dots =
            new Dictionary<string, Reciever_Dot>();
    }

    private static readonly Dictionary<string, Beacon> Beacons =
        new Dictionary<string, Beacon>();

    private static readonly Dictionary<Reciever, RadarState> Radars =
        new Dictionary<Reciever, RadarState>();

    private static readonly Dictionary<int, List<Beacon>> PendingParts =
        new Dictionary<int, List<Beacon>>();

    private static readonly Queue<IslandReceiverSnapshotMessage> IncomingPackets =
        new Queue<IslandReceiverSnapshotMessage>();

    private static readonly FieldInfo DotPrefabField = AccessTools.Field(typeof(Reciever), "dotPrefab");
    private static readonly FieldInfo DotParentField = AccessTools.Field(typeof(Reciever), "dotParent");
    private static readonly FieldInfo RadarSectionField = AccessTools.Field(typeof(Reciever), "radarSection");
    private static readonly FieldInfo RadarLengthField = AccessTools.Field(typeof(Reciever), "radarLength");
    private static readonly FieldInfo RadarWidthField = AccessTools.Field(typeof(Reciever), "radarUIWidth");
    private static readonly FieldInfo DotTextField = AccessTools.Field(typeof(Reciever_Dot), "dotNumberText");

    private static string worldKey;
    private static bool worldLoaded;
    private static bool inGame;
    private static float nextScan;
    private static float nextBroadcast;
    private static float lastClientSnapshot;
    private static int sequence;
    private static int pendingSequence = -1;
    private static int expectedParts;
    private static int lastSequence = -1;

    private static MethodInfo getRaftIdMethod;
    private static MethodInfo getRaftNameMethod;
    private static bool bridgeResolved;
    private static float nextBridgeScan;

    private static IslandLife networkMod;
    private static MethodInfo sendNetworkMessageMethod;
    private static bool networkSubscribed;

    public static void InitializeNetworking(IslandLife mod, Harmony harmony)
    {
        networkMod = mod;
        networkSubscribed = false;
        sendNetworkMessageMethod = null;

        if (mod == null || harmony == null)
            return;

        try
        {
            BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            MethodInfo callback = null;
            MethodInfo subscription = null;
            MethodInfo sender = null;

            foreach (MethodInfo method in typeof(Mod).GetMethods(flags))
            {
                if (method.Name == "OnNetworkMessage" && !method.IsStatic && callback == null)
                    callback = method;

                if (method.Name == "SubscribeToNetworkChannel" &&
                    CanBuildArguments(method, null, false))
                    subscription = method;

                if (method.Name == "SendNetworkMessage" &&
                    CanBuildArguments(method, new IslandReceiverSnapshotMessage(0, 0, 1, ""), true))
                    sender = method;
            }

            if (callback == null || subscription == null || sender == null)
            {
                Debug.LogWarning("[IslandLife] Modern receiver network API unavailable - " +
                    "OnNetworkMessage=" + (callback != null) +
                    ", SubscribeToNetworkChannel=" + (subscription != null) +
                    ", SendNetworkMessage=" + (sender != null));
                return;
            }

            MethodInfo prefix = AccessTools.Method(typeof(IslandReceiverTracking_NetworkCallback), "Prefix");
            harmony.Patch(callback, prefix: new HarmonyMethod(prefix));

            subscription.Invoke(mod, BuildArguments(subscription, null, false));
            sendNetworkMessageMethod = sender;
            networkSubscribed = true;
            Debug.Log("[IslandLife] Receiver network channel subscribed: " + Channel);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[IslandLife] Receiver network initialization failed - " + e);
            sendNetworkMessageMethod = null;
            networkSubscribed = false;
        }
    }

    public static void ShutdownNetworking()
    {
        if (networkSubscribed && networkMod != null)
        {
            try
            {
                BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (MethodInfo method in typeof(Mod).GetMethods(flags))
                {
                    if (method.Name == "UnsubscribeFromNetworkChannel" &&
                        CanBuildArguments(method, null, false))
                    {
                        method.Invoke(networkMod, BuildArguments(method, null, false));
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[IslandLife] Receiver network unsubscribe failed - " + e.Message);
            }
        }

        networkSubscribed = false;
        sendNetworkMessageMethod = null;
        networkMod = null;
        lock (IncomingPackets)
            IncomingPackets.Clear();
    }

    // Выбираются методы с поддерживаемыми параметрами
    private static bool CanBuildArguments(MethodInfo method, Message packet, bool isSend)
    {
        ParameterInfo[] parameters = method.GetParameters();
        bool foundChannel = false;
        bool foundPacket = !isSend;

        foreach (ParameterInfo parameter in parameters)
        {
            Type type = parameter.ParameterType;
            if (type == typeof(int) && !foundChannel)
                foundChannel = true;
            else if (isSend && packet != null && !foundPacket &&
                     (type == typeof(object) || type == typeof(Message) ||
                      type.IsAssignableFrom(packet.GetType())))
                foundPacket = true;
            else if (type == typeof(EP2PSend) || type == typeof(Target))
                continue;
            else if (!parameter.IsOptional)
                return false;
        }

        return foundChannel && foundPacket;
    }

    private static object[] BuildArguments(MethodInfo method, Message packet, bool isSend,
                                           Target target = Target.Other)
    {
        ParameterInfo[] parameters = method.GetParameters();
        object[] args = new object[parameters.Length];
        bool foundChannel = false;
        bool foundPacket = !isSend;

        for (int i = 0; i < parameters.Length; i++)
        {
            Type type = parameters[i].ParameterType;
            if (type == typeof(int) && !foundChannel)
            {
                args[i] = Channel;
                foundChannel = true;
            }
            else if (isSend && packet != null && !foundPacket &&
                     (type == typeof(object) || type == typeof(Message) ||
                      type.IsAssignableFrom(packet.GetType())))
            {
                args[i] = packet;
                foundPacket = true;
            }
            else if (type == typeof(EP2PSend))
                args[i] = EP2PSend.k_EP2PSendReliable;
            else if (type == typeof(Target))
                args[i] = target;
            else
                args[i] = parameters[i].DefaultValue;
        }

        return args;
    }

    public static void HandleNetworkCallback(Mod mod, object[] arguments)
    {
        if (mod != networkMod || arguments == null)
            return;

        for (int i = 0; i < arguments.Length; i++)
        {
            Message packet = ExtractPacket(arguments[i], 3);
            if (packet == null)
                continue;

            if (!Raft_Network.IsHost && (int)packet.Type == PacketType)
            {
                IslandReceiverSnapshotMessage snapshot = packet as IslandReceiverSnapshotMessage;
                if (snapshot != null)
                    lock (IncomingPackets)
                        IncomingPackets.Enqueue(snapshot);
                return;
            }

            if (Raft_Network.IsHost && (int)packet.Type == RenamePacketType)
            {
                IslandReceiverRenameMessage rename = packet as IslandReceiverRenameMessage;
                if (rename != null)
                    ApplyNameChange(rename.Id, rename.Name);
                return;
            }
        }
    }

    private static Message ExtractPacket(object value, int depth)
    {
        Message message = value as Message;
        if (message != null || value == null || depth <= 0)
            return message;

        Type type = value.GetType();
        string[] names = { "message", "Message", "data", "Data", "payload", "Payload", "msg" };
        foreach (string name in names)
        {
            try
            {
                object inner = null;
                FieldInfo field = AccessTools.Field(type, name);
                if (field != null)
                    inner = field.GetValue(value);
                else
                {
                    PropertyInfo property = AccessTools.Property(type, name);
                    if (property != null && property.GetIndexParameters().Length == 0)
                        inner = property.GetValue(value, null);
                }

                if (inner == null || ReferenceEquals(inner, value))
                    continue;

                message = ExtractPacket(inner, depth - 1);
                if (message != null)
                    return message;
            }
            catch (Exception)
            {
                // Недоступное поле пропускается
            }
        }

        return null;
    }

    private static void ProcessReceivedPackets()
    {
        for (int i = 0; i < 64; i++)
        {
            IslandReceiverSnapshotMessage packet;
            lock (IncomingPackets)
            {
                if (IncomingPackets.Count == 0)
                    return;
                packet = IncomingPackets.Dequeue();
            }
            AcceptSnapshot(packet);
        }
    }

    public static void Update()
    {
        if (!RAPI.IsCurrentSceneGame())
        {
            if (inGame)
                Reset();
            return;
        }

        inGame = true;
        string key = Raft_Network.IsHost
            ? SaveAndLoad.CurrentGameFileName
            : "<remote-world>";
        if (string.IsNullOrEmpty(key))
            return;

        if (!worldLoaded || worldKey != key)
        {
            ResetWorld(key);
            if (Raft_Network.IsHost)
                ReadBeacons();
        }

        float now = Time.unscaledTime;

        if (Raft_Network.IsHost)
        {
            if (now >= nextScan)
            {
                ScanPlacedReceivers();
                nextScan = now + ScanInterval;
            }

            if (now >= nextBroadcast)
            {
                SendSnapshot();
                nextBroadcast = now + BroadcastInterval;
            }
        }
        else
        {
            ProcessReceivedPackets();

            if (lastClientSnapshot > 0f && now - lastClientSnapshot > ClientTimeout)
            {
                Beacons.Clear();
                lastClientSnapshot = 0f;
            }
        }
    }

    public static void Save()
    {
        if (!Raft_Network.IsHost || !worldLoaded)
            return;

        ScanPlacedReceivers();
        string path = GetPath();
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            string directory = Path.GetDirectoryName(path);
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            List<string> lines = new List<string>();
            lines.Add("IslandReceiverBeaconsV1");
            foreach (Beacon beacon in Beacons.Values)
            {
                if (!beacon.IsRaft)
                    lines.Add(SerializeBeacon(beacon));
            }

            string tempPath = path + ".tmp";
            File.WriteAllLines(tempPath, lines.ToArray(), Encoding.UTF8);
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", true);
                File.Delete(path);
            }
            File.Move(tempPath, path);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[IslandLife] Receiver beacon save failed - " + e.Message);
        }
    }

    public static void LoadAfterWorldRestore()
    {
        if (!Raft_Network.IsHost)
            return;

        string key = SaveAndLoad.CurrentGameFileName;
        if (string.IsNullOrEmpty(key))
            return;

        ResetWorld(key);
        ReadBeacons();
        nextScan = 0f;
        nextBroadcast = 0f;
    }

    public static void Remove(string id)
    {
        if (!Raft_Network.IsHost || string.IsNullOrEmpty(id))
            return;

        if (Beacons.Remove(id))
            nextBroadcast = 0f;
    }

    public static string GetIslandReceiverId(Reciever receiver)
    {
        if (receiver == null)
            return null;
        IslandBlockTag tag = FindIslandTag(receiver);
        if (tag == null || tag.Root == null || tag.Root.Landmark == null)
            return null;

        // У клиентов RecordId может отличаться от идентификатора хоста
        Block block = receiver.GetComponentInParent<Block>();
        if (block != null && block.ObjectIndex != 0U)
        {
            foreach (Beacon beacon in Beacons.Values)
            {
                if (!beacon.IsRaft && beacon.NetworkBlockId == block.ObjectIndex)
                    return beacon.Id;
            }
        }
        return tag.RecordId;
    }

    public static string GetName(string id)
    {
        Beacon beacon;
        return !string.IsNullOrEmpty(id) && Beacons.TryGetValue(id, out beacon) &&
               !string.IsNullOrEmpty(beacon.Name) ? beacon.Name : "Island";
    }

    private static string NormalizeName(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "Island";

        StringBuilder result = new StringBuilder(MaxNameLength);
        for (int i = 0; i < value.Length && result.Length < MaxNameLength; i++)
        {
            if (!char.IsControl(value[i]))
                result.Append(value[i]);
        }
        string name = result.ToString().Trim();
        return name.Length == 0 ? "Island" : name;
    }

    public static void RequestNameChange(string id, string name)
    {
        if (string.IsNullOrEmpty(id) || !worldLoaded)
            return;

        name = NormalizeName(name);
        if (Raft_Network.IsHost)
        {
            ApplyNameChange(id, name);
            return;
        }

        if (!networkSubscribed || sendNetworkMessageMethod == null || networkMod == null)
        {
            Debug.LogWarning("[IslandLife] Receiver name change unavailable - network not subscribed");
            return;
        }

        try
        {
            Message packet = new IslandReceiverRenameMessage(id, name);
            sendNetworkMessageMethod.Invoke(networkMod,
                BuildArguments(sendNetworkMessageMethod, packet, true, Target.Other));
        }
        catch (Exception e)
        {
            Debug.LogWarning("[IslandLife] Receiver rename request failed - " + e.Message);
        }
    }

    private static void ApplyNameChange(string id, string name)
    {
        if (!Raft_Network.IsHost || string.IsNullOrEmpty(id) || id.Length > 128)
            return;

        Beacon beacon;
        if (!Beacons.TryGetValue(id, out beacon))
        {
            uint blockIndex;
            if (!id.StartsWith("net-", StringComparison.Ordinal) ||
                !uint.TryParse(id.Substring(4), out blockIndex))
                return;

            foreach (Beacon candidate in Beacons.Values)
            {
                if (!candidate.IsRaft && candidate.NetworkBlockId == blockIndex && blockIndex != 0U)
                {
                    beacon = candidate;
                    id = candidate.Id;
                    break;
                }
            }
        }
        if (beacon == null || beacon.IsRaft)
            return;

        // Проверяется существование островного приёмника
        bool exists = false;
        foreach (Reciever receiver in Reciever.AllRecievers)
        {
            if (receiver != null && GetIslandReceiverId(receiver) == id)
            {
                exists = true;
                break;
            }
        }
        if (!exists)
            return;

        string normalized = NormalizeName(name);
        if (beacon.Name == normalized)
            return;
        beacon.Name = normalized;
        nextBroadcast = 0f;
        Save();
    }

    public static void UpdateRadar(Reciever receiver)
    {
        if (receiver == null || !worldLoaded || !IsPlaced(receiver))
            return;

        RadarState state;
        if (!Radars.TryGetValue(receiver, out state))
        {
            state = new RadarState();
            Radars.Add(receiver, state);
        }

        GameObject section = GetField<GameObject>(RadarSectionField, receiver);
        RectTransform parent = GetField<RectTransform>(DotParentField, receiver);
        Reciever_Dot prefab = GetField<Reciever_Dot>(DotPrefabField, receiver);

        if (section == null || !section.activeInHierarchy || parent == null || prefab == null)
        {
            HideDots(state);
            return;
        }

        IslandBlockTag ownTag = FindIslandTag(receiver);
        bool isIslandReceiver = ownTag != null && ownTag.Root != null &&
                                ownTag.Root.Landmark != null;
        long ownIsland = isIslandReceiver
            ? (long)ownTag.Root.Landmark.uniqueLandmarkIndex : -1L;

        HashSet<string> visible = new HashSet<string>();
        foreach (Beacon beacon in Beacons.Values)
        {
            if (!beacon.Online)
                continue;

            if (beacon.IsRaft)
            {
                // Плоты отображаются только на островных радарах
                // На плотах их уже отображает RaftReceiverTracking
                if (!isIslandReceiver)
                    continue;
            }
            else if (ownIsland >= 0 && ownIsland == beacon.LandmarkId)
            {
                continue;
            }

            Reciever_Dot dot;
            if (!state.Dots.TryGetValue(beacon.Id, out dot) || dot == null)
            {
                dot = UnityEngine.Object.Instantiate(prefab, parent);
                dot.transform.localScale = prefab.transform.localScale;
                dot.chunkPoint = null;
                dot.SetTargetedByReciever(false);
                dot.name = "IslandLife_ReceiverDot";
                state.Dots[beacon.Id] = dot;
                AttachIslandLabel(dot);
            }

            visible.Add(beacon.Id);
            PositionDot(receiver, beacon.Position, dot);
            UpdateBeaconLabel(dot, beacon.Name);
        }

        List<string> obsolete = null;
        foreach (KeyValuePair<string, Reciever_Dot> pair in state.Dots)
        {
            if (visible.Contains(pair.Key))
                continue;

            if (obsolete == null)
                obsolete = new List<string>();
            obsolete.Add(pair.Key);
        }

        if (obsolete != null)
        {
            foreach (string id in obsolete)
            {
                Reciever_Dot dot = state.Dots[id];
                if (dot != null)
                    UnityEngine.Object.Destroy(dot.gameObject);
                state.Dots.Remove(id);
            }
        }
    }

    public static void RemoveRadar(Reciever receiver)
    {
        RadarState state;
        if (!Radars.TryGetValue(receiver, out state))
            return;

        DestroyDots(state);
        Radars.Remove(receiver);
    }

    public static void Reset()
    {
        foreach (RadarState state in Radars.Values)
            DestroyDots(state);

        Radars.Clear();
        Beacons.Clear();
        PendingParts.Clear();
        lock (IncomingPackets)
            IncomingPackets.Clear();
        worldKey = null;
        worldLoaded = false;
        inGame = false;
        nextScan = 0f;
        nextBroadcast = 0f;
        lastClientSnapshot = 0f;
        sequence = 0;
        pendingSequence = -1;
        expectedParts = 0;
        lastSequence = -1;
    }

    private static void ResetWorld(string key)
    {
        Reset();
        worldKey = key;
        worldLoaded = true;
        inGame = true;
    }

    private static void ScanPlacedReceivers()
    {
        List<Reciever> receivers = Reciever.AllRecievers;
        if (receivers == null)
            return;

        HashSet<string> liveRafts = new HashSet<string>();
        foreach (Beacon existing in Beacons.Values)
        {
            if (existing.IsRaft)
                existing.Online = false;
        }

        for (int i = 0; i < receivers.Count; i++)
        {
            Reciever receiver = receivers[i];
            if (receiver == null || !receiver.gameObject.activeInHierarchy ||
                !IsPlaced(receiver))
                continue;

            IslandBlockTag tag = FindIslandTag(receiver);
            if (tag != null && tag.Root != null && tag.Root.Landmark != null)
            {
                if (string.IsNullOrEmpty(tag.RecordId))
                    continue;

                Beacon beacon;
                if (!Beacons.TryGetValue(tag.RecordId, out beacon))
                {
                    beacon = new Beacon { Id = tag.RecordId, Name = "Island" };
                    Beacons.Add(beacon.Id, beacon);
                }

                beacon.IsRaft = false;
                Block islandBlock = receiver.GetComponentInParent<Block>();
                beacon.NetworkBlockId = islandBlock != null ? islandBlock.ObjectIndex : 0U;
                beacon.LandmarkId = (long)tag.Root.Landmark.uniqueLandmarkIndex;
                beacon.Position = receiver.transform.position;
                beacon.Online = IsFunctional(receiver);
                continue;
            }

            // На плоту выбирается один работоспособный приёмник для каждой метки
            int raftId = GetExternalRaftId(receiver);
            string id = raftId != int.MinValue ? "raft:" + raftId :
                        "raft-receiver:" + receiver.ObjectIndex;
            liveRafts.Add(id);

            Beacon raft;
            bool functional = IsFunctional(receiver);
            if (!Beacons.TryGetValue(id, out raft))
            {
                raft = new Beacon { Id = id, IsRaft = true };
                Beacons.Add(id, raft);
            }
            raft.IsRaft = true;
            raft.Name = GetExternalRaftName(raftId);
            if (!raft.Online || functional)
            {
                raft.Position = receiver.transform.position;
                raft.Online = functional;
            }
        }

        List<string> obsolete = new List<string>();
        foreach (Beacon beacon in Beacons.Values)
        {
            if (beacon.IsRaft && !liveRafts.Contains(beacon.Id))
                obsolete.Add(beacon.Id);
        }
        foreach (string id in obsolete)
            Beacons.Remove(id);
    }

    private static bool IsPlaced(Reciever receiver)
    {
        FieldInfo field = AccessTools.Field(typeof(Reciever), "hasBeenPlaced");
        return field != null && (bool)field.GetValue(receiver);
    }

    private static void ResolveRaftBridge()
    {
        if (bridgeResolved && (getRaftIdMethod != null && getRaftNameMethod != null ||
                               Time.unscaledTime < nextBridgeScan))
            return;
        bridgeResolved = true;
        nextBridgeScan = Time.unscaledTime + 5f;
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type radar = assembly.GetType("RaftReceiverRadar", false);
            Type names = assembly.GetType("RaftNameRegistry", false);
            if (radar != null)
            {
                getRaftIdMethod = AccessTools.Method(radar, "GetRaftId");
            }
            if (names != null)
                getRaftNameMethod = AccessTools.Method(names, "GetName");
        }
    }

    private static int GetExternalRaftId(Reciever receiver)
    {
        ResolveRaftBridge();
        if (getRaftIdMethod == null)
            return int.MinValue;
        try
        {
            object value = getRaftIdMethod.Invoke(null, new object[] { receiver });
            return value is int ? (int)value : int.MinValue;
        }
        catch
        {
            return int.MinValue;
        }
    }

    private static string GetExternalRaftName(int raftId)
    {
        if (raftId != int.MinValue && getRaftNameMethod != null)
        {
            try
            {
                string name = getRaftNameMethod.Invoke(null, new object[] { raftId }) as string;
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            catch { }
        }
        return raftId == 0 ? "Main Raft" : raftId > 0 ? "Raft " + raftId : "Raft";
    }

    private static IslandBlockTag FindIslandTag(Reciever receiver)
    {
        IslandBlockTag tag = receiver.GetComponentInParent<IslandBlockTag>();
        if (tag == null)
            tag = receiver.GetComponentInChildren<IslandBlockTag>();
        return tag;
    }

    private static bool IsFunctional(Reciever receiver)
    {
        if (!receiver.hasSignal || !receiver.HasCorrectAltitude ||
            receiver.battery == null || !receiver.battery.CanGiveElectricity ||
            receiver.antennas == null || receiver.antennas.Length == 0)
            return false;

        for (int i = 0; i < receiver.antennas.Length; i++)
        {
            Reciever_Antenna antenna = receiver.antennas[i];
            if (antenna == null || !antenna.HasValidConnection)
                return false;
        }

        return true;
    }

    private static void SendSnapshot()
    {
        if (!networkSubscribed || sendNetworkMessageMethod == null || networkMod == null)
            return;

        List<Beacon> snapshot = new List<Beacon>();
        foreach (Beacon beacon in Beacons.Values)
        {
            if (beacon.Online)
                snapshot.Add(beacon);
        }

        int count = Math.Max(1, (snapshot.Count + EntriesPerPacket - 1) / EntriesPerPacket);
        sequence++;

        for (int part = 0; part < count; part++)
        {
            StringBuilder builder = new StringBuilder();
            int start = part * EntriesPerPacket;
            int end = Math.Min(start + EntriesPerPacket, snapshot.Count);
            for (int i = start; i < end; i++)
            {
                if (builder.Length > 0)
                    builder.Append('\n');
                builder.Append(SerializeBeacon(snapshot[i]));
            }

            try
            {
                Message packet = new IslandReceiverSnapshotMessage(sequence, part, count, builder.ToString());
                sendNetworkMessageMethod.Invoke(networkMod,
                    BuildArguments(sendNetworkMessageMethod, packet, true));
            }
            catch (Exception e)
            {
                if (part == 0)
                    Debug.LogWarning("[IslandLife] Receiver beacon sync failed - " + e);
                break;
            }
        }
    }

    private static void AcceptSnapshot(IslandReceiverSnapshotMessage packet)
    {
        if (!worldLoaded || packet == null || packet.PartCount <= 0 || packet.PartCount > 4096 ||
            packet.PartIndex < 0 || packet.PartIndex >= packet.PartCount ||
            packet.Sequence <= lastSequence)
            return;

        if (packet.Sequence != pendingSequence)
        {
            if (packet.Sequence < pendingSequence)
                return;

            PendingParts.Clear();
            pendingSequence = packet.Sequence;
            expectedParts = packet.PartCount;
        }

        if (expectedParts != packet.PartCount || PendingParts.ContainsKey(packet.PartIndex))
            return;

        List<Beacon> parsed = new List<Beacon>();
        string[] lines = (packet.Payload ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string line in lines)
        {
            Beacon beacon;
            if (TryParseBeacon(line, out beacon) && beacon.Online)
                parsed.Add(beacon);
        }

        PendingParts.Add(packet.PartIndex, parsed);
        if (PendingParts.Count != expectedParts)
            return;

        Dictionary<string, Beacon> replacement = new Dictionary<string, Beacon>();
        for (int part = 0; part < expectedParts; part++)
        {
            List<Beacon> received;
            if (!PendingParts.TryGetValue(part, out received))
                return;

            foreach (Beacon beacon in received)
                replacement[beacon.Id] = beacon;
        }

        Beacons.Clear();
        foreach (KeyValuePair<string, Beacon> pair in replacement)
            Beacons.Add(pair.Key, pair.Value);

        lastSequence = pendingSequence;
        pendingSequence = -1;
        PendingParts.Clear();
        lastClientSnapshot = Time.unscaledTime;
    }

    private static string SerializeBeacon(Beacon beacon)
    {
        CultureInfo culture = CultureInfo.InvariantCulture;
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(beacon.Id)) + "|" +
               beacon.LandmarkId.ToString(culture) + "|" +
               beacon.Position.x.ToString("R", culture) + "|" +
               beacon.Position.y.ToString("R", culture) + "|" +
               beacon.Position.z.ToString("R", culture) + "|" +
               (beacon.Online ? "1" : "0") + "|" +
               (beacon.IsRaft ? "R" : "I") + "|" +
               Convert.ToBase64String(Encoding.UTF8.GetBytes(beacon.Name ??
                   (beacon.IsRaft ? "Raft" : "Island"))) + "|" +
               beacon.NetworkBlockId.ToString(culture);
    }

    private static bool TryParseBeacon(string line, out Beacon beacon)
    {
        beacon = null;
        string[] fields = line.Split('|');
        if (fields.Length != 6 && fields.Length != 8 && fields.Length != 9)
            return false;

        try
        {
            CultureInfo culture = CultureInfo.InvariantCulture;
            string id = Encoding.UTF8.GetString(Convert.FromBase64String(fields[0]));
            long landmark;
            float x, y, z;
            if (string.IsNullOrEmpty(id) || !long.TryParse(fields[1], NumberStyles.Integer, culture, out landmark) ||
                !float.TryParse(fields[2], NumberStyles.Float, culture, out x) ||
                !float.TryParse(fields[3], NumberStyles.Float, culture, out y) ||
                !float.TryParse(fields[4], NumberStyles.Float, culture, out z) ||
                float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z) ||
                float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z))
                return false;

            beacon = new Beacon
            {
                Id = id,
                LandmarkId = landmark,
                Position = new Vector3(x, y, z),
                Online = fields[5] == "1",
                IsRaft = fields.Length == 8 && fields[6] == "R",
                Name = fields.Length >= 8 ?
                    Encoding.UTF8.GetString(Convert.FromBase64String(fields[7])) : "Island",
                NetworkBlockId = fields.Length == 9 ? uint.Parse(fields[8], culture) : 0U
            };
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string GetPath()
    {
        if (string.IsNullOrEmpty(worldKey) || string.IsNullOrEmpty(SaveAndLoad.WorldPath))
            return null;
        return Path.Combine(SaveAndLoad.WorldPath, worldKey, FileName);
    }

    private static void ReadBeacons()
    {
        string path = GetPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;

        try
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0 || lines[0].Trim('\uFEFF') != "IslandReceiverBeaconsV1")
                return;

            for (int i = 1; i < lines.Length; i++)
            {
                Beacon beacon;
                if (TryParseBeacon(lines[i], out beacon) && !beacon.IsRaft)
                {
                    // Индекс сетевого блока обновляется после загрузки острова
                    beacon.NetworkBlockId = 0U;
                    Beacons[beacon.Id] = beacon;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[IslandLife] Receiver beacon load failed - " + e.Message);
        }
    }

    private static void PositionDot(Reciever source, Vector3 target, Reciever_Dot dot)
    {
        Vector2 delta = new Vector2(target.x - source.transform.position.x,
                                     target.z - source.transform.position.z);
        float distance = delta.magnitude;
        float angle = Vector2.SignedAngle(Vector2.up, delta);
        if (angle < 0f)
            angle += 360f;

        float rotation = 360f - source.transform.eulerAngles.y + (360f - angle);
        Vector3 direction = new Vector3(Mathf.Sin(rotation * Mathf.Deg2Rad),
                                        Mathf.Cos(rotation * Mathf.Deg2Rad), 0f) * -1f;
        float length = GetFloat(RadarLengthField, source, 50f);
        float width = GetFloat(RadarWidthField, source, 270f);

        dot.gameObject.SetActive(true);
        dot.SetLocalPosition(direction * ((width * 0.5f) *
                             Mathf.Clamp01(distance / Mathf.Max(0.01f, length))));
        dot.SetLengthToPoint(distance);
        dot.SetText(distance.ToString("F0") + "m");
        dot.SetTargetedByReciever(false);
        dot.transform.SetAsLastSibling();
    }

    private static void AttachIslandLabel(Reciever_Dot dot)
    {
        Text original = GetField<Text>(DotTextField, dot);
        if (original == null)
            return;

        GameObject child = new GameObject("IslandLife_IslandLabel", typeof(RectTransform), typeof(Text), typeof(Outline));
        child.transform.SetParent(dot.transform, false);
        Text text = child.GetComponent<Text>();
        text.font = original.font;
        text.fontSize = original.fontSize;
        text.fontStyle = original.fontStyle;
        text.color = original.color;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        text.text = IslandLifeLocalization.Get("default_island");

        RectTransform rect = text.rectTransform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = new Vector2(0f, 22f);
        rect.sizeDelta = new Vector2(100f, 25f);

        Outline outline = child.GetComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
        outline.effectDistance = new Vector2(1f, -1f);
    }

    private static void UpdateBeaconLabel(Reciever_Dot dot, string name)
    {
        Transform child = dot.transform.Find("IslandLife_IslandLabel");
        if (child == null)
            return;
        Text text = child.GetComponent<Text>();
        if (text == null)
            return;

        name = string.IsNullOrEmpty(name) || name == "Island"
            ? IslandLifeLocalization.Get("default_island") : name;
        if (name == "Raft")
            name = IslandLifeLocalization.Get("default_raft");
        else if (name == "Main Raft")
            name = IslandLifeLocalization.Get("default_main_raft");
        else if (name.StartsWith("Raft ", StringComparison.Ordinal))
        {
            int raftNumber;
            if (int.TryParse(name.Substring(5), out raftNumber))
                name = IslandLifeLocalization.Get("default_raft") + " " + raftNumber;
        }
        if (text.text == name)
            return;

        text.text = name;
        RectTransform rect = text.rectTransform;
        rect.sizeDelta = new Vector2(Mathf.Clamp(text.preferredWidth + 12f, 55f, 260f),
                                      Mathf.Max(23f, text.preferredHeight + 4f));
    }

    private static void HideDots(RadarState state)
    {
        foreach (Reciever_Dot dot in state.Dots.Values)
        {
            if (dot != null)
                dot.gameObject.SetActive(false);
        }
    }

    private static void DestroyDots(RadarState state)
    {
        foreach (Reciever_Dot dot in state.Dots.Values)
        {
            if (dot != null)
                UnityEngine.Object.Destroy(dot.gameObject);
        }
        state.Dots.Clear();
    }

    private static T GetField<T>(FieldInfo field, object receiver) where T : class
    {
        return field != null ? field.GetValue(receiver) as T : null;
    }

    private static float GetFloat(FieldInfo field, object receiver, float fallback)
    {
        if (field == null)
            return fallback;
        object value = field.GetValue(receiver);
        return value is float ? (float)value : fallback;
    }
}

[HarmonyPatch(typeof(Reciever), "Update")]
public static class Reciever_Update_IslandReceiverTracking
{
    [HarmonyPostfix]
    public static void Postfix(Reciever __instance)
    {
        IslandReceiverTracking.UpdateRadar(__instance);
    }
}

[HarmonyPatch(typeof(Reciever), "OnDestroy")]
public static class Reciever_Destroy_IslandReceiverTracking
{
    [HarmonyPrefix]
    public static void Prefix(Reciever __instance)
    {
        IslandReceiverTracking.RemoveRadar(__instance);
    }
}

[HarmonyPatch(typeof(IslandLifeStorage), "RemoveRecord")]
public static class IslandLifeStorage_RemoveReceiverBeacon
{
    [HarmonyPostfix]
    public static void Postfix(string recordId)
    {
        IslandReceiverTracking.Remove(recordId);
    }
}

[HarmonyPatch(typeof(SaveAndLoad), "SaveGame")]
public static class SaveAndLoad_SaveReceiverBeacons
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        IslandReceiverTracking.Save();
    }
}

[HarmonyPatch(typeof(SaveAndLoad), "RestoreRGDGame")]
public static class SaveAndLoad_LoadReceiverBeacons
{
    [HarmonyPostfix]
    public static void Postfix()
    {
        IslandReceiverTracking.LoadAfterWorldRestore();
    }
}

// Входящие сообщения обрабатываются через callback Mod
public static class IslandReceiverTracking_NetworkCallback
{
    public static void Prefix(Mod __instance, object[] __args)
    {
        IslandReceiverTracking.HandleNetworkCallback(__instance, __args);
    }
}
