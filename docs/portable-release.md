# Portable ZIP 준비 및 업데이트 복구

앱 프로젝트의 버전으로 정식 Windows x64 Portable ZIP을 만든다. 루트 `KoTrans2.exe`에는 관리 DLL이 포함된다. 별도 네이티브 DLL, NMeCab, 두 워커 폴더와 기타 리소스는 압축을 푼 폴더에 함께 둔다.

## 사용자 데이터 계약

앱 위치와 관계없이 `%LOCALAPPDATA%\KoTrans2`를 사용한다. settings.json(스키마 3, 최대 1MiB, 프로필 최대 200개), DPAPI CurrentUser 키, Models/HyMT2의 고정 GGUF 경로를 유지한다. App의 단일 인스턴스 잠금/현재 사용자 IPC도 이 데이터 폴더를 기준으로 하며 설정 로드·백업 전에 소유권을 확보한다. 새 ZIP은 KoTrans2.exe를 직접 실행하며 저장소 Run.cmd·Dev-Run.cmd·Install-Main.ps1을 요구하지 않는다.

새 릴리즈 ID(AssemblyInformationalVersion의 버전+소스 커밋)를 처음 읽을 때 SettingsStore는 원본을 파싱/마이그레이션하기 전에 버전별 백업을 만든다. 개발 커밋이 바뀌면 개발 실행본의 ID도 바뀐다. 백업은 Backups/settings-before-<안전한 ID 앞부분>-<ID 해시>.json이며 BOM/공백을 포함한 원본 바이트 그대로다. 쓰기 후 크기·SHA-256·내용을 확인한다. update-state.json은 별도 작은 원자적 메타데이터다. 같은 릴리즈는 중복 생성/덮어쓰기하지 않는다. 최초 설치에 기존 설정이 없으면 백업하지 않는다. 기존 settings.json.bak와 settings.json.v1/v2.bak는 그대로 유지한다.

최근 관리 백업 5개를 유지한다. 메타데이터에 기록되고 이름·크기·해시가 맞는 파일만 정리하며 사용자 파일·다른 이름의 백업·수정된 백업은 지우지 않는다. 정리 권한이 없으면 파일을 남긴다. 백업 생성/검증/메타데이터 쓰기가 실패하면 설정 로드/저장을 중단하고 원본을 보존한다. 손상/미지원 최신 스키마도 정상 기본값으로 덮어쓰지 않는다. API 키를 복호화해 백업하지 않으며 키 파일과 모델은 읽기/변경 대상이 아니다.

## 업데이트와 복구

트레이 아이콘 우클릭 → 종료(X는 숨김) 후 새 ZIP을 별도 폴더에 풀어 실행한다. 기존 프로필/키/모델은 동일 계정에서 공유한다. 새 버전 동작을 확인한 뒤 이전 프로그램 폴더를 삭제한다.

문제 시 모든 버전을 종료한다. 현재 settings.json을 다른 이름으로 보관하고, 원하는 버전을 실행하기 전의 Backups 파일을 복사해 settings.json으로 복구한다. 키와 Models는 그대로 둔다. 이전 실행 파일이 새 설정을 읽는 역호환성은 보장하지 않는다. update-state.json 자체가 손상되었거나 복구 후 백업 충돌로 시작하지 못하면 해당 메타데이터와 Backups 폴더를 각각 별도 이름으로 **보관**한 뒤, 복구한 settings.json으로 실행해 새 복구 지점을 만든다. 원본/백업을 지우며 문제를 해결하지 않는다.

캐릭터 사전 JSON을 이전 프로그램 폴더 안에 두었다면 그 폴더를 삭제하기 전에 사전을 따로 보관하고 새 위치를 프로필에 지정한다. 기본 OCR은 새 EXE 옆 models, 일본어 분석은 새 EXE 옆 IpaDic을 사용한다. KO_TRANS2_MODELS 같은 명시적 사용자 경로는 우선하므로 경로를 직접 관리해야 한다.

## 개발자의 패키지 생성

