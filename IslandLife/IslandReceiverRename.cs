using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

public static class IslandReceiverRenameWindow
{
    private static GameObject panel;
    private static InputField input;
    private static Reciever targetReciever;
    private static string targetReceiverId;

    private static bool cursorVisible;
    private static CursorLockMode cursorLockMode;
    private static string previousActionMap;
    private static bool previousMovementFree;
    private static bool movementStateCaptured;
    private static int pendingCloseMode;

    public static bool IsOpen
    {
        get
        {
            return panel != null &&
                panel.activeSelf;
        }
    }

    public static void Open(
        Reciever reciever)
    {
        if (reciever == null)
        {
            return;
        }

        string receiverId = IslandReceiverTracking.GetIslandReceiverId(reciever);

        if (string.IsNullOrEmpty(receiverId))
        {
            return;
        }

        EnsureUI();

        if (panel == null ||
            input == null)
        {
            return;
        }

        targetReciever =
            reciever;

        targetReceiverId = receiverId;

        input.text = IslandReceiverTracking.GetName(receiverId);

        input.characterLimit =
            IslandReceiverTracking.MaxNameLength;

        cursorVisible =
            Cursor.visible;

        cursorLockMode =
            Cursor.lockState;

        Cursor.visible = true;
        Cursor.lockState =
            CursorLockMode.None;

        PlayerInput playerInput =
            PlayerInput.GetPlayerByIndex(0);

        previousActionMap =
            playerInput != null &&
            playerInput.currentActionMap != null
                ? playerInput.currentActionMap.name
                : null;

        if (playerInput != null &&
            playerInput.actions != null &&
            playerInput.actions.FindActionMap(
                "UI",
                false
            ) != null)
        {
            playerInput.SwitchCurrentActionMap(
                "UI"
            );
        }

        Network_Player player =
            ComponentManager<Network_Player>.Value;

        if (player != null)
        {
            if (player.PersonController != null)
            {
                previousMovementFree =
                    player.PersonController
                        .IsMovementFree;

                movementStateCaptured =
                    true;

                player.PersonController
                    .IsMovementFree =
                        false;

                player.PersonController
                    .crouching =
                        false;

            }

            if (player.PlayerScript != null)
            {
                player.PlayerScript.SetMouseLookScripts(
                    false
                );
            }
        }

        pendingCloseMode = 0;

        panel.SetActive(true);
        panel.transform.SetAsLastSibling();

        input.Select();
        input.ActivateInputField();
        input.MoveTextEnd(false);
    }

    public static void Update()
    {
        if (!IsOpen)
        {
            return;
        }

        Cursor.visible = true;
        Cursor.lockState =
            CursorLockMode.None;

        Network_Player localPlayer =
            ComponentManager<Network_Player>.Value;

        if (localPlayer != null &&
            localPlayer.PersonController != null)
        {
            localPlayer.PersonController
                .IsMovementFree =
                    false;

            localPlayer.PersonController
                .crouching =
                    false;

        }

        if (targetReciever == null)
        {
            Close(false);
            return;
        }

        Keyboard keyboard =
            Keyboard.current;

        if (keyboard == null)
        {
            return;
        }

        if (pendingCloseMode != 0)
        {
            bool closeKeyPressed =
                pendingCloseMode > 0
                    ? keyboard.enterKey.isPressed ||
                      keyboard.numpadEnterKey.isPressed
                    : keyboard.escapeKey.isPressed;

            if (!closeKeyPressed)
            {
                bool save =
                    pendingCloseMode > 0;

                pendingCloseMode = 0;

                Close(save);
            }

            return;
        }

        if (keyboard.escapeKey.wasPressedThisFrame)
        {
            pendingCloseMode = -1;
            return;
        }

        if (keyboard.enterKey.wasPressedThisFrame ||
            keyboard.numpadEnterKey.wasPressedThisFrame)
        {
            pendingCloseMode = 1;
        }
    }

    public static void Close(
        bool save)
    {
        if (!IsOpen)
        {
            targetReciever = null;
            targetReceiverId = null;
            pendingCloseMode = 0;
            return;
        }

        if (save && !string.IsNullOrEmpty(targetReceiverId))
        {
            IslandReceiverTracking.RequestNameChange(targetReceiverId,
                input != null ? input.text : string.Empty);
        }

        if (input != null)
        {
            input.DeactivateInputField();
        }

        panel.SetActive(false);

        PlayerInput playerInput =
            PlayerInput.GetPlayerByIndex(0);

        if (playerInput != null &&
            playerInput.actions != null &&
            !string.IsNullOrEmpty(previousActionMap) &&
            playerInput.actions.FindActionMap(
                previousActionMap,
                false
            ) != null)
        {
            playerInput.SwitchCurrentActionMap(
                previousActionMap
            );
        }

        Network_Player player =
            ComponentManager<Network_Player>.Value;

        if (player != null)
        {
            if (movementStateCaptured &&
                player.PersonController != null)
            {
                player.PersonController
                    .IsMovementFree =
                        previousMovementFree;
            }

            if (player.PlayerScript != null)
            {
                player.PlayerScript.SetMouseLookScripts(
                    true
                );
            }
        }

        movementStateCaptured = false;
        pendingCloseMode = 0;

        Cursor.visible =
            cursorVisible;

        Cursor.lockState =
            cursorLockMode;

        targetReciever = null;
        targetReceiverId = null;
        previousActionMap = null;
    }

