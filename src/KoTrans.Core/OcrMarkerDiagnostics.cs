namespace KoTrans.Core;

// Deliberately contains no recognized text, hashes, pixels, titles or file paths.
public enum OcrMarkerStage { SeparateCandidate, JoinedTail }
public enum OcrMarkerDecision { PolicyDisabled, AdvLetterPreserved, NotShortCandidate, OutsideImage,
    MultipleColourComponents, NoCompletedParent, GeometryMismatch, BodyOverlap, ColourUncertain,
    ExcludedDifferentColour, ExcludedChromaticCore, InvalidBox, NoUniqueTailComponent,
    InsufficientNeutralBody, NeutralInMargin, CropProposed, CropAccepted, CropLowConfidence, CropIncompleteBody }
public sealed record OcrMarkerObservation(OcrMarkerStage Stage, OcrMarkerDecision Decision,
    PixelRect Bounds, float Confidence, int Characters, int TrailingLatinDigits, bool ClosingPunctuation,
    PixelRect? ParentBounds = null, float? ParentConfidence = null, int? ColourComponents = null,
    int? BodyNeutralPixels = null, int? OverlapNeutralPixels = null, bool? ParentClosingPunctuation = null);
public static class OcrMarkerDiagnostics
{
    public const int Limit = 32;
    public static OcrMarkerObservation Describe(OcrLine line, OcrMarkerStage stage, OcrMarkerDecision decision,
        OcrLine? parent = null, int? components = null, int? neutral = null, int? overlap = null)
    {
        int tail = 0;
        for (int i = line.Text.Length - 1; i >= 0; i--) {
            char c = line.Text[i];
            if (!(char.IsAsciiLetterOrDigit(c) || c is >= '０' and <= '９' or >= 'Ａ' and <= 'Ｚ' or >= 'ａ' and <= 'ｚ')) break;
            tail++;
        }
        return new(stage, decision, line.Bounds, line.Confidence, line.Text.Length, tail,
            line.Text.Length > 0 && "。！？!?」』".Contains(line.Text[^1]), parent?.Bounds, parent?.Confidence, components, neutral, overlap,
            parent is null ? null : parent.Text.Length > 0 && "。！？!?」』".Contains(parent.Text[^1]));
    }
}
