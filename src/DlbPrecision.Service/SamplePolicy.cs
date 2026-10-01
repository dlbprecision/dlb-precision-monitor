namespace DlbPrecision.Service
{
    internal static class SamplePolicy
    {
        // A widget's 1-second WinForms timer can fire a few milliseconds early and a fresh hardware
        // sample takes about 50 ms. Measuring from when the previous sample started, with a window
        // shorter than the fastest refresh, gives every 1-second poll new readings while widgets
        // polling at nearly the same moment still share one sample.
        internal const long ReuseMilliseconds = 750;

        internal static bool CanReuse(long requestedAt, long lastSampleStartedAt) =>
            requestedAt - lastSampleStartedAt < ReuseMilliseconds;
    }
}
