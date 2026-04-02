using System;
using System.Collections.Generic;

namespace NetData.Metrics
{
    internal static class MetricsMath
    {
        internal static float Percentile(List<float> samples, float percentile)
        {
            if (samples.Count == 0) return 0f;

            samples.Sort();
            int index = (int)Math.Ceiling(samples.Count * percentile) - 1;
            if (index < 0) index = 0;
            if (index >= samples.Count) index = samples.Count - 1;
            return samples[index];
        }

        internal static float Median(List<float> samples)
        {
            if (samples.Count == 0) return 0f;

            samples.Sort();
            int middle = samples.Count / 2;
            if (samples.Count % 2 == 1) return samples[middle];
            return (samples[middle - 1] + samples[middle]) * 0.5f;
        }
    }
}
