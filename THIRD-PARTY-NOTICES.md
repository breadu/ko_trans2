# Third-party notices

선택형 GPU OCR Worker는 ONNX Runtime DirectML 1.24.4 (MIT)와 Microsoft.AI.DirectML 1.15.4 (Microsoft DirectML 라이선스)를 수정 없이 사용한다. CPU 프로세스의 ONNX Runtime 1.31.0과 별도 디렉터리·프로세스로 격리한다. 공식 NuGet 패키지의 LICENSE·LICENSE-CODE·ThirdPartyNotices 전문과 버전/패키지 SHA-256은 `licenses/DirectML-*`, `licenses/ONNX-Runtime-DirectML-*`에 보존한다. DirectML의 Windows/Xbox 응용 프로그램 내 재배포 조항을 확인했으며, 이를 일반적인 MIT 구성요소로 표기하지 않는다.

KO Trans 2 자체 소스에는 루트의 MIT 라이선스가 적용된다. 아래 구성요소는 원래의 재배포 조건을 유지한다. 배포 폴더에는 이 문서와 `licenses/`를 함께 제공한다.

| 구성 | 버전 / 조건 | 공식 출처 및 보존 고지 |
|---|---|---|
| .NET / WPF | 배포 profile 10.0.14, MIT 및 포함 구성요소 조건 | [runtime](https://github.com/dotnet/runtime/tree/v10.0.14), [WPF](https://github.com/dotnet/wpf/tree/v10.0.14); dotnet-*-LICENSE/NOTICES |
| ONNX Runtime | Microsoft.ML.OnnxRuntime 1.31.0, MIT | [공식](https://github.com/microsoft/onnxruntime/tree/v1.31.0), ONNX-Runtime-MIT/ThirdPartyNotices |
| OpenCvSharp / native OpenCV | 4.11.0.20250507 / 4.11.0, Apache-2.0 및 하위 구성요소 | [wrapper](https://github.com/shimat/opencvsharp/tree/4.11.0.20250507), [native source](https://github.com/opencv/opencv/tree/31b0eee); Apache-2.0 및 opencv/ |
| PaddleOCR 공식 모델 | v5 Server det·en Mobile rec·Server rec, 선택형 v5 Mobile, v6 Medium; Apache-2.0 | [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR), docs/model-manifests의 모델별 공식 URL·SHA·변환 기록 |
| NMeCab | LibNMeCab 0.10.2; LGPL-2.1-or-later 선택 | [v0.10.2](https://github.com/komutan/NMeCab/tree/v0.10.2), NMeCab-COPYING/LGPL, 대응 소스 ZIP |
| IPA 사전 | LibNMeCab.IpaDicBin 0.10.0; 래퍼 GPL-2.0-or-later 또는 LGPL-2.1-or-later, 사전 데이터 NAIST/ICOT | IPADIC-COPYING 전문, IpaDic/COPYING·AUTHORS, 소스 저장소의 docs/ipa-manifest.json |
| 원본 KO Trans | MIT, Copyright (c) 2026 Breadu Soft | [공식 저장소](https://github.com/breadu/ko_trans), Original-KO-Trans-MIT.txt; 기능/프롬프트/인덱스 계약 참고 |

LibNMeCab은 수정하지 않았다. 교체 가능한 별도 DLL로 배포하며 라이브러리 수정·교체 및 해당 수정의 디버깅에 필요한 권리를 제한하지 않는다. 검토된 v0.10.2 대응 소스 archive를 `licenses/NMeCab-v0.10.2-source.zip`으로 함께 제공한다. app 단일 EXE에 합치거나 trimming하지 않는다. LGPL 전문과 원 저작권 고지는 그대로 보존한다. IPA 사전 데이터에는 별도의 조건이 적용된다. IPA 래퍼의 고정 v0.10.0 대응 소스는 `licenses/NMeCab-v0.10.0-source.zip`으로 함께 제공하며, 출처는 `licenses/IpaDicBin-source.json`에 기록한다. 두 DLL에는 LGPL 2.1 또는 이후 버전 조건을 선택한다.

OpenCvSharpExtern은 OpenCV 4.11.0의 core, imgproc, imgcodecs에서 빌드한다. zlib 1.3.1, libjpeg-turbo 3.0.3 및 libpng 1.6.43 고지를 `licenses/opencv/`에 동봉한다. Intel IPP/IW 및 ITT는 빌드에서 비활성화한다. 빌드 입력과 옵션은 `scripts/Prepare-OpenCv.ps1`에 기록한다.

This software is based in part on the work of the Independent JPEG Group.

사용하지 않는 FFmpeg DLL은 배포 profile에서 제외한다. Python·PaddlePaddle·Paddle2ONNX는 OCR 모델 변환에만 사용한다. Mobile det은 공식 [모델 카드](https://huggingface.co/PaddlePaddle/PP-OCRv5_mobile_det)의 Apache-2.0과 고정 revision을 확인했으며 준비된 고속 세트를 패키지에 포함한다. 파일명이 craft.onnx인 기존 모델의 구조·권한은 확인되지 않아 배포하지 않는다.

이 문서는 구성요소와 고지 수집 기록이다. 실제 Publish 산출물 감사·새 PC 실행 검증·정식 릴리즈 승인을 완료했다는 뜻이 아니다.

2026-10-30 고속 OCR 선택 세트는 공식 PP-OCRv5_mobile_det 및 [PP-OCRv5_mobile_rec ONNX](https://huggingface.co/PaddlePaddle/PP-OCRv5_mobile_rec_onnx/tree/ed152b8b495f84de93cda5709d768548a9127622), Apache-2.0이다. 원출처·SHA·사전 계약은 소스 저장소의 `docs/model-manifests/fast/`에 기록한다. 모델 바이너리는 Git에 포함하지 않으며 배포 시 docs/model-manifests/fast의 고정 manifest와 바이너리·사전 SHA, 변환/공식 ONNX 출처 기록을 검증한다.
# Optional PP-OCRv6 Medium

Official PaddlePaddle PP-OCRv6 Medium detector and recognizer ONNX exports are Apache-2.0 according to their official model cards. Sources, pinned revisions, SHA-256 and runtime contracts are recorded in `docs/model-manifests/paddle/v6-medium`. The exports are unmodified; the recognition dictionary follows official PaddleX space-character behavior. Prepared complete v6 sets are included by the explicit release publishing command, with pinned hashes and provenance. Model binaries are not committed. Existing PaddleOCR Apache-2.0 notices continue to apply.

## 내장 로컬 AI 번역

LLamaSharp 0.30.0과 CPU/Vulkan backend 0.30.0을 별도 worker 폴더에 포함한다. LLamaSharp의 고정 소스는 SciSharp/LLamaSharp d03b6cbca3c66357e2ed9ee9d0324f5eaeabfd4f이며, 패키지가 명시한 llama.cpp revision은 c0c7e147e7efa6c5858754b47259ba4880f8a906이다. 양쪽 MIT 전문과 llama.cpp에 포함된 nlohmann/json의 MIT 고지, CommunityToolkit/Reactive 고지, NuGet 의존성별 버전·원출처는 `licenses/local-ai/`에 보존한다.

Hy-MT2-7B-GGUF 모델은 Tencent의 Apache-2.0 고지를 따르며 고정 revision ab8472660ac61fac25f1af43fac2599d52a8a775의 `LICENSE.txt` 전문을 `licenses/local-ai/Hy-MT2-LICENSE.txt`에 함께 제공한다. 모델 바이너리는 앱 패키지나 Git에 포함하지 않는다. 사용자의 설치 동의 후 고정 GGUF를 다운로드하고 SHA-256으로 확인한다.

Microsoft C++ CRT/OpenMP는 공식 Visual Studio Redist 디렉터리의 Microsoft 서명 파일을 앱 로컬로 포함한다. 파일별 버전·SHA-256은 `local-worker/local-worker-runtime.json`에 기록한다. OS/그래픽 드라이버가 제공하는 Vulkan loader는 시스템 구성요소를 사용하며 드라이버를 설치하거나 수정하지 않는다. .NET 10.0.14의 기존 runtime/WPF 고지도 함께 제공한다.

선택형 Hy-MT2-1.8B-Q4_K_M 모델은 Tencent의 Apache-2.0 고지를 따른다. 고정 revision `a0c709d9fac510f2c807aa3af52872340dc37a4a`의 라이선스 전문은 `licenses/Hy-MT2-1.8B-Apache-2.0.txt`, 크기·SHA-256·공식 출처는 `licenses/Hy-MT2-1.8B-source.json`에 보존한다. GGUF는 Git/프로그램 ZIP에 포함하지 않으며 선택 모델의 다운로드 동의 후에만 별도 설치한다.

## .NET 전이 패키지 고지

앱과 OCR worker의 System.Numerics.Tensors 9.0.0 MIT 전문·서드파티 고지, 로컬 AI worker의 .NET 10.0.13 패키지에서 공통으로 제공하는 서드파티 고지는 `licenses/dotnet-packages/`에 보존한다. 로컬 AI worker의 개별 버전과 프로젝트 출처는 `licenses/local-ai/packages.json`에 기록한다.

## Apache 2.0 구성요소 저작권

공통 라이선스 전문은 `licenses/Apache-2.0.txt`의 Apache Software Foundation 표준 텍스트다. 아래 구성요소의 저작권과 모델 출처는 각각 유지한다.

- PaddleOCR 및 PaddlePaddle 공식 OCR 모델. Copyright (c) 2016 PaddlePaddle Authors. All Rights Reserved. 배포된 모델의 원본·변환 여부·고정 SHA-256은 동봉하는 모델 매니페스트와 변환/출처 기록에 있다. [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR)
- OpenCvSharp 4.11.0.20250507. Copyright 2008 shimat. [고정 버전 LICENSE](https://github.com/shimat/opencvsharp/blob/4.11.0.20250507/LICENSE)
- OpenCV 4.11.0. OpenCV 기여자의 원 저작권과 구성요소별 별도 조건은 고정 소스 및 동봉된 `licenses/opencv/` 고지를 참고한다. [고정 버전 LICENSE](https://github.com/opencv/opencv/blob/31b0eee/LICENSE)
