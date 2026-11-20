using HarmonyLib;
using Kitchen;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Shrinks the tables' seat-count indicators when the restaurant is shown smaller than the
    /// game ever shows it (user request 2026-11-19: zoomed out over a very big restaurant, they
    /// covered the whole screen).
    ///
    /// The game draws them on its UI camera at a fixed size in pixels, placed over the table
    /// (ViewMode.WorldToScreen), so they don't shrink with the restaurant. Here they're scaled by
    /// how big a tile is on screen compared with the smallest the game's own camera makes it (its
    /// maximum distance and field of view: PlayCamera.IndicatorScale). A normal restaurant at the
    /// normal zoom keeps the game's size exactly; they never grow past it.
    /// </summary>
    internal class IndicatorZoomScale : MonoBehaviour
    {
        private Vector3 Original;

        private void Awake() => Original = transform.localScale;

#if DEBUG
        internal Vector3 TestOriginal => Original;
#endif

        private void LateUpdate()
        {
            Vector3 wanted = Original * PlayCamera.IndicatorScale;
            if (transform.localScale != wanted)
            {
                transform.localScale = wanted;
            }
        }
    }

    [HarmonyPatch(typeof(TableIndicatorView), nameof(TableIndicatorView.Initialise))]
    internal static class TableIndicatorScalePatch
    {
        private static void Postfix(TableIndicatorView __instance)
        {
            if (__instance.GetComponent<IndicatorZoomScale>() == null)
            {
                __instance.gameObject.AddComponent<IndicatorZoomScale>();
            }
        }
    }
}
