using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Cinemachine;
using Controllers;
using Kitchen;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EasyFloorPlanner
{
    /// <summary>
    /// The camera during play: a margin for the player HUD, plus per-player zoom, pan and an optional
    /// follow mode. Local to this machine (each online player has their own camera; local co-op
    /// players share one). Nothing is networked or saved, except the follow preference (PlayerPrefs).
    ///
    /// Why: the game frames the whole restaurant (four day markers at its bounds, plus anything that
    /// wanders outside). A restaurant grown with this mod can be large or oddly shaped, so the view
    /// zooms out and the player cards at the bottom of the screen cover the front row.
    ///
    /// How: the game has one Cinemachine virtual camera for the whole session (GameCreator), with a
    /// FramingTransposer that frames the target group. Three of its settings are adjusted each
    /// frame, without touching the group's targets (so vanilla's framing logic keeps working):
    /// - m_GroupFramingSize: how much of the screen the group fills. Larger zooms in.
    /// - m_ScreenX / m_ScreenY: where on screen the group's centre goes (0..1, y = 0 at the top).
    ///   This pans: under group framing the transposer ignores m_TrackedObjectOffset, so the
    ///   screen position is the only offset that works. Cinemachine clamps it to -0.5..1.5.
    /// - m_MinimumFOV / m_MaximumFOV: the game's limits (10..25 degrees) are widened only in
    ///   proportion to the zoom, so the default view is unchanged.
    /// The HUD margin frames the restaurant into the top (1 - HudFraction) of the screen.
    ///
    /// While the floor plan editor is open (FloorPlanCamera), it owns panning; only zoom applies
    /// there, with its own level. Outside the restaurant (HQ, menus) vanilla's values are restored.
    /// </summary>
    internal static class PlayCamera
    {
        /// <summary>Bottom share of the screen kept clear for the player cards.</summary>
        public const float HudFraction = 0.08f;

        public const float MinZoom = 0.6f;
        public const float MaxZoom = 3f;
        private const float WheelStep = 1.12f;
        private const float StickZoomRate = 1.4f;   // e-folds per second at full stick
        private const float StickPanRate = 0.8f;    // screens per second at full stick
        private const float BasePanLimit = 0.15f;   // screens, at normal zoom
        private const float MaxPanLimit = 1f;       // Cinemachine's screen-position range allows one screen
        private const float FollowMinZoom = 1.05f;  // follow only acts when zoomed in
        private const float FollowEdge = 0.22f;     // keep chefs at least this far (viewport) from the edges
        private const float FollowRate = 5f;
        private const string FollowPref = "EasyFloorPlanner.CameraFollow";

        /// <summary>Play zoom (1 = the game's framing). Reset when leaving the restaurant.</summary>
        public static float Zoom { get; private set; } = 1f;

        /// <summary>Pan in screens (+x shows more to the right, +y more towards the back).</summary>
        public static Vector2 Pan { get; private set; }

        /// <summary>Keep this machine's chefs in view while zoomed in.</summary>
        public static bool Follow { get; private set; }

        private static float EditorZoom = 1f;
        private static bool WasEditor;

        private static GameObject DriverObject;
        private static CinemachineVirtualCamera Vcam;
        private static CinemachineFramingTransposer Framing;
        private static bool HaveVanilla;
        private static float VanillaSize, VanillaScreenX, VanillaScreenY, VanillaMinFov, VanillaMaxFov;
        private static bool Applied;

        private static string Toast;
        private static float ToastUntil;

        private static Vector2 MiddleDownAt;
        private static float MiddleDownTime;
        private static bool MiddleDragged;

        /// <summary>
        /// Creates the driver if it doesn't exist (Unity's null check also catches a destroyed
        /// object). Called at mod load and again whenever a restaurant layout is built, so a lost
        /// driver comes back.
        /// </summary>
        public static void Install()
        {
            if (DriverObject != null)
            {
                return;
            }
            if (Installs++ > 0)
            {
                ModMain.LogWarning($"Play camera driver was gone; recreating it (install #{Installs}).");
            }
            try
            {
                Follow = PlayerPrefs.GetInt(FollowPref, 0) == 1;
            }
            catch (Exception)
            {
                Follow = false;
            }
            DriverObject = new GameObject("EasyFloorPlanner Play Camera");
            UnityEngine.Object.DontDestroyOnLoad(DriverObject);
            DriverObject.AddComponent<Driver>();
            ModMain.Log("Play camera driver created.");
        }

        private static int Installs;
        private static int Ticks;
        private static SceneType LastScene = (SceneType)(-1);
        private static string LastSearch = "not searched yet";

        // ================================================================= per frame

        private static void Tick()
        {
            Ticks++;
            if (GameInfo.CurrentScene != LastScene)
            {
                LastScene = GameInfo.CurrentScene;
                ModMain.Log($"Play camera: scene {LastScene}.");
            }
            bool inRestaurant = GameInfo.CurrentScene == SceneType.Kitchen;
            if (!inRestaurant || !FindCamera())
            {
                Restore();
                Zoom = 1f;
                Pan = Vector2.zero;
                EditorZoom = 1f;
                return;
            }
            bool editor = FloorPlanCamera.Active;
            if (editor != WasEditor)
            {
                WasEditor = editor;
                EditorZoom = 1f; // each editor session starts at the editor's own framing
            }
            if (InputAllowed(editor))
            {
                ReadInput(editor);
            }
            if (!editor && Follow && Zoom > FollowMinZoom)
            {
                FollowChefs();
            }
            Pan = ClampPan(Pan, Zoom);
            ApplySettings(editor);
        }

        private static void ApplySettings(bool editor)
        {
            float zoom = editor ? EditorZoom : Zoom;
            float hud = editor ? 0f : HudFraction;
            Vector2 pan = editor ? Vector2.zero : Pan;
            Framing.m_GroupFramingSize = VanillaSize * (1f - hud) * zoom;
            Framing.m_ScreenX = Mathf.Clamp(VanillaScreenX - pan.x, -0.5f, 1.5f);
            Framing.m_ScreenY = Mathf.Clamp(VanillaScreenY - hud / 2f + pan.y, -0.5f, 1.5f);
            // Widen the game's FOV limits only as far as the zoom (and HUD margin) needs.
            Framing.m_MinimumFOV = VanillaMinFov / Mathf.Max(zoom, 1f);
            Framing.m_MaximumFOV = Mathf.Min(VanillaMaxFov / ((1f - hud) * Mathf.Min(zoom, 1f)), 60f);
            Applied = true;
        }

        private static void Restore()
        {
            if (Applied && Framing != null)
            {
                Framing.m_GroupFramingSize = VanillaSize;
                Framing.m_ScreenX = VanillaScreenX;
                Framing.m_ScreenY = VanillaScreenY;
                Framing.m_MinimumFOV = VanillaMinFov;
                Framing.m_MaximumFOV = VanillaMaxFov;
            }
            Applied = false;
        }

        private static bool FindCamera()
        {
            if (Framing != null && Vcam != null)
            {
                return true;
            }
            Framing = null;
            var seen = new List<string>();
            foreach (CinemachineVirtualCamera v in UnityEngine.Object.FindObjectsOfType<CinemachineVirtualCamera>())
            {
                CinemachineFramingTransposer f = v.GetCinemachineComponent<CinemachineFramingTransposer>();
                seen.Add($"{v.name} (body {v.GetCinemachineComponent(CinemachineCore.Stage.Body)?.GetType().Name ?? "none"})");
                if (f != null)
                {
                    Vcam = v;
                    Framing = f;
                    break;
                }
            }
            string search = seen.Count == 0 ? "no virtual cameras" : string.Join(", ", seen);
            if (Framing == null)
            {
                if (search != LastSearch)
                {
                    LastSearch = search;
                    ModMain.LogWarning($"Play camera: no framing camera found yet ({search}).");
                }
                return false;
            }
            LastSearch = search;
            if (!HaveVanilla)
            {
                // The game sets these once at startup (GameCreator); nothing else changes them.
                HaveVanilla = true;
                VanillaSize = Framing.m_GroupFramingSize;
                VanillaScreenX = Framing.m_ScreenX;
                VanillaScreenY = Framing.m_ScreenY;
                VanillaMinFov = Framing.m_MinimumFOV;
                VanillaMaxFov = Framing.m_MaximumFOV;
                ModMain.Log($"Play camera: framing {VanillaSize}, screen ({VanillaScreenX}, {VanillaScreenY}), FOV {VanillaMinFov}..{VanillaMaxFov}.");
            }
            return true;
        }

        private static Vector2 ClampPan(Vector2 pan, float zoom)
        {
            float limit = Mathf.Min(BasePanLimit + Mathf.Max(zoom - 1f, 0f) / 2f, MaxPanLimit);
            return new Vector2(Mathf.Clamp(pan.x, -limit, limit), Mathf.Clamp(pan.y, -limit, limit));
        }

        // ================================================================= input

        /// <summary>
        /// No camera input while a menu, the pause screen or a popup has this machine's input. In
        /// the editor its own lock is expected, so only the global lock counts there.
        /// </summary>
        private static bool InputAllowed(bool editor)
        {
            if (!Application.isFocused)
            {
                return false;
            }
            BaseInputSource source = InputSourceIdentifier.DefaultInputSource;
            if (source == null)
            {
                return true;
            }
            if (source.GlobalLock != null && source.GlobalLock.IsLocked)
            {
                return false;
            }
            return editor || !AnyLocalPlayerLocked(source);
        }

        private static readonly FieldInfo PlayersField =
            typeof(BaseInputSource).GetField("Players", BindingFlags.Instance | BindingFlags.NonPublic);

        private static bool AnyLocalPlayerLocked(BaseInputSource source)
        {
            try
            {
                if (PlayersField?.GetValue(source) is Dictionary<int, PlayerData> players)
                {
                    return players.Values.Any(p => p?.Lock != null && p.Lock.IsLocked);
                }
            }
            catch (Exception)
            {
                // If the game changes, camera input just isn't gated by menus.
            }
            return false;
        }

        private static void ReadInput(bool editor)
        {
            float dt = Time.unscaledDeltaTime;
            float zoom = editor ? EditorZoom : Zoom;
            Vector2 pan = Pan;
            Keyboard kb = Keyboard.current;
            Mouse mouse = Mouse.current;

            if (mouse != null)
            {
                // The editor uses the plain wheel for brushes, so zoom there is Ctrl + wheel.
                bool ctrl = kb != null && kb.ctrlKey.isPressed;
                float wheel = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(wheel) > 0.01f && (!editor || ctrl))
                {
                    zoom *= wheel > 0f ? WheelStep : 1f / WheelStep;
                }
                if (!editor)
                {
                    // Middle drag pans (grab the view); a middle click without dragging resets it.
                    if (mouse.middleButton.wasPressedThisFrame)
                    {
                        MiddleDownAt = mouse.position.ReadValue();
                        MiddleDownTime = Time.unscaledTime;
                        MiddleDragged = false;
                    }
                    if (mouse.middleButton.isPressed)
                    {
                        Vector2 d = mouse.delta.ReadValue();
                        if ((mouse.position.ReadValue() - MiddleDownAt).sqrMagnitude > 64f)
                        {
                            MiddleDragged = true;
                        }
                        if (MiddleDragged && Screen.width > 0 && Screen.height > 0)
                        {
                            pan.x -= d.x / Screen.width;
                            pan.y -= d.y / Screen.height;
                        }
                    }
                    if (mouse.middleButton.wasReleasedThisFrame && !MiddleDragged && Time.unscaledTime - MiddleDownTime < 0.4f)
                    {
                        Reset();
                        return;
                    }
                }
            }
            if (!editor && kb != null && kb.fKey.wasPressedThisFrame)
            {
                SetFollow(!Follow);
            }

            foreach (Gamepad pad in Gamepad.all)
            {
                Vector2 stick = pad.rightStick.ReadValue();
                bool rb = pad.rightShoulder.isPressed;
                if (stick.magnitude < 0.2f)
                {
                    stick = Vector2.zero;
                }
                if (rb)
                {
                    zoom *= Mathf.Exp(stick.y * StickZoomRate * dt);
                }
                else if (!editor)
                {
                    pan += stick * (StickPanRate * dt);
                }
                if (pad.rightStickButton.wasPressedThisFrame && !editor)
                {
                    if (rb)
                    {
                        SetFollow(!Follow);
                    }
                    else
                    {
                        Reset();
                        return;
                    }
                }
            }

            zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
            if (editor)
            {
                EditorZoom = zoom;
            }
            else
            {
                Zoom = zoom;
                Pan = pan;
            }
        }

        public static void Reset()
        {
            Zoom = 1f;
            Pan = Vector2.zero;
            ShowToast("Camera reset");
        }

        public static void SetFollow(bool on)
        {
            Follow = on;
            try
            {
                PlayerPrefs.SetInt(FollowPref, on ? 1 : 0);
            }
            catch (Exception)
            {
                // Preference not saved; it still applies for this session.
            }
            ShowToast(on ? "Camera: follows your chef when zoomed in" : "Camera: stays where you put it");
        }

        private static void ShowToast(string text)
        {
            Toast = text;
            ToastUntil = Time.unscaledTime + 2f;
        }

        // ================================================================= follow

        private static readonly FieldInfo PlayerDataField =
            typeof(PlayerView).GetField("Data", BindingFlags.Instance | BindingFlags.NonPublic);
        private static PlayerView[] CachedPlayers = new PlayerView[0];
        private static float PlayersCachedAt = -10f;

        /// <summary>This machine's chefs (local co-op can have several).</summary>
        private static List<Vector3> LocalChefs()
        {
            if (Time.unscaledTime - PlayersCachedAt > 1f)
            {
                PlayersCachedAt = Time.unscaledTime;
                CachedPlayers = UnityEngine.Object.FindObjectsOfType<PlayerView>();
            }
            var result = new List<Vector3>();
            int local = (int)InputSourceIdentifier.Identifier;
            foreach (PlayerView p in CachedPlayers)
            {
                if (p == null || PlayerDataField == null)
                {
                    continue;
                }
                if (PlayerDataField.GetValue(p) is PlayerView.ViewData data && data.InputSource == local)
                {
                    result.Add(p.transform.position);
                }
            }
            return result;
        }

        /// <summary>Nudges the pan so this machine's chefs stay away from the screen edges.</summary>
        private static void FollowChefs()
        {
            Camera cam = Camera.main;
            List<Vector3> chefs = LocalChefs();
            if (cam == null || chefs.Count == 0)
            {
                return;
            }
            float minX = 1f, maxX = 0f, minY = 1f, maxY = 0f;
            foreach (Vector3 c in chefs)
            {
                Vector3 v = cam.WorldToViewportPoint(c); // y up
                minX = Mathf.Min(minX, v.x);
                maxX = Mathf.Max(maxX, v.x);
                minY = Mathf.Min(minY, v.y);
                maxY = Mathf.Max(maxY, v.y);
            }
            float bottom = FollowEdge + HudFraction;
            float top = 1f - FollowEdge;
            Vector2 correction = new Vector2(
                Need(minX, maxX, FollowEdge, 1f - FollowEdge),
                Need(minY, maxY, bottom, top));
            Pan += correction * Mathf.Min(1f, FollowRate * Time.unscaledDeltaTime);
        }

        /// <summary>How far (viewport units) the view must move so [lo, hi] fits in [min, max]; centres it if it can't.</summary>
        private static float Need(float lo, float hi, float min, float max)
        {
            if (hi - lo > max - min)
            {
                return (lo + hi) / 2f - (min + max) / 2f;
            }
            if (lo < min)
            {
                return lo - min;
            }
            if (hi > max)
            {
                return hi - max;
            }
            return 0f;
        }

        // ================================================================= driver

        private class Driver : MonoBehaviour
        {
            private void Update()
            {
                try
                {
                    Tick();
                }
                catch (Exception e)
                {
                    ModMain.LogError($"Play camera error: {e}");
                    enabled = false;
                    Restore();
                }
            }

            private GUIStyle Style;

            private void OnGUI()
            {
                if (Toast == null || Time.unscaledTime > ToastUntil)
                {
                    return;
                }
                if (Style == null)
                {
                    Style = new GUIStyle(GUI.skin.label)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = Mathf.Max(16, Screen.height / 40),
                        fontStyle = FontStyle.Bold
                    };
                }
                var rect = new Rect(0f, Screen.height * 0.12f, Screen.width, Screen.height / 20f);
                Color old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.7f);
                GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), Toast, Style);
                GUI.color = Color.white;
                GUI.Label(rect, Toast, Style);
                GUI.color = old;
            }
        }

#if DEBUG
        internal static string TestDiagnostics =>
            $"driver {(DriverObject != null ? (DriverObject.activeInHierarchy ? "alive" : "inactive") : "missing")}, ticks {Ticks}, scene {LastScene}, installs {Installs}, last search: {LastSearch}";
        internal static bool TestApplied => Applied;
        internal static CinemachineFramingTransposer TestFraming => Framing;
        internal static float TestVanillaSize => VanillaSize;
        internal static float TestVanillaScreenY => VanillaScreenY;
        internal static float TestEditorZoom => EditorZoom;

        internal static void TestSet(float zoom, Vector2 pan)
        {
            Zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
            Pan = pan;
        }

        internal static void TestSetFollowSilently(bool on) => Follow = on;

        internal static int TestLocalChefCount => LocalChefs().Count;
#endif
    }
}
