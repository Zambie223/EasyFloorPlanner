using System.Collections.Generic;
using System.Linq;
using Cinemachine;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// The camera while the floor plan editor (or a spectator view) is open on this machine. It
    /// pans beyond the restaurant and looks straight down, so walls don't hide the tiles behind
    /// them. Local to this machine; nothing is networked.
    ///
    /// Panning: the game's camera is a Cinemachine FramingTransposer that frames a target group
    /// (GameCreator: DollyThenZoom, group framing size 1.2). Vanilla's ViewBoundsMaintainer fills
    /// that group with the restaurant's bound markers. While active, the group's targets are
    /// swapped for two anchors at the corners of the current framing; moving both anchors pans the
    /// view, with the game's own smoothing and zoom. If vanilla assigns new targets meanwhile (it
    /// replaces the array whenever a marker view updates), those become the ones to restore, and
    /// ours are re-applied.
    ///
    /// Top-down: the virtual camera has no Aim stage, so it keeps its transform's rotation; the
    /// FramingTransposer places it along its forward axis. Its pitch is eased to 89 degrees (yaw
    /// kept) and back afterwards. Nothing else in the game rotates it.
    ///
    /// A small driver object runs this every frame, so the tilt back can finish after the view
    /// that used it is gone.
    /// </summary>
    internal static class FloorPlanCamera
    {
        /// <summary>Tiles per second at full stick / key.</summary>
        private const float PanSpeed = 11f;

        /// <summary>How far past the editable area the view centre may go.</summary>
        private const float PanMargin = 6f;

        private const float TopDownPitch = 89f; // not exactly 90: keeps "up the screen" well defined
        private const float TiltDegreesPerSecond = 220f;

        private static readonly HashSet<object> Users = new HashSet<object>();
        private static CinemachineTargetGroup Group;
        private static CinemachineTargetGroup.Target[] Saved;
        private static CinemachineTargetGroup.Target[] Ours;
        private static GameObject AnchorRoot;
        private static Vector3 StartCentre;

        private static Transform Vcam;
        private static Quaternion VcamOriginal;
        private static GameObject DriverObject;

        public static bool Active => Users.Count > 0 && Group != null;

        /// <summary>How far the view has been panned from where it started.</summary>
        public static Vector3 Offset => AnchorRoot != null ? AnchorRoot.transform.position - StartCentre : Vector3.zero;

        public static void Begin(object user)
        {
            if (Users.Contains(user))
            {
                return;
            }
            if (Users.Count == 0 && !Take())
            {
                return;
            }
            Users.Add(user);
        }

        public static void End(object user)
        {
            if (!Users.Remove(user) || Users.Count > 0)
            {
                return;
            }
            Release();
        }

        /// <summary>Pans by <paramref name="input"/> (stick or WASD, -1..1 per axis) for this frame. Screen-relative.</summary>
        public static void Pan(Vector2 input, float deltaTime)
        {
            if (!Active || AnchorRoot == null || input.sqrMagnitude < 0.04f)
            {
                return;
            }
            Vector3 right = Vector3.right, up = Vector3.forward;
            Camera cam = Camera.main;
            if (cam != null)
            {
                // The camera's up axis, flattened, is "up the screen" whether it's tilted or top-down.
                right = Flat(cam.transform.right, Vector3.right);
                up = Flat(cam.transform.up, Vector3.forward);
            }
            Vector2 v = Vector2.ClampMagnitude(input, 1f);
            Move((right * v.x + up * v.y) * (PanSpeed * deltaTime));
        }

        /// <summary>Moves the view by a world offset (clamped to the editable area plus a margin).</summary>
        public static void Move(Vector3 delta)
        {
            if (AnchorRoot == null)
            {
                return;
            }
            Vector3 p = AnchorRoot.transform.position + new Vector3(delta.x, 0f, delta.z);
            float limit = LayoutEdits.MaxExtent + PanMargin;
            p.x = Mathf.Clamp(p.x, -limit, limit);
            p.z = Mathf.Clamp(p.z, -limit, limit);
            AnchorRoot.transform.position = p;
        }

        /// <summary>Self-test: centres the view on a world point (clamped like <see cref="Move"/>).</summary>
        internal static void TestMoveTo(Vector3 point)
        {
            if (AnchorRoot != null)
            {
                Move(point - AnchorRoot.transform.position);
            }
        }

        /// <summary>Call every Update and LateUpdate while active: keeps our targets in the group.</summary>
        public static void Maintain()
        {
            if (!Active)
            {
                return;
            }
            if (Group.m_Targets != Ours)
            {
                // Vanilla replaced the targets (a marker view changed): restore those on exit.
                Saved = Group.m_Targets;
                Group.m_Targets = Ours;
            }
        }

        /// <summary>Run by the driver every frame: maintain the targets, and ease the tilt.</summary>
        private static void Tick()
        {
            Maintain();
            if (Vcam == null)
            {
                Object.Destroy(DriverObject);
                DriverObject = null;
                return;
            }
            Quaternion goal = Active
                ? Quaternion.Euler(TopDownPitch, VcamOriginal.eulerAngles.y, 0f)
                : VcamOriginal;
            Vcam.rotation = Quaternion.RotateTowards(Vcam.rotation, goal, TiltDegreesPerSecond * Time.unscaledDeltaTime);
            if (!Active && Quaternion.Angle(Vcam.rotation, VcamOriginal) < 0.01f)
            {
                // Back where it was: done.
                Vcam.rotation = VcamOriginal;
                Vcam = null;
                Object.Destroy(DriverObject);
                DriverObject = null;
            }
        }

        private static Vector3 Flat(Vector3 v, Vector3 fallback)
        {
            v.y = 0f;
            return v.sqrMagnitude > 0.0001f ? v.normalized : fallback;
        }

        private static bool Take()
        {
            Group = Object.FindObjectOfType<CinemachineTargetGroup>();
            if (Group == null)
            {
                ModMain.LogWarning("No camera target group found; the view can't be panned.");
                return false;
            }
            Saved = Group.m_Targets;

            // The current framing: every live target, with its radius.
            bool any = false;
            Bounds box = default;
            foreach (CinemachineTargetGroup.Target t in Saved ?? new CinemachineTargetGroup.Target[0])
            {
                if (t.target == null || t.weight <= 0f)
                {
                    continue;
                }
                var b = new Bounds(t.target.position, Vector3.one * (t.radius * 2f));
                if (any)
                {
                    box.Encapsulate(b);
                }
                else
                {
                    box = b;
                    any = true;
                }
            }
            if (!any)
            {
                box = new Bounds(Vector3.zero, new Vector3(16f, 0f, 12f));
            }

            AnchorRoot = new GameObject("EasyFloorPlanner Camera Anchors");
            StartCentre = new Vector3(box.center.x, box.center.y, box.center.z);
            AnchorRoot.transform.position = StartCentre;
            Transform min = new GameObject("Min").transform;
            Transform max = new GameObject("Max").transform;
            min.SetParent(AnchorRoot.transform, false);
            max.SetParent(AnchorRoot.transform, false);
            min.localPosition = new Vector3(-box.extents.x, 0f, -box.extents.z);
            max.localPosition = new Vector3(box.extents.x, 0f, box.extents.z);
            Ours = new[]
            {
                new CinemachineTargetGroup.Target { target = min, weight = 1f, radius = 0f },
                new CinemachineTargetGroup.Target { target = max, weight = 1f, radius = 0f },
            };
            Group.m_Targets = Ours;

            // The virtual camera following this group (the one the game created).
            if (Vcam == null)
            {
                CinemachineVirtualCamera vcam = Object.FindObjectsOfType<CinemachineVirtualCamera>()
                    .FirstOrDefault(c => c.Follow == Group.transform)
                    ?? Object.FindObjectOfType<CinemachineVirtualCamera>();
                if (vcam != null)
                {
                    Vcam = vcam.transform;
                    VcamOriginal = Vcam.rotation;
                }
                else
                {
                    ModMain.LogWarning("No virtual camera found; the view stays at its usual angle.");
                }
            }
            // (If Vcam is still set, a previous tilt back hasn't finished: keep its original rotation.)
            if (DriverObject == null)
            {
                DriverObject = new GameObject("EasyFloorPlanner Camera Driver");
                DriverObject.AddComponent<Driver>();
            }
            ModMain.Log($"Camera panning on (view {box.size.x:0.#} x {box.size.z:0.#} around {StartCentre}), top-down.");
            return true;
        }

        private static void Release()
        {
            if (Group != null && Group.m_Targets == Ours)
            {
                Group.m_Targets = Saved ?? new CinemachineTargetGroup.Target[0];
            }
            if (AnchorRoot != null)
            {
                Object.Destroy(AnchorRoot);
            }
            AnchorRoot = null;
            Ours = null;
            Saved = null;
            Group = null;
            // The driver keeps running until the camera has tilted back.
            ModMain.Log("Camera panning off; the game's framing is restored.");
        }

        private class Driver : MonoBehaviour
        {
            private void Update() => Maintain();

            private void LateUpdate() => Tick();
        }

#if DEBUG
        internal static CinemachineTargetGroup TestGroup => Group != null ? Group : Object.FindObjectOfType<CinemachineTargetGroup>();
        internal static bool TestOursApplied => Active && Group.m_Targets == Ours;
        internal static float TestPitch => Vcam != null ? Vcam.rotation.eulerAngles.x : (Camera.main != null ? Camera.main.transform.rotation.eulerAngles.x : -1f);
        internal static bool TestTiltDone => Vcam == null;
#endif
    }
}
