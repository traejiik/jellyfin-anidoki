using System;
using System.Threading.Channels;
namespace jellyfin_anidoki.ManualMarks;

public sealed class ManualMarkQueue
{
    internal Channel<Guid> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<Guid>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.Wait });
    public bool TryWrite(Guid operationId) => Channel.Writer.TryWrite(operationId);
    internal bool TryRead(out Guid operationId) => Channel.Reader.TryRead(out operationId);
}
