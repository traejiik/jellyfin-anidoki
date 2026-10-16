#nullable enable
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
namespace jellyfin_anidoki.Notifications.Discord;

public sealed class DiscordPosterProvider(IImageProcessor processor) {
    public async Task<byte[]?> Read(BaseItem item, CancellationToken cancellation) {
        try {
            var image = item.GetImageInfo(ImageType.Primary, 0);
            if (image == null || !File.Exists(image.Path) || new FileInfo(image.Path).Length > 20 * 1024 * 1024)
                return null;
            var dimensions = processor.GetImageDimensions(item, image);
            if (dimensions.Width <= 0 || dimensions.Height <= 0 || (double)dimensions.Width * dimensions.Height > 40000000)
                return null;
            var processed = await processor.ProcessImage(new ImageProcessingOptions { Item = item, ItemId = item.Id, Image = image, ImageIndex = 0, MaxWidth = 240, MaxHeight = 360, Quality = 80, SupportedOutputFormats = [ImageFormat.Jpg] });
            if (new FileInfo(processed.Item1).Length > 256 * 1024)
                return null;
            var bytes = await File.ReadAllBytesAsync(processed.Item1, cancellation);
            return bytes.Length <= 256 * 1024 ? bytes : null;
        } catch (Exception) { return null; }
    }
}
