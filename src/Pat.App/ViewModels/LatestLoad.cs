namespace Pat.App.ViewModels;

/// <summary>Each request owns its token. A cancelled provider may still finish; its result must be ignored.</summary>
public sealed class LatestLoad : IDisposable
{
    private CancellationTokenSource? _current;
    public CancellationTokenSource Begin()
    {
        _current?.Cancel();
        return _current = new CancellationTokenSource();
    }
    public bool IsCurrent(CancellationTokenSource request) => ReferenceEquals(request, _current) && !request.IsCancellationRequested;
    public void End(CancellationTokenSource request)
    {
        if (ReferenceEquals(request, _current)) _current = null;
        request.Dispose();
    }
    public void Cancel() { _current?.Cancel(); _current = null; }
    public void Dispose() => Cancel();
}
