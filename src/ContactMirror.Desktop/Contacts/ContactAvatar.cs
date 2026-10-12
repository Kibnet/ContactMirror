using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ContactMirror.Core;
using ContactMirror.Infrastructure.Contacts;

namespace ContactMirror.Desktop.Contacts;

/// <summary>Reads and decodes only avatars realized by the virtualized table.</summary>
internal sealed class ContactAvatar : Border
{
    private CancellationTokenSource? loading;
    private Bitmap? bitmap;
    public ContactAvatar(ContactRowViewModel row, string root, ContactThumbnailLoader thumbnails)
    {
        Width=32; Height=32; Margin=new Thickness(8,3); CornerRadius=new CornerRadius(16); ClipToBounds=true;
        Background=Brushes.LightSlateGray;
        var initials=string.Concat(row.Name.Split(' ',StringSplitOptions.RemoveEmptyEntries).Take(2).Select(word=>System.Globalization.StringInfo.GetNextTextElement(word))).ToUpperInvariant();
        var fallback=new TextBlock {Text=initials,Foreground=Brushes.White,FontSize=12,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        Child=fallback;ToolTip.SetTip(this,row.Name);
        AttachedToVisualTree+=async (_,_)=>
        {
            loading?.Cancel();loading?.Dispose();loading=new();var token=loading.Token;
            if(row.Item.Document["photo"]?.GetValue<string>() is not { } relative)return;
            try
            {
                var image=await thumbnails.LoadAsync(root,relative,token);
                if(token.IsCancellationRequested){image?.Dispose();return;}
                bitmap?.Dispose();bitmap=image;
                if(image is not null)Child=new Image {Source=image,Stretch=Stretch.UniformToFill};
            }
            catch(Exception error) when(error is OperationCanceledException or IOException or UnauthorizedAccessException or SyncException or ArgumentException or InvalidOperationException){ }
        };
        DetachedFromVisualTree+=(_,_)=>{loading?.Cancel();loading?.Dispose();loading=null;Child=fallback;bitmap?.Dispose();bitmap=null;};
    }
}

/// <summary>A single decoder and 32 small cached PNGs bound memory across rapid scrolling.</summary>
internal sealed class ContactThumbnailLoader
{
    private static readonly SemaphoreSlim decoder=new(1,1);
    private readonly Dictionary<string,byte[]> cache=new(StringComparer.Ordinal);
    private readonly Queue<string> order=new();
    private int epoch;
    internal int DecodedSources {get;private set;}
    internal int CachedCount {get{lock(cache)return cache.Count;}}
    internal long CachedBytes {get{lock(cache)return cache.Values.Sum(value=>(long)value.Length);}}
    public void Reset(){lock(cache){epoch++;cache.Clear();order.Clear();}}
    public async Task<Bitmap?> LoadAsync(string root,string relative,CancellationToken token)
    {
        var key=root+"\n"+relative;int version;byte[]? encoded;
        lock(cache){version=epoch;cache.TryGetValue(key,out encoded);}
        await decoder.WaitAsync(token);
        try
        {
            lock(cache){if(epoch==version)cache.TryGetValue(key,out encoded);}
            return await Task.Run(async()=>
            {
                token.ThrowIfCancellationRequested();
                if(encoded is null)
                {
                    var bytes=await ContactEditorStore.ReadPhotoPreviewAsync(root,relative,token);
                    token.ThrowIfCancellationRequested();if(bytes is null)return null;
                    DecodedSources++;
                    using var preview=ContactPhotoPreview.DecodeBounded(bytes,64);
                    token.ThrowIfCancellationRequested();using var output=new MemoryStream();preview.Save(output,new PngBitmapEncoderOptions());encoded=output.ToArray();
                    lock(cache)
                    {
                        if(epoch==version&&!cache.ContainsKey(key))
                        {while(cache.Count>=32)cache.Remove(order.Dequeue());cache.Add(key,encoded);order.Enqueue(key);}
                    }
                }
                token.ThrowIfCancellationRequested();using var thumbnail=new MemoryStream(encoded);return new Bitmap(thumbnail);
            },token);
        }
        finally{decoder.Release();}
    }
}
