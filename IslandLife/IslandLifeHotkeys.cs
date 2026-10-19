using System;
using System.Collections.Generic;
using System.Reflection;
using HMLLibrary;
using UnityEngine;
using UnityEngine.UI;

public static class IslandLifeHotkeys
{
    private const string PrefPrefix = "IslandLife_Keybind_v1_";
    private const float ScanInterval = 0.8f;

    private sealed class Binding
    {
        public string Id;
        public string Label;
        public KeyCode Default;
        public KeyCode Main;
        public KeyCode Alternate;

        public Binding(string id, string label, KeyCode initial)
        {
            Id = id;
            Label = label;
            Default = initial;
            Main = initial;
        }
    }

    private sealed class Row
    {
        public GameObject Root;
        public Binding Binding;
        public Text Label;
        public Text MainText;
        public Text AlternateText;
        public Button MainButton;
        public Button AlternateButton;
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
    private static readonly KeyCode[] Codes = (KeyCode[])Enum.GetValues(typeof(KeyCode));

    private static Transform parent;
    private static Binding assigning;
    private static bool assigningAlternate;
    private static int assigningStartFrame;
    private static int previousLanguageVersion = -1;
    private static float nextScan;
    private static RectTransform adjustedContent;
    private static float adjustedHeight;
    private static KeyCode gameRotateKey = KeyCode.R;
    private static KeyCode gameRotateAltKey = KeyCode.None;

    public static void Initialize()
    {
        foreach (Binding binding in Bindings)
        {
            binding.Main = Load(binding.Id, false, binding.Default);
            binding.Alternate = Load(binding.Id, true, KeyCode.None);
        }

        assigning = null;
        gameRotateKey = KeyCode.R;
        gameRotateAltKey = KeyCode.None;
        previousLanguageVersion = -1;
        nextScan = 0f;
    }

    public static void Update()
    {
        if (assigning != null && (parent == null || !parent.gameObject.activeInHierarchy))
            CancelCapture();
        if (assigning != null)
            Capture();

        if (Time.unscaledTime < nextScan)
            return;

        nextScan = Time.unscaledTime + ScanInterval;
        EnsureControlsRows();
        if (previousLanguageVersion != IslandLifeLocalization.Version)
            RefreshRows();
    }

    public static bool Held(string id)
    {
        if (assigning != null)
            return false;

        Binding binding = Find(id);
        return binding != null && (Held(binding.Main) || Held(binding.Alternate));
    }

    public static bool Down(string id)
    {
        if (assigning != null)
            return false;

        Binding binding = Find(id);
        return binding != null && (Down(binding.Main) || Down(binding.Alternate));
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

    public static KeyCode Primary(string id)
    {
        Binding binding = Find(id);
        return binding != null ? (binding.Main != KeyCode.None ? binding.Main : binding.Alternate) : KeyCode.None;
    }

    public static string Display(string id)
    {
        Binding binding = Find(id);
        if (binding == null)
            return "?";

        if (binding.Alternate == KeyCode.None)
            return Format(binding.Main);
        if (binding.Main == KeyCode.None)
            return Format(binding.Alternate);
        return Format(binding.Main) + " / " + Format(binding.Alternate);
    }

    public static void Reset()
    {
        CancelCapture();
        ClearRows();
        parent = null;
        nextScan = 0f;
        previousLanguageVersion = -1;
    }

    private static bool Held(KeyCode key)
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

    private static bool Down(KeyCode key)
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

    private static Binding Find(string id)
    {
        for (int i = 0; i < Bindings.Length; i++)
            if (Bindings[i].Id == id)
                return Bindings[i];
        return null;
    }

    private static KeyCode Load(string id, bool alternate, KeyCode fallback)
    {
        string key = PrefPrefix + id + (alternate ? "_Alt" : "_Main");
        if (!PlayerPrefs.HasKey(key))
            return fallback;

        KeyCode loaded = (KeyCode)PlayerPrefs.GetInt(key);
        return Enum.IsDefined(typeof(KeyCode), loaded) ? loaded : fallback;
    }

    private static void Save(Binding binding, bool commit = true)
    {
        PlayerPrefs.SetInt(PrefPrefix + binding.Id + "_Main", (int)binding.Main);
        PlayerPrefs.SetInt(PrefPrefix + binding.Id + "_Alt", (int)binding.Alternate);
        if (commit)
            PlayerPrefs.Save();
    }

    private static void RestoreDefaults()
    {
        CancelCapture();
        foreach (Binding binding in Bindings)
        {
            if (binding.Id == "restore_defaults")
                continue;
            binding.Main = binding.Default;
            binding.Alternate = KeyCode.None;
            Save(binding, false);
        }
        PlayerPrefs.Save();
        RefreshRows();
    }

    private static void StartCapture(Binding binding, bool alternate)
    {
        assigning = binding;
        KeybindInterface.AssigningKey = true;
        assigningAlternate = alternate;
        assigningStartFrame = Time.frameCount;
        RefreshRows();
    }

    private static void CancelCapture()
    {
        assigning = null;
        KeybindInterface.AssigningKey = false;
        RefreshRows();
    }

    private static void Capture()
    {
        if (assigning == null || Time.frameCount <= assigningStartFrame)
            return;

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            CancelCapture();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))
        {
            if (assigningAlternate)
                assigning.Alternate = KeyCode.None;
            else
                assigning.Main = KeyCode.None;
            Save(assigning);
            CancelCapture();
            return;
        }

