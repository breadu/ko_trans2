using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using KoTrans.Core;

namespace KoTrans.Infrastructure;

public sealed record MarkerLearningStatus(MarkerLearningMode Mode,int Candidates,int Confirmed,int BestPages,int AnimatedPages,
    int Excluded,int WouldExclude,bool StorageError=false)
{
    public long ExcludedTotal {get;init;}
    public long ExcludingOcrCalls {get;init;}
    public long SkippedFrames {get;init;}
    public long RecheckFrames {get;init;}
    public long ExtraBodyChecks {get;init;}
    public long BodyCheckCacheHits {get;init;}
    public long FastSamples {get;init;}
    public long FastCycles {get;init;}
    public long FramePreparations {get;init;}
    public long FramePreparationFailures {get;init;}
    public long RecoveredCycles {get;init;}
    public MarkerRecoveryFailure RecoveryFailure {get;init;}
    public int FrameNoisePixels {get;init;}
    public long ValidatedCycles {get;init;}
    public long BodyChangeDiscards {get;init;}
    public long ExpiredWatches {get;init;}
    public MarkerFrameDecision FrameDecision {get;init;}
    public MarkerFrameFailure FrameFailure {get;init;}
    public string DisplayText => StorageError ? "학습 저장 실패 · 이번 실행에서만 사용합니다. 폴더 권한을 확인하세요."
        : Mode==MarkerLearningMode.Off ? "학습 사용 안 함"
        : $"{(Mode==MarkerLearningMode.Observe ? "학습만" : "학습 후 자동 제외")} · 확정 {Confirmed}개 · 관찰 {Candidates}개\n"
          + $"가장 많이 관찰한 표시: 서로 다른 대사 {BestPages}/5개 · 깜빡임 확인 {AnimatedPages}/2개\n"
          + $"이번 OCR: 제외 {Excluded}개 · 제외 가능 {WouldExclude}개\n"
          + $"현재 세션 누적: 표시 제외 {ExcludedTotal}개 ({ExcludingOcrCalls}회 OCR) · OCR 생략 {SkippedFrames}프레임\n"
          + $"재확인 전환 {RecheckFrames}회 · 본문 추가 확인 {ExtraBodyChecks}회 · 본문 확인 재사용 {BodyCheckCacheHits}회\n"
          + $"작은 영역 빠른 관찰 {FastSamples}회 · 점멸 포착 {FastCycles}회 (본문 재확인 후 반영)\n"
          + $"본문 검증을 통과한 점멸 {ValidatedCycles}회 · 본문 변화로 중단 {BodyChangeDiscards}회 · 관찰 만료 {ExpiredWatches}회";
}

