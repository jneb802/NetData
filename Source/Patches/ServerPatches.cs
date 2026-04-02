using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using HarmonyLib;
using NetData.Metrics;
using UnityEngine;

namespace NetData.Patches
{
    internal static class ServerPatches
    {
        private static readonly List<ZDO> TempSyncList = new();
        private static readonly System.Reflection.MethodInfo? CreateSyncListMethod =
            AccessTools.Method(typeof(ZDOMan), "CreateSyncList");

        // --- ZDOMan.SendZDOs prefix: per-peer ZDO queue depth ---
        // Applied manually in Plugin.cs because ZDOPeer is a private inner class.

        internal static void SendZDOsPrefix(ZDOMan __instance, object __0, bool __1, out SendZDOState __state)
        {
            __state = default;
            try
            {
                var zdoPeer = (ZDOMan.ZDOPeer)__0;
                long uid = zdoPeer.m_peer.m_uid;
                int queueDepth = zdoPeer.m_zdos.Count + zdoPeer.m_forceSend.Count + zdoPeer.m_invalidSector.Count;
                ServerMetrics.PeerZdoQueueDepths[uid] = queueDepth;

                int sendQueueBytes = zdoPeer.m_peer.m_socket.GetSendQueueSize();
                int headroomBytes = Mathf.Max(0, 10240 - sendQueueBytes);
                bool sendBlocked = !__1 && sendQueueBytes > 10240;
                bool lowHeadroom = headroomBytes < 2048;

                TempSyncList.Clear();
                CreateSyncListMethod?.Invoke(__instance, new object[] { zdoPeer, TempSyncList });

                var candidateAgesMs = new List<float>(TempSyncList.Count);
                float now = Time.time;
                foreach (var zdo in TempSyncList)
                {
                    if (zdoPeer.m_zdos.TryGetValue(zdo.m_uid, out var peerInfo))
                        candidateAgesMs.Add(Mathf.Max(0f, (now - peerInfo.m_syncTime) * 1000f));
                    else
                        candidateAgesMs.Add(0f);
                }

                __state = new SendZDOState(
                    uid,
                    sendQueueBytes,
                    headroomBytes,
                    sendBlocked,
                    lowHeadroom,
                    TempSyncList.Count,
                    candidateAgesMs,
                    ZDOMan.instance.GetSentZDOs()
                );
            }
            catch (Exception)
            {
                // Silently ignore if cast fails — private inner class may not be accessible
            }
        }

        internal static void SendZDOsPostfix(in SendZDOState __state)
        {
            if (__state.CandidateAgesMs == null) return;

            int sentCount = Mathf.Clamp(ZDOMan.instance.GetSentZDOs() - __state.SentBefore, 0, __state.CandidateCount);
            int unsentCount = Mathf.Max(0, __state.CandidateCount - sentCount);

            float oldestUnsentAgeMs = 0f;
            float unsentAgeP95Ms = 0f;
            if (unsentCount > 0)
            {
                var unsentAges = __state.CandidateAgesMs.GetRange(sentCount, unsentCount);
                if (unsentAges.Count > 0)
                {
                    oldestUnsentAgeMs = MetricsMath.Percentile(unsentAges, 1f);
                    unsentAgeP95Ms = MetricsMath.Percentile(unsentAges, 0.95f);
                }
            }

            ServerMetrics.RecordSendWindow(
                __state.Uid,
                __state.SendQueueBytes,
                __state.HeadroomBytes,
                __state.CandidateCount,
                sentCount,
                unsentCount,
                oldestUnsentAgeMs,
                unsentAgeP95Ms,
                __state.SendBlocked,
                __state.LowHeadroom
            );
        }

        // --- ZRoutedRpc.HandleRoutedRPC prefix: count RPCs by method hash ---

        [HarmonyPatch(typeof(ZRoutedRpc), "HandleRoutedRPC")]
        [HarmonyPrefix]
        static void HandleRoutedRPCPrefix(ZRoutedRpc.RoutedRPCData data)
        {
            ServerMetrics.RpcCounts.AddOrUpdate(data.m_methodHash, 1, (_, old) => old + 1);
        }

        // --- Terminal.InitTerminal postfix: register console commands ---

        [HarmonyPatch(typeof(Terminal), nameof(Terminal.InitTerminal))]
        [HarmonyPostfix]
        static void TerminalInitPostfix()
        {
            ServerMetrics.RegisterConsoleCommands();
        }

        internal readonly struct SendZDOState
        {
            internal SendZDOState(
                long uid,
                int sendQueueBytes,
                int headroomBytes,
                bool sendBlocked,
                bool lowHeadroom,
                int candidateCount,
                List<float> candidateAgesMs,
                int sentBefore)
            {
                Uid = uid;
                SendQueueBytes = sendQueueBytes;
                HeadroomBytes = headroomBytes;
                SendBlocked = sendBlocked;
                LowHeadroom = lowHeadroom;
                CandidateCount = candidateCount;
                CandidateAgesMs = candidateAgesMs;
                SentBefore = sentBefore;
            }

            internal long Uid { get; }
            internal int SendQueueBytes { get; }
            internal int HeadroomBytes { get; }
            internal bool SendBlocked { get; }
            internal bool LowHeadroom { get; }
            internal int CandidateCount { get; }
            internal List<float>? CandidateAgesMs { get; }
            internal int SentBefore { get; }
        }
    }
}