        if (!Input.anyKeyDown)
            return;

        foreach (KeyCode code in Codes)
        {
            if (code == KeyCode.None || code == KeyCode.Escape ||
                code == KeyCode.Backspace || code == KeyCode.Delete || !Input.GetKeyDown(code))
                continue;

            if (assigningAlternate)
                assigning.Alternate = code;
            else
                assigning.Main = code;

            Save(assigning);
            CancelCapture();
            return;
        }
    }

    private static void EnsureControlsRows()
    {
        if (parent == null || Rows.Count != Bindings.Length)
        {
            ClearRows();
            parent = null;
        }
        else if (parent.gameObject.activeInHierarchy)
        {
            return;
        }

        Settings settings = ComponentManager<Settings>.Value;
        Transform controlsRoot = settings != null && settings.controls != null
            ? settings.controls.transform : null;
        if (controlsRoot == null || !controlsRoot.gameObject.activeInHierarchy)
            return;

        KeybindInterface[] interfaces = Resources.FindObjectsOfTypeAll<KeybindInterface>();
        KeybindInterface template = null;
        int lastIndex = -1;

        foreach (KeybindInterface current in interfaces)
        {
            if (current == null || !current.gameObject.activeInHierarchy ||
                !current.transform.IsChildOf(controlsRoot) ||
                current.gameObject.name.StartsWith("IslandLife_", StringComparison.Ordinal))
                continue;

            // Клавиша поворота берётся из управления самой игры
            Keybind original = current.Keybind;
            if (original != null && !string.IsNullOrEmpty(original.Identifier))
            {
                string identifier = original.Identifier.Trim().ToLowerInvariant();
                if (identifier == "rotate" || identifier == "rotate block" ||
                    identifier == "rotate building" || identifier == "block rotation")
                {
                    gameRotateKey = original.MainKey;
                    gameRotateAltKey = original.AltKey;
                }
            }

            Transform owner = current.transform.parent;
            if (owner == null || !(owner is RectTransform))
                continue;

            object mainConnection = GetConnection(current, "mainKey");
            if (GetField<Button>(mainConnection, "button") == null ||
                GetField<Text>(mainConnection, "text") == null)
                continue;

            int position = current.transform.GetSiblingIndex();
            if (template == null || position > lastIndex)
            {
                template = current;
                lastIndex = position;
            }
        }

        if (template == null)
            return;

        // Новые строки располагаются в том же списке, что и штатные клавиши Raft
        if (parent != null && parent != template.transform.parent)
            ClearRows();

        parent = template.transform.parent;
        if (Rows.Count == Bindings.Length)
            return;

        RectTransform templateRect = template.transform as RectTransform;
        LayoutGroup layout = parent.GetComponent<LayoutGroup>();
        float stride = templateRect != null ? Mathf.Max(32f, templateRect.rect.height) : 42f;
        float direction = -1f;

        RectTransform previous = null;
        for (int i = template.transform.GetSiblingIndex() - 1; i >= 0; i--)
        {
            previous = parent.GetChild(i) as RectTransform;
            if (previous != null)
                break;
        }
        if (templateRect != null && previous != null)
        {
            float difference = templateRect.anchoredPosition.y - previous.anchoredPosition.y;
            if (Mathf.Abs(difference) > 2f)
            {
                direction = Mathf.Sign(difference);
                stride = Mathf.Abs(difference);
            }
        }

        for (int i = 0; i < Bindings.Length; i++)
        {
            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, parent, false);
            clone.name = "IslandLife_Keybind_" + Bindings[i].Id;
            clone.transform.SetAsLastSibling();

            KeybindInterface native = clone.GetComponent<KeybindInterface>();
            object mainConnection = GetConnection(native, "mainKey");
            object altConnection = GetConnection(native, "altKey");
            Button mainButton = GetField<Button>(mainConnection, "button");
            Button altButton = GetField<Button>(altConnection, "button");
            Text mainText = GetField<Text>(mainConnection, "text");
            Text altText = GetField<Text>(altConnection, "text");

            if (mainButton == null || mainText == null)
            {
                UnityEngine.Object.Destroy(clone);
                continue;
            }

            // Обработчики копии заменяются, чтобы Raft не переназначал чужое действие
            mainButton.onClick = new Button.ButtonClickedEvent();
            if (altButton != null)
                altButton.onClick = new Button.ButtonClickedEvent();

            // Штатные локализаторы удаляются, чтобы переводы игры не заменяли текст мода
            foreach (MonoBehaviour component in clone.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (component != null && component.GetType().FullName == "I2.Loc.Localize")
                {
                    component.enabled = false;
                    UnityEngine.Object.Destroy(component);
                }
            }

            Text label = null;
            foreach (Text text in clone.GetComponentsInChildren<Text>(true))
            {
                if (text != mainText && text != altText)
                {
                    label = text;
                    break;
                }
            }

            if (native != null)
                UnityEngine.Object.Destroy(native);

            if (layout == null && templateRect != null)
            {
                RectTransform rect = clone.transform as RectTransform;
                if (rect != null)
                    rect.anchoredPosition = templateRect.anchoredPosition +
                        new Vector2(0f, direction * stride * (i + 1));
            }

            Binding binding = Bindings[i];
            Row row = new Row
            {
                Root = clone,
                Binding = binding,
                Label = label,
                MainText = mainText,
                AlternateText = altText,
                MainButton = mainButton,
                AlternateButton = altButton
            };
            Rows.Add(row);
            if (binding.Id == "restore_defaults")
            {
                mainButton.onClick.AddListener(RestoreDefaults);
                if (altButton != null)
                    altButton.gameObject.SetActive(false);
            }
            else
            {
                mainButton.onClick.AddListener(() => StartCapture(binding, false));
                if (altButton != null)
                    altButton.onClick.AddListener(() => StartCapture(binding, true));
            }
        }