// Bounded, session-local evidence. Only mature relative shapes are persisted.
// Identical OCR repeated on the same page cannot increase confidence.
public sealed record MarkerRegionProbe(long Id,PixelRect Region);
public sealed class GameMarkerLearner(MarkerLearningMode mode,IReadOnlyList<GameMarkerPattern> saved)
{
    private sealed class Evidence(GameMarkerPattern pattern,bool mature)
    {
        public GameMarkerPattern Pattern=pattern;
        public bool Mature=mature;
        public readonly HashSet<string> Pages=[];
        public readonly List<(string Id,string Body)> Bodies=[]; // RAM only, bounded, never serialized or reported.
        public readonly HashSet<string> Animated=[];
    }
    private sealed record Watch(Evidence Evidence,PixelRect Region,PixelRect Parent,MarkerBodyShape Body,string Page,
        int Width,int Height,MarkerVisual Initial,long Started)
    {
        public MarkerVisual? Alternate;
        public long ChangedAt;
        public long Id,LastBodyCheck;
        public bool PendingCycle,Component;
    }
    private readonly List<Evidence> evidence=saved.Take(12).Select(p=>new Evidence(p,true)).ToList();
    private readonly List<Watch> watches=[];
    private readonly object sync=new();
    private int excluded,wouldExclude;
    private long nextProbe,fastSamples,fastCycles,validatedCycles,bodyChangeDiscards,expiredWatches;
    private int probeTurn;
    public int Revision {get;private set;}
    public IReadOnlyList<GameMarkerPattern> Confirmed {get {lock(sync)return evidence.Where(e=>e.Mature).Select(e=>e.Pattern).ToArray();}}
    public MarkerLearningStatus Status {get {lock(sync) {
        var best=evidence.MaxBy(e=>e.Mature?20:Math.Min(5,e.Pages.Count)+5*Math.Min(2,e.Animated.Count));
        return new(mode,evidence.Count(e=>!e.Mature),evidence.Count(e=>e.Mature),best is null?0:best.Mature?5:best.Pages.Count,
            best is null?0:best.Mature?2:best.Animated.Count,excluded,wouldExclude)
            {FastSamples=fastSamples,FastCycles=fastCycles,ValidatedCycles=validatedCycles,BodyChangeDiscards=bodyChangeDiscards,ExpiredWatches=expiredWatches};
    }}}
    public MarkerRegionProbe? NextRegionProbe(long now)
    {
        lock(sync) {
            var eligible=watches.Where(w=>!w.Evidence.Mature && !w.PendingCycle && now-w.LastBodyCheck<=1200 && w.Region.Width<=192 && w.Region.Height<=192).ToArray();
            if(eligible.Length==0)return null;var w=eligible[(probeTurn++ & int.MaxValue)%eligible.Length];return new(w.Id,w.Region);
        }
    }
    public void ObserveRegion(MarkerRegionProbe probe,ImageFrame frame,long now)
    {
        lock(sync) {
            var w=watches.FirstOrDefault(w=>w.Id==probe.Id && w.Region==probe.Region);
            if(w is null || w.PendingCycle || now-w.LastBodyCheck>1200 || frame.Width!=probe.Region.Width || frame.Height!=probe.Region.Height)return;
            fastSamples++;
            var local=new PixelRect(0,0,frame.Width,frame.Height);
            Track(w,w.Component?TerminalMarkerScanner.ComponentVisual(frame,local,w.Initial.Chromatic):GameMarkerGeometry.Visual(frame,local),now,true);
        }
    }
    private void Track(Watch w,MarkerVisual visual,long now,bool fast)
    {
        if(now-w.Started<150)return;
        if(w.Alternate is null) {
            if(!w.Initial.Similar(visual) && System.Numerics.BitOperations.PopCount(w.Initial.Mask^visual.Mask)>=6)
                {w.Alternate=visual;w.ChangedAt=now;}
        } else if(now-w.ChangedAt>=100 && w.Initial.Similar(visual)) {
            if(fast){w.PendingCycle=true;fastCycles++;return;}
            CompleteCycle(w);
        }
    }
    private void CompleteCycle(Watch w)
    {
        if(w.Evidence.Animated.Add(w.Page))validatedCycles++;
        if(!w.Initial.Chromatic && w.Alternate is {HasShape:true} alternate && !w.Evidence.Pattern.Shapes.Any(s=>s.Similar(alternate)) && w.Evidence.Pattern.Shapes.Count<8 && !w.Evidence.Mature)
            w.Evidence.Pattern=w.Evidence.Pattern with {Shapes=w.Evidence.Pattern.Shapes.Append(alternate).ToArray()};
        Promote(w.Evidence);watches.Remove(w);
    }
    public void ObserveFrame(ImageFrame frame,long now)
    {
        if(mode==MarkerLearningMode.Off)return;
        lock(sync) foreach(var w in watches.ToArray()) {
            // A changed body/page, crop or capture size ends the observation. Never
            // interpret a page transition as a blinking marker.
            if(w.Evidence.Mature){watches.Remove(w);continue;}
            if(now-w.Started>15000){expiredWatches++;watches.Remove(w);continue;}
            if(frame.Width!=w.Width || frame.Height!=w.Height || !w.Body.Matches(frame)) {bodyChangeDiscards++;watches.Remove(w);continue;}
            w.LastBodyCheck=now;
            if(w.PendingCycle){CompleteCycle(w);continue;}
            var visual=w.Component ? TerminalMarkerScanner.ComponentVisual(frame,w.Region,w.Initial.Chromatic) : GameMarkerGeometry.Visual(frame,w.Region);
            Track(w,visual,now,false);
        }
    }
    public void ObserveOcr(ImageFrame frame,OcrResult result,long now)
    {
        if(mode==MarkerLearningMode.Off)return;
        lock(sync) {
            excluded=result.LearnedMarkerExclusions;wouldExclude=0;
            var lines=result.Candidates??result.Lines;
            var body=lines.Where(l=>!l.Vertical && l.Confidence>=.85 && l.Text.Length>=4 && l.Bounds.Width>=2*l.Bounds.Height).ToArray();
            if(body.Length==0)return;
            // NVL pages append text without altering the already verified rows.
            // Such old watches must not occupy both slots until the timeout.
            // A blink's short noise box is not a new body row.
            foreach(var w in watches.ToArray())if(body.Any(l=>
                l.Bounds.Y>=w.Parent.Y+w.Parent.Height*.8
                || Math.Abs(l.Bounds.Y-w.Parent.Y)<w.Parent.Height*.5
                    && l.Bounds.X+l.Bounds.Width>w.Parent.X+w.Parent.Width+w.Parent.Height*.5)) {
                bodyChangeDiscards++;watches.Remove(w);
            }
            var bodyText=new string(string.Concat(body.OrderBy(l=>l.Bounds.Y).ThenBy(l=>l.Bounds.X).Select(l=>l.Text))
                .Where(c=>c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff').Take(4096).ToArray());
            if(bodyText.Length<4)return;
            var observations=(result.TerminalMarkers??[]).Take(2).ToList();
            foreach(var region in result.DetectedBoxes.Take(1000)) {
                if(observations.Any(s=>GameMarkerGeometry.Intersects(s.Bounds,region)))continue;
                if(GameMarkerGeometry.Locate(region,lines,result.DetectedBoxes) is not {} location)continue;
                var visual=GameMarkerGeometry.Visual(frame,region);if(!visual.HasShape)continue;
                observations.Add(new(region,new("",1,location.Parent),location,visual));
            }
            foreach(var sample in observations) {
                var region=sample.Bounds;var location=sample.Location;var visual=sample.Visual;
                var match=evidence.FirstOrDefault(e=>GameMarkerGeometry.AppearanceMatches(e.Pattern,sample))
                    ?? evidence.FirstOrDefault(e=>!e.Mature && SameFamily(e.Pattern,sample));
                if(match is null) {
                    if(evidence.Count>=12) {
                        var weak=evidence.Where(e=>!e.Mature && e.Animated.Count==0).OrderBy(e=>e.Pages.Count).FirstOrDefault();
                        if(weak is null)continue;
                        evidence.Remove(weak);watches.RemoveAll(w=>ReferenceEquals(w.Evidence,weak));
                    }
                    match=new(new(location.Anchor,location.X,location.Y,location.Width,location.Height,[visual]),false);evidence.Add(match);
                }
                if(match.Mature) {wouldExclude++;continue;}
                if(!AddVariant(match,sample))continue;
                // Appending an NVL sentence changes the terminal utterance even
                // when most earlier rows stay identical. Compare that utterance.
                string terminalText=sample.Parent.Text.Length==0 ? bodyText : new string(sample.Parent.Text.Where(c=>c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff').ToArray());
                if(terminalText.Length<8)terminalText=bodyText;
                var prior=match.Bodies.FirstOrDefault(p=>SamePage(p.Body,terminalText));
                string page;
                if(prior.Id is not null)page=prior.Id;
                else {
                    if(match.Pages.Count>=32) {
                        // Bound memory without disabling late blink evidence.
                        var oldest=match.Bodies.FirstOrDefault(p=>!match.Animated.Contains(p.Id));
                        if(oldest.Id is null)oldest=match.Bodies[0];
                        match.Bodies.Remove(oldest);match.Pages.Remove(oldest.Id);match.Animated.Remove(oldest.Id);
                    }
                    page=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(terminalText)));
                    match.Bodies.Add((page,terminalText));match.Pages.Add(page);
                }
                Promote(match);
                if(match.Mature || match.Animated.Contains(page))continue;
                if(!watches.Any(w=>ReferenceEquals(w.Evidence,match) && w.Page==page) && watches.Count<2) {
                    var bounds=body.Where(l=>sample.Parent.Text.Length==0 || Math.Abs(l.Bounds.Y-sample.Parent.Bounds.Y)>l.Bounds.Height*.5)
                        .Select(l=>l.Bounds).Concat(sample.Parent.Text.Length==0 ? [] : new[]{sample.Parent.Bounds}).ToArray();
                    // For detecting later extensions, remember the original OCR
                    // extent: a joined icon may extend beyond the verified crop.
                    var extent=lines.FirstOrDefault(l=>Math.Abs(l.Bounds.X-location.Parent.X)<=location.Parent.Height*.25
                        && Math.Abs(l.Bounds.Y-location.Parent.Y)<=location.Parent.Height*.25)?.Bounds??location.Parent;
                    if(MarkerBodyShape.Create(frame,bounds) is {} shape)
                        watches.Add(new(match,region,extent,shape,page,frame.Width,frame.Height,visual,now){Id=++nextProbe,LastBodyCheck=now,Component=sample.Parent.Text.Length>0});
                }
            }
        }
    }
    private static bool SameFamily(GameMarkerPattern p,TerminalMarkerSample s)
    {
        if(!p.Shapes.Any(v=>v.Chromatic==s.Visual.Chromatic && Math.Abs(v.Red-s.Visual.Red)+Math.Abs(v.Green-s.Visual.Green)+Math.Abs(v.Blue-s.Visual.Blue)<=60
            && (s.Visual.Chromatic || v.Similar(s.Visual))))return false;
        // Location is already constrained to a complete terminal row / following
        // row by the scanner. A family may wrap onto the following row or rotate.
        double ratio=p.Width*p.Height/(s.Location.Width*s.Location.Height);
        return ratio is >= .35 and <= 2.85;
    }
    private static bool AddVariant(Evidence e,TerminalMarkerSample s)
    {
        if(!e.Pattern.Shapes.Any(v=>v.Similar(s.Visual))) {
            if(e.Pattern.Shapes.Count>=8)return false;
            e.Pattern=e.Pattern with {Shapes=e.Pattern.Shapes.Append(s.Visual).ToArray()};
        }
        if(!GameMarkerGeometry.GeometryMatches(e.Pattern,s.Location)) {
            var placements=e.Pattern.Placements??[];if(placements.Count>=8)return false;
            e.Pattern=e.Pattern with {Placements=placements.Append(new(s.Location.Anchor,s.Location.X,s.Location.Y,s.Location.Width,s.Location.Height)).ToArray()};
        }
        return true;
    }
    private void Promote(Evidence item)
    {
        if(!item.Mature && item.Pages.Count>=5 && item.Animated.Count>=2) {
            item.Mature=true;Revision++;item.Bodies.Clear();item.Pages.Clear();item.Animated.Clear();
        }
    }
    private static bool SamePage(string a,string b)
    {
        // OCR punctuation/ASCII noise is already removed. Also tolerate a few
        // recognition substitutions, rather than counting them as new dialogue.
        int common=Math.Min(a.Length,b.Length), differences=Math.Abs(a.Length-b.Length);
        for(int i=0;i<common;i++)if(a[i]!=b[i])differences++;
        return differences<Math.Max(4,Math.Max(a.Length,b.Length)/5);
    }
}