변경을 커밋한 작업 트리에서 이미 검토된 OCR·사전·고지를 사용한다. 공개 버전은 사용자와 결정한 값을 명시하며 검증용 버전은 외부 공개에 사용하지 않는다.

```powershell
pwsh -NoProfile -File scripts/Publish-Release.ps1 `
  -OutputDirectory '<존재하지 않는 별도 Publish 폴더>' `
  -NMeCabSourceArchive '<NMeCab-v0.10.2-source.zip>' `
  -IpaDicSourceArchive '<NMeCab-v0.10.0-source.zip>' `
  -Version '<사용자가 결정한 버전>'

$release = Get-Content '<Publish 폴더>/release.json' -Raw | ConvertFrom-Json
pwsh -NoProfile -File scripts/Pack-PortableRelease.ps1 `
  -PublishDirectory '<검증된 Publish 폴더>' -OutputDirectory '<별도 ZIP 출력 폴더>' `
  -Version $release.version -SourceCommit $release.sourceCommit
```

Publish는 기존 빌드/모델/사전 검사를 재사용하고, self-contained worker·고정 CPU/Vulkan DLL 해시·앱 로컬 Microsoft 서명·고지를 검증한다. 실행 파일의 실제 ProductVersion(버전+커밋)과 release.json이 일치해야 한다. 모든 프로젝트의 디버그 심볼은 공개 폴더에서 제외한다.

publish-files.json은 검증된 파일별 경로·크기·SHA-256이다. Pack은 원본 폴더를 수정하지 않고 기존 Test-ReleasePackage -Portable로 추가/변경/누락을 검출한다. 사용자 설정/키/백업/로그/캡처/개발 소스/스크립트/fixture/GGUF를 거절한다. 승인된 LGPL 대응 소스 ZIP과 라이선스는 유지한다.

산출물은 KoTrans2_v<버전>_win-x64.zip, .zip.sha256, .zip.release.json이다. ZIP 안에는 KoTrans2_v<버전>/ 아래 EXE·local-worker·models·IpaDic·LICENSE·THIRD-PARTY-NOTICES.md·licenses·help·PORTABLE-README.txt·USER-GUIDE.md·release.json·publish-files.json이 있다. ZIP 자체 SHA는 자기 참조를 피하기 위해 외부 sidecar에 둔다. 폴더·ZIP/sidecar가 있으면 덮어쓰지 않는다. 실패한 Publish/임시 ZIP은 조사용으로 남을 수 있으며 사용자 폴더를 재귀 정리하지 않는다.

동일한 검증 입력 바이트와 동일한 압축 런타임에서는 경로의 ordinal 정렬·고정 ZIP entry 시각으로 ZIP SHA가 재현된다. 별도로 다시 빌드한 실행 파일이나 buildUtc가 바뀐 Publish까지 동일하다는 주장은 아니다.

## 검증과 미실행 범위

Settings의 PortableUpdateTests는 합성 프로필 10/200개, 스키마 v1/v2/v3, 원본 BOM/바이트, 백업 실패, 손상/미지원 설정, 같은 버전 반복, 최근 5개 관리, DPAPI 파일/모델 유지, 복구와 프로그램 폴더 이동을 검증한다. 기존 WPF의 임시 데이터/다른 프로세스 IPC 검사로 단일 인스턴스 회귀를 확인한다. ZIP 검증은 생성·전 entry 해시·재현 SHA·재압축 거부·외부 데이터/변조/필수 파일 누락 거부를 확인한다.

실제 새 Windows 계정/깨끗한 PC, 실제 API 키 이전, 사용자 설정/실제 GGUF로 업데이트·폴더 삭제·롤백, 앱 GUI/게임 실행은 NOT RUN이다. 실행 파일의 외부 코드 서명/SmartScreen 신뢰와 개인 외부 경로는 공개 전 별도 확인 항목이다. 이 개발 결과를 새 PC 전체 호환성 또는 외부 공개 승인으로 해석하지 않는다.
