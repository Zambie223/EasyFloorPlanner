using Kitchen;
using KitchenMods;
using Unity.Entities;
using UnityEngine;
using UnityEngine.AI;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Keeps vanilla's chair-reachability check working when the restaurant grows west.
    ///
    /// When tables are updated (each night, and at the start of each day), vanilla computes a
    /// navmesh path from SPerformTableUpdate.DefaultPathingSource, a fixed (-15, 0, 0), to every
    /// chair. Chairs without a complete path are marked unusable (MarkUnpathableChairs,
    /// FindTablePlaces). Vanilla buildings never reach x = -15, so that point is always open yard.
    /// A building grown that far could cover it, and inside a wall or under an appliance every
    /// path fails, so customers would never sit.
    ///
    /// Only while the building comes within 2 tiles of that point, this moves it to where
    /// customers actually spawn (x = Bounds.min.x - 10, z = -6; see CreateCustomerGroup), snapped
    /// onto the navmesh. NavmeshAreaPatch keeps that area walkable. Otherwise vanilla's value is
    /// left alone, and it's always restored outside the restaurant (HQ uses table updates too).
    ///
    /// DefaultPathingSource is a static field read when the table update is requested, so setting
    /// it every frame needs no ordering against vanilla systems.
    /// </summary>
    [AlwaysUpdateSystem]
    public class TablePathingSourceGuard : GenericSystemBase, IModSystem
    {
        private static readonly Vector3 VanillaSource = new Vector3(-15f, 0f, 0f);
        private const float Clearance = 2f;

        private Vector3 _lastLogged = VanillaSource;

        protected override void OnUpdate()
        {
            Vector3 wanted = VanillaSource;
            if (Has<SKitchenMarker>() && HasSingleton<SLayout>())
            {
                Bounds building = Bounds;
                building.Expand(new Vector3(Clearance * 2f, 0f, Clearance * 2f));
                if (building.size != Vector3.zero && building.Contains(new Vector3(VanillaSource.x, building.center.y, VanillaSource.z)))
                {
                    wanted = new Vector3(Bounds.min.x - 10f, 0f, NavmeshAreaPatch.CustomerSpawnZ);
                    if (NavMesh.SamplePosition(wanted, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                    {
                        wanted = hit.position;
                    }
                }
            }

            if (SPerformTableUpdate.DefaultPathingSource != wanted)
            {
                SPerformTableUpdate.DefaultPathingSource = wanted;
                if ((wanted - _lastLogged).sqrMagnitude > 0.01f)
                {
                    _lastLogged = wanted;
                    ModMain.Log(wanted == VanillaSource
                        ? "Chair pathing source back to vanilla (-15, 0, 0)."
                        : $"Building reaches vanilla's chair pathing point (-15, 0, 0); using the customer spawn area {wanted} instead.");
                }
            }
        }
    }
}
