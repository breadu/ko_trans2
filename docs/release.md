# 릴리즈 준비 절차

## 일상 개발과 분리

기본은 `Work.ps1 -BuildOnly`와 변경 관련 FAST다. GUI/실제 Desktop/전체 회귀/Publish는 명시적 요청 단계다. 스크립트는 Git commit/push/merge나 설치를 수행하지 않는다.

## 배포 구성

`FolderWinX64.pubxml`: win-x64, self-contained, .NET 10.0.14, 폴더형, single-file/trim/ReadyToRun 없음. LibNMeCab.dll은 교체 가능한 별도 파일로 유지한다. 런타임 버전을 바꿀 때 고지와 패키지 검사 기준도 함께 갱신한다. 사용하지 않는 FFmpeg video plugin은 Publish 목록에서 제외한다. OpenCvSharpExtern의 이미지 처리 기능은 유지한다.

배포에는 v5 기본 3개, 고속 v5 2개, v6 Medium 2개까지 총 7개 ONNX가 필요하다. 고정 매니페스트·변환 기록·사전과 SHA-256을 검증한다. 개발 원본에 별도 파일이 있는 경우 Publish는 승인된 경로의 파일만 복사한다.

EXE 옆 models를 읽으며 명시적 KO_TRANS2_MODELS가 우선한다. 개발 checkout/models와 사용자 데이터 경로도 보존한다. 설정은 LocalAppData에만 저장한다. IPA 0.10.0의 6개 파일과 SHA는 `ipa-manifest.json`, 대응 소스 매니페스트는 `licenses/NMeCab-source.json`(0.10.2)과 `licenses/IpaDicBin-source.json`(0.10.0)이다.

## 명시적인 검사와 패키징

```powershell
# 읽기 전용 모델 검증 (추론 없음)
pwsh -NoProfile -File scripts/Test-ReleasePackage.ps1 -Path models -ModelsOnly

# 두 대응 소스는 docs/licenses의 NMeCab-source.json과 IpaDicBin-source.json에 기록된 공식 URL에서 받는다.
# 이 검사는 합성 PE 헤더를 쓰며 실제 패키지 실행 성공을 뜻하지 않는다.
pwsh -NoProfile -File scripts/Test-ReleaseLayout.ps1 -NMeCabSourceArchive '<NMeCab-v0.10.2-source.zip>' -IpaDicSourceArchive '<NMeCab-v0.10.0-source.zip>'

# 별도 사용자 요청 시에만 실행. 반드시 존재하지 않는 새 디렉터리.
pwsh -NoProfile -File scripts/Publish-Release.ps1 -OutputDirectory '<new-isolated-folder>' -NMeCabSourceArchive '<NMeCab-v0.10.2-source.zip>' -IpaDicSourceArchive '<NMeCab-v0.10.0-source.zip>'
pwsh -NoProfile -File scripts/Test-ReleasePackage.ps1 -Path '<prepared-folder>'
```

Publish는 기존 폴더를 덮어쓰지 않는다. 모델 준비/변환/다운로드를 자동 실행하지 않는다. 해시가 검토된 NMeCab 대응 소스 ZIP, licenses, IPA, 모델, .NET/WPF·ONNX·OpenCV native를 넣고 자기 포함 runtimeconfig/PE x64를 확인한다. 실패 시 부분 출력은 조사용으로 남으며 자동 삭제하지 않는다.

## main 병합 준비

```powershell
# 사용자 요청 시에만 Full Rebuild + 전체 Background
pwsh -NoProfile -File scripts/Work.ps1 -Action MainMerge

# 별도 Desktop 및 Publish 요청 시에만
pwsh -NoProfile -File scripts/Work.ps1 -Action MainMerge -Desktop -ReleaseDirectory '<new-folder>' -NMeCabSourceArchive '<NMeCab-v0.10.2-source.zip>' -IpaDicSourceArchive '<NMeCab-v0.10.0-source.zip>'
```

Desktop 경로의 자동 검사는 현재 WindowLifecycle뿐이다. 환경 실패 exit 3과 기능 실패 exit 1을 구분한다. 실제 키/캡처/게임/청취를 대신하지 않는다. `-Approve`는 자동 병합/설치를 실행하지 않고 별도 최종 사용자 승인을 요구하는 안내로 중단한다. 검토 결과를 제시한 뒤 사용자가 정확한 main 병합·설치를 승인하면 별도의 Git/설치 작업으로 수행한다.

## 정식 릴리즈 전 확인

- 전체 Background, 명시적 WindowLifecycle과 실제 Alt+F12·입력 전달·캡처 제외·MP3 청취·게임 연속 실행.
- Windows 10/11 지원 대상 각각, 일반 사용자 계정, 혼합 DPI/다중 모니터, .NET SDK/Python 없는 새 PC에서 폴더 실행. VC++ 런타임 등 native 종속성도 이때 확인한다. 누락을 발견하면 배포 의존성 해결 전 릴리즈하지 않는다.
- 실제 Publish 파일의 native/DLL/사전/모델 해시·고지, LibNMeCab 대응 소스와 교체 가능성. native 라이선스 수집은 최종 배포물 감사의 대체가 아니다.
- 실제 게임 오류 자료(공유 허용분만), 장시간 메모리·핸들·임시 음성 파일, 외부 AI 연결(사용자가 명시적으로 요청한 경우만).
- 사용자 설정/API 키/화면/원문이 포함되지 않았는지 확인. 필요한 서명·설치·업데이트 배포 방식은 별도 승인 범위다.

이 체크리스트가 남아 있는 동안 정식 배포 준비가 모두 끝났다고 보고하지 않는다.
