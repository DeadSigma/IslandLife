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
    private static MethodInfo guiLabelMethod;

    public static void HandleInput()
    {
        if (CanvasHelper.ActiveMenu != MenuType.None || lastToggleFrame == Time.frameCount)
            return;

        if (!IslandLifeHotkeys.Down("grid"))
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
        if (guiBoxMethod == null || guiLabelMethod == null)
            return;

        string[] hints =
        {
            "[" + IslandLifeHotkeys.Display("grid") + "] " + IslandLifeLocalization.Get(Enabled ? "hint_grid_disable" : "hint_grid_enable"),
            "[" + IslandLifeHotkeys.GameRotateDisplay() + " + " + IslandLifeLocalization.Get("hint_mouse") + "] " + IslandLifeLocalization.Get("hint_rotate"),
            "[" + IslandLifeHotkeys.Display("height") + " + " + IslandLifeLocalization.Get("hint_mouse") + " / " + IslandLifeLocalization.Get("hint_scroll") + "] " + IslandLifeLocalization.Get("hint_height"),
            "[" + IslandLifeHotkeys.Display("tilt_x") + " + " + IslandLifeLocalization.Get("hint_mouse") + " / " + IslandLifeLocalization.Get("hint_scroll") + "] " + IslandLifeLocalization.Get("hint_tilt_x"),
            "[" + IslandLifeHotkeys.Display("tilt_z") + " + " + IslandLifeLocalization.Get("hint_mouse") + " / " + IslandLifeLocalization.Get("hint_scroll") + "] " + IslandLifeLocalization.Get("hint_tilt_z"),
            "[" + IslandLifeHotkeys.Display("reset_modifier") + " + " + IslandLifeHotkeys.Display("reset") + "] " + IslandLifeLocalization.Get("hint_reset"),
            "[" + IslandLifeHotkeys.Display("align") + "] " + IslandLifeLocalization.Get("hint_align")
        };

        const float width = 410f;
        const float paddingX = 14f;
        const float paddingY = 11f;
        const float rowHeight = 19f;
        const float rowGap = 3f;
        const float rightMargin = 22f;
        const float bottomMargin = 70f;

        float height = 2f * paddingY +
                       hints.Length * rowHeight +
                       (hints.Length - 1) * rowGap;

        Rect rect = new Rect(
            Screen.width - width - rightMargin,
            Screen.height - height - bottomMargin,
            width,
            height);

        try
        {
            // Фон и текст рисуются отдельно для ровных отступов
            guiBoxMethod.Invoke(null, new object[] { rect, string.Empty });

            for (int i = 0; i < hints.Length; i++)
            {
                Rect rowRect = new Rect(
                    rect.x + paddingX,
                    rect.y + paddingY + i * (rowHeight + rowGap),
                    width - 2f * paddingX,
                    rowHeight);

                guiLabelMethod.Invoke(null, new object[] { rowRect, hints[i] });
            }
        }
        catch
        {
            guiBoxMethod = null;
            guiLabelMethod = null;
            guiLookupDone = false;
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

        guiLabelMethod = guiType.GetMethod(
            "Label",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Rect), typeof(string) },
            null);
    }
}
