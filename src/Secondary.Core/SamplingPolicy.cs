namespace Secondary.Core;

public static class SamplingPolicy
{
    public static int IntervalMs(bool game, bool high) => game && !high ? 3000 : 1000;
    public static int GpuIntervalSeconds(bool game, bool high) => game && !high ? 10 : 2;
}
