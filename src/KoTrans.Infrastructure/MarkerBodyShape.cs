using System.Collections.Generic;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

// Temporary native-resolution edge features only. No image, dialogue or
// fingerprint is persisted. This validates learning evidence; it never masks
// pixels or authorizes skipping OCR by itself.
internal sealed class MarkerBodyShape
{
    private sealed record Region(PixelRect Bounds,byte[] Edges);
    private readonly Region[] regions;
    private readonly FramePixelFormat format;
    private MarkerBodyShape(Region[] regions,FramePixelFormat format){this.regions=regions;this.format=format;}
    internal static MarkerBodyShape? Create(ImageFrame frame,IReadOnlyList<PixelRect> bounds)
    {
        if(bounds.Count==0 || bounds.Any(b=>!GameMarkerGeometry.Inside(frame,b))
            || bounds.Sum(b=>(long)b.Width*b.Height)>2_000_000)return null;
        var regions=new List<Region>();int strong=0;
        foreach(var b in bounds) {
            var edges=new byte[b.Width*b.Height];
            for(int y=0;y<b.Height;y++)for(int x=0;x<b.Width;x++) {
                byte edge=Edge(frame,b,x,y);edges[y*b.Width+x]=edge;if((edge>>4)!=0)strong++;
            }
            regions.Add(new(b,edges));
        }
        return strong<16 ? null : new(regions.ToArray(),frame.PixelFormat);
    }
    internal bool Matches(ImageFrame frame)
    {
        if(frame.PixelFormat!=format)return false;
        foreach(var region in regions) {
            var b=region.Bounds;if(!GameMarkerGeometry.Inside(frame,b))return false;
            for(int y=0;y<b.Height;y++)for(int x=0;x<b.Width;x++) {
                byte before=region.Edges[y*b.Width+x],after=Edge(frame,b,x,y);
                // Strong edges must still exist; new strong edges must already
                // have support at this exact position and with the same sign.
                // A weak/strong band absorbs intensity rounding without moving,
                // resizing, averaging, or dropping small punctuation strokes.
                if(((before>>4)&~after&15)!=0 || ((after>>4)&~before&15)!=0)return false;
            }
        }
        return true;
    }
    private static byte Edge(ImageFrame f,PixelRect b,int x,int y)
    {
        var p=f.Pixels.Span;int i=(b.Y+y)*f.Stride+(b.X+x)*4;
        int light=(p[i]*29+p[i+1]*150+p[i+2]*77)>>8;
        int right=x+1<b.Width ? i+4 : i,below=y+1<b.Height ? i+f.Stride : i;
        int h=((p[right]*29+p[right+1]*150+p[right+2]*77)>>8)-light;
        int v=((p[below]*29+p[below+1]*150+p[below+2]*77)>>8)-light;
        return (byte)(Bits(h,v,12)|(Bits(h,v,24)<<4));
    }
    private static int Bits(int h,int v,int threshold)=>(h>=threshold?1:0)|(h<=-threshold?2:0)|(v>=threshold?4:0)|(v<=-threshold?8:0);
}
