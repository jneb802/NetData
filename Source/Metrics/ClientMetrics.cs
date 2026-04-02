using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NetData.Metrics
{
    internal class ClientMetrics : MonoBehaviour
    {
        private static ClientMetrics? _instance;
        private CsvLogger _csv = null!;
        private float _csvTimer;
        private const float CsvInterval = 5f;
        private const float LongFrameThresholdMs = 50f;

        private static readonly List<float> FrameTimeSamplesMs = new();
        private static readonly List<float> MonsterCorrectionSamples = new();
        private static readonly List<float> MonsterStalenessSamples = new();
        private static readonly List<float> MonsterRotationErrorSamples = new();
        private static readonly List<float> MonsterUpdateIntervalSamplesMs = new();
        private static readonly List<float> MonsterAnimTriggerLatencySamplesMs = new();
        private static readonly List<float> MonsterAnimParamLatencySamplesMs = new();
        private static readonly Dictionary<ZDOID, float> LastMonsterRevisionSeenAt = new();
        private static readonly Dictionary<string, float> LastRemoteAnimValues = new();
        private static readonly HashSet<ZDOID> NearbyRemoteMonsterIds = new();
        private static readonly Dictionary<long, int> NearbyRemoteMonsterOwners = new();

        // Written by ClientPatches
        internal static float LastCorrectionMagnitude;
        internal static float MaxCorrectionMagnitude;
        internal static int SnapCount;
        internal static float MaxTargetPosTimer;
        internal static int ZdoDataReceiveCount;
        internal static int OwnerLongFrameCount;
        internal static float MaxMonsterCorrectionMagnitude;
        internal static int MonsterSnapCount;
        internal static float MaxMonsterStaleness;
        internal static float MaxMonsterRotationError;

        // Cached identity (resolved once after player profile is available)
        private string? _playerName;
        private string? _worldName;

        private void Start()
        {
            _csv = new CsvLogger(
                $"netdata_client_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
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
            _instance = this;
            NetDataPlugin.Log.LogInfo("ClientMetrics started");
        }

        private void Update()
        {
            if (ZNet.instance == null) return;

            ResolveIdentity();
            UpdateOverlay();

            float frameTimeMs = Time.unscaledDeltaTime * 1000f;
            FrameTimeSamplesMs.Add(frameTimeMs);
            if (frameTimeMs >= LongFrameThresholdMs)
                OwnerLongFrameCount++;

            _csvTimer += Time.unscaledDeltaTime;
            if (_csvTimer >= CsvInterval)
            {
                _csvTimer = 0f;
                WriteCsvRow();
            }
        }

        private void ResolveIdentity()
        {
            if (_playerName != null && _worldName != null) return;

            if (_playerName == null && Game.instance?.GetPlayerProfile() != null)
                _playerName = Game.instance.GetPlayerProfile().GetName();

            if (_worldName == null && ZNet.instance != null)
                _worldName = ZNet.instance.GetWorldName();
        }

        private void UpdateOverlay()
        {
            if (ZDOMan.instance == null) return;

            float fps = 1f / Time.unscaledDeltaTime;
            ZNet.instance.GetNetStats(out float localQ, out float remoteQ, out int pingMs,
                out float outBytes, out float inBytes);

            Terminal.m_testList["ND fps"] = $"{fps:F0}";
            Terminal.m_testList["ND ping"] = $"{pingMs}ms";
            Terminal.m_testList["ND quality"] = $"L:{localQ:F2} R:{remoteQ:F2}";
            Terminal.m_testList["ND bw"] = $"out:{outBytes:F0} in:{inBytes:F0} B/s";
            Terminal.m_testList["ND correction"] = $"{LastCorrectionMagnitude:F3}m (max:{MaxCorrectionMagnitude:F3})";
            Terminal.m_testList["ND snaps"] = $"{SnapCount}";
            Terminal.m_testList["ND staleness"] = $"{MaxTargetPosTimer:F3}s";
            Terminal.m_testList["ND monsters"] =
                $"corr:{MaxMonsterCorrectionMagnitude:F2} snap:{MonsterSnapCount} stale:{MaxMonsterStaleness:F2}s";
            Terminal.m_testList["ND zdos"] = $"s:{ZDOMan.instance.GetSentZDOs()} r:{ZDOMan.instance.GetRecvZDOs()} total:{ZDOMan.instance.NrOfObjects()}";
            Terminal.m_testList["ND queue"] = $"{ZDOMan.instance.GetClientChangeQueue()}";
        }

        private void WriteCsvRow()
        {
            if (ZDOMan.instance == null) return;

            ZNet.instance.GetNetStats(out float localQ, out float remoteQ, out int pingMs,
                out float outBytes, out float inBytes);
            float fps = 1f / Time.unscaledDeltaTime;
            float ownerFrameP95Ms = MetricsMath.Percentile(FrameTimeSamplesMs, 0.95f);
            float monsterCorrectionP95 = MetricsMath.Percentile(MonsterCorrectionSamples, 0.95f);
            float monsterStalenessP95 = MetricsMath.Percentile(MonsterStalenessSamples, 0.95f);
            float monsterRotationErrorP95 = MetricsMath.Percentile(MonsterRotationErrorSamples, 0.95f);
            float monsterAnimTriggerLatencyMs = MetricsMath.Percentile(MonsterAnimTriggerLatencySamplesMs, 0.95f);
            float monsterAnimParamLatencyMs = MetricsMath.Percentile(MonsterAnimParamLatencySamplesMs, 0.95f);
            float monsterUpdateIntervalP50Ms = MetricsMath.Median(MonsterUpdateIntervalSamplesMs);
            float monsterUpdateIntervalP95Ms = MetricsMath.Percentile(MonsterUpdateIntervalSamplesMs, 0.95f);
            int nearbyRemoteMonsterCount = NearbyRemoteMonsterIds.Count;
            string nearbyRemoteMonsterOwnerPeer = ResolvePrimaryMonsterOwner();

            _csv.WriteRow(
                DateTime.UtcNow.ToString("o"),
                _playerName ?? "unknown",
                _worldName ?? "unknown",
                fps.ToString("F1"),
                pingMs,
                localQ.ToString("F3"),
                remoteQ.ToString("F3"),
                outBytes.ToString("F0"),
                inBytes.ToString("F0"),
                MaxCorrectionMagnitude.ToString("F4"),
                SnapCount,
                MaxTargetPosTimer.ToString("F4"),
                ownerFrameP95Ms.ToString("F2"),
                OwnerLongFrameCount,
                MaxMonsterCorrectionMagnitude.ToString("F4"),
                monsterCorrectionP95.ToString("F4"),
                MonsterSnapCount,
                MaxMonsterStaleness.ToString("F4"),
                monsterStalenessP95.ToString("F4"),
                MaxMonsterRotationError.ToString("F2"),
                monsterRotationErrorP95.ToString("F2"),
                monsterAnimTriggerLatencyMs.ToString("F1"),
                monsterAnimParamLatencyMs.ToString("F1"),
                monsterUpdateIntervalP50Ms.ToString("F1"),
                monsterUpdateIntervalP95Ms.ToString("F1"),
                nearbyRemoteMonsterCount,
                nearbyRemoteMonsterOwnerPeer,
                ZDOMan.instance.GetSentZDOs(),
                ZDOMan.instance.GetRecvZDOs(),
                ZDOMan.instance.NrOfObjects(),
                ZDOMan.instance.GetClientChangeQueue(),
                ZdoDataReceiveCount
            );

            SendMetricsToServer(fps, pingMs, localQ, remoteQ, outBytes, inBytes);

            // Reset accumulators
            MaxCorrectionMagnitude = 0f;
            SnapCount = 0;
            MaxTargetPosTimer = 0f;
            ZdoDataReceiveCount = 0;
            OwnerLongFrameCount = 0;
            MaxMonsterCorrectionMagnitude = 0f;
            MonsterSnapCount = 0;
            MaxMonsterStaleness = 0f;
            MaxMonsterRotationError = 0f;
            FrameTimeSamplesMs.Clear();
            MonsterCorrectionSamples.Clear();
            MonsterStalenessSamples.Clear();
            MonsterRotationErrorSamples.Clear();
            MonsterUpdateIntervalSamplesMs.Clear();
            MonsterAnimTriggerLatencySamplesMs.Clear();
            MonsterAnimParamLatencySamplesMs.Clear();
            NearbyRemoteMonsterIds.Clear();
            NearbyRemoteMonsterOwners.Clear();
        }

        private void SendMetricsToServer(float fps, int pingMs, float localQ, float remoteQ,
            float outBytes, float inBytes)
        {
            if (ZRoutedRpc.instance == null || ZNet.instance?.GetServerPeer() == null) return;

            var pkg = new ZPackage();
            pkg.Write(_playerName ?? "unknown");
            pkg.Write(fps);
            pkg.Write(pingMs);
            pkg.Write(localQ);
            pkg.Write(remoteQ);
            pkg.Write(outBytes);
            pkg.Write(inBytes);
            pkg.Write(MaxCorrectionMagnitude);
            pkg.Write(SnapCount);
            pkg.Write(MaxTargetPosTimer);
            pkg.Write(MetricsMath.Percentile(FrameTimeSamplesMs, 0.95f));
            pkg.Write(OwnerLongFrameCount);
            pkg.Write(MaxMonsterCorrectionMagnitude);
            pkg.Write(MetricsMath.Percentile(MonsterCorrectionSamples, 0.95f));
            pkg.Write(MonsterSnapCount);
            pkg.Write(MaxMonsterStaleness);
            pkg.Write(MetricsMath.Percentile(MonsterStalenessSamples, 0.95f));
            pkg.Write(MaxMonsterRotationError);
            pkg.Write(MetricsMath.Percentile(MonsterRotationErrorSamples, 0.95f));
            pkg.Write(MetricsMath.Percentile(MonsterAnimTriggerLatencySamplesMs, 0.95f));
            pkg.Write(MetricsMath.Percentile(MonsterAnimParamLatencySamplesMs, 0.95f));
            pkg.Write(MetricsMath.Median(MonsterUpdateIntervalSamplesMs));
            pkg.Write(MetricsMath.Percentile(MonsterUpdateIntervalSamplesMs, 0.95f));
            pkg.Write(NearbyRemoteMonsterIds.Count);
            pkg.Write(ResolvePrimaryMonsterOwner());
            pkg.Write(ZDOMan.instance?.GetSentZDOs() ?? 0);
            pkg.Write(ZDOMan.instance?.GetRecvZDOs() ?? 0);
            pkg.Write(ZDOMan.instance?.NrOfObjects() ?? 0);
            pkg.Write(ZDOMan.instance?.GetClientChangeQueue() ?? 0);
            pkg.Write(ZdoDataReceiveCount);

            ZRoutedRpc.instance.InvokeRoutedRPC("NetData_ClientReport", (object)pkg);
        }

        internal static void RecordMonsterMovement(
            ZSyncTransform syncTransform,
            ZDO zdo,
            float correctionMagnitude,
            float rotationError,
            float staleness,
            bool snapped)
        {
            if (_instance == null) return;

            if (correctionMagnitude > 0.001f)
            {
                MonsterCorrectionSamples.Add(correctionMagnitude);
                if (correctionMagnitude > MaxMonsterCorrectionMagnitude)
                    MaxMonsterCorrectionMagnitude = correctionMagnitude;
            }

            MonsterRotationErrorSamples.Add(rotationError);
            if (rotationError > MaxMonsterRotationError)
                MaxMonsterRotationError = rotationError;

            MonsterStalenessSamples.Add(staleness);
            if (staleness > MaxMonsterStaleness)
                MaxMonsterStaleness = staleness;

            if (snapped)
                MonsterSnapCount++;

            NearbyRemoteMonsterIds.Add(zdo.m_uid);
            long owner = zdo.GetOwner();
            if (owner != 0L)
                NearbyRemoteMonsterOwners[owner] = NearbyRemoteMonsterOwners.TryGetValue(owner, out int count) ? count + 1 : 1;

            float now = Time.realtimeSinceStartup;
            if (LastMonsterRevisionSeenAt.TryGetValue(zdo.m_uid, out float lastSeenAt) && zdo.DataRevision != 0U)
            {
                float deltaMs = (now - lastSeenAt) * 1000f;
                if (deltaMs > 0f)
                    MonsterUpdateIntervalSamplesMs.Add(deltaMs);
            }
            LastMonsterRevisionSeenAt[zdo.m_uid] = now;
        }

        internal static void RecordMonsterAnimTriggerLatency(long ticksUtc)
        {
            if (ticksUtc <= 0) return;
            double latencyMs = (DateTime.UtcNow.Ticks - ticksUtc) / TimeSpan.TicksPerMillisecond;
            if (latencyMs >= 0)
                MonsterAnimTriggerLatencySamplesMs.Add((float)latencyMs);
        }

        internal static void RecordMonsterAnimParamLatency(long ticksUtc)
        {
            if (ticksUtc <= 0) return;
            double latencyMs = (DateTime.UtcNow.Ticks - ticksUtc) / TimeSpan.TicksPerMillisecond;
            if (latencyMs >= 0)
                MonsterAnimParamLatencySamplesMs.Add((float)latencyMs);
        }

        internal static bool ShouldRecordMonster(ZNetView? nview, BaseAI? baseAi, Character? character)
        {
            if (nview?.GetZDO() == null) return false;
            if (!nview.HasOwner() || nview.IsOwner()) return false;
            if (baseAi is not MonsterAI) return false;
            return character != null && !character.IsPlayer();
        }

        internal static void TrackRemoteAnimParam(
            ZNetView? nview,
            BaseAI? baseAi,
            Character? character,
            int hash,
            float value)
        {
            if (!ShouldRecordMonster(nview, baseAi, character)) return;

            ZDO zdo = nview!.GetZDO();
            string key = $"{zdo.m_uid}:{hash}";
            if (LastRemoteAnimValues.TryGetValue(key, out float lastValue) && Mathf.Approximately(lastValue, value))
                return;

            LastRemoteAnimValues[key] = value;
            RecordMonsterAnimParamLatency(zdo.GetLong(NetDataKeys.AnimParamTimestamp(hash), 0L));
        }

        private static string ResolvePrimaryMonsterOwner()
        {
            if (NearbyRemoteMonsterOwners.Count == 0) return "none";
            if (NearbyRemoteMonsterOwners.Count == 1) return NearbyRemoteMonsterOwners.Keys.First().ToString();

            var ordered = NearbyRemoteMonsterOwners.OrderByDescending(kv => kv.Value).ToArray();
            if (ordered.Length > 1 && ordered[0].Value == ordered[1].Value) return "mixed";
            return ordered[0].Key.ToString();
        }

        /// <summary>
        /// Called by ZNet.StopAll prefix patch to flush final metrics before disconnect.
        /// </summary>
        internal static void FlushToServer()
        {
            if (_instance == null) return;
            if (ZDOMan.instance == null) return;
            if (ZRoutedRpc.instance == null || ZNet.instance?.GetServerPeer() == null) return;

            ZNet.instance.GetNetStats(out float localQ, out float remoteQ, out int pingMs,
                out float outBytes, out float inBytes);
            float fps = 1f / Time.unscaledDeltaTime;

            _instance.SendMetricsToServer(fps, pingMs, localQ, remoteQ, outBytes, inBytes);
        }

        private void OnDestroy()
        {
            string[] keys = { "ND fps", "ND ping", "ND quality", "ND bw",
                "ND correction", "ND snaps", "ND staleness", "ND monsters", "ND zdos", "ND queue" };
            foreach (var key in keys)
                Terminal.m_testList.Remove(key);
            _instance = null;
            _csv?.Dispose();
        }
    }
}
