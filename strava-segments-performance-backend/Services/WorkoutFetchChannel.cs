using System.Threading.Channels;

namespace StravaSegmentsPerformanceBackend.Services;

public class WorkoutFetchChannel
{
    private readonly Channel<int> _channel = Channel.CreateUnbounded<int>();

    public ChannelWriter<int> Writer => _channel.Writer;
    public ChannelReader<int> Reader => _channel.Reader;
}
