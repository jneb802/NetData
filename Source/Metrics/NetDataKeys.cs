namespace NetData.Metrics
{
    internal static class NetDataKeys
    {
        private static readonly int AnimParamTimestampSeed = "netdata_anim_param_ticks".GetStableHashCode();

        internal static int AnimParamTimestamp(int hash) => AnimParamTimestampSeed ^ hash;
    }
}
