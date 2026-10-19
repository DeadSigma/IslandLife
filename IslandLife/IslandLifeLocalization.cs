using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization.Json;
using UnityEngine;

public static class IslandLifeLocalization
{
    private static readonly Dictionary<string, Dictionary<string, string>> Translations =
        new Dictionary<string, Dictionary<string, string>>();

    private static Type managerType;
    private static PropertyInfo languageProperty;
    private static PropertyInfo languageCodeProperty;
    private static string language = "eng";
    private static string previousGameLanguage;
    private static float nextLanguageCheck;
    private static float nextManagerLookup;

    public static int Version { get; private set; }

    public static void Initialize(byte[] english, byte[] russian)
    {
        Translations.Clear();
        Translations["eng"] = Parse(english);
        Translations["ru"] = Parse(russian);
        previousGameLanguage = null;
        nextLanguageCheck = 0f;
        nextManagerLookup = 0f;
        Version++;
        Update(true);
    }

    public static void Update(bool force = false)
    {
        if (!force && Time.unscaledTime < nextLanguageCheck)
            return;

        nextLanguageCheck = Time.unscaledTime + 0.5f;
        string gameLanguage = ReadGameLanguage();
        if (!force && gameLanguage == previousGameLanguage)
            return;

        previousGameLanguage = gameLanguage;
        string selected = IsRussian(gameLanguage) ? "ru" : "eng";
        if (language != selected)
        {
            language = selected;
            Version++;
        }
    }

    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        Dictionary<string, string> terms;
        string value;
        if (Translations.TryGetValue(language, out terms) && terms.TryGetValue(key, out value))
            return value;

        if (Translations.TryGetValue("eng", out terms) && terms.TryGetValue(key, out value))
            return value;

        return key;
    }

    public static void Reset()
    {
        Translations.Clear();
        managerType = null;
        languageProperty = null;
        languageCodeProperty = null;
        previousGameLanguage = null;
        nextLanguageCheck = 0f;
        nextManagerLookup = 0f;
        language = "eng";
        Version++;
    }

    private static Dictionary<string, string> Parse(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return new Dictionary<string, string>();

        try
        {
            DataContractJsonSerializerSettings settings = new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            };
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(
                typeof(Dictionary<string, string>), settings);

            using (MemoryStream stream = new MemoryStream(bytes, false))
            {
                Dictionary<string, string> parsed = serializer.ReadObject(stream) as Dictionary<string, string>;
                return parsed ?? new Dictionary<string, string>();
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[IslandLife] Translation JSON: " + ex.Message);
            return new Dictionary<string, string>();
        }
    }

    private static string ReadGameLanguage()
    {
        if (managerType == null && Time.unscaledTime >= nextManagerLookup)
        {
            nextManagerLookup = Time.unscaledTime + 10f;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                managerType = assembly.GetType("I2.Loc.LocalizationManager", false);
                if (managerType != null)
                    break;
            }
        }

        if (managerType == null)
            return "English";

        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public;
        if (languageCodeProperty == null)
            languageCodeProperty = managerType.GetProperty("CurrentLanguageCode", flags);
        if (languageProperty == null)
            languageProperty = managerType.GetProperty("CurrentLanguage", flags);

        // Код языка используется раньше названия, чтобы не зависеть от его написания
        try
        {
            if (languageCodeProperty != null)
            {
                string code = languageCodeProperty.GetValue(null, null) as string;
                if (!string.IsNullOrEmpty(code))
                    return code;
            }
        }
        catch (Exception) { }

        try
        {
            if (languageProperty != null)
            {
                string name = languageProperty.GetValue(null, null) as string;
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
        }
        catch (Exception) { }

        return "English";
    }

    private static bool IsRussian(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        string normalized = value.Trim().ToLowerInvariant();
        return normalized == "ru" ||
               normalized.StartsWith("ru-", StringComparison.Ordinal) ||
               normalized.StartsWith("ru_", StringComparison.Ordinal) ||
               normalized.EndsWith("[ru]", StringComparison.Ordinal) ||
               normalized.StartsWith("рус", StringComparison.Ordinal) ||
               normalized.StartsWith("pус", StringComparison.Ordinal) ||
               normalized.StartsWith("russian", StringComparison.Ordinal);
    }
}
