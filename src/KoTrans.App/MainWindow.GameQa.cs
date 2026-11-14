using KoTrans.Infrastructure;

namespace KoTrans.App;

public partial class MainWindow
{
    private readonly GameQaRecorder gameQa = new();
    private readonly GameQaWriter qaWriter = new();
    private Task qaSaveTask = Task.CompletedTask;
    internal GameQaRecorder GameQa => gameQa;
    private void UpdateGameQaHotkeyStatus() => model.GameQaStatus = "문제 장면 저장 단축키는 연결되어 있지 않습니다.";
    internal Task SaveGameQaAsync(string? directory = null)
    {
        if (closing) return Task.CompletedTask;
        if (!qaSaveTask.IsCompleted) { model.GameQaStatus = "Game QA 저장 중 · 중복 요청 생략"; return qaSaveTask; }
        var snapshot = gameQa.Snapshot;
        if (snapshot is null) { model.GameQaStatus = "Game QA: 저장할 OCR 입력이 없습니다. 화면/이미지 OCR을 먼저 실행하세요."; return Task.CompletedTask; }
        // Freeze references now. Never re-read mutable UI text or take a new screen capture.
        qaSaveTask = SaveCoreAsync(snapshot, directory ?? GameQaFiles.FindDirectory(AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
        return qaSaveTask;
    }
    private async Task SaveCoreAsync(GameQaSnapshot snapshot, string directory)
    {
        model.GameQaStatus = "Game QA 저장 중…";
        try
        {
            var saved = await qaWriter.SaveAsync(directory, snapshot);
            model.GameQaStatus = $"Game QA 저장 완료 ({snapshot.Record.OcrStatus}) · {saved}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { model.GameQaStatus = "Game QA 저장 실패 · 폴더 권한/여유 공간/파일 상태를 확인하고 다시 누르세요. 기존 결과는 유지됩니다."; }
        // No Show, Activate, dialog, clipboard, input injection or network calls.
    }
}
