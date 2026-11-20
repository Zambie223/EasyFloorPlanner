using System.Collections.Generic;
using System.Linq;
using Kitchen;
using Kitchen.Layouts;
using KitchenData;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Keeps the restaurant setting's street-front decorations fitted to the restaurant: the
    /// street decorator's neighbouring buildings and street, and the train-station decorator's
    /// front border.
    ///
    /// Why: settings that use vanilla's StreetDecorator (Turbo, for one) line the street with
    /// "wall pieces": neighbouring buildings standing from 1.5 tiles past each side wall out to
    /// x = ±20, plus a row of "internal wall pieces" behind the back wall. The decorator places them
    /// once, from the original bounds. Each piece is anchored on the street row, but its building
    /// reaches back over the whole lot. So when the restaurant grows sideways, the new floor ends
    /// up inside a building: the floor and anyone standing on it disappear under a grey block (seen in
    /// play, 2026-11-14). SceneryBulldozer can't catch this, because it only looks at each piece's
    /// anchor, which is several tiles from the new floor. TrainStationDecorator's front border
    /// has the same problem in a milder form: it spans only the original front.
    ///
    /// Fix: after every layout write, compute where the active setting's decorators would put
    /// these pieces for the new layout (the exact same formulas), remove pieces that aren't on
    /// that pattern and create the missing ones. Comparing against the current layout (not the
    /// previous edit's) also repairs restaurants grown before this fix, on their next apply.
    /// Nothing happens if none of the pieces exist (another mod removed them, or an old save).
    ///
    /// Added 2026-11-19 with the movable front door and outside doors:
    ///   - the front border with a gap at the door (Countryside and Witch decorators, 0.7 in front),
    ///     Halloween's front wall (also gapped at the door) and the Countryside cobble path, which runs
    ///     from x = -20 to the door. They follow the door when it moves (and the width on resizes);
    ///   - with the yard ring (YardRing, a side or back door), the street decorator's side buildings
    ///     and back row stand YardRing.Width tiles further out, leaving an alley round the building.
    /// </summary>
    internal static class StreetPieces
    {
        // StreetDecorator's private MinPlacementPos / MaxPlacementPos.
        private const int MinPlacementPos = -20;
        private const int MaxPlacementPos = 20;
        private const float Tolerance = 0.01f;

        private enum Role { Street, Wall, InternalWall, FrontBorder, GapBorder, HalloweenWall, CobblePath }

        // The countryside-type decorators' private PathStartLocation.
        private const float PathStart = -20f;

        /// <summary>The active setting's managed pieces: which IDs each role accepts, and which to create.</summary>
        private sealed class Decor
        {
            public readonly Dictionary<Role, HashSet<int>> Ids = new Dictionary<Role, HashSet<int>>();
            public readonly Dictionary<Role, int> DefaultId = new Dictionary<Role, int>();
            public bool HasStreet;
            public float BorderSpacing;
            public float GapSpacing;
            public float HalloweenSpacing;

            public bool Any => Ids.Count > 0;

            public bool Manages(int id) => Ids.Values.Any(ids => ids.Contains(id));

            public Role? RoleOf(int id)
            {
                foreach (KeyValuePair<Role, HashSet<int>> kv in Ids)
                {
                    if (kv.Value.Contains(id))
                    {
                        return kv.Key;
                    }
                }
                return null;
            }
        }

        private static readonly Dictionary<int, Decor> Cache = new Dictionary<int, Decor>();

        /// <summary>Result of the last sync (removed, added), for logging and the self-test.</summary>
        public static (int removed, int added) LastSync { get; private set; }

        /// <summary>The managed pieces of the run's restaurant setting (CSetting on the SLayout entity).</summary>
        private static Decor Active(EntityManager em)
        {
            EntityQuery q = em.CreateEntityQuery(ComponentType.ReadOnly<SLayout>());
            try
            {
                if (q.CalculateEntityCount() != 1)
                {
                    return new Decor();
                }
                Entity layout = q.GetSingletonEntity();
                if (!em.HasComponent<CSetting>(layout))
                {
                    return new Decor();
                }
                return For(em.GetComponentData<CSetting>(layout).RestaurantSetting);
            }
            finally
            {
                q.Dispose();
            }
        }

        private static Decor For(int settingId)
        {
            if (Cache.TryGetValue(settingId, out Decor cached))
            {
                return cached;
            }
            var decor = new Decor();
            if (GameData.Main.TryGet(settingId, out RestaurantSetting setting) && setting.Decorators != null)
            {
                foreach (IDecorationConfiguration c in setting.Decorators)
                {
                    if (c is StreetDecorator.DecorationsConfiguration street)
                    {
                        decor.HasStreet = true;
                        Add(decor, Role.Street, street.StreetPiece);
                        Add(decor, Role.Wall, street.WallPiece);
                        Add(decor, Role.InternalWall, street.InternalWallPiece);
                    }
                    else if (c is TrainStationDecorator.DecorationsConfiguration train && train.FrontBorder != null && train.BorderSpacing > 0.01f)
                    {
                        decor.BorderSpacing = train.BorderSpacing;
                        Add(decor, Role.FrontBorder, train.FrontBorder);
                    }
                    else if (c is CountrysideDecorator.DecorationsConfiguration country)
                    {
                        if (country.FrontBorder != null && country.BorderSpacing > 0.01f)
                        {
                            decor.GapSpacing = country.BorderSpacing;
                            Add(decor, Role.GapBorder, country.FrontBorder);
                        }
                        Add(decor, Role.CobblePath, country.Cobblestone);
                    }
                    else if (c is WitchDecorator.DecorationsConfiguration witch && witch.FrontBorder != null && witch.BorderSpacing > 0.01f)
                    {
                        decor.GapSpacing = witch.BorderSpacing;
                        Add(decor, Role.GapBorder, witch.FrontBorder);
                    }
                    else if (c is HalloweenDecorator.DecorationsConfiguration halloween && halloween.FrontWall != null && halloween.BorderSpacing > 0.01f)
                    {
                        decor.HalloweenSpacing = halloween.BorderSpacing;
                        Add(decor, Role.HalloweenWall, halloween.FrontWall);
                    }
                }
            }
            Cache[settingId] = decor;
            return decor;
        }

        private static void Add(Decor decor, Role role, Appliance piece)
        {
            if (piece == null)
            {
                return;
            }
            // CreateNewKitchen swaps in the weak variant on some runs, so either ID may be in the world.
            var ids = new HashSet<int> { piece.ID };
            if (piece.WeakVariant != null)
            {
                ids.Add(piece.WeakVariant.ID);
            }
            decor.Ids[role] = ids;
            decor.DefaultId[role] = piece.ID;
        }

        /// <summary>
        /// The appliance IDs this class keeps fitted, for the current run's setting. SceneryBulldozer
        /// and the occupancy rule leave these to the sync.
        /// </summary>
        public static HashSet<int> ManagedIds(EntityManager em) =>
            new HashSet<int>(Active(em).Ids.Values.SelectMany(ids => ids));

        /// <summary>Where the decorators put each role's pieces for this layout (StreetDecorator / TrainStationDecorator.Decorate).</summary>
        private static List<(Role role, Vector3 position)> Pattern(Decor decor, LayoutBlueprint bp)
        {
            Bounds b = bp.GetBounds(); // tile coordinates; y is world z, as in Decorate
            float minX = b.min.x, maxX = b.max.x, minY = b.min.y, maxY = b.max.y;
            var points = new List<(Role, Vector3)>();
            float ring = YardRing.Active ? YardRing.Width : 0f; // the street's side and back buildings stand back
            if (decor.HasStreet)
            {
                if (decor.Ids.ContainsKey(Role.Street))
                {
                    // Vanilla's street runs from -20 to 20. It's extended (same spacing and parity)
                    // when the restaurant reaches past that, so its front still faces a street.
                    int from = MinPlacementPos, to = MaxPlacementPos;
                    while (from > minX - 2f)
                    {
                        from -= 2;
                    }
                    while (to < maxX + 2f)
                    {
                        to += 2;
                    }
                    for (int i = from; i < to; i += 2)
                    {
                        points.Add((Role.Street, new Vector3(i, 0f, minY - 1.5f)));
                    }
                }
                for (float x = minX - 1.5f - ring; x > MinPlacementPos; x -= 2f)
                {
                    AddSide(decor, points, x, minY - 0.5f);
                }
                for (float x = maxX + 1.5f + ring; x < MaxPlacementPos; x += 2f)
                {
                    AddSide(decor, points, x, minY - 0.5f);
                }
                if (decor.Ids.ContainsKey(Role.InternalWall))
                {
                    for (float x = minX - 1f - ring; x <= maxX + 1f + ring; x += 2f)
                    {
                        points.Add((Role.InternalWall, new Vector3(x, 0f, maxY + 0.65f + ring)));
                    }
                }
            }
            Vector3 door = bp.GetFrontDoor();
            if (decor.Ids.ContainsKey(Role.GapBorder))
            {
                // CountrysideDecorator / WitchDecorator: the border, with a gap at the door and none in front of a garden.
                for (float x = minX; x <= maxX; x += decor.GapSpacing)
                {
                    if (!(Mathf.Abs(x - door.x) < 0.7f) && bp[(int)x, (int)minY].Type != RoomType.Garden)
                    {
                        points.Add((Role.GapBorder, new Vector3(x, 0f, minY - 0.7f)));
                    }
                }
            }
            if (decor.Ids.ContainsKey(Role.HalloweenWall))
            {
                // HalloweenDecorator: the front wall between its pillars, with a gap at the door.
                for (float x = minX; x <= maxX + 1f; x += decor.HalloweenSpacing)
                {
                    if (!(Mathf.Abs(x - door.x) < 0.7f) && !(x >= maxX + decor.HalloweenSpacing / 2f))
                    {
                        points.Add((Role.HalloweenWall, new Vector3(x, 0f, minY - 0.7f)));
                    }
                }
            }
            if (decor.Ids.ContainsKey(Role.CobblePath))
            {
                // CountrysideDecorator: the path runs from x = -20 to the door.
                for (float x = PathStart; x <= door.x; x += 0.8f)
                {
                    points.Add((Role.CobblePath, new Vector3(x, 0f, minY - 1.2f)));
                }
            }
            if (decor.Ids.ContainsKey(Role.FrontBorder))
            {
                Vector3 frontDoor = door;
                for (float x = minX; x <= maxX; x += decor.BorderSpacing)
                {
                    if (!(Mathf.Abs(x - frontDoor.x) < 0.7f) && bp[(int)x, (int)minY].Type != RoomType.Garden)
                    {
                        points.Add((Role.FrontBorder, new Vector3(x, 0f, minY - 0.5f)));
                    }
                }
            }
            return points;
        }

        private static void AddSide(Decor decor, List<(Role, Vector3)> points, float x, float z)
        {
            if (decor.Ids.ContainsKey(Role.Wall))
            {
                points.Add((Role.Wall, new Vector3(x, 0f, z)));
            }
            if (decor.Ids.ContainsKey(Role.InternalWall))
            {
                points.Add((Role.InternalWall, new Vector3(x, 0f, z)));
            }
        }

        /// <summary>
        /// Whether a piece stands on a row these decorators use: the street (front - 1.5), the
        /// front (front - 0.5), or, for internal wall pieces, a back row (some row + 0.65). The
        /// front row never moves (the street side can't change), so this holds for pieces placed
        /// from the original bounds too. It keeps the sync away from the same appliance placed
        /// anywhere else.
        /// </summary>
        private static bool OnManagedRow(Role role, Vector3 p, float minY)
        {
            bool front = Mathf.Abs(p.z - (minY - 0.5f)) < Tolerance;
            switch (role)
            {
                case Role.Street:
                    return Mathf.Abs(p.z - (minY - 1.5f)) < Tolerance;
                case Role.InternalWall:
                    float frac = p.z - Mathf.Floor(p.z);
                    return front || Mathf.Abs(frac - 0.65f) < Tolerance;
                case Role.GapBorder:
                case Role.HalloweenWall:
                    return Mathf.Abs(p.z - (minY - 0.7f)) < Tolerance;
                case Role.CobblePath:
                    return Mathf.Abs(p.z - (minY - 1.2f)) < Tolerance;
                default:
                    return front;
            }
        }

        /// <summary>Moves the managed pieces to the pattern for <paramref name="bp"/>.</summary>
        public static (int removed, int added) Sync(EntityManager em, LayoutBlueprint bp)
        {
            LastSync = (0, 0);
            Decor decor = Active(em);
            if (!decor.Any)
            {
                return LastSync;
            }

            float minY = bp.GetBounds().min.y;
            var mine = new List<(Entity e, Role role, int id, Vector3 p)>();
            EntityQuery q = em.CreateEntityQuery(new EntityQueryDesc
            {
                All = new[] { ComponentType.ReadOnly<CAppliance>(), ComponentType.ReadOnly<CPosition>() },
                None = new[] { ComponentType.ReadOnly<CHeldBy>() },
            });
            using (NativeArray<Entity> entities = q.ToEntityArray(Allocator.Temp))
            using (NativeArray<CAppliance> appliances = q.ToComponentDataArray<CAppliance>(Allocator.Temp))
            using (NativeArray<CPosition> positions = q.ToComponentDataArray<CPosition>(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Role? role = decor.RoleOf(appliances[i].ID);
                    if (role.HasValue && OnManagedRow(role.Value, positions[i].Position, minY))
                    {
                        mine.Add((entities[i], role.Value, appliances[i].ID, positions[i].Position));
                    }
                }
            }
            q.Dispose();
            if (mine.Count == 0)
            {
                return LastSync; // none in the world (another mod removed them, or an old save): leave it alone
            }

            // Create new pieces with the variant this run already uses.
            var createId = new Dictionary<Role, int>(decor.DefaultId);
            foreach ((Entity _, Role role, int id, Vector3 _) in mine)
            {
                createId[role] = id;
            }

            List<(Role role, Vector3 position)> pattern = Pattern(decor, bp);
            bool[] used = Match(pattern, mine.Select(m => (m.role, m.p)).ToList(), out List<int> unmatched);
            int removed = 0, added = 0;
            foreach (int i in unmatched)
            {
                em.DestroyEntity(mine[i].e);
                removed++;
            }
            for (int i = 0; i < pattern.Count; i++)
            {
                if (!used[i])
                {
                    Entity e = em.CreateEntity();
                    em.AddComponentData(e, new CCreateAppliance { ID = createId[pattern[i].role] });
                    em.AddComponentData(e, new CPosition(pattern[i].position));
                    added++;
                }
            }
            if (removed + added > 0)
            {
                ModMain.Log($"Fitted the street-front decorations to the restaurant: removed {removed}, added {added}.");
            }
            LastSync = (removed, added);
            return LastSync;
        }

        /// <summary>Pairs each piece with a free pattern spot of its role; returns which spots are taken and which pieces had none.</summary>
        private static bool[] Match(List<(Role role, Vector3 position)> pattern, List<(Role role, Vector3 p)> pieces, out List<int> unmatched)
        {
            var used = new bool[pattern.Count];
            unmatched = new List<int>();
            for (int j = 0; j < pieces.Count; j++)
            {
                int match = -1;
                for (int i = 0; i < pattern.Count && match < 0; i++)
                {
                    if (!used[i] && pattern[i].role == pieces[j].role
                        && Mathf.Abs(pattern[i].position.x - pieces[j].p.x) < Tolerance
                        && Mathf.Abs(pattern[i].position.z - pieces[j].p.z) < Tolerance)
                    {
                        match = i;
                    }
                }
                if (match >= 0)
                {
                    used[match] = true;
                }
                else
                {
                    unmatched.Add(j);
                }
            }
            return used;
        }

#if DEBUG
        /// <summary>
        /// For the self-test: of these appliances (placed or pending), how many are managed pieces,
        /// how many of those are off the pattern for this layout, and how many pattern spots are empty.
        /// </summary>
        public static (int pieces, int offPattern, int missing, string roles) TestCheck(EntityManager em, IEnumerable<(int id, Vector3 p)> appliances, LayoutBlueprint bp)
        {
            Decor decor = Active(em);
            float minY = bp.GetBounds().min.y;
            var mine = new List<(Role role, Vector3 p)>();
            foreach ((int id, Vector3 p) in appliances)
            {
                Role? role = decor.RoleOf(id);
                if (role.HasValue && OnManagedRow(role.Value, p, minY))
                {
                    mine.Add((role.Value, p));
                }
            }
            string roles = string.Join(", ", mine.GroupBy(m => m.role).Select(g => $"{g.Key} {g.Count()}"));
            if (mine.Count == 0)
            {
                return (0, 0, 0, roles);
            }
            List<(Role role, Vector3 position)> pattern = Pattern(decor, bp);
            bool[] used = Match(pattern, mine, out List<int> unmatched);
            return (mine.Count, unmatched.Count, used.Count(u => !u), roles);
        }

        /// <summary>For the self-test: the run's setting, and which roles it manages.</summary>
        public static string TestDescribe(EntityManager em)
        {
            Decor decor = Active(em);
            return decor.Any ? string.Join(", ", decor.Ids.Keys) : "none";
        }
#endif
    }
}

