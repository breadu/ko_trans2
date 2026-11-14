# OCR 모델 준비

앱은 Python을 실행하지 않는다. 아래 변환은 Windows 개발 PC에서 한 번 하는 명시적 작업이며 DEV-RUN에는 연결하지 않았다.

## 검증 환경과 재현

Python 3.12 x64, PaddlePaddle **CPU 3.1.1**, Paddle2ONNX **2.1.0**, ONNX 1.17.0. 기존 GPU 3.3.0 venv를 수정하지 않고 별도 환경에서 변환했다. 이전 `paddle2onnx_cpp2py_export` DLL 오류가 사라졌고 3개 모델 checker와 C# 실제 이미지 추론이 통과했다.

[공식 ONNX 가이드](https://paddlepaddle.github.io/PaddleOCR/main/en/version3.x/deployment/obtaining_onnx_models.html)의 PaddleX 경로를 확인하고, 같은 공식 exporter의 console entry point를 독립 CPU 환경에서 실행했다. `python -m paddle2onnx`에는 __main__이 없어 실패하므로 `Scripts/paddle2onnx.exe`를 호출한다. 2.0.2 정식 wheel은 조회 결과 없어 사용하지 않았다. setuptools 부재로 import가 실패하여 requirements에 명시했다. WSL/비공식 ONNX가 필요하지 않았다.

```powershell
python -m venv work/convert-venv
work/convert-venv/Scripts/python.exe -m pip install -r scripts/requirements-convert.txt
pwsh -NoProfile -File scripts/Prepare-Models.ps1 -Python work/convert-venv/Scripts/python.exe -Source '<허용된 official_models 캐시 경로>'
```

source 캐시에 아래 3개 폴더의 inference.json, inference.pdiparams, inference.yml이 필요하다. 스크립트는 자동 다운로드하지 않는다. output 기본값은 저장소의 models/다. source/output 중첩을 거절하고 원본 파일을 수정하지 않는다. 기존 ONNX는 provenance와 원본 weight hash가 일치할 때만 재사용한다.

| 폴더 | 모델 / 공식 출처 | 사용 |
|---|---|---|
| det | [PP-OCRv5_server_det](https://huggingface.co/PaddlePaddle/PP-OCRv5_server_det) | DB 계열 검출 |
| en | [en_PP-OCRv5_mobile_rec](https://huggingface.co/PaddlePaddle/en_PP-OCRv5_mobile_rec) | 영어 |
| ja | [PP-OCRv5_server_rec](https://huggingface.co/PaddlePaddle/PP-OCRv5_server_rec) | 일본어·세로쓰기 |

공식 모델 카드의 라이선스는 Apache-2.0이다. 설치된 PaddleOCR의 japan 선택 결과가 server_rec이며 사전에 히라가나·가타카나가 있고 공식 모델 카드도 일본어 지원을 명시한다. 실제 일본어 정답 이미지로 검증했다. 중국어는 앱 언어 선택에 제공하지 않는다.

## 파일과 계약

각 폴더: model.onnx, manifest.json, conversion.json; 인식 모델에는 dictionary.txt도 있다. [기록된 manifest](model-manifests/)에는 실제 변환 파일·사전 SHA-256, 출처·라이선스·전처리 값이 있다. conversion 기록에는 원본 json/params/yml SHA, exporter 버전·실제 opset이 있다. upstream revision은 캐시에 없어 원본 해시로 식별한다. 모델 바이너리와 개인 화면은 Git에 포함하지 않는다.

공식 Mobile 변환도 `--detector PP-OCRv5_mobile_det --detector-only`로 명시적으로 선택할 수 있다. 실제 export yml의 BGR/정규화/DB 계약을 검사하며 기본 Server를 자동 교체하지 않는다.

검출: float [1,3,H,W] BGR/NCHW → [1,1,H,W] sigmoid. 긴 변 960/32 배수, mean [.485,.456,.406], std [.229,.224,.225], threshold .3, box score .6, unclip 1.5.

인식: float [1,3,48,W] BGR/NCHW → [1,T,C] 확률. 비율 유지 width(최소 320, 최대 3200), (pixel/255-.5)/.5, padding 0. 두 모델의 실제 yml/PaddleX 경로는 같은 계약이다. exporter는 예상 yml 계약과 다르면 중단한다. embedded character_dict 순서에 PaddleX BaseRecLabelDecode와 동일하게 공백을 마지막에 추가하고 blank index 0은 디코더에서 처리한다.

앱은 KO_TRANS2_MODELS → EXE 옆 models → checkout/models 순으로 찾는다. linked checkout에 models가 없으면 Git gitdir/commondir로 연결된 동일 저장소의 common checkout/models를 재사용한다. checkout 밖에서는 LocalAppData/KoTrans2/models를 사용한다. 명시한 경로나 checkout에 이미 있는 모델 폴더가 잘못된 경우 다른 모델로 조용히 대체하지 않는다. 모델/사전 hash는 최초 세션 로딩 때 검사한다. 부재·손상은 UI 오류로 안내한다. 기본은 CPU이며 DirectML을 선택하면 별도 OCR 워커를 사용한다. 워커가 실패하면 CPU로 전환한다.


## 모델 파일 검증

```powershell
pwsh -NoProfile -File scripts/Test-ReleasePackage.ps1 -Path models -ModelsOnly
```

이 명령은 배포용 모델 7개와 사전의 해시 및 출처 기록을 확인한다. OCR 추론은 실행하지 않는다.
