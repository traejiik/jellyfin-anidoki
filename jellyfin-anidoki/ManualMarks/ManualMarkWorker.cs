using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
namespace jellyfin_anidoki.ManualMarks;

public sealed class ManualMarkWorker(ManualMarkQueue queue, ManualMarkService service) : BackgroundService {
    protected override Task ExecuteAsync(CancellationToken cancellation) => Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Consume(cancellation)));
    private async Task Consume(CancellationToken cancellation) {
        await foreach (var operation in queue.Channel.Reader.ReadAllAsync(cancellation))
            await service.Execute(operation, cancellation);
    }
}
