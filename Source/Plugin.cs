using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using NetData.Metrics;
using NetData.Patches;

namespace NetData
{
    [BepInPlugin(ModGUID, ModName, ModVersion)]
    public class NetDataPlugin : BaseUnityPlugin
    {
        internal const string ModGUID = "warpalicious.NetData";
        internal const string ModName = "NetData";
        internal const string ModVersion = "1.0.0";

        internal static ManualLogSource Log = null!;
        internal static Harmony HarmonyInstance = null!;
        private static bool _patchesApplied;

        public void Awake()
        {
            Log = Logger;
            HarmonyInstance = new Harmony(ModGUID);

            // Defer detection until ZNet.instance exists
            HarmonyInstance.Patch(
                AccessTools.Method(typeof(ZNet), "Awake"),
                postfix: new HarmonyMethod(typeof(NetDataPlugin), nameof(ZNetAwakePostfix))
            );
        }

        private static void ZNetAwakePostfix(ZNet __instance)
        {
            // Patches only need to be applied once (they survive ZNet lifecycle).
            // MonoBehaviours must be re-attached each time ZNet is created.
            if (!_patchesApplied)
            {
                _patchesApplied = true;

                if (__instance.IsDedicated())
                {
                    Log.LogInfo("Dedicated server detected — applying server patches");
                    HarmonyInstance.PatchAll(typeof(ServerPatches));

                    var sendZDOsMethod = AccessTools.Method(typeof(ZDOMan), "SendZDOs");
                    if (sendZDOsMethod != null)
                    {
                        HarmonyInstance.Patch(sendZDOsMethod,
                            prefix: new HarmonyMethod(typeof(ServerPatches), nameof(ServerPatches.SendZDOsPrefix)),
                            postfix: new HarmonyMethod(typeof(ServerPatches), nameof(ServerPatches.SendZDOsPostfix)));
                        Log.LogInfo("Patched ZDOMan.SendZDOs");
                    }
                }
                else
                {
                    Log.LogInfo("Client detected — applying client patches");
                    HarmonyInstance.PatchAll(typeof(ClientPatches));
                }
            }

            // Attach metrics MonoBehaviour every time ZNet is created
            if (__instance.IsDedicated())
                __instance.gameObject.AddComponent<ServerMetrics>();
            else
                __instance.gameObject.AddComponent<ClientMetrics>();
        }

        private void OnDestroy()
        {
            HarmonyInstance?.UnpatchSelf();
        }
    }
}
