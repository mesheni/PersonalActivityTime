namespace Pat.Core;

/// <summary>
/// Источник событий активности. Реализации живут в Pat.Infrastructure (Win32);
/// тесты и альтернативные источники могут подменять реализацию.
/// </summary>
public interface IActivityCollector
{
    void Start(System.Threading.Channels.ChannelWriter<Events.ActivityEvent> writer);

    void Stop();
}
