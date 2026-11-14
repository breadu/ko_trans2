# Phase 5 구조

| 프로젝트 | 책임 |
|---|---|
| App | WPF 프로필·실시간 UI, 단축키, 입력 훅/XInput, 클립보드 listener, 오버레이 |
| Core | ImageFrame, 설정 schema 2, OCR·번역 계약, 상태/측정 DTO |
| Infrastructure | 원자 저장, DPAPI, GDI/창 대상, HTTP 제공자, 문맥, TranslationPipeline |
| Ocr | Phase 2 manifest/CPU ONNX/CTC/ADV·NVL·세로쓰기·변화·ROI |
| Tests / OcrLab | 관련 FAST / 명시적으로 실행하는 실제 모델 평가 |

UI 시작 시 OCR 모델을 읽지 않는다. 최초 요청에서 비동기 초기화하며 검출 세션과 현재 언어 recognizer를 재사용한다. native 추론은 강제 종료하지 않는다. BGRX/BGRA·stride·소유권 계약을 유지하며 단계별 시간 측정, 동일 float 입력을 만드는 텐서 lookup, 보수적인 실제 ROI crop을 추가했다.

TranslationPipeline은 400ms 캡처 루프와 직렬 작업자 하나를 갖는다. capacity 1/drop oldest 채널에 최신 프레임만 유지한다. 변화 시 요청 세대를 증가시키고 OCR/HTTP를 취소한다. UI에서도 파이프라인 객체·프로필·세대를 확인한다. 파이프라인 종료 시 진행 작업을 기다린 뒤 소유 CTS/Semaphore와 프레임 참조를 정리한다. 공유 OCR 세션은 앱이 정상 종료 때 해제한다.

두 프레임 안정화 후 OCR, 자동 요청은 OCR 이후 추가 700ms 정숙 시간을 확인한다. 수동은 이를 건너뛴다. ROI와 전체 영역의 비교 기준을 분리해 ROI 변경만으로 OCR을 반복하지 않는다. 5회(기본 약 2초)마다 전체를 검사하고 변화 시 다음 tick에서 안정화를 확인한다. 전체 캡처는 매 tick 유지한다. Phase 5는 비용 감소와 영역 바깥 모든 RGB 동일 조건을 만족할 때 실제 OCR crop도 적용한다. 수동·크기 변경·바깥 새 글자·crop 경계·빈 결과·crop 4회 이후에는 전체 탐색한다. 반환 좌표와 레이아웃은 원본 기준으로 복원한다.

문맥은 프로필 UUID·언어·제공자·모델·endpoint·사전 prompt별로 분리한다. 최대 32세션, 각 최근 4쌍/6,000자 이내 문맥과 최근 성공 번역 하나만 메모리에 저장한다. 현재 같은 문장만 재사용하고 다른 문장 뒤 재등장하면 새 문맥으로 요청한다. 재번역은 마지막 쌍을 교체한다. 실패/취소/오래된 결과는 문맥에 넣지 않는다.

공유 HttpClient, redirect 금지, timeout/취소를 사용한다. 명시적 HTTP 429/503만 Retry-After를 고려해 한 번 재시도한다(5초보다 긴 대기는 사용자 재시도 안내). 네트워크 단절·timeout·파싱 실패는 자동 재전송하지 않는다. 원문/키/응답 본문을 오류나 로그에 기록하지 않는다. OpenAI store=false, API 키는 DPAPI CurrentUser 별도 파일이다.

창 모드는 선택한 HWND/PID/프로세스 시작 시간을 검증하고 매번 client 영역을 물리 화면 좌표로 계산한다. HWND는 저장·추측하지 않는다. 대상이 비활성·최소화·종료되면 보류한다. GDI는 가려진/보호/독점 전체화면을 복원하지 않는다.

오버레이는 WDA_EXCLUDEFROMCAPTURE와 layered/transparent/noactivate, HTTRANSPARENT를 사용한다. 지원되지 않으면 캡처 영역 밖으로 옮겨야 한다. 자동 캡처마다 창을 숨기지 않는다. 캡처 제외되지 않은 메인/학습창이 영역을 가리면 보류한다. 학습창의 캡처 제외가 성공한 경우에는 보류하지 않는다. 이동/크기 변경은 명시적 편집 모드다.

