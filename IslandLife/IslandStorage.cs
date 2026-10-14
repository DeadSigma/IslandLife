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

[Serializable]
public class IslandLifeSaveData
{
    [OptionalField]
    public int version = 3;
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

    [OptionalField]
    public bool receiverStateSaved;
    [OptionalField]
    public bool receiverBatteryInstalled;
    [OptionalField]
    public int receiverBatteryItemIndex;
    [OptionalField]
    public int receiverBatteryUses;
    [OptionalField]
    public bool receiverBatteryOn;

    [OptionalField]
    public RGD_Block vanillaBlockState;
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
            data.version = 3;

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
                        false,
                        int.MinValue,
                        record.vanillaBlockState != null ? record.vanillaBlockState.BlockObjectIndex : 0U,
                        record.vanillaBlockState != null ? record.vanillaBlockState.NetworkedObjectIndex : 0U
                    );

                if (restored != null)
                {
                    RestoreVanillaBlockState(restored, record, player);
                    RestoreReceiverState(restored, record);
                    IslandLifeNetwork.BroadcastExisting(restored);
                }
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

        CaptureReceiverState(record, block.GetComponentInChildren<Reciever>(true));
        CaptureVanillaBlockState(record, block);
    }

    public static void CaptureBeforeUnload(IslandBlockTag tag)
    {
        if (!Raft_Network.IsHost || restoring || tag == null ||
            string.IsNullOrEmpty(tag.RecordId) ||
            string.IsNullOrEmpty(loadedWorldKey) || loadedWorldKey != GetWorldKey())
            return;

        IslandBlockRecord record = FindRecord(tag.RecordId);
        if (record == null)
            return;

        Block block = tag.GetComponent<Block>();
        if (block == null)
            return;

        try
        {
            FillRecord(record, block, tag);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[IslandLife] Не удалось сохранить состояние перед выгрузкой " +
                             tag.RecordId + ": " + ex.Message);
        }
    }

    private static void CaptureVanillaBlockState(IslandBlockRecord record, Block block)
    {
        if (record == null || block == null)
            return;

        try
        {
            RGD_Block state = block.Serialize_Save() as RGD_Block;
            if (state != null)
                record.vanillaBlockState = state;
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[IslandLife] RGD сохранение " + block.name + ": " + ex.Message);
        }
    }

    public static void CaptureVanillaGameState(RGD_Game game)
    {
        if (!Raft_Network.IsHost || game == null || game.behaviours == null)
            return;

        EnsureLoaded();
        if (!storageHealthy)
            return;

        Dictionary<uint, IslandBlockTag> tagsByIndex = new Dictionary<uint, IslandBlockTag>();
        IslandBlockTag[] tags = UnityEngine.Object.FindObjectsOfType<IslandBlockTag>();
        for (int i = 0; i < tags.Length; i++)
        {
            IslandBlockTag tag = tags[i];
            Block block = tag != null ? tag.GetComponent<Block>() : null;
            if (block != null && tag.Root != null && tag.Root.Landmark != null)
                tagsByIndex[block.ObjectIndex] = tag;
        }

        for (int i = 0; i < game.behaviours.Count; i++)
        {
            RGD_Block state = game.behaviours[i] as RGD_Block;
            if (state == null)
                continue;

            IslandBlockTag tag;
            if (!tagsByIndex.TryGetValue(state.BlockObjectIndex, out tag))
                continue;

            IslandBlockRecord record = FindRecord(tag.RecordId);
            if (record == null)
            {
                record = new IslandBlockRecord { id = tag.RecordId };
                data.blocks.Add(record);
            }

            Block block = tag.GetComponent<Block>();
            if (block == null)
                continue;

            FillRecord(record, block, tag);
            record.vanillaBlockState = state;
        }
    }

    private static void RestoreVanillaBlockState(
        Block block, IslandBlockRecord record, Network_Player player)
    {
        if (block == null || record == null || record.vanillaBlockState == null)
            return;

        RGD_Block state = record.vanillaBlockState;
        Vector3 worldPosition = block.transform.position;
        Quaternion worldRotation = block.transform.rotation;

        try
        {
            state.RestoreBlock(block);
            block.transform.SetPositionAndRotation(worldPosition, worldRotation);

            // Дополнительные данные восстанавливаются теми же методами, что и в Raft
            switch (state.Type)
            {
                case RGDType.Block_Door:
                    (state as RGD_Door)?.Restore(block.GetComponent<Door>());
                    break;
                case RGDType.Block_Cropplot:
                    (state as RGD_Cropplot)?.RestoreCropplot(
                        block.GetComponent<Cropplot>(), player.PlantManager);
                    break;
                case RGDType.Block_CookingStand:
                case RGDType.Block_Electric_CookingStand:
                    (state as RGD_CookingStand)?.RestoreStand(block.GetComponent<Block_CookingStand>());
                    break;
                case RGDType.Block_Storage:
                {
                    Storage_Small storage = block.GetComponent<Storage_Small>();
                    if (storage != null)
                        (state as RGD_Storage)?.RestoreInventory(storage.GetInventoryReference());
                    break;
                }
                case RGDType.Block_AnchorStationary:
                    (state as RGD_AnchorStationary)?.RestoreAnchor(block.GetComponent<Anchor_Stationary>());
                    break;
                case RGDType.Block_AnchorThrowable:
                    (state as RGD_AnchorThrowable)?.RestoreAnchorThrowable(
                        block, block.GetComponent<Anchor_Throwable_Stand>());
                    break;
                case RGDType.Block_ItemCollector:
                    (state as RGD_ItemCollector)?.RestoreItemCollector(
                        block.GetComponentInChildren<ItemCollector>());
                    break;
                case RGDType.Block_Scarecrow:
                    (state as RGD_Scarecrow)?.RestoreScarecrow(block.GetComponent<Scarecrow>());
                    break;
                case RGDType.Block_Brick:
                    (state as RGD_Brick)?.RestoreBrick(block as Brick_Wet);
                    break;
                case RGDType.Block_BirdsNest:
                    (state as RGD_Birdsnest)?.RestoreBirdsNest(block.GetComponent<BirdsNest>());
                    break;
                case RGDType.Block_ResearchTable:
                    (state as RGD_ResearchTable)?.RestoreResearchTable(block.GetComponent<ResearchTable>());
                    break;
                case RGDType.Block_Sail:
                    (state as RGD_Sail)?.RestoreSail(block.GetComponent<Sail>());
                    break;
                case RGDType.Block_Sharkbait:
                    (state as RGD_Sharkbait)?.RestoreSharbait(block.GetComponent<SharkBait>());
                    break;
                case RGDType.Block_Reciever:
                {
                    Reciever receiver = block.GetComponent<Reciever>();
                    if (receiver != null)
                        receiver.Restore(state as RGD_Reciever);
                    break;
                }
                case RGDType.ID_TrophyHolder:
                    (state as RGDTrophyHolder)?.RestoreTrophyHolder(
                        block.GetComponentInChildren<TrophyHolder>());
                    break;
                case RGDType.Block_Sprinkler:
                {
                    RGD_Block_Sprinkler sprinkler = state as RGD_Block_Sprinkler;
                    if (sprinkler != null)
                        sprinkler.rgdSprinkler.RestoreSprinkler(
                            block.GetComponentInChildren<Sprinkler>());
                    break;
                }
                case RGDType.Block_Radio:
                {
                    RGD_Block_Radio radio = state as RGD_Block_Radio;
                    RadioPlayer playerRadio = block.GetComponentInChildren<RadioPlayer>();
                    if (radio != null && playerRadio != null)
                        playerRadio.RestoreRadio(radio.rgdRadio);
                    break;
                }
                case RGDType.Block_SteeringWheel:
                {
                    SteeringWheel wheel = block.GetComponentInChildren<SteeringWheel>();
                    if (wheel != null)
                        wheel.RestoreWheel(state as RGD_SteeringWheel);
                    break;
                }
                case RGDType.Block_Motor_Wheel:
                {
                    MotorWheel motor = block.GetComponentInChildren<MotorWheel>();
                    if (motor != null)
                        motor.RestoreMotor(state as RGD_MotorWheel);
                    break;
                }
                case RGDType.Block_BioFuelCreator:
                {
                    RGD_Block_BiofuelExtractor extractor = state as RGD_Block_BiofuelExtractor;
                    Placeable_BiofuelExtractor component = block.GetComponent<Placeable_BiofuelExtractor>();
                    if (extractor != null && component != null)
                        component.RestoreExtractor(extractor.rgdBioFuelCreator);
                    break;
                }
                case RGDType.Block_BeeHive:
                    (state as RGD_Block_BeeHive)?.RestoreBeehive(
                        block.GetComponentInChildren<BeeHive>());
                    break;
                case RGDType.Block_BatteryCharger:
                {
                    RGD_Block_BatteryCharger charger = state as RGD_Block_BatteryCharger;
                    if (charger != null)
                        charger.rgdBatteryCharger.RestoreBatteryCharger(
                            block.GetComponentInChildren<BatteryCharger>());
                    break;
                }
                case RGDType.Block_WindTurbine:
                {
                    RGD_Block_WindTurbine turbine = state as RGD_Block_WindTurbine;
                    if (turbine != null)
                        turbine.rgdWindTurbine.Restore(
                            block.GetComponentInChildren<WindTurbine>());
                    break;
                }
                case RGDType.Block_Recycler:
                {
                    RGD_Block_Recycler recycler = state as RGD_Block_Recycler;
                    Placeable_RecyclerExtractor component = block.GetComponent<Placeable_RecyclerExtractor>();
                    if (recycler != null && component != null)
                        component.RestoreExtractor(recycler.rgdRecycler);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[IslandLife] RGD восстановление " + block.name +
                             " (" + state.Type + "): " + ex);
        }
        finally
        {
            block.transform.SetPositionAndRotation(worldPosition, worldRotation);
        }
    }

    private static void CaptureReceiverState(IslandBlockRecord record, Reciever reciever)
    {
        if (record == null || reciever == null || reciever.battery == null)
            return;

        Battery battery = reciever.battery;
        record.receiverStateSaved = true;
        record.receiverBatteryInstalled = !battery.BatterySlotIsEmpty;
        record.receiverBatteryOn = record.receiverBatteryInstalled && battery.On;

        if (record.receiverBatteryInstalled)
        {
            ItemInstance instance = battery.GetBatteryInstance();
            record.receiverBatteryItemIndex = instance.UniqueIndex;
            record.receiverBatteryUses = instance.Uses;
        }
        else
        {
            record.receiverBatteryItemIndex = 0;
            record.receiverBatteryUses = 0;
        }
    }

    private static void RestoreReceiverState(Block block, IslandBlockRecord record)
    {
        if (block == null || record == null || !record.receiverStateSaved)
            return;

        Reciever reciever = block.GetComponentInChildren<Reciever>(true);
        if (reciever == null || reciever.battery == null)
            return;

        Battery battery = reciever.battery;
        if (!battery.BatterySlotIsEmpty)
            return;

        if (!record.receiverBatteryInstalled)
        {
            battery.On = false;
            return;
        }

        if (record.receiverBatteryItemIndex <= 0 ||
            ItemManager.GetItemByIndex(record.receiverBatteryItemIndex) == null)
        {
            Debug.LogWarning("[IslandLife] Неизвестный аккумулятор приёмника: " + record.receiverBatteryItemIndex);
            return;
        }

        RGD_Battery savedBattery = new RGD_Battery
        {
            installedUniqueIndex = record.receiverBatteryItemIndex,
            batteryUses = record.receiverBatteryUses
        };

        savedBattery.RestoreBattery(battery);
        if (!battery.BatterySlotIsEmpty)
            battery.On = record.receiverBatteryOn && battery.CanGiveElectricity;
    }

    public static void CaptureReceiverBeforeDestroy(Reciever reciever)
    {
        if (!Raft_Network.IsHost || restoring || reciever == null ||
            string.IsNullOrEmpty(loadedWorldKey) || loadedWorldKey != GetWorldKey())
            return;

        IslandBlockTag tag = reciever.GetComponentInParent<IslandBlockTag>();
        if (tag == null || string.IsNullOrEmpty(tag.RecordId))
            return;

        IslandBlockRecord record = FindRecord(tag.RecordId);
        if (record != null)
            CaptureReceiverState(record, reciever);
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

        target.receiverStateSaved = source.receiverStateSaved;
        target.receiverBatteryInstalled = source.receiverBatteryInstalled;
        target.receiverBatteryItemIndex = source.receiverBatteryItemIndex;
        target.receiverBatteryUses = source.receiverBatteryUses;
        target.receiverBatteryOn = source.receiverBatteryOn;
        target.vanillaBlockState = source.vanillaBlockState;
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

        IslandLifeStorage.CaptureVanillaGameState(__result);

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

[HarmonyPatch(typeof(Reciever), "OnDestroy")]
public static class Reciever_OnDestroy_IslandLifeBattery
{
    [HarmonyPrefix]
    public static void Prefix(Reciever __instance)
    {
        IslandLifeStorage.CaptureReceiverBeforeDestroy(__instance);
    }
}

[HarmonyPatch(typeof(IslandBlockTag), "OnDisable")]
public static class IslandBlockTag_OnDisable_SaveState
{
    [HarmonyPrefix]
    public static void Prefix(IslandBlockTag __instance)
    {
        IslandLifeStorage.CaptureBeforeUnload(__instance);
    }
}

[HarmonyPatch(typeof(IslandBlockTag), "OnDestroy")]
public static class IslandBlockTag_OnDestroy_SaveState
{
    [HarmonyPrefix]
    public static void Prefix(IslandBlockTag __instance)
    {
        IslandLifeStorage.CaptureBeforeUnload(__instance);
    }
}