namespace EasyFloorPlanner
{
    /// <summary>Marks that the street-front decorations were checked since this run was loaded (never saved).</summary>
    public struct CStreetPiecesChecked : KitchenMods.IModComponent
    {
    }

    /// <summary>
    /// Once per loaded run, on its first night: fits the street-front decorations to the layout.
    /// This repairs runs whose restaurant was grown before the sync existed, without waiting for
    /// the next edit (an Apply with no changes doesn't write). On an unedited restaurant the
    /// pieces already match, so it changes nothing.
    /// </summary>
    public class StreetPiecesLoadCheck : Kitchen.NightSystem, KitchenMods.IModSystem
    {
        private Unity.Entities.EntityQuery Checked;

        /// <summary>Result of the last load check (removed, added); null until one has run. For the self-test.</summary>
        public static (int removed, int added)? LastResult { get; private set; }

        protected override void Initialise()
        {
            base.Initialise();
            Checked = GetEntityQuery(typeof(CStreetPiecesChecked));
        }

        protected override void OnUpdate()
        {
            if (!Checked.IsEmpty || !Has<Kitchen.SKitchenMarker>() || !TryGetSingletonEntity<Kitchen.SLayout>(out Unity.Entities.Entity layout))
            {
                return;
            }
            EntityManager.CreateEntity(typeof(CStreetPiecesChecked), typeof(Kitchen.CDoNotPersist));
            LayoutBlueprint bp = LayoutWriter.Read(EntityManager, layout);
            YardRing.Refresh(bp); // before the sync: the street's side buildings depend on it
            LastResult = StreetPieces.Sync(EntityManager, bp);
        }
    }
}
