using AppAutomation.Avalonia.Headless.Session;
using ContactMirror.Desktop.Contacts;
using SkiaSharp;
using TUnit.Assertions;
using TUnit.Core;

namespace ContactMirror.UiTests.Headless.Tests;

public sealed partial class MainWindowHeadlessTests
{
    private static byte[] ThumbnailPng(int width,int height)
    {
        using var bitmap=new SKBitmap(width,height);bitmap.Erase(SKColors.CornflowerBlue);
        using var image=SKImage.FromBitmap(bitmap);using var encoded=image.Encode(SKEncodedImageFormat.Png,100);return encoded.ToArray();
    }
    [Test,NotInParallel("DesktopUi")]
    public async Task Very_tall_and_wide_photos_keep_both_preview_dimensions_bounded()
    {
        foreach(var shape in new[]{(1,8192),(8192,1),(2000,2000)})
        {
            var bytes=ThumbnailPng(shape.Item1,shape.Item2);int width=0,height=0;
            HeadlessRuntime.Dispatch(()=>{using var preview=ContactPhotoPreview.DecodeBounded(bytes,64);width=preview.PixelSize.Width;height=preview.PixelSize.Height;});
            await Assert.That(width).IsGreaterThan(0);await Assert.That(height).IsGreaterThan(0);
            await Assert.That(Math.Max(width,height)).IsLessThanOrEqualTo(64);
        }
    }
    [Test,NotInParallel("DesktopUi")]
    public async Task Shared_large_photo_decodes_once_and_cache_cancel_and_refresh_stay_bounded()
    {
        var root=Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"ContactMirror-thumbnail-"+Guid.NewGuid().ToString("N"))).FullName;
        Directory.CreateDirectory(Path.Combine(root,"photos"));var bytes=ThumbnailPng(2000,2000);
        await File.WriteAllBytesAsync(Path.Combine(root,"photos","shared.png"),bytes);var loader=new ContactThumbnailLoader();
        var requests=Enumerable.Range(0,30).Select(_=>loader.LoadAsync(root,"photos/shared.png",CancellationToken.None)).ToArray();
        foreach(var preview in await Task.WhenAll(requests))preview?.Dispose();
        await Assert.That(loader.DecodedSources).IsEqualTo(1);
        using(var cancelled=new CancellationTokenSource())
        {
            cancelled.Cancel();var refused=false;
            try{using var preview=await loader.LoadAsync(root,"photos/shared.png",cancelled.Token);}catch(OperationCanceledException){refused=true;}
            await Assert.That(refused).IsTrue();await Assert.That(loader.DecodedSources).IsEqualTo(1);
        }
        for(var index=0;index<36;index++)
        {
            var name=$"photos/photo-{index}.png";await File.WriteAllBytesAsync(Path.Combine(root,name),ThumbnailPng(64,64));
            using var preview=await loader.LoadAsync(root,name,CancellationToken.None);
        }
        await Assert.That(loader.CachedCount).IsEqualTo(32);await Assert.That(loader.CachedBytes).IsLessThan(32L*64*64*4);
        loader.Reset();await Assert.That(loader.CachedCount).IsEqualTo(0);
        using var refreshed=await loader.LoadAsync(root,"photos/shared.png",CancellationToken.None);
        await Assert.That(loader.DecodedSources).IsEqualTo(38);
    }
}