    public static void Reset()
    {
        Close(false);
        if (panel != null)
            UnityEngine.Object.Destroy(panel);
        panel = null;
        input = null;
        targetReciever = null;
        targetReceiverId = null;
    }

    public static void CloseIfReceiver(
        Reciever reciever)
    {
        if (reciever != null &&
            targetReciever == reciever)
        {
            Close(false);
        }
    }

    private static void EnsureUI()
    {
        if (panel != null &&
            input != null)
        {
            return;
        }

        CanvasHelper canvas =
            ComponentManager<CanvasHelper>.Value;

        if (canvas == null)
        {
            return;
        }

        Font font =
            null;

        Text existingText =
            canvas.GetComponentInChildren<Text>(
                true
            );

        if (existingText != null)
        {
            font =
                existingText.font;
        }

        if (font == null)
        {
            font =
                Resources.GetBuiltinResource<Font>(
                    "Arial.ttf"
                );
        }

        panel =
            new GameObject(
                "IslandLife_ReceiverRenameWindow",
                typeof(RectTransform),
                typeof(Image)
            );

        panel.transform.SetParent(
            canvas.transform,
            false
        );

        RectTransform panelRect =
            panel.GetComponent<RectTransform>();

        panelRect.anchorMin =
            new Vector2(0.5f, 0.5f);

        panelRect.anchorMax =
            new Vector2(0.5f, 0.5f);

        panelRect.pivot =
            new Vector2(0.5f, 0.5f);

        panelRect.sizeDelta =
            new Vector2(480f, 180f);

        panelRect.anchoredPosition =
            Vector2.zero;

        Image panelImage =
            panel.GetComponent<Image>();

        panelImage.color =
            new Color(
                0.05f,
                0.08f,
                0.10f,
                0.96f
            );

        CreateText(
            panel.transform,
            font,
            "Island receiver name",
            24,
            new Vector2(0f, 52f),
            new Vector2(430f, 36f),
            TextAnchor.MiddleCenter
        );

        GameObject inputObject =
            new GameObject(
                "Input",
                typeof(RectTransform),
                typeof(Image),
                typeof(InputField)
            );

        inputObject.transform.SetParent(
            panel.transform,
            false
        );

        RectTransform inputRect =
            inputObject.GetComponent<RectTransform>();

        inputRect.anchorMin =
            new Vector2(0.5f, 0.5f);

        inputRect.anchorMax =
            new Vector2(0.5f, 0.5f);

        inputRect.pivot =
            new Vector2(0.5f, 0.5f);

        inputRect.sizeDelta =
            new Vector2(400f, 46f);

        inputRect.anchoredPosition =
            new Vector2(0f, 2f);

        Image inputImage =
            inputObject.GetComponent<Image>();

        inputImage.color =
            new Color(
                0.15f,
                0.19f,
                0.22f,
                1f
            );

        Text text =
            CreateText(
                inputObject.transform,
                font,
                string.Empty,
                20,
                Vector2.zero,
                new Vector2(370f, 42f),
                TextAnchor.MiddleLeft
            );

        Font inputFont =
            Resources.GetBuiltinResource<Font>(
                "Arial.ttf"
            );

        if (inputFont != null)
        {
            text.font =
                inputFont;

            text.fontStyle =
                FontStyle.Normal;
        }

        Text placeholder =
            CreateText(
                inputObject.transform,
                inputFont != null
                    ? inputFont
                    : font,
                "Enter island receiver name",
                20,
                Vector2.zero,
                new Vector2(370f, 42f),
                TextAnchor.MiddleLeft
            );

        placeholder.fontStyle =
            FontStyle.Normal;

        placeholder.color =
            new Color(
                1f,
                1f,
                1f,
                0.35f
            );

        input =
            inputObject.GetComponent<InputField>();

        input.textComponent =
            text;

        input.placeholder =
            placeholder;

        input.lineType =
            InputField.LineType.SingleLine;

        input.contentType =
            InputField.ContentType.Standard;

        input.characterLimit =
            IslandReceiverTracking.MaxNameLength;

        CreateText(
            panel.transform,
            font,
            "Enter - Save     Esc - Cancel",
            16,
            new Vector2(0f, -52f),
            new Vector2(430f, 30f),
            TextAnchor.MiddleCenter
        );

        panel.SetActive(false);
    }

    private static Text CreateText(
        Transform parent,
        Font font,
        string value,
        int fontSize,
        Vector2 position,
        Vector2 size,
        TextAnchor alignment)
    {
        GameObject textObject =
            new GameObject(
                "Text",
                typeof(RectTransform),
                typeof(Text)
            );

        textObject.transform.SetParent(
            parent,
            false
        );

        RectTransform rect =
            textObject.GetComponent<RectTransform>();

        rect.anchorMin =
            new Vector2(0.5f, 0.5f);

        rect.anchorMax =
            new Vector2(0.5f, 0.5f);

        rect.pivot =
            new Vector2(0.5f, 0.5f);

        rect.sizeDelta =
            size;

        rect.anchoredPosition =
            position;

        Text text =
            textObject.GetComponent<Text>();

        text.font =
            font;

        text.fontSize =
            fontSize;

        text.alignment =
            alignment;

        text.color =
            Color.white;

        text.text =
            value;

        return text;
    }
}

