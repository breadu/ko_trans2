# 녹화 모드

KO Trans 2를 알림 영역 아이콘의 **종료**로 완전히 종료한 다음 실행한다.

```powershell
.\KoTrans2.exe --recording
```

이 옵션은 녹화 프로그램을 실행하거나 영상을 저장하지 않는다. 새로 생성되는 KO Trans 2 창에 캡처 제외 속성을 설정하지 않아, OBS의 디스플레이 캡처 등에서 자막·조작 툴바·문장 해설창을 함께 볼 수 있게 한다. 설정 파일과 프로필에는 저장되지 않는다. 다음에 옵션 없이 실행하면 기존 캡처 제외 정책을 사용한다.

| 실행 옵션 | 자막 텍스트·툴바 | 문장 해설·마법사 | 디버그창 |
| --- | --- | --- | --- |
| 없음 | 캡처 제외 | 캡처 제외 | 자동 표시 안 함 |
| `--debug-overlay` | 캡처 제외 | 캡처 허용 | 표시 |
| `--recording` | 캡처 허용 | 캡처 허용 | 자동 표시 안 함 |
| 두 옵션 동시 사용 | 캡처 허용 | 캡처 허용 | 표시 |

기존에 캡처 제외를 적용하지 않던 일반 설정·안내창은 계속 캡처 가능하다. 실행 중인 앱에 두 번째 프로세스로 옵션을 전달해도 정책이 바뀌지 않으며, 완전 종료 후 다시 실행하라는 안내를 표시한다.

## OCR 영역과의 겹침

녹화 모드에서는 자막도 화면 캡처에 포함된다. 자막·툴바·해설창·팝업을 OCR 인식 영역 밖에 배치한다. 해당 창이 화면 기반 OCR 영역을 가리면 기존 가림 보호가 캡처를 대기시키며, 위치를 옮기거나 숨기면 다시 확인한다. 녹화 모드 때문에 가림 검사를 해제하지 않는다.

게임 원본 창을 직접 읽는 기존 캡처 경로는 유지한다. Magpie 사용 시에도 원본 창 캡처 설정을 자동으로 변경하지 않는다. 화면 영역/GDI/Magpie 확대 화면을 읽는 경로와 마커 관측은 가림 보호를 계속 거친다. 원본 창 캡처 실패 시 보호 없이 화면 캡처로 대체하지 않는다.

## 검증

```powershell
pwsh -NoProfile -File scripts/Test-Fast.ps1 -Suite Wpf
pwsh -NoProfile -File scripts/Test-Desktop.ps1 -Suite RecordingMode -RecordingVariant Normal
pwsh -NoProfile -File scripts/Test-Desktop.ps1 -Suite RecordingMode -RecordingVariant Debug
pwsh -NoProfile -File scripts/Test-Desktop.ps1 -Suite RecordingMode -RecordingVariant Recording
pwsh -NoProfile -File scripts/Test-Desktop.ps1 -Suite RecordingMode -RecordingVariant RecordingDebug
```

Desktop 검사는 명시적으로 요청했을 때만 실행한다. 독립 프로세스에서 실제 HWND의 `GetWindowDisplayAffinity`, 텍스트 창과 툴바 각각의 상태, 해설·마법사·디버그창, 가림 보호, 팝업, 클릭 통과와 설정 저장을 검사한다. 실제 게임·유료 API·사용자 설정은 사용하지 않는다. 이 검사는 OBS 녹화 파일 검증을 대신하지 않는다.
