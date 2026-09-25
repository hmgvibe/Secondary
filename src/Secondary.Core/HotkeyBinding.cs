namespace Secondary.Core;

// Register the replacement first: a conflict must not disable the working key.
public sealed class HotkeyBinding(Func<int, HotkeyGesture, bool> register, Action<int> unregister) : IDisposable
{
    public int ActiveId { get; private set; }
    private HotkeyGesture? current;
    public bool Set(HotkeyGesture gesture)
    {
        if (current == gesture) return true;
        var next = ActiveId == 701 ? 702 : 701;
        if (!register(next, gesture)) return false;
        if (ActiveId != 0) unregister(ActiveId);
        ActiveId = next;
        current = gesture;
        return true;
    }
    public void Dispose()
    {
        if (ActiveId != 0) unregister(ActiveId);
        ActiveId = 0;
        current = null;
    }
}
