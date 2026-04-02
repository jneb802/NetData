using HarmonyLib;
using NetData.Metrics;
using UnityEngine;
using System;

namespace NetData.Patches
{
    [HarmonyPatch]
    internal static class ClientPatches
    {
        // --- ZSyncTransform.SyncPosition: measure position correction magnitude ---

        [HarmonyPatch(typeof(ZSyncTransform), "SyncPosition")]
        [HarmonyPrefix]
        static void SyncPositionPrefix(ZSyncTransform __instance, out Vector3 __state, out Quaternion __stateRot)
        {
            __state = __instance.transform.position;
            __stateRot = __instance.transform.rotation;
        }

        [HarmonyPatch(typeof(ZSyncTransform), "SyncPosition")]
        [HarmonyPostfix]
        static void SyncPositionPostfix(ZSyncTransform __instance, ZDO zdo, Vector3 __state, Quaternion __stateRot)
        {
            Vector3 newPos = __instance.transform.position;
            float magnitude = Vector3.Distance(__state, newPos);

            if (magnitude > 0.001f)
            {
                ClientMetrics.LastCorrectionMagnitude = magnitude;
                if (magnitude > ClientMetrics.MaxCorrectionMagnitude)
                    ClientMetrics.MaxCorrectionMagnitude = magnitude;
            }

            Vector3 targetPos = zdo.GetPosition();
            if (zdo.HasOwner())
            {
                float timer = __instance.m_targetPosTimer;
                Vector3 vel = zdo.GetVec3(ZDOVars.s_velHash, Vector3.zero);
                targetPos += vel * timer;
            }

            float distToTarget = Vector3.Distance(__state, targetPos);
            bool snapped = distToTarget >= 5.0f && magnitude > 4.0f;
            if (snapped)
                ClientMetrics.SnapCount++;

            float staleness = __instance.m_targetPosTimer;
            if (staleness > ClientMetrics.MaxTargetPosTimer)
                ClientMetrics.MaxTargetPosTimer = staleness;

            var nview = __instance.GetComponent<ZNetView>();
            var baseAi = __instance.GetComponent<BaseAI>();
            var character = __instance.GetComponent<Character>();
            if (!ClientMetrics.ShouldRecordMonster(nview, baseAi, character))
                return;

            float rotationError = Quaternion.Angle(__stateRot, __instance.transform.rotation);
            ClientMetrics.RecordMonsterMovement(__instance, zdo, magnitude, rotationError, staleness, snapped);
        }

        // --- ZDOMan.RPC_ZDOData: count incoming ZDO data packets ---

        [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
        [HarmonyPrefix]
        static void RPC_ZDODataPrefix()
        {
            ClientMetrics.ZdoDataReceiveCount++;
        }

        // --- ZNet.StopAll: flush final metrics to server before disconnect ---

        [HarmonyPatch(typeof(ZNet), "StopAll")]
        [HarmonyPrefix]
        static void StopAllPrefix()
        {
            ClientMetrics.FlushToServer();
        }

        [HarmonyPatch(typeof(ZSyncAnimation), "Awake")]
        [HarmonyPostfix]
        static void ZSyncAnimationAwakePostfix(ZSyncAnimation __instance)
        {
            __instance.GetComponent<ZNetView>()?.Register<string, long>(
                "NetData_TriggerTimed",
                (sender, triggerName, ticksUtc) =>
                {
                    var nview = __instance.GetComponent<ZNetView>();
                    var baseAi = __instance.GetComponent<BaseAI>();
                    var character = __instance.GetComponent<Character>();
                    if (!ClientMetrics.ShouldRecordMonster(nview, baseAi, character))
                        return;

                    ClientMetrics.RecordMonsterAnimTriggerLatency(ticksUtc);
                });
        }

        [HarmonyPatch(typeof(ZSyncAnimation), "SetTrigger")]
        [HarmonyPostfix]
        static void SetTriggerPostfix(ZSyncAnimation __instance, string name)
        {
            var nview = __instance.GetComponent<ZNetView>();
            if (nview?.GetZDO() == null || !nview.IsOwner())
                return;

            nview.InvokeRPC(ZNetView.Everybody, "NetData_TriggerTimed", name, DateTime.UtcNow.Ticks);
        }

        [HarmonyPatch(typeof(ZSyncAnimation), "SetBool", new[] { typeof(int), typeof(bool) })]
        [HarmonyPostfix]
        static void SetBoolPostfix(ZSyncAnimation __instance, int hash)
        {
            StampAnimParamTimestamp(__instance, hash);
        }

        [HarmonyPatch(typeof(ZSyncAnimation), "SetFloat", new[] { typeof(int), typeof(float) })]
        [HarmonyPostfix]
        static void SetFloatPostfix(ZSyncAnimation __instance, int hash)
        {
            StampAnimParamTimestamp(__instance, hash);
        }

        [HarmonyPatch(typeof(ZSyncAnimation), "SetInt", new[] { typeof(int), typeof(int) })]
        [HarmonyPostfix]
        static void SetIntPostfix(ZSyncAnimation __instance, int hash)
        {
            StampAnimParamTimestamp(__instance, hash);
        }

        [HarmonyPatch(typeof(ZSyncAnimation), "SyncParameters", new[] { typeof(float), typeof(bool) })]
        [HarmonyPostfix]
        static void SyncParametersPostfix(ZSyncAnimation __instance)
        {
            var nview = __instance.GetComponent<ZNetView>();
            var baseAi = __instance.GetComponent<BaseAI>();
            var character = __instance.GetComponent<Character>();
            if (!ClientMetrics.ShouldRecordMonster(nview, baseAi, character))
                return;

            Animator animator = __instance.GetComponentInChildren<Animator>();
            if (animator == null) return;

            foreach (int hash in __instance.m_boolHashes)
                ClientMetrics.TrackRemoteAnimParam(nview, baseAi, character, hash, animator.GetBool(hash) ? 1f : 0f);

            foreach (int hash in __instance.m_floatHashes)
                ClientMetrics.TrackRemoteAnimParam(nview, baseAi, character, hash, animator.GetFloat(hash));

            foreach (int hash in __instance.m_intHashes)
                ClientMetrics.TrackRemoteAnimParam(nview, baseAi, character, hash, animator.GetInteger(hash));
        }

        static void StampAnimParamTimestamp(ZSyncAnimation instance, int hash)
        {
            var nview = instance.GetComponent<ZNetView>();
            if (nview?.GetZDO() == null || !nview.IsOwner())
                return;

            nview.GetZDO().Set(NetDataKeys.AnimParamTimestamp(hash), DateTime.UtcNow.Ticks);
        }
    }
}
