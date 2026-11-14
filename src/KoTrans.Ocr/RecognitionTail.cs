using KoTrans.Core;
using OpenCvSharp;
namespace KoTrans.Ocr;

// Recover a light Japanese glyph/punctuation immediately after a detected horizontal line.
// No screen expansion, model rerun, language inference or extra recognition pass.
internal static class RecognitionTail
{
    internal static PixelRect Extend(Mat image, PixelRect box, IReadOnlyList<PixelRect> otherBoxes)
    {
        if (box.Height < 12 || box.Width < box.Height * 3 || box.X < 0 || box.Y < 0
            || (long)box.Y + box.Height > image.Height || (long)box.X + box.Width >= image.Width) return box;
        int end = box.X + box.Width, h = box.Height;
        int limit = Math.Min(image.Width, end + (int)Math.Ceiling(h * 1.25));
        foreach (var other in otherBoxes) {
            if (other == box || other.X < end) continue;
            int overlap = Math.Min(box.Y+h,other.Y+other.Height)-Math.Max(box.Y,other.Y);
            if (overlap > Math.Min(h,other.Height) * .4) limit = Math.Min(limit,other.X);
        }
        if (limit <= end + 2) return box;
        int start = Math.Max(box.X,end-h), top = box.Y + h/10, bottom = box.Y+h;
        using var strip = new Mat(image,new Rect(start,top,limit-start,bottom-top));
        using var mask = new Mat();
        // Deliberately limited to bright neutral lettering. Dark/coloured fonts keep the detector crop.
        Cv2.InRange(strip,new Scalar(180,180,180),new Scalar(255,255,255),mask);
        using var labels = new Mat(); using var stats = new Mat(); using var centers = new Mat();
        int count = Cv2.ConnectedComponentsWithStats(mask,labels,stats,centers);
        var components = new List<PixelRect>();
        for(int i=1;i<count;i++) {
            int x=stats.At<int>(i,0)+start,y=stats.At<int>(i,1)+top,w=stats.At<int>(i,2),height=stats.At<int>(i,3),area=stats.At<int>(i,4);
            // Reject broad background patches and thin horizontal effects/dashes; do not turn them into kanji.
            if (area < Math.Max(3,h*h*.002) || height < Math.Max(3,h*.10) || w < 2
                || height > h || w > h*1.1 || w > h*.65 && height < h*.2) continue;
            components.Add(new(x,y,w,height));
        }
        // A continuation must be adjacent to the existing line, never a detached UI label.
        int right=end; bool extended=false;
        foreach(var component in components.OrderBy(c=>c.X)) {
            if(component.X+component.Width<=end) continue;
            if(component.X > right+h*.3) break;
            right=Math.Max(right,component.X+component.Width);extended=true;
        }
        return extended ? box with {Width=Math.Min(limit,right+Math.Max(2,h/12))-box.X} : box;
    }
}
