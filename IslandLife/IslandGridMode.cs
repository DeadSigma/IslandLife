using HMLLibrary;
using System;
using System.Reflection;
using UnityEngine;

public static class IslandGridMode
{
    public static bool Enabled = true;

    private static int lastIslandBuildFrame = -100;
    private static int lastToggleFrame = -1;

    private static bool guiLookupDone;
    private static MethodInfo guiBoxMethod;

    public static void HandleInput()
    {
        if (CanvasHelper.ActiveMenu != MenuType.None || lastToggleFrame == Time.frameCount)
            return;

        if (!Input.GetKeyDown(KeyCode.G))
            return;

        Enabled = !Enabled;
        lastToggleFrame = Time.frameCount;
    }

    public static void MarkIslandBuildActive()
    {
        lastIslandBuildFrame = Time.frameCount;
    }

    public static void DrawHint()
    {
        if (Time.frameCount - lastIslandBuildFrame > 1)
            return;

        EnsureGuiMethod();
        if (guiBoxMethod == null)
            return;

        string text = Enabled
            ? "[G] Disable island grid"
            : "[G] Enable island grid";

        const float width = 220f;
        const float height = 32f;
        const float rightMargin = 22f;
        const float bottomMargin = 70f;

        Rect rect = new Rect(
            Screen.width - width - rightMargin,
            Screen.height - height - bottomMargin,
            width,
            height);

        try
        {
            guiBoxMethod.Invoke(null, new object[] { rect, text });
        }
        catch
        {
            guiBoxMethod = null;
        }
    }

    private static void EnsureGuiMethod()
    {
        if (guiLookupDone)
            return;

        guiLookupDone = true;

        Type guiType = Type.GetType("UnityEngine.GUI, UnityEngine.IMGUIModule", false);
        if (guiType == null)
            return;

        guiBoxMethod = guiType.GetMethod(
            "Box",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Rect), typeof(string) },
            null);
    }
}
