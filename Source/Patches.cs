using ColossalFramework;
using HarmonyLib;
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
}
