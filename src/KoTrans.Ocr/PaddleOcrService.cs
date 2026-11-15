using System.Diagnostics;
using System.Runtime.InteropServices;
using KoTrans.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace KoTrans.Ocr;

public sealed class PaddleOcrService(string modelDirectory, OcrExecutionOptions? execution = null) : IOcrService
{
    private readonly OcrExecutionOptions execution = execution ?? ModelCatalog.PreferredExecution(modelDirectory);
    private readonly SemaphoreSlim gate = new(1, 1);
    private InferenceSession? detector, recognizer;
    private ModelSpec? detSpec, recSpec;
    private OcrRunIdentity? identity;
    private string[] dictionary = [];
    private string loadedDetector = "", loadedRecognizer = "";
    private readonly MarkerBodyRecognitionCache markerBodies=new();
    private readonly OcrRecognitionCache recognitionCache=new();
    private bool bypassRecognitionCache;
    private bool disposed;
    public OcrState State { get; private set; } = OcrState.NotConfigured;
    public TimeSpan LastLoadTime { get; private set; }
    private static void Validate(OcrOptions options)
    {
        if(!GameMarkerGeometry.ValidPatterns(options.MarkerPatterns))throw new ArgumentException("문장 끝 표시 학습 정보가 올바르지 않습니다.");
        if(!TerminalMarkerReference.Valid(options.MarkerReference))throw new ArgumentException("문장 끝 표시의 임시 본문 정보가 올바르지 않습니다.");
        if (options.DetectionLongSide != 0 && options.DetectionLongSide is < 32 or > 1920) throw new ArgumentException("검출 해상도 범위를 확인하세요.");
        if (options.Language is not ("en" or "ja") || options.ReadingMode is not ("ADV" or "NVL")) throw new ArgumentException("지원 언어는 영어/일본어, 읽기 모드는 ADV/NVL입니다.");
    }
    public async Task InitializeAsync(OcrOptions options, CancellationToken cancellationToken)
    {
        Validate(options); await gate.WaitAsync(cancellationToken);
        try { await Task.Run(() => OcrExecutionPolicy.Run(() => Load(options, cancellationToken)), cancellationToken); }
        finally { gate.Release(); }
    }
    private void Load(OcrOptions options, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this); token.ThrowIfCancellationRequested(); LastLoadTime = TimeSpan.Zero;
        if (options.ModelPresetId != execution.ModelPresetId) throw new OcrInitializationException("선택한 모델 구성과 OCR 세션이 다릅니다. 세션을 다시 선택하세요.");
        var selection = PaddleModelSelection.Resolve(modelDirectory, execution, options.Language);
        string detKey = selection.Detector.Stamp(), recKey = selection.Recognizer.Stamp();
        if (detector is not null && recognizer is not null && loadedDetector == detKey && loadedRecognizer == recKey) {
            if (!recSpec!.Languages.Contains(options.Language)) throw new OcrInitializationException("선택한 인식 모델이 요청 언어를 지원하지 않습니다.");
            State = OcrState.Ready; return;
        }
        State = OcrState.Loading; var clock = Stopwatch.StartNew();
        try
        {
            execution.Validate();
            using var sessionOptions = OcrExecutionPolicy.CreateSessionOptions(execution.ThreadCount);
            if (detector is null || loadedDetector != detKey)
            {
                var det = ModelCatalog.Load(selection.Detector.Root, selection.Detector.Folder);
                detector?.Dispose(); detector = null; loadedDetector = "";
                detector = OcrExecutionPolicy.CreateSession(det.Path, sessionOptions); ValidateSession(detector, det.Spec, 0);
                detSpec = det.Spec; loadedDetector = detKey;
            }
            token.ThrowIfCancellationRequested();
            if (recognizer is null || loadedRecognizer != recKey)
            {
                markerBodies.Clear();recognitionCache.Clear();
                var rec = ModelCatalog.Load(selection.Recognizer.Root, selection.Recognizer.Folder);
                if (!rec.Spec.Languages.Contains(options.Language)) throw new InvalidDataException("모델 manifest가 요청 언어를 지원하지 않습니다.");
                // One recognizer at a time, identified by actual files and contract, not just language.
                recognizer?.Dispose(); recognizer = null; loadedRecognizer = "";
                recognizer = OcrExecutionPolicy.CreateSession(rec.Path, sessionOptions); ValidateSession(recognizer, rec.Spec, rec.Characters.Length);
                recSpec = rec.Spec; dictionary = rec.Characters; loadedRecognizer = recKey;
            }
            if (selection.Detector.Stamp() != detKey || selection.Recognizer.Stamp() != recKey)
                throw new InvalidDataException("로딩 중 모델 파일이 변경되었습니다. 파일 준비를 마친 뒤 다시 시도하세요.");
            identity = new(OcrIdentity.From(detSpec!), OcrIdentity.From(recSpec!), execution.AllowUpscale, execution.ThreadCount) { ModelPresetId = execution.ModelPresetId, FlushDenormalsToZero = true };
            LastLoadTime = clock.Elapsed; State = OcrState.Ready; token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { State = recognizer is null ? OcrState.NotConfigured : OcrState.Ready; throw; }
        catch (Exception ex) {
            State = OcrState.Failed; markerBodies.Clear();recognitionCache.Clear();detector?.Dispose(); detector = null; recognizer?.Dispose(); recognizer = null; loadedDetector = loadedRecognizer = "";
            if(ex is FileNotFoundException or DirectoryNotFoundException)
                throw new OcrInitializationException("OCR 모델 또는 문자 사전 파일을 찾을 수 없습니다. models 폴더나 KO_TRANS2_MODELS 경로를 확인하세요. 이 OCR 요청은 번역 API로 전송되지 않았습니다.");
            if(ex is InvalidDataException)
                throw new OcrInitializationException("OCR 모델·사전의 형식 또는 SHA-256 검증에 실패했습니다. 정상 models 파일을 복원하세요. " + ex.Message + " 이 OCR 요청은 번역 API로 전송되지 않았습니다.");
            if(ex is UnauthorizedAccessException)
                throw new OcrInitializationException("OCR 모델·사전 파일을 읽을 권한이 없습니다. 파일 읽기 권한을 확인하세요. 이 OCR 요청은 번역 API로 전송되지 않았습니다.");
            throw;
        }
    }
    private static void ValidateSession(InferenceSession session, ModelSpec spec, int characters)
    {
        if (session.InputMetadata.Count != 1 || session.OutputMetadata.Count != 1
            || session.InputMetadata.Values.Single().ElementType != typeof(float)
            || session.InputMetadata.Values.Single().Dimensions.Length != 4 || session.InputMetadata.Values.Single().Dimensions[1] != 3
            || session.OutputMetadata.Values.Single().ElementType != typeof(float))
            throw new InvalidDataException("지원하지 않는 ONNX 입력/출력 계약입니다.");
        var output = session.OutputMetadata.Values.Single().Dimensions;
        if (spec.Kind == "detection" ? output.Length != 4 || output[1] != 1
            : output.Length != 3 || output[2] > 0 && output[2] != characters + 1)
            throw new InvalidDataException("ONNX DB/CTC 출력 또는 문자 사전 크기가 다릅니다.");
        if (spec.TensorContract is { } contract && (session.InputMetadata.Keys.Single() != contract.InputName
            || session.OutputMetadata.Keys.Single() != contract.OutputName
            || !session.InputMetadata.Values.Single().Dimensions.SequenceEqual(contract.InputShape)
            || !output.SequenceEqual(contract.OutputShape)))
            throw new InvalidDataException("ONNX tensor 이름/shape가 모델 manifest와 다릅니다.");
    }
    public async Task<OcrResult> RecognizeAsync(ImageFrame frame, OcrOptions options, CancellationToken cancellationToken)
    {
        Validate(options);
        if(options.SearchArea is { } requested && (!requested.IsValid || requested.X<0 || requested.Y<0 || requested.X+(long)requested.Width>frame.Width || requested.Y+(long)requested.Height>frame.Height))
            throw new ArgumentException("OCR 탐색 영역이 이미지 밖입니다.");
        if(options.KeepLocalParagraph && options.SearchArea is null) throw new ArgumentException("문단 추적에는 탐색 영역이 필요합니다.");
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() => OcrExecutionPolicy.Run(() =>
            {
                Load(options, cancellationToken); var loadTime = LastLoadTime; var clock = Stopwatch.StartNew();
                recognitionCache.BeginRequest();bypassRecognitionCache=options.BypassOcrCaches;
                if(bypassRecognitionCache)markerBodies.Clear();
                using var fullImage = ToBgr(frame);
                var area = options.SearchArea ?? new PixelRect(0, 0, frame.Width, frame.Height);
                if (!area.IsValid || area.X < 0 || area.Y < 0 || area.X + (long)area.Width > frame.Width || area.Y + (long)area.Height > frame.Height) throw new ArgumentException("OCR 탐색 영역이 이미지 밖입니다.");
                using var image = new Mat(fullImage, new Rect(area.X, area.Y, area.Width, area.Height));
                // Capture stays exactly as requested. Add background pixels only after capture/ROI crop.
                // One padded detection replaces the old unpadded + conditional boundary retry.
                var usedPadding = DetectionBorder.Default(image.Width,image.Height);
                using var padded = new Mat(); var recognitionImage = image;
                if (usedPadding is not null) {
                    Cv2.CopyMakeBorder(image,padded,usedPadding.Top,usedPadding.Bottom,usedPadding.Left,usedPadding.Right,BorderTypes.Constant,Scalar.Black);
                    recognitionImage = padded;
                }
                var conversionTime = clock.Elapsed;
                TimeSpan preparation, inference, post; (int Width, int Height) detectorSize;
                var boxes = Detect(recognitionImage, cancellationToken, out preparation, out inference, out post, out detectorSize, options.DetectionLongSide);
                if (usedPadding is not null)
                    boxes = boxes.Where(b=>DetectionBorder.Restore(b.Bounds,usedPadding,image.Width,image.Height) is not null).ToList();
                var detectionTime = clock.Elapsed;
                bool dialogueRegion = DialogueRegionSelection.Supported(options);
                // DB unclip already provides vertical padding. Select first,
                // then protect word ends with only a few horizontal pixels.
                var selectedBoxes = dialogueRegion ? DialogueRegionSelection.Select(boxes, b => b.Bounds).ToList() : boxes;
                var recognitionBoxes = JoinDetectionFragments(selectedBoxes);
                var mergeTime = clock.Elapsed - detectionTime;
                var lines = new List<OcrLine>();
                List<OcrMarkerObservation>? markerDiagnostics = options.CollectMarkerDiagnostics ? [] : null;
                int markerObservations = 0;
                void ObserveMarker(OcrMarkerObservation item) {
                    markerObservations++;
                    if(markerDiagnostics!.Count == OcrMarkerDiagnostics.Limit) markerDiagnostics.RemoveAt(0);
                    markerDiagnostics.Add(item);
                }
                var learnedPatterns = GameMarkerGeometry.Supported(options) ? options.MarkerPatterns : null;
                int learnedExclusions = 0,markerBodyChecks=0,markerBodyCacheHits=0;
                // A learned marker is checked against the body recognized in THIS
                // request. No stale absolute rectangle is blanked before detection.
                var orderedBoxes = learnedPatterns is {Count:>0}
                    ? recognitionBoxes.OrderByDescending(b=>b.Bounds.Height).ToList() : recognitionBoxes;
                foreach (var box in orderedBoxes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (learnedPatterns is {Count:>0} && GameMarkerGeometry.CanExclude(frame,Shift(box.Bounds),
                        lines.Select(l=>l with {Bounds=Shift(l.Bounds)}).ToArray(),recognitionBoxes.Select(b=>Shift(b.Bounds)).ToArray(),learnedPatterns)) {
                        learnedExclusions++;continue;
                    }
                    bool vertical = !dialogueRegion && box.Bounds.Height > box.Bounds.Width * 1.5 && options.Language == "ja";
                    // A large false detector box can make a complete dialogue row
                    // look like ruby. Recognize both before judging kana/kanji evidence.
                    // LayoutProcessor performs the shared post-recognition suppression.
                    var recognitionBox = box;
                    if (dialogueRegion) {
                        var extended = DialogueRegionSelection.WithEndMargin(box.Bounds, recognitionBoxes.Select(b => b.Bounds).ToArray(), recognitionImage.Width);
                        var points = box.Points.ToArray();
                        int left = box.Bounds.X - extended.X, right = extended.X + extended.Width - box.Bounds.X - box.Bounds.Width;
                        points[0].X -= left; points[3].X -= left;
                        points[1].X = Math.Min(recognitionImage.Width - 1, points[1].X + right);
                        points[2].X = Math.Min(recognitionImage.Width - 1, points[2].X + right);
                        recognitionBox = new(points, extended);
                    }
                    if (!dialogueRegion && options.Language == "ja" && !vertical && Math.Abs(box.Points[1].Y-box.Points[0].Y) <= 3) {
                        var extended = RecognitionTail.Extend(recognitionImage,box.Bounds,recognitionBoxes.Select(b=>b.Bounds).ToArray());
                        if (extended != box.Bounds) {
                            var points = box.Points.ToArray();
                            points[1].X = points[2].X = extended.X+extended.Width-1;
                            recognitionBox = new(points,extended);
                        }
                    }
                    using var crop = Crop(recognitionImage, recognitionBox.Points, vertical);
                    var text = Recognize(crop, cancellationToken);
                    if (!dialogueRegion && options.Language == "ja" && !vertical && Math.Abs(recognitionBox.Points[1].Y-recognitionBox.Points[0].Y) <= 3) {
                        var proposed = ContinuationMarker.SeparateTail(recognitionImage, recognitionBox.Bounds, out var decision);
                        var originalLine = markerDiagnostics is null ? null : new OcrLine(text.Text,text.Confidence,recognitionBox.Bounds);
                        OcrLine? confirmedBody = null;
                        if(proposed is { } body) {
                            var points = recognitionBox.Points.ToArray(); points[1].X = points[2].X = body.X+body.Width-1;
                            using var bodyCrop = Crop(recognitionImage, points, false);
                            int priorInferences=recognitionCache.Stats.Inferences;
                            var bodyText = Recognize(bodyCrop,cancellationToken);
                            markerBodyChecks+=recognitionCache.Stats.Inferences-priorInferences;
                            if(recognitionCache.Stats.Inferences==priorInferences)markerBodyCacheHits++;
                            confirmedBody = markerDiagnostics is null ? null : new(bodyText.Text,bodyText.Confidence,body);
                            // Geometry/colour alone cannot justify trimming a real suffix.
                            decision=bodyText.Confidence < .8f ? OcrMarkerDecision.CropLowConfidence : OcrMarkerDecision.CropIncompleteBody;
                            if (bodyText.Confidence >= .8f && ContinuationMarker.CompletedJapanese(bodyText.Text)) {
                                text = bodyText; recognitionBox = new(points,body); decision=OcrMarkerDecision.CropAccepted;
                            }
                        }
                        if(originalLine is not null) {
                            var observation=OcrMarkerDiagnostics.Describe(originalLine,OcrMarkerStage.JoinedTail,decision,confirmedBody);
                            if(observation.TrailingLatinDigits>0 || proposed is not null)
                                ObserveMarker(observation with {Bounds=Shift(observation.Bounds),ParentBounds=confirmedBody is null ? null : Shift(confirmedBody.Bounds)});
                        }
                    }
                    if (text.Confidence >= .45 && !string.IsNullOrWhiteSpace(text.Text))
                        lines.Add(new(TextProcessing.Normalize(text.Text, options.Language), text.Confidence, recognitionBox.Bounds, vertical));
                }
                cancellationToken.ThrowIfCancellationRequested();
                var recognitionTime = clock.Elapsed - detectionTime - mergeTime;
                PixelRect Shift(PixelRect box) {
                    var source = usedPadding is null ? box : DetectionBorder.Restore(box,usedPadding,image.Width,image.Height)
                        ?? throw new InvalidDataException("가상 여백 좌표를 원본 이미지로 복원할 수 없습니다.");
                    return source with { X = source.X + area.X, Y = source.Y + area.Y };
                }
                lines = lines.Select(l => l with { Bounds = Shift(l.Bounds) }).ToList();
                var recoveryClock = Stopwatch.StartNew();
                if (!dialogueRegion) {
                    lines = OverlappingRowRecovery.Recover(fullImage, lines, options, bounds => {
                        cancellationToken.ThrowIfCancellationRequested();
                        using var crop = new Mat(fullImage, new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height));
                        return Recognize(crop, cancellationToken);
                    });
                    lines = DialoguePrefixRecovery.Recover(fullImage, lines, options, crop => {
                        cancellationToken.ThrowIfCancellationRequested();
                        return Recognize(crop, cancellationToken);
                    });
                    lines = DialogueInkFilter.Exclude(fullImage, lines, options);
                }
                recognitionTime += recoveryClock.Elapsed;
                var terminalProposals = options.CollectMarkerLearning && GameMarkerGeometry.Supported(options)
                    ? TerminalMarkerScanner.Scan(frame,lines,proposeJoinedNeutral:true) : null;
                var referenceParent=terminalProposals is not null ? options.MarkerReference?.CurrentParent(frame,lines) : null;
                var referenceProposals=referenceParent is null?[]:TerminalMarkerScanner.Scan(frame,options.MarkerReference!.Lines,proposeJoinedNeutral:true);
                var proposals=referenceProposals.Select(s=>(Sample:s,Parent:referenceParent,Verify:true)).Concat((terminalProposals??[])
                    .Select(s=>(Sample:s,Parent:lines.FirstOrDefault(l=>l.Bounds.X==s.Parent.Bounds.X && l.Bounds.Y==s.Parent.Bounds.Y && l.Bounds.Height==s.Parent.Bounds.Height),Verify:false)));
                List<TerminalMarkerSample>? terminalSamples=terminalProposals is null?null:[];
                int verificationAttempts=0;
                foreach(var proposal in proposals) {
                    // A rejected punctuation crop must not crowd a genuine
                    // following-row proposal out of the two observation slots.
                    if(terminalSamples!.Count==2)break;
                    var sample=proposal.Sample;var parent=proposal.Parent;
                    if(parent is null || !lines.Any(l=>ReferenceEquals(l,parent)))continue;
                    if(terminalSamples.Any(s=>GameMarkerGeometry.Intersects(s.Bounds,sample.Bounds)))continue;
                    bool verifyProposal=proposal.Verify || TerminalMarkerScanner.RequiresBodyConfirmation(sample,parent,false);
                    bool learned=learnedPatterns?.Any(p=>GameMarkerGeometry.AppearanceMatches(p,sample))==true;
                    var noise=lines.Where(l=>!ReferenceEquals(l,parent) && TerminalMarkerScanner.SeparateNoise(sample,l)).ToArray();
                    // Joined text-coloured indicators used to be rejected before
                    // learning. A fresh crop check makes observation possible,
                    // without treating an OCR box or a learned shape as proof
                    // that the proposed crop preserves the sentence.
                    if(!verifyProposal && (!learned || !TerminalMarkerScanner.RequiresBodyConfirmation(sample,parent,noise.Length>0))) {
                        terminalSamples!.Add(sample);continue;
                    }
                    OcrLine bodyLine;
                    if(!proposal.Verify && TerminalMarkerScanner.FreshBodyConfirms(sample,parent))bodyLine=parent;
                    else {
                        if(++verificationAttempts>4)continue;
                        var confirmClock=Stopwatch.StartNew();var b=sample.Parent.Bounds;
                        var bodyKey=MarkerBodyRecognitionCache.Key(frame,b,loadedRecognizer+":"+options.Language);
                        (string Text,float Confidence) confirmed;
                        if(!bypassRecognitionCache && markerBodies.TryGet(bodyKey,out confirmed))markerBodyCacheHits++;
                        else {
                            using var bodyCrop=Crop(fullImage,[new(b.X,b.Y),new(b.X+b.Width-1,b.Y),new(b.X+b.Width-1,b.Y+b.Height-1),new(b.X,b.Y+b.Height-1)],false);
                            int priorInferences=recognitionCache.Stats.Inferences;
                            confirmed=Recognize(bodyCrop,cancellationToken);
                            markerBodyChecks+=recognitionCache.Stats.Inferences-priorInferences;
                            if(recognitionCache.Stats.Inferences==priorInferences)markerBodyCacheHits++;
                            markerBodies.Remember(bodyKey,confirmed);
                        }
                        recognitionTime+=confirmClock.Elapsed;
                        bodyLine=new OcrLine(TextProcessing.Normalize(confirmed.Text,options.Language),confirmed.Confidence,b);
                    }
                    // Learned placement/colour/shape proposes a crop. Only exact
                    // body text from this request or these identical crop pixels
                    // authorizes its use. Joined/reference crops are still checked.
                    if(!TerminalMarkerScanner.ConfirmsBody(sample.Parent.Text,bodyLine))continue;
                    terminalSamples!.Add(sample);
                    if(!learned)continue;
                    lines.RemoveAll(l=>ReferenceEquals(l,parent)||noise.Contains(l));lines.Add(bodyLine);learnedExclusions++;
                }
                var layout = options.KeepLocalParagraph ? options with { ReadingMode = "NVL" } : options;
                var bodyLines = dialogueRegion ? lines : ContinuationMarker.Exclude(lines, fullImage, options, markerDiagnostics is null ? null : ObserveMarker);
                var arranged = dialogueRegion ? DialogueRegionSelection.Arrange(bodyLines) : LayoutProcessor.Arrange(bodyLines, layout, frame.Width, frame.Height);
                return new OcrResult(arranged, clock.Elapsed)
                { RecognitionCache=recognitionCache.Stats, Candidates = lines.ToArray(), MarkerDiagnostics=markerDiagnostics?.ToArray(), MarkerObservations=markerObservations, LearnedMarkerExclusions=learnedExclusions, MarkerBodyChecks=markerBodyChecks, MarkerBodyCacheHits=markerBodyCacheHits, TerminalMarkers=terminalSamples,
                  Models = identity, ModelLoadTime = loadTime, DetectionTime = detectionTime, RecognitionTime = recognitionTime,
                  PreprocessTime = conversionTime + preparation, DetectorInferenceTime = inference, DetectionPostprocessTime = post,
                  MergeTime = mergeTime, DetectorWidth = detectorSize.Width, DetectorHeight = detectorSize.Height,
                  PostprocessTime = clock.Elapsed - detectionTime - mergeTime - recognitionTime,
                  DetectionPasses = 1, DetectorPadding = usedPadding, DetectedRegions = boxes.Count, DetectedBoxes = boxes.Select(b => Shift(b.Bounds)).ToArray() };
            }), cancellationToken);
        }
        finally { gate.Release(); }
    }
    public static Mat ToBgr(ImageFrame frame) => OcrImage.ToBgr(frame);
    internal sealed record DetectedBox(Point2f[] Points, PixelRect Bounds);
    private static List<DetectedBox> JoinDetectionFragments(List<DetectedBox> boxes)
    {
        var joined = new List<DetectedBox>();
        foreach (var source in boxes)
        {
            var current = source;
            for (int i = 0; i < joined.Count; i++)
            {
                var other = joined[i];
                if (!LayoutProcessor.ShouldJoinDetectionFragments(current.Bounds, other.Bounds)) continue;
                var a = current.Points[1] - current.Points[0]; var b = other.Points[1] - other.Points[0];
                double lengths = Math.Sqrt((a.X * a.X + a.Y * a.Y) * (double)(b.X * b.X + b.Y * b.Y));
                if (lengths == 0 || Math.Abs(a.X * b.X + a.Y * b.Y) / lengths < Math.Cos(Math.PI / 18)) continue;
                var points = Order(Cv2.MinAreaRect(current.Points.Concat(other.Points)).Points());
                int x = Math.Min(current.Bounds.X, other.Bounds.X), y = Math.Min(current.Bounds.Y, other.Bounds.Y);
                var bounds = new PixelRect(x, y,
                    Math.Max(current.Bounds.X + current.Bounds.Width, other.Bounds.X + other.Bounds.Width) - x,
                    Math.Max(current.Bounds.Y + current.Bounds.Height, other.Bounds.Y + other.Bounds.Height) - y);
                current = new(points, bounds); joined.RemoveAt(i); i = -1;
            }
            joined.Add(current);
        }
        return joined;
    }
    private List<DetectedBox> Detect(Mat image, CancellationToken token, out TimeSpan preparation, out TimeSpan inference, out TimeSpan post, out (int Width, int Height) size, int longSide = 0)
    {
        var clock = Stopwatch.StartNew();
        var spec = detSpec!;
        size = OcrExecutionOptions.DetectionSize(image.Width, image.Height, longSide == 0 ? spec.DetectionLongSide : longSide, execution.AllowUpscale);
        int w = size.Width, h = size.Height;
        using var resized = new Mat(); Cv2.Resize(image, resized, new Size(w, h));
        var input = TensorFrom(resized, w, h, spec);
        preparation = clock.Elapsed;
        using var results = detector!.Run([NamedOnnxValue.CreateFromTensor(detector.InputMetadata.Keys.Single(), input)]);
        inference = clock.Elapsed - preparation;
        token.ThrowIfCancellationRequested(); var output = results.First().AsTensor<float>();
        if (output.Rank != 4 || output.Dimensions[0] != 1 || output.Dimensions[1] != 1) throw new InvalidDataException("DB 출력은 [1,1,H,W]이어야 합니다.");
        int oh = output.Dimensions[2], ow = output.Dimensions[3]; var values = output.ToArray();
        using var probability = new Mat(oh, ow, MatType.CV_32FC1); Marshal.Copy(values, 0, probability.Data, values.Length);
        using var bitmap = new Mat(); Cv2.Threshold(probability, bitmap, spec.Threshold, 255, ThresholdTypes.Binary); bitmap.ConvertTo(bitmap, MatType.CV_8UC1);
        Cv2.FindContours(bitmap, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        var boxes = new List<DetectedBox>();
        foreach (var contour in contours.OrderByDescending(c => Math.Abs(Cv2.ContourArea(c))).Take(1000))
        {
            token.ThrowIfCancellationRequested(); if (contour.Length < 3) continue;
            var rect = Cv2.MinAreaRect(contour); if (Math.Min(rect.Size.Width, rect.Size.Height) < 3) continue;
            var bounds = Cv2.BoundingRect(contour) & new Rect(0, 0, ow, oh);
            using var mask = new Mat(bounds.Height, bounds.Width, MatType.CV_8UC1, Scalar.Black);
            Cv2.FillPoly(mask, [contour.Select(p => new Point(p.X - bounds.X, p.Y - bounds.Y)).ToArray()], Scalar.White);
            using var region = new Mat(probability, bounds);
            if (Cv2.Mean(region, mask).Val0 < spec.BoxThreshold) continue;
            // DB unclip distance A*r/P. For a min-area rectangle, expanding both axes by 2d
            // gives the same enclosing rectangle as a round polygon offset.
            float distance = rect.Size.Width * rect.Size.Height * spec.UnclipRatio / (2 * (rect.Size.Width + rect.Size.Height));
            rect.Size = new Size2f(rect.Size.Width + 2 * distance, rect.Size.Height + 2 * distance);
            var points = Order(rect.Points()).Select(p => new Point2f(Math.Clamp(p.X * image.Width / ow, 0, image.Width - 1), Math.Clamp(p.Y * image.Height / oh, 0, image.Height - 1))).ToArray();
            int x = (int)points.Min(p => p.X), y = (int)points.Min(p => p.Y);
            var area = new PixelRect(x, y, Math.Max(1, (int)Math.Ceiling(points.Max(p => p.X)) - x), Math.Max(1, (int)Math.Ceiling(points.Max(p => p.Y)) - y));
            boxes.Add(new(points, area));
        }
        post = clock.Elapsed - preparation - inference; return boxes;
    }
    private static Point2f[] Order(Point2f[] points)
    {
        var x = points.OrderBy(p => p.X).ToArray(); var left = x.Take(2).OrderBy(p => p.Y).ToArray(); var right = x.Skip(2).OrderBy(p => p.Y).ToArray();
        return [left[0], right[0], right[1], left[1]];
    }
    private static Mat Crop(Mat image, Point2f[] points, bool vertical)
    {
        static double Length(Point2f a, Point2f b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        int w = Math.Max(2, (int)Math.Max(Length(points[0], points[1]), Length(points[2], points[3])));
        int h = Math.Max(2, (int)Math.Max(Length(points[0], points[3]), Length(points[1], points[2])));
        using var transform = Cv2.GetPerspectiveTransform(points, [new(0, 0), new(w - 1, 0), new(w - 1, h - 1), new(0, h - 1)]);
        var crop = new Mat(); Cv2.WarpPerspective(image, crop, transform, new Size(w, h), InterpolationFlags.Cubic, BorderTypes.Replicate);
        if (vertical) Cv2.Rotate(crop, crop, RotateFlags.Rotate90Counterclockwise);
        return crop;
    }
    private (string Text, float Confidence) Recognize(Mat crop, CancellationToken token)
    {
        var spec = recSpec!;
        int resizedWidth = Math.Clamp((int)Math.Ceiling(spec.Height * (double)crop.Width / crop.Height), 1, spec.MaxWidth);
        int width = Math.Max(spec.MinWidth, resizedWidth);
        using var resized = new Mat(); Cv2.Resize(crop, resized, new Size(resizedWidth, spec.Height));
        var input = TensorFrom(resized, width, spec.Height, spec);
        return recognitionCache.Recognize(input.Buffer.Span,width,spec.Height,loadedRecognizer,bypassRecognitionCache,token,()=> {
            using var result = recognizer!.Run([NamedOnnxValue.CreateFromTensor(recognizer.InputMetadata.Keys.Single(), input)]);
            token.ThrowIfCancellationRequested(); var output = result.First().AsTensor<float>();
            if (output.Rank != 3 || output.Dimensions[0] != 1) throw new InvalidDataException("CTC 출력은 [1,T,C]이어야 합니다.");
            return TextProcessing.DecodeCtc(output.ToArray(), output.Dimensions[1], output.Dimensions[2], dictionary);
        });
    }
    internal static unsafe DenseTensor<float> TensorFrom(Mat image, int width, int height, ModelSpec spec)
    {
        var values = new float[checked(3 * height * width)];
        // Exactly the same float expression, evaluated once for each byte/channel instead of each pixel.
        Span<float> normalization = stackalloc float[3 * 256];
        for (int c = 0; c < 3; c++) for (int p = 0; p < 256; p++) normalization[c * 256 + p] = (p / 255f - spec.Mean[c]) / spec.Std[c];
        int plane = width * height;
        for (int y = 0; y < image.Height; y++)
        {
            var row = (byte*)image.Ptr(y); int offset = y * width;
            for (int x = 0; x < image.Width; x++)
            {
                values[offset + x] = normalization[row[x * 3]];
                values[plane + offset + x] = normalization[256 + row[x * 3 + 1]];
                values[2 * plane + offset + x] = normalization[512 + row[x * 3 + 2]];
            }
        }
        return new DenseTensor<float>(values, [1, 3, height, width]);
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try { if (disposed) return; disposed = true; markerBodies.Clear();recognitionCache.Clear();detector?.Dispose(); recognizer?.Dispose(); detector = recognizer = null; State = OcrState.NotConfigured; }
        finally { gate.Release(); }
    }
}
