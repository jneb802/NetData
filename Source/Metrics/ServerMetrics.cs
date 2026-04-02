using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace NetData.Metrics
{
    internal class ServerMetrics : MonoBehaviour
    {
        private CsvLogger _csv = null!;
        private float _csvTimer;
        private const float CsvInterval = 10f;
        private const float LongFrameThresholdMs = 50f;
        private const float BandwidthCapBytesPerSec = 65536f;

        // Save timing
        private static float _saveStartTime;
        internal static float LastSaveDuration;
        private static bool _saveInProgress;

        // Written by ServerPatches
        internal static readonly ConcurrentDictionary<long, int> PeerZdoQueueDepths = new();
        internal static readonly ConcurrentDictionary<int, int> RpcCounts = new();
        internal static readonly ConcurrentDictionary<long, PeerSendWindow> PeerSendWindows = new();
        internal static readonly List<float> FrameTimeSamplesMs = new();
        internal static int SendBlockedCount;
        internal static int SendLowHeadroomCount;
        internal static int ServerLongFrameCount;

        // Console command registration guard
        private static bool _commandsRegistered;

        private CsvLogger? _clientCsv;
        private string? _worldName;

        private void Start()
        {
            _csv = new CsvLogger(
                $"netdata_server_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                new[]
                {
                    "timestamp", "world_name", "frame_time_ms", "peer_count",
                    "frame_time_p95_ms", "long_frame_count",
                    "total_zdos", "zdos_sent_sec", "zdos_recv_sec", "ai_count",
                    "last_save_duration_ms", "total_rpc_count",
                    "send_blocked_count", "send_low_headroom_count",
                    "max_peer_send_queue_bytes", "max_peer_send_queue_fill_ratio",
                    "min_peer_send_budget_headroom_bytes", "max_peer_candidate_zdos_count",
                    "max_peer_sent_zdos_count", "max_peer_unsent_zdos_count",
                    "max_peer_oldest_unsent_age_ms", "max_peer_unsent_age_p95_ms",
                    "max_replication_budget_utilization", "peer_details"
                }
            );

            _clientCsv = new CsvLogger(
                $"netdata_clients_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
                new[]
                {
                    "timestamp", "player_name", "world_name", "fps", "ping_ms",
                    "local_quality", "remote_quality", "out_bytes_sec", "in_bytes_sec",
                    "correction_max", "snap_count", "max_staleness",
                    "owner_frame_time_p95_ms", "owner_long_frame_count",
                    "monster_correction_max", "monster_correction_p95", "monster_snap_count",
                    "monster_staleness_max", "monster_staleness_p95",
                    "monster_rotation_error_max", "monster_rotation_error_p95",
                    "monster_anim_trigger_latency_ms", "monster_anim_param_latency_ms",
                    "monster_update_interval_ms_p50", "monster_update_interval_ms_p95",
                    "nearby_remote_monster_count", "nearby_remote_monster_owner_peer",
                    "zdos_sent_sec", "zdos_recv_sec", "total_zdos",
                    "change_queue", "zdo_data_recv_count"
                }
            );

            // Register RPC handler for client metric reports
            ZRoutedRpc.instance.Register<ZPackage>("NetData_ClientReport",
                new Action<long, ZPackage>(RPC_ClientReport));
            NetDataPlugin.Log.LogInfo("Registered NetData_ClientReport RPC");

            ZNet.WorldSaveStarted += OnSaveStarted;
            ZNet.WorldSaveFinished += OnSaveFinished;

            NetDataPlugin.Log.LogInfo("ServerMetrics started");
        }

        private void RPC_ClientReport(long sender, ZPackage pkg)
        {
            try
            {
                string playerName = pkg.ReadString();
                float fps = pkg.ReadSingle();
                int pingMs = pkg.ReadInt();
                float localQ = pkg.ReadSingle();
                float remoteQ = pkg.ReadSingle();
                float outBytes = pkg.ReadSingle();
                float inBytes = pkg.ReadSingle();
                float correctionMax = pkg.ReadSingle();
                int snapCount = pkg.ReadInt();
                float maxStaleness = pkg.ReadSingle();
                float ownerFrameTimeP95Ms = pkg.ReadSingle();
                int ownerLongFrameCount = pkg.ReadInt();
                float monsterCorrectionMax = pkg.ReadSingle();
                float monsterCorrectionP95 = pkg.ReadSingle();
                int monsterSnapCount = pkg.ReadInt();
                float monsterStalenessMax = pkg.ReadSingle();
                float monsterStalenessP95 = pkg.ReadSingle();
                float monsterRotationErrorMax = pkg.ReadSingle();
                float monsterRotationErrorP95 = pkg.ReadSingle();
                float monsterAnimTriggerLatencyMs = pkg.ReadSingle();
                float monsterAnimParamLatencyMs = pkg.ReadSingle();
                float monsterUpdateIntervalP50Ms = pkg.ReadSingle();
                float monsterUpdateIntervalP95Ms = pkg.ReadSingle();
                int nearbyRemoteMonsterCount = pkg.ReadInt();
                string nearbyRemoteMonsterOwnerPeer = pkg.ReadString();
                int zdosSent = pkg.ReadInt();
                int zdosRecv = pkg.ReadInt();
                int totalZdos = pkg.ReadInt();
                int changeQueue = pkg.ReadInt();
                int zdoDataRecvCount = pkg.ReadInt();

                _clientCsv?.WriteRow(
                    DateTime.UtcNow.ToString("o"),
                    playerName,
                    _worldName ?? "unknown",
                    fps.ToString("F1"),
                    pingMs,
                    localQ.ToString("F3"),
                    remoteQ.ToString("F3"),
                    outBytes.ToString("F0"),
                    inBytes.ToString("F0"),
                    correctionMax.ToString("F4"),
                    snapCount,
                    maxStaleness.ToString("F4"),
                    ownerFrameTimeP95Ms.ToString("F2"),
                    ownerLongFrameCount,
                    monsterCorrectionMax.ToString("F4"),
                    monsterCorrectionP95.ToString("F4"),
                    monsterSnapCount,
                    monsterStalenessMax.ToString("F4"),
                    monsterStalenessP95.ToString("F4"),
                    monsterRotationErrorMax.ToString("F2"),
                    monsterRotationErrorP95.ToString("F2"),
                    monsterAnimTriggerLatencyMs.ToString("F1"),
                    monsterAnimParamLatencyMs.ToString("F1"),
                    monsterUpdateIntervalP50Ms.ToString("F1"),
                    monsterUpdateIntervalP95Ms.ToString("F1"),
                    nearbyRemoteMonsterCount,
                    nearbyRemoteMonsterOwnerPeer,
                    zdosSent,
                    zdosRecv,
                    totalZdos,
                    changeQueue,
                    zdoDataRecvCount
                );
            }
            catch (Exception e)
            {
                NetDataPlugin.Log.LogWarning($"Failed to parse client report from peer {sender}: {e.Message}");
            }
        }

        private static void OnSaveStarted()
        {
            _saveStartTime = Time.realtimeSinceStartup;
            _saveInProgress = true;
        }

        private static void OnSaveFinished()
        {
            if (!_saveInProgress) return;
            LastSaveDuration = (Time.realtimeSinceStartup - _saveStartTime) * 1000f;
            _saveInProgress = false;
        }

        private void Update()
        {
            if (ZNet.instance == null) return;

            if (_worldName == null)
                _worldName = ZNet.instance.GetWorldName();

            float frameTimeMs = Time.unscaledDeltaTime * 1000f;
            FrameTimeSamplesMs.Add(frameTimeMs);
            if (frameTimeMs >= LongFrameThresholdMs)
                ServerLongFrameCount++;

            _csvTimer += Time.unscaledDeltaTime;
            if (_csvTimer >= CsvInterval)
            {
                _csvTimer = 0f;
                WriteCsvRow();
            }
        }

        private void WriteCsvRow()
        {
            var peers = ZNet.instance.GetConnectedPeers();
            float frameTime = Time.unscaledDeltaTime * 1000f;
            float frameTimeP95 = MetricsMath.Percentile(FrameTimeSamplesMs, 0.95f);
            int aiCount = BaseAI.GetAllInstances().Count;
            int totalRpcs = 0;
            foreach (var kv in RpcCounts) totalRpcs += kv.Value;

            var peerDetails = new StringBuilder();
            peerDetails.Append("[");
            bool first = true;
            int maxPeerSendQueueBytes = 0;
            float maxPeerSendQueueFillRatio = 0f;
            int minPeerHeadroomBytes = 10240;
            int maxPeerCandidateZdosCount = 0;
            int maxPeerSentZdosCount = 0;
            int maxPeerUnsentZdosCount = 0;
            float maxPeerOldestUnsentAgeMs = 0f;
            float maxPeerUnsentAgeP95Ms = 0f;
            float maxReplicationBudgetUtilization = 0f;
            foreach (var p in peers)
            {
                if (!p.IsReady()) continue;
                try
                {
                    p.m_socket.GetConnectionQuality(
                        out float localQ, out float remoteQ, out int ping,
                        out float outBytes, out float inBytes);
                    int sendQ = p.m_socket.GetSendQueueSize();
                    PeerZdoQueueDepths.TryGetValue(p.m_uid, out int zdoQ);
                    PeerSendWindows.TryGetValue(p.m_uid, out PeerSendWindow? sendWindow);
                    int headroomBytes = Math.Max(0, 10240 - sendQ);
                    float fillRatio = sendQ / 10240f;
                    float utilization = outBytes / BandwidthCapBytesPerSec;

                    maxPeerSendQueueBytes = Math.Max(maxPeerSendQueueBytes, sendQ);
                    maxPeerSendQueueFillRatio = Math.Max(maxPeerSendQueueFillRatio, fillRatio);
                    minPeerHeadroomBytes = Math.Min(minPeerHeadroomBytes, headroomBytes);
                    maxPeerCandidateZdosCount = Math.Max(maxPeerCandidateZdosCount, sendWindow?.MaxCandidateZdosCount ?? 0);
                    maxPeerSentZdosCount = Math.Max(maxPeerSentZdosCount, sendWindow?.MaxSentZdosCount ?? 0);
                    maxPeerUnsentZdosCount = Math.Max(maxPeerUnsentZdosCount, sendWindow?.MaxUnsentZdosCount ?? 0);
                    maxPeerOldestUnsentAgeMs = Math.Max(maxPeerOldestUnsentAgeMs, sendWindow?.MaxOldestUnsentAgeMs ?? 0f);
                    maxPeerUnsentAgeP95Ms = Math.Max(maxPeerUnsentAgeP95Ms, sendWindow?.MaxUnsentAgeP95Ms ?? 0f);
                    maxReplicationBudgetUtilization = Math.Max(maxReplicationBudgetUtilization, utilization);

                    if (!first) peerDetails.Append(";");
                    first = false;
                    peerDetails.Append(
                        $"{p.m_playerName}:ping={ping}ms|sq={sendQ}|fill={fillRatio:F2}|head={headroomBytes}|zdoq={zdoQ}" +
                        $"|cand={sendWindow?.MaxCandidateZdosCount ?? 0}|sent={sendWindow?.MaxSentZdosCount ?? 0}" +
                        $"|unsent={sendWindow?.MaxUnsentZdosCount ?? 0}|old={sendWindow?.MaxOldestUnsentAgeMs ?? 0f:F0}" +
                        $"|out={outBytes:F0}|in={inBytes:F0}|util={utilization:F2}");
                }
                catch (Exception)
                {
                    // Stale connection handle — skip this peer
                }
            }
            peerDetails.Append("]");

            _csv.WriteRow(
                DateTime.UtcNow.ToString("o"),
                _worldName ?? "unknown",
                frameTime.ToString("F2"),
                peers.Count,
                frameTimeP95.ToString("F2"),
                ServerLongFrameCount,
                ZDOMan.instance.NrOfObjects(),
                ZDOMan.instance.GetSentZDOs(),
                ZDOMan.instance.GetRecvZDOs(),
                aiCount,
                LastSaveDuration.ToString("F1"),
                totalRpcs,
                SendBlockedCount,
                SendLowHeadroomCount,
                maxPeerSendQueueBytes,
                maxPeerSendQueueFillRatio.ToString("F2"),
                peers.Count > 0 ? minPeerHeadroomBytes : 10240,
                maxPeerCandidateZdosCount,
                maxPeerSentZdosCount,
                maxPeerUnsentZdosCount,
                maxPeerOldestUnsentAgeMs.ToString("F1"),
                maxPeerUnsentAgeP95Ms.ToString("F1"),
                maxReplicationBudgetUtilization.ToString("F2"),
                peerDetails.ToString().Replace(",", "|")
            );

            RpcCounts.Clear();
            PeerSendWindows.Clear();
            FrameTimeSamplesMs.Clear();
            SendBlockedCount = 0;
            SendLowHeadroomCount = 0;
            ServerLongFrameCount = 0;
        }

        internal static void RegisterConsoleCommands()
        {
            if (_commandsRegistered) return;
            _commandsRegistered = true;

            new Terminal.ConsoleCommand("netdata", "Print NetData server metrics",
                (Terminal.ConsoleEventFailable)(args =>
                {
                    if (ZNet.instance == null)
                    {
                        args.Context.AddString("NetData: no active server");
                        return false;
                    }

                    var peers = ZNet.instance.GetConnectedPeers();
                    float frameTime = Time.unscaledDeltaTime * 1000f;
                    int aiCount = BaseAI.GetAllInstances().Count;

                    args.Context.AddString("--- NetData Server Metrics ---");
                    args.Context.AddString($"Frame time: {frameTime:F1}ms ({1000f / frameTime:F0} fps)");
                    args.Context.AddString($"Frame p95: {MetricsMath.Percentile(FrameTimeSamplesMs, 0.95f):F1}ms longFrames={ServerLongFrameCount}");
                    args.Context.AddString($"Peers: {peers.Count}");
                    args.Context.AddString($"Total ZDOs: {ZDOMan.instance.NrOfObjects()}");
                    args.Context.AddString($"ZDOs/sec sent:{ZDOMan.instance.GetSentZDOs()} recv:{ZDOMan.instance.GetRecvZDOs()}");
                    args.Context.AddString($"Active AI: {aiCount}");
                    args.Context.AddString($"Last save: {LastSaveDuration:F0}ms");
                    args.Context.AddString($"Send blocks: {SendBlockedCount} lowHeadroom: {SendLowHeadroomCount}");

                    foreach (var p in peers)
                    {
                        if (!p.IsReady()) continue;
                        try
                        {
                            p.m_socket.GetConnectionQuality(
                                out float lq, out float rq, out int ping,
                                out float outB, out float inB);
                            int sq = p.m_socket.GetSendQueueSize();
                            PeerZdoQueueDepths.TryGetValue(p.m_uid, out int zdoQ);
                            PeerSendWindows.TryGetValue(p.m_uid, out PeerSendWindow? sendWindow);
                            args.Context.AddString(
                                $"  {p.m_playerName}: ping={ping}ms sq={sq} zdoQ={zdoQ} cand={sendWindow?.MaxCandidateZdosCount ?? 0}" +
                                $" sent={sendWindow?.MaxSentZdosCount ?? 0} unsent={sendWindow?.MaxUnsentZdosCount ?? 0}" +
                                $" old={sendWindow?.MaxOldestUnsentAgeMs ?? 0f:F0}ms out={outB:F0}B/s in={inB:F0}B/s lq={lq:F2} rq={rq:F2}");
                        }
                        catch (Exception)
                        {
                            args.Context.AddString($"  {p.m_playerName}: (connection stats unavailable)");
                        }
                    }

                    return true;
                }),
                isCheat: false, isNetwork: false, onlyServer: false);
        }

        private void OnDestroy()
        {
            ZNet.WorldSaveStarted -= OnSaveStarted;
            ZNet.WorldSaveFinished -= OnSaveFinished;
            _csv?.Dispose();
            _clientCsv?.Dispose();
        }

        internal static void RecordSendWindow(
            long uid,
            int sendQueueBytes,
            int headroomBytes,
            int candidateCount,
            int sentCount,
            int unsentCount,
            float oldestUnsentAgeMs,
            float unsentAgeP95Ms,
            bool sendBlocked,
            bool lowHeadroom)
        {
            var window = PeerSendWindows.GetOrAdd(uid, _ => new PeerSendWindow());
            lock (window)
            {
                window.MaxSendQueueBytes = Math.Max(window.MaxSendQueueBytes, sendQueueBytes);
                window.MaxSendQueueFillRatio = Math.Max(window.MaxSendQueueFillRatio, sendQueueBytes / 10240f);
                window.MinHeadroomBytes = Math.Min(window.MinHeadroomBytes, headroomBytes);
                window.MaxCandidateZdosCount = Math.Max(window.MaxCandidateZdosCount, candidateCount);
                window.MaxSentZdosCount = Math.Max(window.MaxSentZdosCount, sentCount);
                window.MaxUnsentZdosCount = Math.Max(window.MaxUnsentZdosCount, unsentCount);
                window.MaxOldestUnsentAgeMs = Math.Max(window.MaxOldestUnsentAgeMs, oldestUnsentAgeMs);
                window.MaxUnsentAgeP95Ms = Math.Max(window.MaxUnsentAgeP95Ms, unsentAgeP95Ms);
            }

            if (sendBlocked) SendBlockedCount++;
            if (lowHeadroom) SendLowHeadroomCount++;
        }

        internal sealed class PeerSendWindow
        {
            internal int MaxSendQueueBytes;
            internal float MaxSendQueueFillRatio;
            internal int MinHeadroomBytes = 10240;
            internal int MaxCandidateZdosCount;
            internal int MaxSentZdosCount;
            internal int MaxUnsentZdosCount;
            internal float MaxOldestUnsentAgeMs;
            internal float MaxUnsentAgeP95Ms;
        }
    }
}
