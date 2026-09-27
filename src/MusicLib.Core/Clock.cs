namespace MusicLib.Core;

internal static class Clock
{
    public static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
