namespace DownloadLimit.Core;

public enum RunMode { Disabled, Enabled, Bypassing, Faulted }

// UI-thread state machine. Deadline time must include sleep (GetTickCount64 on Windows).
public sealed class BypassController
{
    public const long DurationMilliseconds = 15 * 60 * 1000;
    public RunMode Mode { get; private set; } = RunMode.Disabled;
    public long DeadlineMilliseconds { get; private set; }
    public void Enable() { Mode = RunMode.Enabled; DeadlineMilliseconds = 0; }
    public void Disable() { Mode = RunMode.Disabled; DeadlineMilliseconds = 0; }
    public void Fault() { Mode = RunMode.Faulted; DeadlineMilliseconds = 0; }

    public bool BeginBypass(long nowMilliseconds)
    {
        if (Mode is not (RunMode.Enabled or RunMode.Bypassing)) return false;
        Mode = RunMode.Bypassing;
        DeadlineMilliseconds = checked(nowMilliseconds + DurationMilliseconds);
        return true;
    }

    public bool TryExpire(long nowMilliseconds)
    {
        if (Mode != RunMode.Bypassing || nowMilliseconds < DeadlineMilliseconds) return false;
        Enable();
        return true;
    }

    public long RemainingMilliseconds(long nowMilliseconds) => Mode == RunMode.Bypassing ?
        Math.Max(0, DeadlineMilliseconds - nowMilliseconds) : 0;
}