기본 F1/Shift+F1/Shift+F2/Shift+PageUp/Shift+PageDown은 RegisterHotKey로 등록하고 충돌을 알린다. 학습 스크롤은 전역 WM_HOTKEY에서 표시된 학습창의 PageUp/PageDown으로 연결되며 활성 창 여부를 요구하거나 게임 포커스를 옮기지 않는다. 게임 키/마우스 훅은 활성화 중에만 연결하고 항상 CallNextHookEx로 전달한다. XInput은 컨트롤러 4대의 Joy1–4 상승 에지를 확인한다. 임의 키 입력을 기록하지 않는다. 클립보드 listener는 해당 모드에서 사용자가 시작했을 때만 연결한다.

schema 1 검증 후 settings.json.v1.bak을 보존하고 schema 2로 읽는다. 원자 저장/독점 잠금/손상 원본 보호를 유지한다. 명시적 --test-data --mock-ai에는 합성 화면 도구가 표시된다. 정상 실행에는 모의 번역이 없다. 학습 해설/MP3는 Phase 4에 구현했으며 Phase 5는 안정화·성능 개선·배포 준비를 추가했다. 실제 게임/정식 릴리즈 검증은 별도로 남아 있다.


## Phase 4 학습

`MainWindow.Study`가 명시적 요청의 원문을 준비한다. `StudyFrame`은 화면 RGB가 정확히 같을 때만 최근 OCR을 재사용한다. 별도 OCR도 앱 소유 OcrServiceManager가 선택한 엔진의 직렬 게이트를 공유한다. 엔진 등록·프로필 수명·QA 재현은 제품의 OCR 팩토리와 세션 관리 코드에서 처리한다. 학습은 TranslationPipeline을 중지하거나 번역 history를 수정하지 않는다.

`ExplanationService`는 기존 제공자 transport/HttpClient/DPAPI/취소/timeout/최대 응답/제한 retry를 사용하고 `OutputSchema`를 지정한다. `StudyJson`은 호환 필드를 정규화하고 타입·배열·텍스트·빈 결과를 검증한다. `ExplanationSession`은 요청 직렬화, 세대별 폐기, 중복 클릭 병합, 8개 메모리 캐시를 소유한다. 키에는 프로필·원문·언어·제공자·정확한 모델·주소·프롬프트 버전이 포함된다.

StudyWindow는 한 번 생성해 재사용한다. 화면 캡처 제외, ShowActivated=false, 사용자 선택 Topmost, X/숨김, 원격 스크롤을 제공한다. UI에는 네이티브 텍스트만 넣으며 HTML을 실행하지 않는다. 일본어 읽기 분석기는 첫 요청 때 만들고 정상 종료 때 해제한다.

VoiceIndex는 선택한 root 아래 LNVI 인덱스와 MP3만 읽는다. 경로 문자열·모든 상위 reparse point·열린 파일 handle의 최종 경로를 검사한다. PronunciationPlayer는 검증된 읽기 스트림에서 임시 MP3를 복사해 MediaPlayer가 원본 경로를 다시 열지 않게 한다. 교체/중지/숨김/종료 시 플레이어와 임시 파일을 정리한다.

TranslationPipeline.DisposeAsync는 monitor/worker뿐 아니라 수동 캡처 종료도 기다린 뒤 자체 CTS/SemaphoreSlim을 해제한다. 공유 OCR은 앱이 해제한다. 종료 중 저장 실패로 사용자가 종료를 취소하면 입력·UI를 유지하고 번역/학습이 중지되어 재시작이 필요함을 안내한다.


## Phase 5 수명 및 배포 경계

발음 인덱스는 단일 폴더/파일 identity·수정 시각별로 재사용한다. 파싱은 작업 스레드에서 하고 UI 폴더 변경이 파싱 잠금을 기다리지 않는다. MP3 경로는 매번 다시 검증한다. 활성 임시 파일과 삭제 대기 파일을 분리하며 실패 시 bounded retry/다음 중지·종료로 정리한다. 종료까지 잠긴 파일은 안내 후 남길 수 있다. 원본 음성 데이터나 다른 세션의 파일을 광범위하게 삭제하지 않는다.

배포는 EXE 옆 models를 읽는 자체 포함 win-x64 폴더를 기준으로 준비했다. 모델/IPA 해시·native x64·런타임·고지·NMeCab 대응 소스를 별도 스크립트로 검사한다. 일반 FAST/BuildOnly에는 모델 Lab/Publish/Desktop/Git 작업을 연결하지 않는다. 실제 릴리즈 검증과 최종 승인 범위는 [release.md](release.md)를 참고한다.