        if (layout == null)
        {
            RectTransform content = parent as RectTransform;
            if (content != null)
            {
                adjustedContent = content;
                adjustedHeight = stride * Rows.Count;
                content.sizeDelta += new Vector2(0f, adjustedHeight);
            }
        }
        else
        {
            LayoutRebuilder.MarkLayoutForRebuild(parent as RectTransform);
        }

        previousLanguageVersion = -1;
        RefreshRows();
    }

    private static object GetConnection(KeybindInterface native, string fieldName)
    {
        if (native == null)
            return null;
        FieldInfo field = typeof(KeybindInterface).GetField(fieldName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return field != null ? field.GetValue(native) : null;
    }

    private static T GetField<T>(object instance, string name) where T : class
    {
        if (instance == null)
            return null;
        BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        FieldInfo field = instance.GetType().GetField(name, flags);
        if (field != null)
            return field.GetValue(instance) as T;
        PropertyInfo property = instance.GetType().GetProperty(name, flags);
        return property != null ? property.GetValue(instance, null) as T : null;
    }

    private static void RefreshRows()
    {
        previousLanguageVersion = IslandLifeLocalization.Version;
        foreach (Row row in Rows)
        {
            if (row.Root == null)
                continue;

            if (row.Label != null)
                row.Label.text = "Island Life - " + IslandLifeLocalization.Get(row.Binding.Label);
            if (row.MainText != null)
                row.MainText.text = row.Binding.Id == "restore_defaults"
                    ? IslandLifeLocalization.Get("key_restore_button")
                    : assigning == row.Binding && !assigningAlternate
                        ? "..." : Format(row.Binding.Main);
            if (row.AlternateText != null)
                row.AlternateText.text = assigning == row.Binding && assigningAlternate
                    ? "..." : Format(row.Binding.Alternate);
        }
    }

    private static void ClearRows()
    {
        if (adjustedContent != null && adjustedHeight > 0f)
            adjustedContent.sizeDelta -= new Vector2(0f, adjustedHeight);
        adjustedContent = null;
        adjustedHeight = 0f;

        foreach (Row row in Rows)
            if (row.Root != null)
                UnityEngine.Object.Destroy(row.Root);
        Rows.Clear();
    }

    private static string Format(KeyCode code)
    {
        switch (code)
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

        string value = code.ToString();
        if (value.StartsWith("Alpha", StringComparison.Ordinal))
            return value.Substring(5);
        if (value.StartsWith("Keypad", StringComparison.Ordinal))
            return "Num " + value.Substring(6);
        return value;
    }
}
