using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Controllers;
using HarmonyLib;
using Kitchen;
using Kitchen.Modules;
using Platforms;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// "Camera Controls" in the pause menu: a panel explaining the play camera and the editor's
    /// camera area, built the way the game's Recipes panel is (RecipeMenu / RecipeViewElement:
    /// a TextPanelElement with left / right prompts to page through). It opens on the page for
    /// the player's own controller.
    /// </summary>
    public class CameraControlsMenu : Menu<MenuAction>
    {
        public override bool RequiresBackingPanel { get; protected set; }

        public CameraControlsMenu(Transform container, ModuleList module_list)
            : base(container, module_list)
        {
        }

        public override void Setup(int player_id)
        {
            CameraControlsElement element = ModuleDirectory.Add<CameraControlsElement>(Container, new Vector2(0f, 0f));
            element.SetPlayer(player_id);
            ModuleList.AddModule(element, element.transform.localPosition.ToFlat());
        }
    }

    public class CameraControlsElement : Element
    {
        private TextPanelElement Text;
        private InputPromptElement LeftPrompt;
        private InputPromptElement RightPrompt;
        private int Page;
        private int User;
        private bool Started;

        public override Bounds BoundingBox => Text != null ? Text.BoundingBox : default;

        public void SetPlayer(int player_id)
        {
            User = player_id;
            ControllerType controller = InputSourceIdentifier.DefaultInputSource?.GetCurrentController(player_id) ?? ControllerType.None;
            Page = (controller & (ControllerType.Keyboard | ControllerType.Mouse)) != 0 || controller == ControllerType.None ? 0 : 1;
        }

        // A little larger than a recipe card (4 x 3), for the longer help text.
        private const float Width = 4.4f;
        private const float Height = 3.3f;

        private void Start()
        {
            Text = Add<TextPanelElement>();
            // Size the panel before attaching the prompts: Attach places them at the panel's
            // bottom corners as they are at that moment.
            Text.SetStyle(TextPanelElement.Style.Recipe);
            Text.Resize(Width, Height);
            (LeftPrompt = Add<InputPromptElement>()).Attach(Text);
            (RightPrompt = Add<InputPromptElement>()).Attach(Text, left_attach: false);
            Started = true;
            Show();
        }

        public override bool HandleInteraction(InputState state)
        {
            int before = Page;
            if (state.MenuRight == ButtonState.Pressed)
            {
                Page++;
            }
            if (state.MenuLeft == ButtonState.Pressed)
            {
                Page--;
            }
            if (Page == before)
            {
                return false;
            }
            Show();
            return true;
        }

        private void Show()
        {
            if (!Started)
            {
                return;
            }
            Page = Mathf.Clamp(Page, 0, PageCount - 1);
            (string title, string body) = Content(Page);
            Text.SetText(title, body);
            // Keep the body clear of the page prompts at the panel's bottom corners.
            foreach (TMPro.TextMeshPro tmp in Text.GetComponentsInChildren<TMPro.TextMeshPro>(true))
            {
                if (tmp.text == body)
                {
                    tmp.margin = new Vector4(0.15f, 0.05f, 0.25f, 0.4f);
                }
            }
            LeftPrompt.SetButtonForUser(Controls.MenuLeft, User);
            RightPrompt.SetButtonForUser(Controls.MenuRight, User);
            LeftPrompt.SetShown(Page > 0);
            RightPrompt.SetShown(Page < PageCount - 1);
        }

        public const int PageCount = 3;

        // Plain text only: the game's font has no "|", ";" or bullet glyphs (";" renders as ":").
        internal static (string title, string body) Content(int page)
        {
            string follow = PlayCamera.Follow ? "on" : "off";
            switch (page)
            {
                case 0:
                    return ("Camera: keyboard and mouse",
                        "Mouse wheel: zoom\n"
                        + "Middle button + drag: pan\n"
                        + "Middle click: reset the view\n"
                        + $"F: follow your chef (now {follow})\n"
                        + "\n"
                        + "Floor Plan editor: Ctrl + wheel zooms.\n"
                        + "Follow keeps your chef on screen while zoomed in.");
                case 1:
                    return ("Camera: controller",
                        "RB + right stick up / down: zoom\n"
                        + "Right stick: pan\n"
                        + "Click right stick: reset the view\n"
                        + $"RB + click right stick: follow ({follow})\n"
                        + "\n"
                        + "Floor Plan editor: RB + right stick zooms.\n"
                        + "Follow keeps your chef on screen while zoomed in.");
                default:
                    return ("Camera area",
                        "Choose what the day camera shows, e.g. to leave out a wing you don't need to watch.\n"
                        + "\n"
                        + "At night, open the Floor Plan terminal and switch to Camera area (Tab or Secondary 2). "
                        + "Drag a box, or right click (Grab) for the whole restaurant, then apply.\n"
                        + "\n"
                        + "It applies to every player, and is saved with the run.");
            }
        }

#if DEBUG
        internal int TestPage => Page;
        internal string TestText => Text != null ? Text.GetComponentsInChildren<TMPro.TextMeshPro>(true).Length + " text(s)" : "none";
#endif
    }

    /// <summary>
    /// Adds "Camera Controls" to the pause menu's main page, just above vanilla's Options, in the
    /// restaurant only. A transpiler, because menu elements get their positions as they're added
    /// (a postfix could only add it after "Continue"). The match is narrow: the
    /// <c>ldarg.0; ldarg.0; call get_Localisation; ldstr "MENU_OPTIONS"</c> sequence. If a game
    /// update changes it, the menu is left as it is and a warning is logged.
    /// </summary>
    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.Setup))]
    internal static class CameraControlsButtonPatch
    {
        public const string Label = "Camera Controls";

        private static readonly MethodInfo AddSubmenuButton = AccessTools.Method(
            typeof(Menu<MenuAction>), "AddSubmenuButton", new[] { typeof(string), typeof(Type), typeof(bool) });

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            for (int i = 3; i < codes.Count; i++)
            {
                if (codes[i].opcode == OpCodes.Ldstr && (codes[i].operand as string) == "MENU_OPTIONS"
                    && codes[i - 1].opcode == OpCodes.Call && (codes[i - 1].operand as MethodInfo)?.Name == "get_Localisation"
                    && codes[i - 2].opcode == OpCodes.Ldarg_0
                    && codes[i - 3].opcode == OpCodes.Ldarg_0)
                {
                    // codes[i - 3] starts the Options statement (and may be a jump target), so its
                    // labels move to our first instruction.
                    var first = new CodeInstruction(OpCodes.Ldarg_0);
                    codes[i - 3].MoveLabelsTo(first);
                    codes.InsertRange(i - 3, new[]
                    {
                        first,
                        new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CameraControlsButtonPatch), nameof(AddButton))),
                    });
                    return codes;
                }
            }
            ModMain.LogWarning("Couldn't find the Options button in the pause menu (game update?); the Camera Controls button won't be shown.");
            return codes;
        }

        private static void AddButton(MainMenu menu)
        {
            try
            {
                if (GameInfo.CurrentScene != SceneType.Kitchen)
                {
                    return;
                }
                if (AddSubmenuButton == null)
                {
                    ModMain.LogWarning("Menu.AddSubmenuButton not found; the Camera Controls button won't be shown.");
                    return;
                }
                AddSubmenuButton.Invoke(menu, new object[] { Label, typeof(CameraControlsMenu), false });
            }
            catch (Exception e)
            {
                // Never let our button break the pause menu.
                ModMain.LogError($"Failed to add the Camera Controls button: {e}");
            }
        }
    }

    /// <summary>
    /// Registers the Camera Controls submenu. The pause view rebuilds its menus (SetupMenus) every
    /// time it opens and then wires up everything registered, so a postfix gets the same
    /// treatment as the vanilla submenus.
    /// </summary>
    [HarmonyPatch(typeof(PlayerPauseView), "SetupMenus")]
    internal static class CameraControlsRegisterPatch
    {
        private static readonly FieldInfo MenusField = AccessTools.Field(typeof(LocalMenuView<MenuAction>), "Menus");
        private static readonly FieldInfo ModuleListField = AccessTools.Field(typeof(LocalMenuView<MenuAction>), "ModuleList");

        private static void Postfix(PlayerPauseView __instance)
        {
            try
            {
                var menus = MenusField?.GetValue(__instance) as Dictionary<Type, Menu<MenuAction>>;
                var moduleList = ModuleListField?.GetValue(__instance) as ModuleList;
                if (menus == null || moduleList == null)
                {
                    ModMain.LogWarning("The pause menu's internals weren't found; Camera Controls can't be registered.");
                    return;
                }
                menus[typeof(CameraControlsMenu)] = new CameraControlsMenu(__instance.ButtonContainer, moduleList);
            }
            catch (Exception e)
            {
                ModMain.LogError($"Failed to register the Camera Controls menu: {e}");
            }
        }
    }
}
