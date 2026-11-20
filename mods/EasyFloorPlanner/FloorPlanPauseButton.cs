using System;
using System.Reflection;
using Controllers;
using HarmonyLib;
using Kitchen;
using Kitchen.Modules;
using Platforms;
using Unity.Entities;

namespace EasyFloorPlanner
{
    /// <summary>
    /// "Floor Planner" in the pause menu at night: opens the editor (or watches it, if someone else
    /// is editing) without walking to the terminal. User report 2026-11-19: a restaurant grown to the
    /// limit can leave the terminal where the player can't see or reach it.
    ///
    /// It travels the way vanilla's own pause actions (Practice Mode, Disconnect) do: a
    /// GameStateRequest in the player's input, which reaches the host on every machine. Ours is a
    /// value the game doesn't define (<see cref="OpenRequest"/>); MessagePack sends enums as
    /// numbers, and PlayerManager.HandleRequest on the host, which ignores unknown values, is patched
    /// to pick it up. The request is one-shot (the next input update carries None again).
    /// </summary>
    internal static class FloorPlanPauseButton
    {
        public const string Label = "Floor Planner";
        public const GameStateRequest OpenRequest = (GameStateRequest)0x4546;

        private static readonly MethodInfo RequestAction = AccessTools.Method(typeof(Menu<MenuAction>), "RequestAction");

        /// <summary>Called from the pause menu transpiler (CameraControlsButtonPatch), above Camera Controls.</summary>
        public static void Add(MainMenu menu, int playerId, Func<string, Action<int>, ButtonElement> addButton)
        {
            if (GameInfo.CurrentScene != SceneType.Kitchen || !GameInfo.IsPreparationTime)
            {
                return;
            }
            // A client whose host allows only the host's players to edit can't use it (the terminal says so too).
            if (Session.NetworkedPlayState == NetworkedPlayState.Client && FloorPlanTerminalLabelPatch.LastLabel == FloorPlanIds.LabelHostOnly)
            {
                return;
            }
            addButton(Label, _ =>
            {
                InputSourceIdentifier.DefaultInputSource?.MakeRequest(playerId, OpenRequest);
                RequestAction?.Invoke(menu, new object[] { new MenuAction(PauseMenuAction.CloseMenu) });
            });
        }
    }

    /// <summary>Host: a player's "Floor Planner" pause-menu request opens the editor for them (at night, in the restaurant).</summary>
    [HarmonyPatch(typeof(PlayerManager), "HandleRequest")]
    internal static class FloorPlanPauseRequestPatch
    {
        private static bool Prefix(PlayerManager __instance, Player player, GameStateRequest request)
        {
            if (request != FloorPlanPauseButton.OpenRequest)
            {
                return true;
            }
            try
            {
                EntityManager em = __instance.EntityManager;
                bool night = !em.CreateEntityQuery(typeof(SIsNightTime)).IsEmpty && !em.CreateEntityQuery(typeof(SKitchenMarker)).IsEmpty;
                if (player != null && night && em.HasComponent<CPlayer>(player.Entity))
                {
                    __instance.World.GetExistingSystem<FloorPlanService>()?.RequestOpen(player.Entity);
                    ModMain.Log($"Player {em.GetComponentData<CPlayer>(player.Entity).ID} asked for the floor plan editor from the pause menu.");
                }
            }
            catch (Exception e)
            {
                ModMain.LogError($"Failed to open the floor plan editor from the pause menu: {e}");
            }
            return false;
        }
    }
}
