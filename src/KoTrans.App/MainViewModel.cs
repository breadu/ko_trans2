using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
namespace KoTrans.App;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string status = "설정을 불러오는 중…";
    private string area = "캡처 영역을 지정하세요";
    private string compactArea = "캡처 영역을 지정하세요";
    private string captureAreaBasis = "화면 기준";
    private string captureAreaPosition = "대사 영역을 지정하세요.";
    private string captureAreaSize = "";
    private string ocrExecution = "OCR: 대기";
    public string OcrExecution { get => ocrExecution; set { ocrExecution = value; Changed(); } }
    private string ocrExecutionDetail = "OCR이 시작되지 않았습니다.";
    public string OcrExecutionDetail { get => ocrExecutionDetail; set { ocrExecutionDetail = value; Changed(); } }
    private string gameQaStatus = "문제 장면 저장 단축키는 연결되어 있지 않습니다.";
    public string GameQaStatus { get => gameQaStatus; set { gameQaStatus = value; Changed(); } }
    private string timing = "시작 시간 측정 중…";
    private BitmapSource? preview;
    internal long StatusRevision { get; private set; }
    public string Status { get => status; set { StatusRevision++; if (status == value) return; status = value; Changed(); } }
    public string Area { get => area; set { area = value; Changed(); } }
    public string CompactArea { get => compactArea; set { compactArea = value; Changed(); } }
    public string CaptureAreaBasis { get => captureAreaBasis; set { captureAreaBasis = value; Changed(); } }
    public string CaptureAreaPosition { get => captureAreaPosition; set { captureAreaPosition = value; Changed(); } }
    public string CaptureAreaSize { get => captureAreaSize; set { captureAreaSize = value; Changed(); } }
    public string Timing { get => timing; set { timing = value; Changed(); } }
    public BitmapSource? Preview { get => preview; set { preview = value; Changed(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
