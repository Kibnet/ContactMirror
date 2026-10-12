using Avalonia.Media.Imaging;
using SkiaSharp;

namespace ContactMirror.Desktop.Contacts;

public static class ContactPhotoPreview
{
    public static Bitmap DecodeBounded(byte[] bytes,int maximumDimension)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDimension);
        using var codec=SKCodec.Create(new MemoryStream(bytes))??throw new ArgumentException("Фотография не декодируется.");
        var info=codec.Info;var size=Math.Min(maximumDimension,Math.Max(info.Width,info.Height));
        // Avalonia's aspect-ratio decode can round the short side of a strip to zero.
        if((long)Math.Min(info.Width,info.Height)*size<Math.Max(info.Width,info.Height))
        {
            var width=Math.Max(1,(int)((long)info.Width*size/Math.Max(info.Width,info.Height)));
            var height=Math.Max(1,(int)((long)info.Height*size/Math.Max(info.Width,info.Height)));
            using var decoded=SKBitmap.Decode(bytes)??throw new ArgumentException("Фотография не декодируется.");
            using var target=new SKBitmap(width,height);
            using(var canvas=new SKCanvas(target))canvas.DrawBitmap(decoded,new SKRect(0,0,width,height));
            using var image=SKImage.FromBitmap(target);using var encoded=image.Encode(SKEncodedImageFormat.Png,100);
            using var thumbnail=new MemoryStream(encoded.ToArray());return new Bitmap(thumbnail);
        }
        using var source=new MemoryStream(bytes);
        return info.Width>=info.Height?Bitmap.DecodeToWidth(source,size):Bitmap.DecodeToHeight(source,size);
    }
}
