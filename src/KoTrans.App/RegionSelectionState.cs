namespace KoTrans.App;

[Flags]
internal enum RegionEdit { None=0, Left=1, Top=2, Right=4, Bottom=8, Move=16, Create=32 }

// Draft only. All coordinates are physical pixels; no profile or screen access here.
internal sealed class RegionSelectionState(PixelRect? initial = null)
{
    internal PixelRect? Area { get; private set; } = initial is { IsValid:true } ? initial : null;
    internal RegionEdit Operation { get; private set; }
    internal bool Dragging => Operation != RegionEdit.None;
    internal event Action? Changed;
    private Point start;
    private PixelRect bounds, origin;
    private PixelRect? before;
    internal bool CanConfirm(PixelRect monitor) => !Dragging && Area is { IsValid:true, Width:>=4, Height:>=4 } r && Contains(monitor,r);
    internal static bool Contains(PixelRect outer, PixelRect inner) => inner.X>=outer.X && inner.Y>=outer.Y
        && (long)inner.X+inner.Width<=(long)outer.X+outer.Width && (long)inner.Y+inner.Height<=(long)outer.Y+outer.Height;
    internal RegionEdit HitTest(Point p, double toleranceX, double toleranceY)
    {
        if (Area is not { } r || p.X<r.X-toleranceX || p.X>(long)r.X+r.Width+toleranceX || p.Y<r.Y-toleranceY || p.Y>(long)r.Y+r.Height+toleranceY) return RegionEdit.Create;
        // Keep a usable interior even when a very small rectangle's resize bands would overlap.
        if(p.X>=r.X && p.X<=(long)r.X+r.Width)toleranceX=Math.Min(toleranceX,r.Width/4d);
        if(p.Y>=r.Y && p.Y<=(long)r.Y+r.Height)toleranceY=Math.Min(toleranceY,r.Height/4d);
        double left=Math.Abs(p.X-r.X), right=Math.Abs(p.X-r.X-r.Width), top=Math.Abs(p.Y-r.Y), bottom=Math.Abs(p.Y-r.Y-r.Height);
        RegionEdit x=Math.Min(left,right)<=toleranceX ? left<=right ? RegionEdit.Left : RegionEdit.Right : RegionEdit.None;
        RegionEdit y=Math.Min(top,bottom)<=toleranceY ? top<=bottom ? RegionEdit.Top : RegionEdit.Bottom : RegionEdit.None;
        return (x|y)!=RegionEdit.None ? x|y : RegionEdit.Move;
    }
    internal void Begin(Point p, PixelRect monitor, double toleranceX, double toleranceY)
    {
        if(monitor.Width<4 || monitor.Height<4) throw new ArgumentException("영역을 지정할 모니터 크기가 올바르지 않습니다.");
        if(Dragging) End();
        before=Area; bounds=monitor; start=Clamp(p); Operation=HitTest(p,toleranceX,toleranceY);
        if(Area is {} previous && (previous.X>=monitor.X+monitor.Width || previous.Y>=monitor.Y+monitor.Height
            || (long)previous.X+previous.Width<=monitor.X || (long)previous.Y+previous.Height<=monitor.Y))Operation=RegionEdit.Create;
        if(Operation==RegionEdit.Create) Area=null;
        else { origin=Fit(Area!.Value,monitor); Area=origin; }
        Changed?.Invoke();
    }
    internal void Move(Point point)
    {
        if(!Dragging)return;
        var p=Clamp(point); int dx=(int)(p.X-start.X), dy=(int)(p.Y-start.Y);
        if(Operation==RegionEdit.Create) Area=new((int)Math.Min(start.X,p.X),(int)Math.Min(start.Y,p.Y),(int)Math.Abs(p.X-start.X),(int)Math.Abs(p.Y-start.Y));
        else if(Operation==RegionEdit.Move) Area=origin with { X=Math.Clamp(origin.X+dx,bounds.X,bounds.X+bounds.Width-origin.Width), Y=Math.Clamp(origin.Y+dy,bounds.Y,bounds.Y+bounds.Height-origin.Height) };
        else {
            int l=origin.X,t=origin.Y,r=l+origin.Width,b=t+origin.Height;
            if(Operation.HasFlag(RegionEdit.Left))l=Math.Clamp(origin.X+dx,bounds.X,r-4);
            if(Operation.HasFlag(RegionEdit.Right))r=Math.Clamp(origin.X+origin.Width+dx,l+4,bounds.X+bounds.Width);
            if(Operation.HasFlag(RegionEdit.Top))t=Math.Clamp(origin.Y+dy,bounds.Y,b-4);
            if(Operation.HasFlag(RegionEdit.Bottom))b=Math.Clamp(origin.Y+origin.Height+dy,t+4,bounds.Y+bounds.Height);
            Area=new(l,t,r-l,b-t);
        }
        Changed?.Invoke();
    }
    internal void End()
    {
        if(!Dragging)return;
        if(Area is not { IsValid:true,Width:>=4,Height:>=4 })Area=before;
        Operation=RegionEdit.None; Changed?.Invoke();
    }
    internal void Clear() { Operation=RegionEdit.None; Area=null; Changed?.Invoke(); }
    internal void CancelGesture() { if(!Dragging)return; Area=before;Operation=RegionEdit.None;Changed?.Invoke(); }
    private Point Clamp(Point p) => new(Math.Clamp(Math.Round(p.X),bounds.X,bounds.X+bounds.Width),Math.Clamp(Math.Round(p.Y),bounds.Y,bounds.Y+bounds.Height));
    private static PixelRect Fit(PixelRect area, PixelRect monitor)
    {
        int w=Math.Clamp(area.Width,4,monitor.Width),h=Math.Clamp(area.Height,4,monitor.Height);
        return new(Math.Clamp(area.X,monitor.X,monitor.X+monitor.Width-w),Math.Clamp(area.Y,monitor.Y,monitor.Y+monitor.Height-h),w,h);
    }
}
