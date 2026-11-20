using System;
using System.Collections.Generic;
using HarmonyLib;
using Kitchen;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace EasyFloorPlanner
{
    /// <summary>
    /// Faster busy days in big restaurants (2026-11-19). Performance Monitor measured ~150 ms of game
    /// systems per frame at ~450 customers, led by these two vanilla systems. Both run on the host
    /// only (clients just draw), so these patches can't desync anyone.
    ///
    /// Background: the game moves items by rebuilding offers every frame. Proposal systems create a
    /// proposal entity per possible move, ~15 AcceptInto* systems turn proposals into acceptances,
    /// and pruning systems pick the winners. Nothing carries over between frames, so the cost is paid
    /// again every frame even when nothing has changed.
    /// </summary>
    internal static class CustomerOrderPerformance
    {
        private static bool PruneFailed, TakeFailed;

        // ------------------------------------------------------------------ A: PruneBuffetsByDistance

        /// <summary>
        /// Vanilla (~55 ms/frame at 450 customers): for every customer, it scans every acceptance in
        /// the restaurant with up to four component lookups per pair, though only that customer's own
        /// buffet acceptances can match. It prunes all of them, then restores the one whose buffet is
        /// nearest. This does the same with one pass over the acceptances, grouped by (group, member)
        /// key, then one pass over the customers. The result is identical:
        /// - the same snapshot (both read every value before writing anything);
        /// - the same writes in the same order (customers in query order, each customer's acceptances
        ///   in query order, the winner restored last), so even duplicate keys end the same way;
        /// - the same tie-breaking (the first acceptance at the smallest distance wins);
        /// - the same quirk: vanilla compares the group of COrderAcceptance even when the acceptance
        ///   only has CPartialOrderAcceptance (whose group it never reads), so a partial acceptance's
        ///   group is Entity.Null here too.
        /// If anything fails before writing, vanilla runs instead (and keeps running for the session).
        /// </summary>
        [HarmonyPatch(typeof(PruneBuffetsByDistance), "OnUpdate")]
        internal static class PrunePatch
        {
            private static bool Prefix(PruneBuffetsByDistance __instance)
            {
                if (PruneFailed)
                {
                    return true;
                }
                List<(Entity entity, CItemTransferAccept value)> writes;
                try
                {
                    writes = PlanPrune(__instance);
                }
                catch (Exception e)
                {
                    PruneFailed = true;
                    ModMain.LogError($"Faster buffet pruning failed; using the game's own from now on: {e}");
                    return true;
                }
                using var ctx = new EntityContext(__instance.EntityManager);
                foreach ((Entity entity, CItemTransferAccept value) in writes)
                {
                    ctx.Set(entity, value);
                }
                return false;
            }
        }

        private struct Candidate
        {
            public Entity Entity;
            public CItemTransferAccept Value;
            public Vector3 SourceForward;
        }

        private static readonly Dictionary<(Entity group, int member), List<Candidate>> Buckets = new Dictionary<(Entity, int), List<Candidate>>();
        private static readonly Stack<List<Candidate>> Spare = new Stack<List<Candidate>>();

        internal static List<(Entity, CItemTransferAccept)> PlanPrune(PruneBuffetsByDistance system)
        {
            EntityManager em = system.EntityManager;
            foreach (List<Candidate> l in Buckets.Values)
            {
                l.Clear();
                Spare.Push(l);
            }
            Buckets.Clear();
            var writes = new List<(Entity, CItemTransferAccept)>();

            using (NativeArray<Entity> accepts = system.Acceptances.ToEntityArray(Allocator.Temp))
            using (NativeArray<CItemTransferAccept> acceptValues = system.Acceptances.ToComponentDataArray<CItemTransferAccept>(Allocator.Temp))
            {
                for (int a = 0; a < accepts.Length; a++)
                {
                    Entity e = accepts[a];
                    CItemTransferAccept value = acceptValues[a];
                    if (value.Status == ItemAcceptStatus.Pruned)
                    {
                        continue;
                    }
                    bool order = Has<COrderAcceptance>(em, e);
                    COrderAcceptance oa = order ? em.GetComponentData<COrderAcceptance>(e) : default;
                    int member = order ? oa.MemberIndex
                        : Has<CPartialOrderAcceptance>(em, e) ? em.GetComponentData<CPartialOrderAcceptance>(e).MemberIndex : -1;
                    if (!Has<CItemTransferProposal>(em, value.Proposal))
                    {
                        continue;
                    }
                    CItemTransferProposal proposal = em.GetComponentData<CItemTransferProposal>(value.Proposal);
                    if ((proposal.Flags & TransferFlags.Buffet) == 0 || !Has<CPosition>(em, proposal.Source))
                    {
                        continue;
                    }
                    var key = (oa.Group, member);
                    if (!Buckets.TryGetValue(key, out List<Candidate> list))
                    {
                        list = Spare.Count > 0 ? Spare.Pop() : new List<Candidate>();
                        Buckets[key] = list;
                    }
                    list.Add(new Candidate { Entity = e, Value = value, SourceForward = em.GetComponentData<CPosition>(proposal.Source).ForwardPosition });
                }
            }

            using (NativeArray<CPosition> positions = system.BuffetUsers.ToComponentDataArray<CPosition>(Allocator.Temp))
            using (NativeArray<CBelongsToGroup> groups = system.BuffetUsers.ToComponentDataArray<CBelongsToGroup>(Allocator.Temp))
            {
                for (int u = 0; u < positions.Length; u++)
                {
                    if (!Buckets.TryGetValue((groups[u].Group, groups[u].IndexInGroup), out List<Candidate> list))
                    {
                        continue;
                    }
                    float best = 99999f;
                    int winner = -1;
                    for (int i = 0; i < list.Count; i++)
                    {
                        CItemTransferAccept pruned = list[i].Value;
                        pruned.Status = ItemAcceptStatus.Pruned;
                        pruned.PrunedBy = system;
                        writes.Add((list[i].Entity, pruned));
                        float d = (list[i].SourceForward - positions[u].Position).sqrMagnitude;
                        if (winner < 0 || d < best)
                        {
                            winner = i;
                            best = d;
                        }
                    }
                    if (winner >= 0)
                    {
                        writes.Add((list[winner].Entity, list[winner].Value));
                    }
                }
            }
            return writes;
        }

        private static bool Has<T>(EntityManager em, Entity e) where T : struct, IComponentData => e != Entity.Null && em.Exists(e) && em.HasComponent<T>(e);

        // ------------------------------------------------------------------ B + D: TakeForCustomerOrders

        private static readonly AccessTools.FieldRef<TakeForCustomerOrders, EntityQuery> BuffetsRef = AccessTools.FieldRefAccess<TakeForCustomerOrders, EntityQuery>("Buffets");
        private static readonly AccessTools.FieldRef<TakeForCustomerOrders, EntityQuery> TableSetsRef = AccessTools.FieldRefAccess<TakeForCustomerOrders, EntityQuery>("TableSets");

        private static readonly Action<TakeForCustomerOrders, EntityContext, Entity, Entity, TransferFlags, Action<Entity>> CreateProposals =
            AccessTools.MethodDelegate<Action<TakeForCustomerOrders, EntityContext, Entity, Entity, TransferFlags, Action<Entity>>>(
                AccessTools.Method(typeof(TakeForCustomerOrders), "CreateProposals"));

        private static readonly List<CTableSetGrabPoints> GrabPoints = new List<CTableSetGrabPoints>();
        private static readonly HashSet<int> WantingMembers = new HashSet<int>();
        private static int Frame;

        /// <summary>
        /// Vanilla (~20 ms/frame, plus ~25 ms of AcceptInto* work on what it creates): for every
        /// occupied table it proposes every item on the table's grab points, and every buffet's item
        /// to every member of the group (up to 7 proposal kinds each), every frame.
        ///
        /// B (always on): the same, minus proposals nothing can accept. These proposals carry
        /// OnlyOrderSatisfaction, so only the three order acceptors can keep them
        /// (PruneOnlyOrderSatisfaction prunes every other acceptance of them), and each of those
        /// accepts only for a waiting item that's either not satisfied yet (AcceptIntoOrderSatisfaction,
        /// AcceptIntoPiecemealSatisfaction) or satisfied with a requested extra not yet given
        /// (AcceptIntoExtraSatisfaction), for the proposal's member if it names one. So a group
        /// with no such item gets no proposals, and a buffet proposal for a member with none is
        /// skipped. Audited 2026-11-19 against the decompiled game; the one way it could differ is a
        /// mod adding its own order acceptor.
        ///
        /// D (host setting, off by default): runs the whole step only every Nth frame, so customers
        /// take from tables and buffets up to N - 1 frames later.
        /// </summary>
        [HarmonyPatch(typeof(TakeForCustomerOrders), "OnUpdate")]
        internal static class TakePatch
        {
            private static bool Prefix(TakeForCustomerOrders __instance)
            {
                int every = Settings.OrderCheckEvery;
                if (every > 1 && (Frame++ % every) != 0)
                {
                    return false; // D
                }
                if (TakeFailed || CreateProposals == null)
                {
                    return true;
                }
                try
                {
                    RunTake(__instance);
                }
                catch (Exception e)
                {
                    // Proposals made before the error stay (vanilla's own would add to them this frame
                    // only); from the next frame vanilla runs alone.
                    TakeFailed = true;
                    ModMain.LogError($"Faster customer order checks failed; using the game's own from now on: {e}");
                }
                return false;
            }
        }

        internal static int TestSkippedGroups, TestSkippedMembers;

        private static void RunTake(TakeForCustomerOrders system)
        {
            EntityManager em = system.EntityManager;
            var ctx = new EntityContext(em);
            using NativeArray<Entity> buffets = BuffetsRef(system).ToEntityArray(Allocator.Temp);
            using NativeArray<Entity> tableSets = TableSetsRef(system).ToEntityArray(Allocator.Temp);
            foreach (Entity table in tableSets)
            {
                if (!Has<COccupiedByGroup>(em, table))
                {
                    continue;
                }
                Entity group = em.GetComponentData<COccupiedByGroup>(table).Group;
                // What the group can still receive (B). Without a waiting list, no acceptor accepts.
                WantingMembers.Clear();
                if (group != Entity.Null && em.Exists(group) && em.HasComponent<CWaitingForItem>(group))
                {
                    DynamicBuffer<CWaitingForItem> waiting = em.GetBuffer<CWaitingForItem>(group);
                    for (int w = 0; w < waiting.Length; w++)
                    {
                        CWaitingForItem item = waiting[w];
                        if (!item.Satisfied || (item.ExtraRequested && !item.ExtraSatisfied))
                        {
                            WantingMembers.Add(item.MemberIndex);
                        }
                    }
                }
                if (WantingMembers.Count == 0)
                {
                    TestSkippedGroups++;
                    continue;
                }
                if (em.HasComponent<CTableSetGrabPoints>(table))
                {
                    em.GetBuffer<CTableSetGrabPoints>(table).Cache(GrabPoints);
                    foreach (CTableSetGrabPoints grabPoint in GrabPoints)
                    {
                        CreateProposals(system, ctx, grabPoint.Entity, table, TransferFlags.OnlyOrderSatisfaction, null);
                    }
                }
                if (!em.HasComponent<CGroupMember>(group))
                {
                    continue;
                }
                int length = em.GetBuffer<CGroupMember>(group).Length;
                foreach (Entity buffet in buffets)
                {
                    for (int i = 0; i < length; i++)
                    {
                        if (!WantingMembers.Contains(i))
                        {
                            TestSkippedMembers++;
                            continue;
                        }
                        int member = i;
                        CreateProposals(system, ctx, buffet, table, TransferFlags.OnlyOrderSatisfaction | TransferFlags.Buffet, prop =>
                        {
                            ctx.Set(prop, new CProposalForMember { MemberIndex = member });
                        });
                    }
                }
            }
        }
    }
}
