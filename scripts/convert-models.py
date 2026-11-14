"""Explicit development-only conversion. Never imported or executed by the app/DEV-RUN."""
import argparse, hashlib, json, pathlib, subprocess, sys, importlib.metadata, re
import onnx
import yaml

p = argparse.ArgumentParser()
p.add_argument('--source', type=pathlib.Path, required=True)
p.add_argument('--output', type=pathlib.Path, required=True)
p.add_argument('--detector', choices=['PP-OCRv5_server_det', 'PP-OCRv5_mobile_det'], default='PP-OCRv5_server_det')
p.add_argument('--detector-only', action='store_true', help='Explicit model comparison; do not export recognizers again')
args = p.parse_args()
source_root, output_root = args.source.resolve(), args.output.resolve()
if output_root == source_root or source_root in output_root.parents or output_root in source_root.parents:
    p.error('Source and output must be separate, non-nested directories.')
def sha(path): return hashlib.sha256(path.read_bytes()).hexdigest()
models = [('det',args.detector,[])]
if not args.detector_only:
    models += [('en','en_PP-OCRv5_mobile_rec',['en']),('ja','PP-OCRv5_server_rec',['ja','en','zh'])]
for folder, name, languages in models:
    source = args.source / name
    dest = args.output / folder
    dest.mkdir(parents=True, exist_ok=True)
    config = yaml.safe_load((source / 'inference.yml').read_text(encoding='utf-8'))
    model = dest / 'model.onnx'
    if model.exists():
        history_file = dest / 'conversion.json'
        if not history_file.exists():
            raise ValueError('Existing ONNX has no conversion provenance. Use a fresh output folder.')
        history = json.loads(history_file.read_text(encoding='utf-8'))
        if history['sourceJsonSha256'] != sha(source/'inference.json') or history['sourceParamsSha256'] != sha(source/'inference.pdiparams'):
            raise ValueError('Cached ONNX belongs to different source weights. Use a fresh output folder.')
    if not model.exists():
        subprocess.run([str(pathlib.Path(sys.executable).parent/'paddle2onnx.exe'),'--model_dir',str(source),'--model_filename','inference.json',
            '--params_filename','inference.pdiparams','--save_file',str(model),'--opset_version','14','--enable_onnx_checker','True'],check=True)
    onnx.checker.check_model(str(model))
    graph = onnx.load(str(model))
    print(name, 'ONNX checker PASS', [(i.name, [d.dim_value or d.dim_param for d in i.type.tensor_type.shape.dim]) for i in graph.graph.input])
    spec = dict(Name=name,Kind='recognition' if languages else 'detection',Version='PP-OCRv5',Languages=languages,
        Source='https://huggingface.co/PaddlePaddle/'+name,License='Apache-2.0',File='model.onnx',Sha256=sha(model),
        ChannelOrder='BGR',Layout='NCHW',Decoder='CTC' if languages else 'DB',Height=48,MinWidth=320,MaxWidth=3200)
    if not languages:
        spec['Languages'] = ['en', 'ja', 'zh', 'zh-Hant']
    if languages:
        resize = next(x['RecResizeImg'] for x in config['PreProcess']['transform_ops'] if 'RecResizeImg' in x)
        decode = next(x['DecodeImage'] for x in config['PreProcess']['transform_ops'] if 'DecodeImage' in x)
        if resize['image_shape'] != [3,48,320] or decode['img_mode'] != 'BGR' or config['PostProcess']['name'] != 'CTCLabelDecode':
            raise ValueError('Unexpected recognition contract; inspect PaddleX preprocessing before exporting.')
        chars = config['PostProcess']['character_dict']
        # PaddleX BaseRecLabelDecode appends space regardless of config's training flag.
        chars = chars + [' ']
        dictionary = dest/'dictionary.txt'
        dictionary.write_text('\n'.join(chars)+'\n',encoding='utf-8')
        spec.update(Dictionary='dictionary.txt',DictionarySha256=sha(dictionary),Mean=[.5]*3,Std=[.5]*3)
    else:
        norm = next(x['NormalizeImage'] for x in config['PreProcess']['transform_ops'] if 'NormalizeImage' in x)
        resize = next(x['DetResizeForTest'] for x in config['PreProcess']['transform_ops'] if 'DetResizeForTest' in x)
        decode = next(x['DecodeImage'] for x in config['PreProcess']['transform_ops'] if 'DecodeImage' in x)
        scale_text = str(norm['scale']).replace(' ', '')
        scale = 1/255 if scale_text in ('1./255.', '1/255', '1./255', '1/255.') else float(scale_text)
        if decode['img_mode'] != 'BGR' or norm['order'] != 'hwc' or config['PostProcess']['name'] != 'DBPostProcess' or abs(scale - 1/255) > 1e-8 or resize.get('resize_long') != 960:
            raise ValueError('Unexpected detection contract; inspect official preprocessing.')
        spec.update(Mean=norm['mean'],Std=norm['std'],Threshold=config['PostProcess']['thresh'],BoxThreshold=config['PostProcess']['box_thresh'],UnclipRatio=config['PostProcess']['unclip_ratio'],DetectionLongSide=resize['resize_long'])
    (dest/'manifest.json').write_text(json.dumps(spec,ensure_ascii=False,indent=2),encoding='utf-8')
    revision_file = source/'revision.txt'
    revision = revision_file.read_text(encoding='utf-8-sig').strip() if revision_file.exists() else None
    if revision is not None and not re.fullmatch('[0-9a-f]{40}', revision):
        raise ValueError('Invalid upstream revision metadata')
    (dest/'conversion.json').write_text(json.dumps(dict(sourceJsonSha256=sha(source/'inference.json'),sourceParamsSha256=sha(source/'inference.pdiparams'),sourceConfigSha256=sha(source/'inference.yml'),upstreamRevision=revision,paddle=importlib.metadata.version('paddlepaddle'),paddle2onnx=importlib.metadata.version('paddle2onnx'),opset=graph.opset_import[0].version),indent=2),encoding='utf-8')