[HarmonyPatch]
public static class IslandRename_MenuInputBlock
{
    private static IEnumerable<MethodBase>
        TargetMethods()
    {
        MethodInfo pauseUpdate =
            AccessTools.Method(
                typeof(PauseMenu),
                "Update"
            );

        if (pauseUpdate != null)
        {
            yield return pauseUpdate;
        }

        MethodInfo canvasUpdate =
            AccessTools.Method(
                typeof(CanvasHelper),
                "Update"
            );

        if (canvasUpdate != null)
        {
            yield return canvasUpdate;
        }
    }

    [HarmonyPrefix]
    private static bool Prefix()
    {
        return !IslandReceiverRenameWindow.IsOpen;
    }
}

[HarmonyPatch]
public static class PersonController_GroundControll_IslandRename
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(
            typeof(PersonController),
            "GroundControll"
        );
    }

    [HarmonyPostfix]
    private static void Postfix(
        PersonController __instance)
    {
        if (!IslandReceiverRenameWindow.IsOpen ||
            __instance == null)
        {
            return;
        }

        __instance.crouching =
            false;
    }
}

[HarmonyPatch]
public static class PlayerInventory_Update_IslandRename
{
    private static IEnumerable<MethodBase>
        TargetMethods()
    {
        MethodInfo update =
            AccessTools.Method(
                typeof(PlayerInventory),
                "Update"
            );

        if (update != null)
        {
            yield return update;
        }
    }

    [HarmonyPrefix]
    private static bool Prefix()
    {
        return !IslandReceiverRenameWindow.IsOpen;
    }
}

[HarmonyPatch]
public static class NoteBookUI_Update_IslandRename
{
    private static IEnumerable<MethodBase>
        TargetMethods()
    {
        MethodInfo update =
            AccessTools.Method(
                typeof(NoteBookUI),
                "Update"
            );

        if (update != null)
        {
            yield return update;
        }
    }

    [HarmonyPrefix]
    private static bool Prefix()
    {
        return !IslandReceiverRenameWindow.IsOpen;
    }
}

[HarmonyPatch]
public static class ChatTextFieldController_Update_IslandRename
{
    private static IEnumerable<MethodBase>
        TargetMethods()
    {
        MethodInfo update =
            AccessTools.Method(
                typeof(ChatTextFieldController),
                "Update"
            );

        if (update != null)
        {
            yield return update;
        }
    }

    [HarmonyPrefix]
    private static bool Prefix()
    {
        return !IslandReceiverRenameWindow.IsOpen;
    }
}


[HarmonyPatch]
public static class Reciever_OnIsRayed_IslandRename
{
    private static MethodBase TargetMethod()
    {
        InterfaceMapping map =
            typeof(Reciever).GetInterfaceMap(
                typeof(IRaycastable)
            );

        for (int i = 0;
             i < map.InterfaceMethods.Length;
             i++)
        {
            if (map.InterfaceMethods[i].Name ==
                "OnIsRayed")
            {
                return map.TargetMethods[i];
            }
        }

        return null;
    }

    [HarmonyPrefix]
    public static bool Prefix()
    {
        return !IslandReceiverRenameWindow.IsOpen;
    }

    [HarmonyPostfix]
    public static void Postfix(
        Reciever __instance)
    {
        if (__instance == null ||
            IslandReceiverRenameWindow.IsOpen ||
            CanvasHelper.ActiveMenu != MenuType.None ||
            ChatTextFieldController.IsChatWindowSelected ||
            !Helper.LocalPlayerIsWithinDistance(
                __instance.transform.position,
                Player.UseDistance
            ))
        {
            return;
        }

        string receiverId = IslandReceiverTracking.GetIslandReceiverId(__instance);

        if (string.IsNullOrEmpty(receiverId))
        {
            return;
        }

        CanvasHelper canvas =
            ComponentManager<CanvasHelper>.Value;

        if (canvas != null &&
            canvas.displayTextManager != null)
        {
            canvas.displayTextManager.ShowText(
                "Set receiver name",
                "N",
                KeyCode.N,
                1,
                0,
                false
            );
        }

        Keyboard keyboard =
            Keyboard.current;

        if (keyboard != null &&
            keyboard.nKey.wasPressedThisFrame)
        {
            IslandReceiverRenameWindow.Open(
                __instance
            );
        }
    }
}

[HarmonyPatch(typeof(Reciever), "OnDestroy")]
public static class Reciever_Destroy_IslandReceiverRename
{
    [HarmonyPrefix]
    public static void Prefix(Reciever __instance)
    {
        IslandReceiverRenameWindow.CloseIfReceiver(__instance);
    }
}
