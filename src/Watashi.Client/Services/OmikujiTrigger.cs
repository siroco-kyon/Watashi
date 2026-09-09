namespace Watashi.Client.Services;

public sealed class OmikujiTrigger
{
    private long? _lastClick;
    private int _count;

    public bool Click(long milliseconds)
    {
        if (_lastClick is null || milliseconds - _lastClick.Value >= 3000 || milliseconds < _lastClick.Value)
            _count = 0;
        _lastClick = milliseconds;
        if (++_count < 10) return false;
        _count = 0;
        _lastClick = null;
        return true;
    }
}
