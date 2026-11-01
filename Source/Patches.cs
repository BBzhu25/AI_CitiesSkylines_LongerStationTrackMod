using ColossalFramework;
using ColossalFramework.Math;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace LongerStationTrackMod
{
    /// <summary>
    /// Long platform asset repair:
    /// Long platform assets from the Workshop often lack the m_stopType=Train
    /// flag on their pedestrian/train lanes, which prevents line stops from
    /// anchoring to the platform track. When the player successfully places a
    /// stop on a long platform (>180m), this patch adds the Train stop flag to
    /// the pedestrian/train lanes of that track's NetInfo (each NetInfo is
    /// fixed only once).
    /// </summary>
    [HarmonyPatch(typeof(TransportTool), "GetStopPosition")]
    public static class Patch_GetStopPosition
    {
        private static readonly HashSet<string> _fixedNetInfos = new HashSet<string>();

        static void Postfix(bool __result, ushort segment)
        {
            try
            {
                if (!__result || segment == 0)
                    return;

                NetSegment seg = Singleton<NetManager>.instance.m_segments.m_buffer[segment];
                if (seg.m_averageLength <= 180f)
                    return;

                NetInfo info = seg.Info;
                if (info == null || info.m_lanes == null)
                    return;

                bool changed = false;
                for (int i = 0; i < info.m_lanes.Length; i++)
                {
                    NetInfo.Lane lane = info.m_lanes[i];
                    if (lane == null)
                        continue;

                    bool isPedestrian = lane.m_laneType == NetInfo.LaneType.Pedestrian;
                    bool isTrainLane = (lane.m_vehicleType & VehicleInfo.VehicleType.Train) != VehicleInfo.VehicleType.None;
                    if (!isPedestrian && !isTrainLane)
                        continue;

                    // Pedestrian lanes of a platform track must also carry
                    // vehicleType=Train: vanilla station platforms do, workshop
                    // long platforms often don't. Without it, FindPathPosition
                    // with laneTypes=Pedestrian + vehicleTypes=Train (used both
                    // by player line creation AND by intercity trains to anchor
                    // their route end) can never match this lane.
                    if (isPedestrian &&
                        (lane.m_vehicleType & VehicleInfo.VehicleType.Train) == VehicleInfo.VehicleType.None)
                    {
                        lane.m_vehicleType |= VehicleInfo.VehicleType.Train;
                        changed = true;
                    }

                    if ((lane.m_stopType & VehicleInfo.VehicleType.Train) == VehicleInfo.VehicleType.None)
                    {
                        lane.m_stopType |= VehicleInfo.VehicleType.Train;
                        changed = true;
                    }
                }

                if (changed && _fixedNetInfos.Add(info.name))
                {
                    Debug.Log("[LongerStationTrack] Repaired missing train stop flag on track asset: " + info.name);
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[LongerStationTrack] GetStopPosition patch exception: " + ex);
            }
        }
    }

    /// <summary>
    /// Core fix (main cause of line creation failures on long platforms):
    /// the segment-level geometric filter in PathManager.FindPathPosition (20-param
    /// overload) - the query box is position +/- (maxDistance*2) expanded by 64m
    /// on each axis. When both end nodes of a track segment fall outside the box,
    /// the entire segment is skipped. On extra-long platforms (>215m) the stop
    /// point sits in the middle of the segment, both end nodes are outside the
    /// box, so the segment is skipped, lane anchoring fails and the game reports
    /// "cannot find path".
    /// Fix: replace the end-node check's bge.un with pop; pop (stack balanced),
    /// so the skip never happens. Safe because m_bounds.Intersects and an exact
    /// distance check still run afterwards.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_FindPathPosition_LongSegmentSkip
    {
        static MethodBase TargetMethod()
        {
            MethodInfo[] methods = typeof(PathManager).GetMethods(AccessTools.all);
            foreach (MethodInfo m in methods)
            {
                if (m.Name != "FindPathPosition")
                    continue;
                if (m.GetParameters().Length == 20)
                    return m;
            }
            return null;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int patched = 0;
            // Vanilla pattern:
            //   ldloc.s A ; ldc.r4 0 ; blt OK
            //   ldloc.s B ; ldc.r4 0 ; bge.un SKIP
            //   OK:
            // Replace bge.un with pop; pop -> pops B and 0, falls through to OK,
            // so the whole segment is never skipped.
            for (int i = 3; i + 3 <= list.Count; i++)
            {
                if (list[i - 1].opcode == OpCodes.Blt
                    && list[i - 3].opcode == OpCodes.Ldloc_S
                    && list[i - 2].opcode == OpCodes.Ldc_R4
                    && IsZero(list[i - 2].operand)
                    && list[i].opcode == OpCodes.Ldloc_S
                    && list[i + 1].opcode == OpCodes.Ldc_R4
                    && IsZero(list[i + 1].operand)
                    && list[i + 2].opcode == OpCodes.Bge_Un)
                {
                    List<CodeInstruction> replacement = new List<CodeInstruction>
                    {
                        new CodeInstruction(OpCodes.Pop),
                        new CodeInstruction(OpCodes.Pop)
                    };
                    list.RemoveRange(i + 2, 1);
                    list.InsertRange(i + 2, replacement);
                    patched++;
                    i += 4;
                }
            }
            Debug.Log("[LongerStationTrack] FindPathPosition long-segment skip filter disabled (patch points=" + patched + ")");
            return list;
        }

        private static bool IsZero(object operand)
        {
            if (operand is float)
                return (float)operand == 0f;
            if (operand is double)
                return (double)operand == 0.0;
            return false;
        }
    }

    /// <summary>
    /// StartPathFind fix: relaxes the laneTypes argument of the two
    /// FindPathPosition stop-anchoring queries from 2 (Pedestrian) to
    /// 3 (Pedestrian|Vehicle), so track lanes can also serve as stop anchors.
    /// Normal platforms still prefer pedestrian lanes; the actual stop lane is
    /// ultimately validated by GetStopLane based on the vehicle type.
    /// </summary>
    [HarmonyPatch(typeof(TransportLineAI), "StartPathFind")]
    public static class Patch_StartPathFind
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int patched = 0;
            // Vanilla pattern: ldc.i4.2 (Pedestrian), ldc.i4.0 (VehicleType.None),
            // ldc.i4.0, conv.i8 (VehicleCategory) - appears before both
            // FindPathPosition calls; laneTypes=2 -> 3
            for (int i = 0; i + 3 < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Ldc_I4_2
                    && list[i + 1].opcode == OpCodes.Ldc_I4_0
                    && list[i + 2].opcode == OpCodes.Ldc_I4_0
                    && list[i + 3].opcode == OpCodes.Conv_I8)
                {
                    list[i] = new CodeInstruction(OpCodes.Ldc_I4_3);
                    patched++;
                    i += 3;
                }
            }
            Debug.Log("[LongerStationTrack] StartPathFind stop-anchoring lane types relaxed (patch points=" + patched + ")");
            return list;
        }
    }

    /// <summary>
    /// Shared lookup for the VehicleManager per-frame SimulationStep entry point
    /// of a vehicle AI type: SimulationStep(ushort, ref Vehicle, ref Frame,
    /// ushort, ref Vehicle, int).
    /// </summary>
    internal static class SimStepLookup
    {
        // NOTE: plain reflection only — CitiesHarmony ships Harmony 2.0.4 at
        // runtime and the AccessTools.DeclaredMethod(type,name,types) overload
        // does not exist there (it was added in Harmony 2.1).
        internal static MethodBase FindSimulationStep(System.Type aiType)
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;
            Type vehicleRef = typeof(Vehicle).MakeByRefType();
            foreach (MethodInfo mi in aiType.GetMethods(All))
            {
                if (mi.Name != "SimulationStep")
                    continue;
                ParameterInfo[] p = mi.GetParameters();
                // NOTE: use Type.Equals, never "==" — the game runs on old Mono
                // (~.NET 3.5) which has no System.Type.op_Equality; comparing
                // types with "==" compiles to that missing method against the
                // v4.8 reference assemblies and throws MissingMethodException.
                if (p.Length == 6 &&
                    p[0].ParameterType.Equals(typeof(ushort)) &&
                    p[1].ParameterType.Equals(vehicleRef) &&
                    // Frame is the nested type Vehicle.Frame. NOTE: by-ref types
                    // include the "&" marker in .Name ("Frame&"), so unwrap the
                    // element type first. IsByRef == true guarantees the element
                    // type exists; do NOT write "GetElementType() != null" — a
                    // Type!=null comparison compiles to System.Type.op_Inequality,
                    // which does not exist in the game's old Mono (~.NET 3.5).
                    p[2].ParameterType.IsByRef &&
                    p[2].ParameterType.GetElementType().Name == "Frame" &&
                    p[3].ParameterType.Equals(typeof(ushort)) &&
                    p[4].ParameterType.Equals(vehicleRef) &&
                    p[5].ParameterType.Equals(typeof(int)))
                    return mi;
            }
            return null;
        }
    }

    /// <summary>
    /// One-shot load-time repair trigger. The first vehicle simulation step
    /// after a level loads is the earliest point where every building/NetInfo
    /// prefab is guaranteed available, so the whole repair suite runs from here
    /// exactly once per load. Kept separate from the (Debug-only) diagnostic
    /// patches so the Release build still performs all repairs.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_LoadRepairTrigger
    {
        static MethodBase TargetMethod()
        {
            return SimStepLookup.FindSimulationStep(typeof(TrainAI));
        }

        static void Postfix()
        {
            LoadRepairs.Run();
        }
    }

    /// <summary>
    /// Load-time repair suite for already-placed stations and Workshop track
    /// assets. All repairs reuse the game's own logic (no counters, no forcing)
    /// so that the vanilla simulation treats long platforms like vanilla ones.
    /// </summary>
    internal static class LoadRepairs
    {
        private static bool _ran;

        internal static void Run()
        {
            if (_ran)
                return;
            _ran = true;
            try
            {
                RepairAllStationTracks();
                RepairStationPrefabs();
                RepairStationConnections();
                RepairStationSegmentFlags();
                RepairStationLanePortals();
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[LongerStationTrack] load-time repair exception: " + ex);
            }
        }

        /// <summary>
        /// One-time load repair: for every loaded NetInfo that is a platform
        /// track (has BOTH a train-vehicle lane AND pedestrian lanes), give the
        /// pedestrian lanes vehicleType=Train and stopType=Train when missing.
        /// Vanilla station platforms ship with these flags; many workshop long
        /// platforms don't, which blocks both player line stops and intercity
        /// trains from anchoring on the platform.
        /// Runs at the first vehicle simulation step after the level loaded,
        /// when all prefabs are available.
        /// </summary>
        private static void RepairAllStationTracks()
        {
            try
            {
                int count = PrefabCollection<NetInfo>.LoadedCount();
                int repairedNets = 0;
                for (uint i = 0; i < (uint)count; i++)
                {
                    NetInfo net = PrefabCollection<NetInfo>.GetLoaded(i);
                    if (net == null || net.m_lanes == null)
                        continue;

                    bool hasTrainLane = false;
                    bool touched = false;
                    for (int l = 0; l < net.m_lanes.Length; l++)
                    {
                        NetInfo.Lane lane = net.m_lanes[l];
                        if (lane == null)
                            continue;
                        if ((lane.m_laneType & NetInfo.LaneType.Vehicle) != NetInfo.LaneType.None &&
                            (lane.m_vehicleType & VehicleInfo.VehicleType.Train) != VehicleInfo.VehicleType.None)
                            hasTrainLane = true;
                    }
                    if (!hasTrainLane)
                        continue;

                    for (int l = 0; l < net.m_lanes.Length; l++)
                    {
                        NetInfo.Lane lane = net.m_lanes[l];
                        if (lane == null)
                            continue;
                        if (lane.m_laneType != NetInfo.LaneType.Pedestrian)
                            continue;
                        bool changed = false;
                        if ((lane.m_vehicleType & VehicleInfo.VehicleType.Train) == VehicleInfo.VehicleType.None)
                        {
                            lane.m_vehicleType |= VehicleInfo.VehicleType.Train;
                            changed = true;
                        }
                        if ((lane.m_stopType & VehicleInfo.VehicleType.Train) == VehicleInfo.VehicleType.None)
                        {
                            lane.m_stopType |= VehicleInfo.VehicleType.Train;
                            changed = true;
                        }
                        if (changed)
                            touched = true;
                    }

                    if (touched)
                    {
                        repairedNets++;
                        Debug.Log("[LongerStationTrack] Load-time repair: pedestrian lanes of platform track '" + net.name + "' now accept trains (vehicleType/stopType += Train)");
                    }
                }
                Debug.Log("[LongerStationTrack] Load-time platform track repair finished, nets repaired: " + repairedNets);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[LongerStationTrack] load-time platform track repair exception: " + ex);
            }
        }

        /// <summary>
        /// Station prefab repair: TransportStationAI.m_transportLineInfo is
        /// hard-required by the intercity connection system — ProduceGoods
        /// skips the station and CreateOutgoingVehicle aborts when it is null,
        /// silently disabling ALL intercity traffic for that station. Some
        /// Workshop station assets leave it unset. When the station is a train
        /// station (m_transportInfo is a Train TransportInfo) but
        /// m_transportLineInfo is null, assign the vanilla "Train Line" NetInfo
        /// prefab so the station behaves like a vanilla train station.
        /// NOTE: connection nodes/segments are created when the building is
        /// PLACED, so an already-placed affected station must be rebuilt
        /// (bulldoze + repaste) to pick the fix up.
        /// </summary>
        private static void RepairStationPrefabs()
        {
            try
            {
                NetInfo trainLine = null;
                int netCount = PrefabCollection<NetInfo>.LoadedCount();
                for (uint i = 0; i < (uint)netCount; i++)
                {
                    NetInfo net = PrefabCollection<NetInfo>.GetLoaded(i);
                    if (net != null && net.name == "Train Line")
                    {
                        trainLine = net;
                        break;
                    }
                }
                if (trainLine == null)
                {
                    Debug.LogError("[LongerStationTrack] Load-time repair: vanilla 'Train Line' NetInfo not found, station prefab repair skipped");
                    return;
                }

                int count = PrefabCollection<BuildingInfo>.LoadedCount();
                int repaired = 0;
                for (uint i = 0; i < (uint)count; i++)
                {
                    BuildingInfo bi = PrefabCollection<BuildingInfo>.GetLoaded(i);
                    if (bi == null || bi.m_buildingAI == null)
                        continue;
                    TransportStationAI station = bi.m_buildingAI as TransportStationAI;
                    if (station == null || station.m_transportLineInfo != null)
                        continue;
                    // Only repair actual train stations, not metro/ship/etc.
                    if (station.m_transportInfo == null ||
                        station.m_transportInfo.m_vehicleType != VehicleInfo.VehicleType.Train)
                        continue;

                    station.m_transportLineInfo = trainLine;
                    repaired++;
                    Debug.Log("[LongerStationTrack] Load-time repair: station prefab '" + bi.name
                        + "' had no transportLineInfo; assigned vanilla 'Train Line' (rebuild placed copies of this station so its connection nodes are recreated)");
                }
                Debug.Log("[LongerStationTrack] Load-time station prefab repair finished, stations repaired: " + repaired);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[LongerStationTrack] station prefab repair exception: " + ex);
            }
        }

        /// <summary>
        /// Station connection node rebuild (fixes already-placed stations):
        /// TransportStationAI.CreateConnectionLines - which creates the station's
        /// connection node (Building.m_netNode) and its connection segments - is
        /// only called when the building is PLACED, RELOCATED or set to emptying.
        /// It never runs on save load. TransportStationAI.ProduceGoods, which
        /// spawns ALL intercity incoming/outgoing vehicles, bails out
        /// immediately when m_netNode == 0 or m_transportLineInfo == null, so a
        /// station whose connection setup failed at placement (e.g. a Workshop
        /// asset whose m_transportLineInfo was null until our prefab repair ran)
        /// receives NO intercity traffic at all until it is bulldozed and
        /// rebuilt. This repair invokes CreateConnectionLines again for such
        /// stations at load time, making the vanilla connection logic run
        /// against the now-repaired prefab.
        /// </summary>
        private static void RepairStationConnections()
        {
            try
            {
                const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;
                MethodInfo ccl = typeof(TransportStationAI).GetMethod(
                    "CreateConnectionLines", All, null,
                    new Type[] { typeof(ushort), typeof(Building).MakeByRefType() }, null);
                // NOTE: (object) cast - plain reference comparison. A direct
                // "ccl == null" on a MethodInfo compiles to
                // MethodInfo.op_Equality, which does not exist in the game's
                // old Mono runtime and throws MissingMethodException at JIT.
                if ((object)ccl == null)
                {
                    Debug.LogError("[LongerStationTrack] TransportStationAI.CreateConnectionLines(ushort, ref Building) not found, connection rebuild skipped");
                    return;
                }

                BuildingManager bm = Singleton<BuildingManager>.instance;
                int repaired = 0;
                for (ushort id = 1; id < bm.m_buildings.m_buffer.Length; id++)
                {
                    Building b = bm.m_buildings.m_buffer[id];
                    if ((b.m_flags & Building.Flags.Created) == Building.Flags.None)
                        continue;
                    if (b.m_netNode != 0)
                        continue; // connection chain exists - nothing to rebuild
                    BuildingInfo info = b.Info; // safe: building is created
                    if (info == null || info.m_buildingAI == null)
                        continue;
                    TransportStationAI station = info.m_buildingAI as TransportStationAI;
                    if (station == null || station.m_transportLineInfo == null)
                        continue;

                    // CreateConnectionLines bails out unless the building
                    // services incoming/outgoing connections. Stations whose
                    // connection setup failed at placement may miss these
                    // flags; restore them so the vanilla rebuild logic runs.
                    if ((b.m_flags & Building.Flags.IncomingOutgoing) != Building.Flags.IncomingOutgoing)
                    {
                        b.m_flags |= Building.Flags.IncomingOutgoing;
                        bm.m_buildings.m_buffer[id] = b;
                    }

                    object[] args = new object[] { id, b };
                    try
                    {
                        ccl.Invoke(station, args);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError("[LongerStationTrack] connection rebuild failed for station id=" + id
                            + " '" + info.name + "': " + ex.Message);
                        continue;
                    }
                    b = (Building)args[1];
                    bm.m_buildings.m_buffer[id] = b;
                    if (b.m_netNode != 0)
                    {
                        repaired++;
                        Debug.Log("[LongerStationTrack] Load-time repair: rebuilt connection lines for station id=" + id
                            + " '" + info.name + "', new netNode=" + b.m_netNode);
                    }
                }
                Debug.Log("[LongerStationTrack] Load-time station connection rebuild finished, stations repaired: " + repaired);
            }
            catch (Exception ex)
            {
                Debug.LogError("[LongerStationTrack] station connection rebuild exception: " + ex);
            }
        }

        /// <summary>
        /// Connection segment PathFailed repair (fixes already-placed stations):
        /// when a station's connection node is created at PLACEMENT time, the
        /// game immediately attempts the first incoming/outgoing path. If that
        /// attempt fails (e.g. because the platform track lanes did not accept
        /// trains yet - exactly what this mod repairs later), the
        /// NetSegment.Flags.PathFailed flag stays on the connection segment
        /// forever. TransportStationAI.ProduceGoods requires a
        /// startNode-matching segment WITHOUT PathFailed before it even starts
        /// counting its spawn timer (NetNode.m_maxWaitTime), so such a station
        /// is permanently silenced: no intercity trains ever spawn, even after
        /// the lanes are repaired, and it never recovers on its own. This
        /// repair clears PathFailed from every segment touching a placed train
        /// station's connection node, letting the vanilla spawn cycle resume.
        /// </summary>
        private static void RepairStationSegmentFlags()
        {
            try
            {
                NetManager nm = Singleton<NetManager>.instance;
                BuildingManager bm = Singleton<BuildingManager>.instance;

                // Collect the connection nodes of every placed train station.
                HashSet<ushort> nodes = new HashSet<ushort>();
                for (ushort id = 1; id < bm.m_buildings.m_buffer.Length; id++)
                {
                    Building b = bm.m_buildings.m_buffer[id];
                    if ((b.m_flags & Building.Flags.Created) == Building.Flags.None)
                        continue;
                    if (b.m_netNode == 0)
                        continue;
                    BuildingInfo info = b.Info; // safe: building is created
                    if (info == null || info.m_buildingAI == null)
                        continue;
                    if (!(info.m_buildingAI is TransportStationAI))
                        continue;
                    nodes.Add(b.m_netNode);
                }

                int cleared = 0;
                for (ushort s = 1; s < nm.m_segments.m_buffer.Length; s++)
                {
                    NetSegment seg = nm.m_segments.m_buffer[s];
                    if ((seg.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None)
                        continue;
                    if (!nodes.Contains(seg.m_startNode) && !nodes.Contains(seg.m_endNode))
                        continue;
                    if ((seg.m_flags & NetSegment.Flags.PathFailed) != NetSegment.Flags.None)
                    {
                        seg.m_flags &= ~NetSegment.Flags.PathFailed;
                        nm.m_segments.m_buffer[s] = seg;
                        cleared++;
                        Debug.Log("[LongerStationTrack] Load-time repair: cleared PathFailed on segment " + s
                            + " (nodes " + seg.m_startNode + "-" + seg.m_endNode + ")");
                    }
                }
                Debug.Log("[LongerStationTrack] Load-time connection segment repair finished, station nodes: "
                    + nodes.Count + ", PathFailed cleared: " + cleared);
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[LongerStationTrack] connection segment repair exception: " + ex);
            }
        }

        /// <summary>
        /// Station connection lane-portal repair (vanilla logic, no forcing).
        ///
        /// Intercity passenger flow reverse-engineered from IL:
        ///  - Station connection nodes are topological islands: they only own
        ///    the directed 'Train Line' connection segments between the
        ///    station (spawnPoint position) and the outside connection.
        ///  - Citizens can only enter/leave such an island through the
        ///    NetNode.m_lane "portal": TransportLineAI.UpdateLaneConnection
        ///    binds each connection node to the nearest valid lane (32 m,
        ///    service/level filtered) around its position, and
        ///    PathFind.ProcessItemMain uses node.m_lane to hop a citizen
        ///    spatially between the connection segment and that lane's
        ///    segment (ProcessItemPublicTransport pays the 3D distance).
        ///  - A citizen riding the connection lane waits at the outside
        ///    node; HumanAI.GetTransportWaitPosition raises that node's
        ///    m_maxWaitTime (BoredOfWaiting sets it to 255 directly), which
        ///    is exactly the gate TransportStationAI.ProduceGoods requires
        ///    before spawning an incoming/outgoing intercity train with
        ///    passengers on board.
        /// If a connection node's portal failed to form at placement time
        /// (e.g. the platform lanes did not pass the FindPathPosition filter
        /// back then), the broken m_lane state persists in the save: citizens
        /// can never complete a route through that station, nobody waits, and
        /// the station is permanently silent. Freshly placed vanilla stations
        /// work, which is why they look "randomly fine".
        ///
        /// Fix: re-run the vanilla TransportLineAI.UpdateLaneConnection on
        /// every node of every placed station's connection chain - exactly
        /// what NetManager does whenever such a segment updates. No counters,
        /// no forcing; the game's own logic rebuilds its own portal.
        /// </summary>
        private static void RepairStationLanePortals()
        {
            try
            {
                const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;
                MethodInfo ulc = typeof(NetAI).GetMethod(
                    "UpdateLaneConnection", All, null,
                    new Type[] { typeof(ushort), typeof(NetNode).MakeByRefType() }, null);
                // (object) cast: plain reference comparison. A direct "ulc == null"
                // compiles to MethodInfo.op_Equality which does not exist on the
                // game's old Mono runtime (MissingMethodException at JIT).
                if ((object)ulc == null)
                {
                    Debug.LogError("[LongerStationTrack] NetAI.UpdateLaneConnection(ushort, ref NetNode) not found, portal repair skipped");
                    return;
                }

                NetManager nm = Singleton<NetManager>.instance;
                BuildingManager bm = Singleton<BuildingManager>.instance;

                int stations = 0;
                int nodesVisited = 0;
                int portalsFixed = 0;
                for (ushort id = 1; id < bm.m_buildings.m_buffer.Length; id++)
                {
                    Building b = bm.m_buildings.m_buffer[id];
                    if ((b.m_flags & Building.Flags.Created) == Building.Flags.None)
                        continue;
                    if (b.m_netNode == 0)
                        continue;
                    BuildingInfo info = b.Info; // safe: building is created
                    if (info == null || info.m_buildingAI == null)
                        continue;
                    if (!(info.m_buildingAI is TransportStationAI))
                        continue;
                    stations++;

                    ushort node = b.m_netNode;
                    int guard = 0;
                    while (node != 0 && guard++ < 32768)
                    {
                        NetNode nn = nm.m_nodes.m_buffer[node];
                        if ((nn.m_flags & NetNode.Flags.Created) == NetNode.Flags.None)
                            break;
                        NetInfo nInfo = nn.Info;
                        string before = DescribePortal(nm, node, ref nn, nInfo);

                        object[] args = new object[] { node, nn };
                        ulc.Invoke(nInfo != null ? nInfo.m_netAI : null, args);

                        nn = (NetNode)args[1];
                        nm.m_nodes.m_buffer[node] = nn;
                        // Vanilla search may still leave portal=0: PathManager.FindPathPosition
                        // skips segments carrying Collapsed/Untouchable/End/Bend/Flooded flags,
                        // which on custom long platform geometry rejects the very platform
                        // pedestrian lanes the portal must bind to. Re-run the same search
                        // with the segment-flag filter dropped, then build the portal with
                        // the vanilla TransportLineAI.AddLaneConnection - the portal itself
                        // is still created by the game's own mechanism.
                        if (nn.m_lane == 0 && nInfo != null && nInfo.m_netAI is TransportLineAI)
                        {
                            bool fixedNow = FormPortalRelaxed(nm, node, ref nn);
                            nm.m_nodes.m_buffer[node] = nn;
                            if (fixedNow)
                                portalsFixed++;
                        }
                        string after = DescribePortal(nm, node, ref nn, nInfo);

                        if (before != after)
                        {
                            portalsFixed++;
                            Debug.Log("[LongerStationTrack] [PORTAL] station=" + id
                                + " '" + info.name + "' node=" + node + " pos=" + nn.m_position
                                + " BEFORE " + before + " -> AFTER " + after);
                        }
                        nodesVisited++;
                        node = nn.m_nextBuildingNode;
                    }
                }
                Debug.Log("[LongerStationTrack] Load-time lane portal repair finished, stations: " + stations
                    + ", chain nodes visited: " + nodesVisited + ", portals changed: " + portalsFixed);
            }
            catch (Exception ex)
            {
                Debug.LogError("[LongerStationTrack] lane portal repair exception: " + ex);
            }
        }

        /// <summary>
        /// Repeats the TransportLineAI.UpdateLaneConnection lane search for a
        /// node whose vanilla FindPathPosition scan found nothing, but without
        /// the segment-flag rejection (Collapsed/Untouchable/End/Bend/Flooded)
        /// and node-flag gates that wrongly exclude long-platform lanes. The
        /// portal itself is still built by the vanilla AddLaneConnection.
        /// </summary>
        private static bool FormPortalRelaxed(NetManager nm, ushort nodeID, ref NetNode nn)
        {
            try
            {
                TransportLineAI tlai = nn.Info.m_netAI as TransportLineAI;
                if (tlai == null)
                    return false;

                ItemClass.Service svc1 = tlai.m_netService;
                ItemClass.Service svc2 = tlai.m_secondaryNetService;
                NetInfo.LaneType wantLane = (NetInfo.LaneType)2; // Pedestrian - vanilla constant
                VehicleInfo.VehicleType wantStop = tlai.m_vehicleType;

                const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;
                MethodInfo alc = typeof(TransportLineAI).GetMethod(
                    "AddLaneConnection", All, null,
                    new Type[] { typeof(ushort), typeof(NetNode).MakeByRefType(),
                                 typeof(uint), typeof(byte) }, null);
                if ((object)alc == null)
                {
                    Debug.LogError("[LongerStationTrack] TransportLineAI.AddLaneConnection not found");
                    return false;
                }

                float bestSqr = 32f * 32f;
                ushort bestSeg = 0;
                uint bestLaneId = 0;
                byte bestOffset = 0;
                Vector3 pos = nn.m_position;

                for (ushort seg = 1; seg < nm.m_segments.m_buffer.Length; seg++)
                {
                    NetSegment s = nm.m_segments.m_buffer[seg];
                    if ((s.m_flags & NetSegment.Flags.Created) == NetSegment.Flags.None)
                        continue;
                    NetInfo sInfo = s.Info;
                    if (sInfo == null || sInfo.m_class == null || sInfo.m_lanes == null)
                        continue;
                    ItemClass.Service sSvc = sInfo.m_class.m_service;
                    if (!sSvc.Equals(svc1) && !sSvc.Equals(svc2))
                        continue;

                    uint laneId = s.m_lanes;
                    for (int li = 0; li < sInfo.m_lanes.Length && laneId != 0; li++)
                    {
                        NetInfo.Lane laneInfo = sInfo.m_lanes[li];
                        NetLane lane = nm.m_lanes.m_buffer[laneId];
                        if ((laneInfo.m_laneType & wantLane) != 0
                            && (wantStop == 0 || (laneInfo.m_stopType & wantStop) != 0))
                        {
                            Bezier3 bez = lane.m_bezier;
                            float bestTSqr = float.MaxValue;
                            float bestT = 0f;
                            for (int k = 0; k <= 16; k++)
                            {
                                float t = k / 16f;
                                float dSqr = (bez.Position(t) - pos).sqrMagnitude;
                                if (dSqr < bestTSqr) { bestTSqr = dSqr; bestT = t; }
                            }
                            if (bestTSqr < bestSqr)
                            {
                                bestSqr = bestTSqr;
                                bestSeg = seg;
                                bestLaneId = laneId;
                                bestOffset = (byte)Mathf.Clamp(Mathf.RoundToInt(bestT * 255f), 0, 255);
                            }
                        }
                        laneId = lane.m_nextLane;
                    }
                }

                if (bestSeg == 0 || bestLaneId == 0)
                    return false;

                object[] pargs = new object[] { nodeID, nn, bestLaneId, bestOffset };
                alc.Invoke(tlai, pargs);
                nn = (NetNode)pargs[1];
                NetSegment bs = nm.m_segments.m_buffer[bestSeg];
                Debug.Log("[LongerStationTrack] [PORTAL] relaxed search bound node=" + nodeID
                    + " pos=" + nn.m_position.ToString("F0")
                    + " -> lane" + bestLaneId + "@seg" + bestSeg + " '"
                    + (bs.Info != null ? bs.Info.name : "null")
                    + "' dist=" + Mathf.Sqrt(bestSqr).ToString("F1")
                    + " nodeLane=" + nn.m_lane);
                return nn.m_lane != 0;
            }
            catch (Exception ex)
            {
                Debug.LogError("[LongerStationTrack] relaxed portal exception node=" + nodeID + ": " + ex);
                return false;
            }
        }

        /// <summary>Short description of a node's lane portal for diffing.</summary>
        internal static string DescribePortal(NetManager nm, ushort nodeID, ref NetNode nn, NetInfo nInfo)
        {
            try
            {
                string aiName = "n/a";
                if (nInfo != null)
                    aiName = nInfo.m_netAI != null ? nInfo.m_netAI.GetType().Name : "nullAI";
                if (nn.m_lane == 0)
                    return "portal=0 ai=" + aiName;
                NetLane lane = nm.m_lanes.m_buffer[nn.m_lane];
                NetSegment seg = nm.m_segments.m_buffer[lane.m_segment];
                NetInfo segInfo = seg.Info;
                string segName = segInfo != null ? segInfo.name : "null";
                Vector3 lanePos = lane.CalculatePosition(0.5f);
                float dist = Vector3.Distance(lanePos, nn.m_position);
                return "portal=lane" + nn.m_lane + "@" + segName
                    + " dist=" + dist.ToString("F1") + " ai=" + aiName;
            }
            catch (Exception ex)
            {
                return "portalDescribeError:" + ex.Message;
            }
        }
    }

    /// <summary>
    /// Intercity train stop anchoring fix (root cause of "intercity trains do
    /// not stop at long platforms"):
    /// TrainAI.StartPathFind anchors the route endpoints of line-less trains
    /// (intercity, depot-spawned) with PathManager.FindPathPosition using
    /// laneType=Vehicle, vehicleType=Train and a hardcoded maxDistance of 32m
    /// around the route endpoint position. For intercity trains that endpoint
    /// is the station's connection node (Vehicle.m_targetBuilding is a NetNode
    /// id, resolved by PassengerTrainAI.StartPathFind), which TransportStationAI
    /// places via FindStopPosition - often offset from the actual platform rail
    /// on Workshop assets. When no rail lane lies within 32m of that node, the
    /// anchor fails, PassengerTrainAI.SetTarget unspawns the train, and the
    /// station never receives intercity traffic.
    /// Fix: raise the anchor radius 32m -> 128m (both anchor queries). Safe:
    /// FindPathPosition returns the CLOSEST matching lane, so existing behavior
    /// is unchanged whenever a lane was already found within 32m.
    /// </summary>
    [HarmonyPatch]
    public static class Patch_TrainStartPathFind_AnchorRadius
    {
        static MethodBase TargetMethod()
        {
            return FindStartPathFind();
        }

        /// <summary>
        /// Shared method lookup: TrainAI.StartPathFind has two overloads; select
        /// the full (ushort, ref Vehicle, Vector3, Vector3, bool, bool) one.
        /// </summary>
        internal static MethodBase FindStartPathFind()
        {
            // NOTE: plain reflection only — CitiesHarmony ships Harmony 2.0.4 at
            // runtime and the AccessTools.DeclaredMethod(type,name,types) overload
            // does not exist there (it was added in Harmony 2.1).
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                     BindingFlags.Instance | BindingFlags.Static;
            Type vehicleRef = typeof(Vehicle).MakeByRefType();
            foreach (MethodInfo mi in typeof(TrainAI).GetMethods(All))
            {
                if (mi.Name != "StartPathFind")
                    continue;
                ParameterInfo[] p = mi.GetParameters();
                if (p.Length == 6 &&
                    p[0].ParameterType.Equals(typeof(ushort)) &&
                    p[1].ParameterType.Equals(vehicleRef) &&
                    p[2].ParameterType.Equals(typeof(Vector3)) &&
                    p[3].ParameterType.Equals(typeof(Vector3)) &&
                    p[4].ParameterType.Equals(typeof(bool)) &&
                    p[5].ParameterType.Equals(typeof(bool)))
                    return mi;
            }
            return null;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int patched = 0;
            // The two FindPathPosition anchor queries push maxDistance with
            // "ldc.r4 32". The only other float constants in this method are
            // 100, 1.2 and 20000, so "ldc.r4 32" is unambiguous.
            foreach (CodeInstruction ci in list)
            {
                if (ci.opcode == OpCodes.Ldc_R4 && ci.operand is float && (float)ci.operand == 32f)
                {
                    ci.operand = 128f;
                    patched++;
                }
            }
            Debug.Log("[LongerStationTrack] Train StartPathFind intercity anchor radius 32->128 (patch points=" + patched + ")");
            return list;
        }
    }
}
