using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using HMLLibrary;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public static class IslandLifeHotkeys
{
    private const string PrefPrefix = "IslandLife_Keybind_v1_";
    private const string IdPrefix = "islandlife_";
    private const string HarmonyId = "el_neuman.islandlife.native-hotkeys";

    private sealed class Binding
    {
        public readonly string Id;
        public readonly string Label;
        public readonly KeyCode Default;
        public KeyCode Main;
        public KeyCode Alt;

        public Binding(string id, string label, KeyCode defaultKey)
        {
            Id = id;
            Label = label;
            Default = defaultKey;
            Main = defaultKey;
        }
    }

    private sealed class Row
    {
        public Binding Binding;
        public GameObject Root;
        public KeybindInterface Native;
        public Component Label;
        public Button MainButton;
        public Button AltButton;
        public Text MainText;
        public Text AltText;
    }

    private static readonly Binding[] Bindings =
    {
        new Binding("grid", "key_grid", KeyCode.G),
        new Binding("height", "key_height", KeyCode.LeftAlt),
        new Binding("tilt_x", "key_tilt_x", KeyCode.X),
        new Binding("tilt_z", "key_tilt_z", KeyCode.Z),
        new Binding("reset_modifier", "key_reset_modifier", KeyCode.LeftControl),
        new Binding("reset", "key_reset", KeyCode.R),
        new Binding("align", "key_align", KeyCode.LeftShift),
        new Binding("rename", "key_rename", KeyCode.N),
        new Binding("restore_defaults", "key_restore_defaults", KeyCode.None)
    };

    private static readonly List<Row> Rows = new List<Row>();
    private static Harmony harmony;
    private static bool installed;
    private static bool creating;
    private static ControlsSettingsBox currentBox;
    private static float nextCheck;
    private static int languageVersion = -1;
    private static string lastStatus;
    private static float rowStep = 50f;
    private static KeyCode gameRotateKey = KeyCode.R;
    private static KeyCode gameRotateAltKey = KeyCode.None;

    public static void Initialize()
    {
        foreach (Binding binding in Bindings)
        {
            binding.Main = LoadKey(binding, false);
            binding.Alt = LoadKey(binding, true);
        }

        if (installed)
            return;

        installed = true;
        nextCheck = 0f;
        languageVersion = -1;
        lastStatus = null;
        gameRotateKey = KeyCode.R;
        gameRotateAltKey = KeyCode.None;

        try
        {
            harmony = new Harmony(HarmonyId);
            Patch("InitializeKeybindInterfaces", nameof(BeforeInitialize), null);
            Patch("Load", nameof(BeforeLoad), nameof(AfterLoad), typeof(RGD_Settings));
            Patch("Save", nameof(BeforeSave), nameof(AfterSave), typeof(RGD_Settings));
            Patch("SetFocus", null, nameof(AfterSetFocus), typeof(bool));
            Maintain();
        }
        catch (Exception exception)
        {
            Debug.LogError("[IslandLife] Hotkeys: Harmony initialization failed: " + exception);
            if (harmony != null)
                harmony.UnpatchAll(HarmonyId);
            harmony = null;
            installed = false;
        }
    }

    public static void Update()
    {
        if (!installed || Time.unscaledTime < nextCheck)
            return;

        nextCheck = Time.unscaledTime + 0.8f;
        Maintain();
    }

    public static void Reset()
    {
        installed = false;
        if (harmony != null)
            harmony.UnpatchAll(HarmonyId);
        harmony = null;

        if (currentBox != null)
        {
            FieldInfo field = AccessTools.Field(typeof(ControlsSettingsBox), "keybindInterfaces");
            KeybindInterface[] registered = field != null ? field.GetValue(currentBox) as KeybindInterface[] : null;
            if (registered != null)
            {
                List<KeybindInterface> vanilla = new List<KeybindInterface>();
                foreach (KeybindInterface entry in registered)
                    if (entry != null && !entry.Identifier.StartsWith(IdPrefix, StringComparison.Ordinal))
                        vanilla.Add(entry);
                field.SetValue(currentBox, vanilla.ToArray());
            }
        }
        DestroyRows();
        currentBox = null;
        nextCheck = 0f;
        languageVersion = -1;
        lastStatus = null;
        if (MyInput.Keybinds != null)
            foreach (Binding binding in Bindings)
                MyInput.Keybinds.Remove(IdPrefix + binding.Id);
    }

    public static bool Held(string id)
    {
        if (KeybindInterface.AssigningKey)
            return false;
        Binding binding = FindBinding(id);
        if (binding == null)
            return false;
        KeyCode main, alt;
        GetKeys(binding, out main, out alt);
        return GetKey(main) || GetKey(alt);
    }

    public static bool Down(string id)
    {
        if (KeybindInterface.AssigningKey)
            return false;
        Binding binding = FindBinding(id);
        if (binding == null)
            return false;
        KeyCode main, alt;
        GetKeys(binding, out main, out alt);
        return GetKeyDown(main) || GetKeyDown(alt);
    }

    public static KeyCode Primary(string id)
    {
        Binding binding = FindBinding(id);
        if (binding == null)
            return KeyCode.None;
        KeyCode main, alt;
        GetKeys(binding, out main, out alt);
        return main != KeyCode.None ? main : alt;
    }

    public static string Display(string id)
    {
        Binding binding = FindBinding(id);
        if (binding == null)
            return "?";
        KeyCode main, alt;
        GetKeys(binding, out main, out alt);
        if (alt == KeyCode.None)
            return Format(main);
        if (main == KeyCode.None)
            return Format(alt);
        return Format(main) + " / " + Format(alt);
    }

    public static string GameRotateDisplay()
    {
        if (gameRotateAltKey == KeyCode.None)
            return Format(gameRotateKey);
        if (gameRotateKey == KeyCode.None)
            return Format(gameRotateAltKey);
        return Format(gameRotateKey) + " / " + Format(gameRotateAltKey);
    }

    public static bool GameRotateReleased()
    {
        return (gameRotateKey != KeyCode.None && Input.GetKeyUp(gameRotateKey)) ||
               (gameRotateAltKey != KeyCode.None && Input.GetKeyUp(gameRotateAltKey));
    }

    private static void Patch(string methodName, string prefixName, string postfixName, params Type[] signature)
    {
        MethodInfo target = signature == null || signature.Length == 0
            ? AccessTools.Method(typeof(ControlsSettingsBox), methodName)
            : AccessTools.Method(typeof(ControlsSettingsBox), methodName, signature);
        if (target == null)
        {
            Debug.LogWarning("[IslandLife] Hotkeys: method not found: " + methodName);
            return;
        }
        HarmonyMethod prefix = prefixName != null ? new HarmonyMethod(typeof(IslandLifeHotkeys), prefixName) : null;
        HarmonyMethod postfix = postfixName != null ? new HarmonyMethod(typeof(IslandLifeHotkeys), postfixName) : null;
        harmony.Patch(target, prefix, postfix);
    }

    private static void BeforeInitialize(ControlsSettingsBox __instance)
    {
        if (installed)
        {
            EnsureRows(__instance);
            foreach (Row row in Rows)
                if (row.Root != null)
                    foreach (EventTrigger trigger in row.Root.GetComponentsInChildren<EventTrigger>(true))
                        trigger.triggers.Clear();
        }
    }

    private static void BeforeLoad(ControlsSettingsBox __instance)
    {
        if (installed)
            EnsureRows(__instance);
    }

    private static void AfterLoad(ControlsSettingsBox __instance)
    {
        if (!installed)
            return;
        if (EnsureRows(__instance))
            Rebuild(__instance);
        MigrateLegacyBindings(__instance);
        SyncFromNative(__instance);
        FixContentHeight(__instance);
        TranslateRows();
    }

    private static void BeforeSave(ControlsSettingsBox __instance)
    {
        if (!installed)
            return;
        if (EnsureRows(__instance))
            Rebuild(__instance);
    }

    private static void AfterSave(ControlsSettingsBox __instance)
    {
        if (!installed || __instance != currentBox)
            return;
        SyncFromNative(__instance);
        foreach (Binding binding in Bindings)
        {
            if (binding.Id == "restore_defaults")
                continue;
            SaveKey(binding, false);
        }
        PlayerPrefs.Save();
    }

    private static void AfterSetFocus(ControlsSettingsBox __instance, bool gamepad)
    {
        if (!installed || gamepad)
            return;
        if (EnsureRows(__instance))
            Rebuild(__instance);
        FixContentHeight(__instance);
        TranslateRows();
    }

    private static void Maintain()
    {
        try
        {
            Settings settings = ComponentManager<Settings>.Value;
            ControlsSettingsBox box = settings != null ? settings.controls : null;
            if (box == null || !box.gameObject.scene.IsValid())
            {
                Status("waiting", "ControlsSettingsBox недоступен");
                return;
            }

            bool added = EnsureRows(box);
            if (added)
                Rebuild(box);
            if (added || !BindingsAvailable())
                SyncFromNative(box);
            FixContentHeight(box);
            if (languageVersion != IslandLifeLocalization.Version)
                TranslateRows();
        }
        catch (Exception exception)
        {
            Status("error", "Ошибка обновления: " + exception);
        }
    }

    private static bool EnsureRows(ControlsSettingsBox box)
    {
        if (creating || box == null)
            return false;
        if (currentBox != null && currentBox != box)
        {
            DestroyRows();
            currentBox = null;
        }

        if (currentBox == box && Rows.Count == Bindings.Length)
        {
            bool valid = true;
            foreach (Row row in Rows)
                if (row.Root == null || row.Native == null || !row.Root.transform.IsChildOf(box.transform))
                {
                    valid = false;
                    break;
                }
            if (valid)
                return false;
        }

        creating = true;
        try
        {
            DestroyRows();
            currentBox = box;
            RectTransform content = GetContent(box);
            KeybindInterface template = SelectTemplate(box, content);
            if (template == null)
            {
                Status("no-template", "Не найдена штатная строка клавиш, contentBox: " + (content != null));
                return false;
            }
            Transform source = FindTemplateRoot(template, box, content);
            Transform parent = source != null ? source.parent : null;
            if (parent == null)
            {
                Status("no-parent", "Не найден контейнер для строк управления");
                return false;
            }

            bool autoLayout = parent.GetComponent<LayoutGroup>() != null;
            RectTransform sourceRect = source as RectTransform;
            rowStep = sourceRect != null ? Mathf.Max(44f, sourceRect.rect.height + 7f) : 50f;

            foreach (Binding binding in Bindings)
            {
                GameObject copy = UnityEngine.Object.Instantiate(source.gameObject, parent, false);
                copy.name = "IslandLife_Keybind_" + binding.Id;
                copy.SetActive(false);
                copy.transform.SetAsLastSibling();
                KeybindInterface native = copy.GetComponentInChildren<KeybindInterface>(true);
                if (native == null)
                {
                    UnityEngine.Object.Destroy(copy);
                    continue;
                }
                KeyConnection main = GetConnection(native, "mainKey");
                KeyConnection alt = GetConnection(native, "altKey");
                if (main == null || main.button == null || main.text == null)
                {
                    UnityEngine.Object.Destroy(copy);
                    continue;
                }

                FieldInfo idField = AccessTools.Field(typeof(KeybindInterface), "identifier");
                FieldInfo mainDefault = AccessTools.Field(typeof(KeybindInterface), "mainKeyDefault");
                FieldInfo altDefault = AccessTools.Field(typeof(KeybindInterface), "altKeyDefault");
                if (idField == null || mainDefault == null || altDefault == null)
                {
                    UnityEngine.Object.Destroy(copy);
                    continue;
                }
                idField.SetValue(native, IdPrefix + binding.Id);
                mainDefault.SetValue(native, binding.Default);
                altDefault.SetValue(native, KeyCode.None);
                native.Set(ReadSavedBinding(box, binding));

                foreach (EventTrigger trigger in copy.GetComponentsInChildren<EventTrigger>(true))
                    trigger.triggers.Clear();
                foreach (MonoBehaviour component in copy.GetComponentsInChildren<MonoBehaviour>(true))
                    if (component != null && component.GetType().FullName == "I2.Loc.Localize")
                        component.enabled = false;

                // Обработчики кнопок клона переназначаются на его собственный компонент
                main.button.onClick = new Button.ButtonClickedEvent();
                if (alt != null && alt.button != null)
                    alt.button.onClick = new Button.ButtonClickedEvent();
                if (binding.Id == "restore_defaults")
                    main.button.onClick.AddListener(RestoreDefaults);
                else
                {
                    main.button.onClick.AddListener(native.StartAssignMainKey);
                    if (alt != null && alt.button != null)
                        alt.button.onClick.AddListener(native.StartAssignAltKey);
                }
                if (binding.Id == "restore_defaults" && alt != null && alt.button != null)
                    alt.button.gameObject.SetActive(false);

                Row row = new Row
                {
                    Binding = binding,
                    Root = copy,
                    Native = native,
                    Label = FindLabel(copy.transform, main.text, alt != null ? alt.text : null),
                    MainButton = main.button,
                    AltButton = alt != null ? alt.button : null,
                    MainText = main.text,
                    AltText = alt != null ? alt.text : null
                };
                Rows.Add(row);
                copy.SetActive(true);
            }

            if (Rows.Count != Bindings.Length)
            {
                Status("partial", "Добавлено строк " + Rows.Count + "/" + Bindings.Length);
                DestroyRows();
                return false;
            }

            if (autoLayout)
            {
                RectTransform parentRect = parent as RectTransform;
                if (parentRect != null)
                    LayoutRebuilder.MarkLayoutForRebuild(parentRect);
            }
            else
                PositionRows(parent);

            languageVersion = -1;
            TranslateRows();
            FixContentHeight(box);
            Status("ready", "Нативные строки добавлены: " + Rows.Count + ", родитель: " + parent.name);
            return true;
        }
        catch (Exception exception)
        {
            Status("error-create", "Ошибка создания клавиш: " + exception);
            DestroyRows();
            return false;
        }
        finally
        {
            creating = false;
        }
    }

    private static void Rebuild(ControlsSettingsBox box)
    {
        MethodInfo method = AccessTools.Method(typeof(ControlsSettingsBox), "InitializeKeybindInterfaces");
        if (method != null)
            method.Invoke(box, null);
    }

    private static KeybindInterface SelectTemplate(ControlsSettingsBox box, RectTransform content)
    {
        KeybindInterface fallback = null;
        foreach (KeybindInterface candidate in box.GetComponentsInChildren<KeybindInterface>(true))
        {
            if (candidate == null || IsOwnOrExternal(candidate.transform))
                continue;
            Transform source = FindTemplateRoot(candidate, box, content);
            if (source == null || source.parent == null)
                continue;
            if (content != null && !source.IsChildOf(content))
                continue;
            KeyConnection main = GetConnection(candidate, "mainKey");
            KeyConnection alt = GetConnection(candidate, "altKey");
            if (main == null || main.button == null || main.text == null ||
                FindLabel(source, main.text, alt != null ? alt.text : null) == null)
                continue;
            if (candidate.Identifier == "GameInfo")
                return candidate;
            if (candidate.Identifier == "Walk")
                fallback = candidate;
            else if (fallback == null)
                fallback = candidate;
        }
        return fallback;
    }

    private static Transform FindTemplateRoot(KeybindInterface item, ControlsSettingsBox box, RectTransform content)
    {
        if (item == null)
            return null;
        KeyConnection main = GetConnection(item, "mainKey");
        KeyConnection alt = GetConnection(item, "altKey");
        Text mainText = main != null ? main.text : null;
        Text altText = alt != null ? alt.text : null;
        Transform source = item.transform;
        for (Transform ancestor = item.transform.parent; ancestor != null && ancestor != box.transform &&
            ancestor != content; ancestor = ancestor.parent)
        {
            if (ancestor.GetComponentsInChildren<KeybindInterface>(true).Length != 1)
                break;
            if (FindLabel(ancestor, mainText, altText) != null)
            {
                source = ancestor;
                break;
            }
        }
        return source;
    }

    private static bool IsOwnOrExternal(Transform transform)
    {
        for (Transform node = transform; node != null; node = node.parent)
            if (node.name == "UnbindableKeybinds" ||
                node.name.StartsWith("IslandLife_Keybind_", StringComparison.Ordinal) ||
                node.name.StartsWith("xSkills_", StringComparison.Ordinal))
                return true;
        return false;
    }

    private static void PositionRows(Transform parent)
    {
        float bottom = float.PositiveInfinity;
        Vector3[] corners = new Vector3[4];
        foreach (Transform child in parent)
        {
            if (child == null || child.name.StartsWith("IslandLife_Keybind_", StringComparison.Ordinal) ||
                child.GetComponentInChildren<KeybindInterface>(true) == null)
                continue;
            RectTransform rect = child as RectTransform;
            if (rect == null)
                continue;
            rect.GetWorldCorners(corners);
            bottom = Mathf.Min(bottom, parent.InverseTransformPoint(corners[0]).y);
        }
        if (float.IsPositiveInfinity(bottom))
            return;

        foreach (Row row in Rows)
        {
            RectTransform rect = row.Root != null ? row.Root.transform as RectTransform : null;
            if (rect == null)
                continue;
            rect.GetWorldCorners(corners);
            float top = parent.InverseTransformPoint(corners[1]).y;
            float delta = bottom - 8f - top;
            if (Mathf.Abs(delta) > 0.5f)
                rect.anchoredPosition += Vector2.up * delta;
            rect.GetWorldCorners(corners);
            bottom = parent.InverseTransformPoint(corners[0]).y;
        }
    }

    private static void FixContentHeight(ControlsSettingsBox box)
    {
        if (box == null || box != currentBox || Rows.Count != Bindings.Length)
            return;
        RectTransform content = GetContent(box);
        if (content == null)
            return;
        float required = 2684f + Rows.Count * rowStep + 20f;
        if (content.sizeDelta.y + 0.5f < required)
        {
            Vector2 size = content.sizeDelta;
            size.y = required;
            content.sizeDelta = size;
        }

        // Добавленные элементы должны помещаться в действительные границы прокрутки
        foreach (Row row in Rows)
        {
            if (row.Root == null)
                continue;
            RectTransform rect = row.Root.transform as RectTransform;
            if (rect == null || !rect.IsChildOf(content))
                continue;
            Vector3[] corners = new Vector3[4];
            rect.GetWorldCorners(corners);
            float bottom = content.InverseTransformPoint(corners[0]).y;
            float missing = content.rect.yMin + 16f - bottom;
            if (missing > 1f)
            {
                Vector2 offset = content.offsetMin;
                offset.y -= missing + 4f;
                content.offsetMin = offset;
            }
        }
    }

    private static void SyncFromNative(ControlsSettingsBox box)
    {
        if (box == null || box != currentBox)
            return;
        foreach (Row row in Rows)
        {
            if (row.Native == null || row.Native.Keybind == null)
                continue;
            Binding binding = row.Binding;
            binding.Main = row.Native.Keybind.MainKey;
            binding.Alt = row.Native.Keybind.AltKey;
            if (MyInput.Keybinds != null)
                MyInput.Keybinds[IdPrefix + binding.Id] = row.Native.Keybind;
        }
    }

    private static bool BindingsAvailable()
    {
        if (MyInput.Keybinds == null)
            return false;
        foreach (Binding binding in Bindings)
            if (!MyInput.Keybinds.ContainsKey(IdPrefix + binding.Id))
                return false;
        return true;
    }

    private static Keybind ReadSavedBinding(ControlsSettingsBox box, Binding binding)
    {
        string id = IdPrefix + binding.Id;
        Settings settings = ComponentManager<Settings>.Value;
        if (settings != null && settings.controls == box)
        {
            RGD_Settings rgd = settings.Current;
            if (rgd != null && rgd.controls != null && rgd.controls.keybinds != null)
                foreach (Keybind stored in rgd.controls.keybinds)
                    if (stored != null && stored.Identifier == id)
                        return new Keybind(id, stored.MainKey, stored.AltKey);
        }
        return new Keybind(id, binding.Main, binding.Alt);
    }

    private static void MigrateLegacyBindings(ControlsSettingsBox box)
    {
        if (box != currentBox)
            return;
        Settings settings = ComponentManager<Settings>.Value;
        if (settings == null || settings.Current == null || settings.Current.controls == null)
            return;
        Keybind[] saved = settings.Current.controls.keybinds;
        foreach (Row row in Rows)
        {
            if (row.Native == null || row.Binding.Id == "restore_defaults")
                continue;
            string id = IdPrefix + row.Binding.Id;
            bool present = false;
            if (saved != null)
                foreach (Keybind item in saved)
                    if (item != null && item.Identifier == id)
                    {
                        present = true;
                        break;
                    }
            if (present)
                continue;
            if (!PlayerPrefs.HasKey(PrefPrefix + row.Binding.Id + "_Main") &&
                !PlayerPrefs.HasKey(PrefPrefix + row.Binding.Id + "_Alt"))
                continue;
            row.Native.Set(new Keybind(id, LoadKey(row.Binding, false), LoadKey(row.Binding, true)));
            if (MyInput.Keybinds != null)
                MyInput.Keybinds[id] = row.Native.Keybind;
        }
    }

    private static void RestoreDefaults()
    {
        foreach (Row row in Rows)
        {
            if (row.Native == null)
                continue;
            row.Native.SetDefaultKeys();
            row.Binding.Main = row.Binding.Default;
            row.Binding.Alt = KeyCode.None;
            if (MyInput.Keybinds != null)
                MyInput.Keybinds[IdPrefix + row.Binding.Id] = row.Native.Keybind;
            if (row.Binding.Id != "restore_defaults")
                SaveKey(row.Binding, false);
        }
        PlayerPrefs.Save();
        TranslateRows();
    }

    private static void TranslateRows()
    {
        languageVersion = IslandLifeLocalization.Version;
        foreach (Row row in Rows)
        {
            if (row.Root == null)
                continue;
            string text = "Island Life - " + IslandLifeLocalization.Get(row.Binding.Label);
            Text label = row.Label as Text;
            if (label != null)
                label.text = text;
            else if (row.Label != null)
            {
                PropertyInfo prop = row.Label.GetType().GetProperty("text");
                if (prop != null && prop.CanWrite)
                    prop.SetValue(row.Label, text, null);
            }
            if (row.Binding.Id == "restore_defaults")
            {
                if (row.MainText != null)
                    row.MainText.text = IslandLifeLocalization.Get("key_restore_button");
                if (row.AltText != null)
                    row.AltText.text = "";
            }
        }
    }

    private static Component FindLabel(Transform root, Text main, Text alt)
    {
        if (root == null)
            return null;
        foreach (Text label in root.GetComponentsInChildren<Text>(true))
            if (label != null && label != main && label != alt && label.GetComponentInParent<Button>() == null)
                return label;
        foreach (Component component in root.GetComponentsInChildren<Component>(true))
        {
            if (component == null || component.GetComponentInParent<Button>() != null)
                continue;
            for (Type type = component.GetType(); type != null; type = type.BaseType)
                if (type.FullName == "TMPro.TMP_Text")
                    return component;
        }
        return null;
    }

    private static KeyConnection GetConnection(KeybindInterface row, string name)
    {
        if (row == null)
            return null;
        FieldInfo field = AccessTools.Field(typeof(KeybindInterface), name);
        return field != null ? field.GetValue(row) as KeyConnection : null;
    }

    private static RectTransform GetContent(ControlsSettingsBox box)
    {
        FieldInfo field = AccessTools.Field(typeof(ControlsSettingsBox), "contentBox");
        return field != null ? field.GetValue(box) as RectTransform : null;
    }

    private static void DestroyRows()
    {
        foreach (Row row in Rows)
            if (row.Root != null)
                UnityEngine.Object.Destroy(row.Root);
        Rows.Clear();
    }

    private static Binding FindBinding(string id)
    {
        foreach (Binding binding in Bindings)
            if (binding.Id == id)
                return binding;
        return null;
    }

    private static void GetKeys(Binding binding, out KeyCode main, out KeyCode alt)
    {
        Keybind native;
        if (MyInput.Keybinds != null &&
            MyInput.Keybinds.TryGetValue(IdPrefix + binding.Id, out native) && native != null)
        {
            main = native.MainKey;
            alt = native.AltKey;
            return;
        }
        main = binding.Main;
        alt = binding.Alt;
    }

    private static KeyCode LoadKey(Binding binding, bool alternate)
    {
        KeyCode fallback = alternate ? KeyCode.None : binding.Default;
        string key = PrefPrefix + binding.Id + (alternate ? "_Alt" : "_Main");
        KeyCode saved = (KeyCode)PlayerPrefs.GetInt(key, (int)fallback);
        return Enum.IsDefined(typeof(KeyCode), saved) ? saved : fallback;
    }

    private static void SaveKey(Binding binding, bool saveNow)
    {
        PlayerPrefs.SetInt(PrefPrefix + binding.Id + "_Main", (int)binding.Main);
        PlayerPrefs.SetInt(PrefPrefix + binding.Id + "_Alt", (int)binding.Alt);
        if (saveNow)
            PlayerPrefs.Save();
    }

    private static bool GetKey(KeyCode key)
    {
        if (key == KeyCode.None)
            return false;
        if (key == KeyCode.LeftAlt || key == KeyCode.RightAlt)
            return Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
        if (key == KeyCode.LeftControl || key == KeyCode.RightControl)
            return Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
        if (key == KeyCode.LeftShift || key == KeyCode.RightShift)
            return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        return Input.GetKey(key);
    }

    private static bool GetKeyDown(KeyCode key)
    {
        if (key == KeyCode.None)
            return false;
        if (key == KeyCode.LeftAlt || key == KeyCode.RightAlt)
            return Input.GetKeyDown(KeyCode.LeftAlt) || Input.GetKeyDown(KeyCode.RightAlt);
        if (key == KeyCode.LeftControl || key == KeyCode.RightControl)
            return Input.GetKeyDown(KeyCode.LeftControl) || Input.GetKeyDown(KeyCode.RightControl);
        if (key == KeyCode.LeftShift || key == KeyCode.RightShift)
            return Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift);
        return Input.GetKeyDown(key);
    }

    private static string Format(KeyCode key)
    {
        switch (key)
        {
            case KeyCode.None: return "-";
            case KeyCode.LeftAlt:
            case KeyCode.RightAlt: return "Alt";
            case KeyCode.LeftShift:
            case KeyCode.RightShift: return "Shift";
            case KeyCode.LeftControl:
            case KeyCode.RightControl: return "Ctrl";
            case KeyCode.Return: return "Enter";
            case KeyCode.Mouse0: return "LMB";
            case KeyCode.Mouse1: return "RMB";
            case KeyCode.Mouse2: return "MMB";
        }
        string result = key.ToString();
        if (result.StartsWith("Alpha", StringComparison.Ordinal))
            return result.Substring(5);
        if (result.StartsWith("Keypad", StringComparison.Ordinal))
            return "Num " + result.Substring(6);
        return result;
    }

    private static void Status(string state, string message)
    {
        if (state == lastStatus)
            return;
        lastStatus = state;
        if (state == "ready")
            Debug.Log("[IslandLife] Hotkeys UI: " + message);
        else
            Debug.LogWarning("[IslandLife] Hotkeys UI: " + message);
    }
}
