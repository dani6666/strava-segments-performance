using System.Threading.Channels;

namespace StravaSegmentsPerformanceBackend.Services;

public record FetchRequest(int UserId, DateTime? AfterUtc, DateTime? BeforeUtc);

public class WorkoutFetchChannel
{
    private readonly Channel<FetchRequest> _channel = Channel.CreateUnbounded<FetchRequest>();

    public ChannelWriter<FetchRequest> Writer => _channel.Writer;
    public ChannelReader<FetchRequest> Reader => _channel.Reader;
}
