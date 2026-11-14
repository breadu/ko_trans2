"""Explicit offline developer preparation of pinned official ONNX exports; no download.

Source: det/{model.onnx,inference.yml}, rec/{model.onnx,inference.yml}.
Requires developer Python with PyYAML and onnxruntime. Not used by the application.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import tempfile

import onnxruntime as ort
import yaml


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    templates = Path(__file__).resolve().parents[1] / 'docs/model-manifests/paddle/v6-medium'
    target = args.output.resolve() / 'paddle/v6-medium'
    if target.exists():
        raise ValueError('Target already exists; keep existing models and prepare into a separate output directory.')
    # Validate every source before writing the destination. No arbitrary names/hashes accepted.
    manifests = {}
    dictionary = b''
    for role in ('det', 'rec'):
        spec = json.loads((templates / role / 'manifest.json').read_text(encoding='utf-8'))
        model = args.source / role / 'model.onnx'
        with model.open('rb') as stream:
            if hashlib.file_digest(stream, 'sha256').hexdigest().upper() != spec['Sha256'].upper():
                raise ValueError(f'{role}: official pinned ONNX SHA-256 mismatch')
        config = yaml.safe_load((args.source / role / 'inference.yml').read_text(encoding='utf-8'))
        if role == 'rec':
            if config['PostProcess']['name'] != 'CTCLabelDecode':
                raise ValueError('Recognition export is not CTC')
            # Official PaddleX BaseRecLabelDecode defaults use_space_char=True.
            chars = config['PostProcess']['character_dict'] + [' ']
            dictionary = ('\n'.join(chars) + '\n').encode('utf-8')
            if len(chars) != 18709 or hashlib.sha256(dictionary).hexdigest().upper() != spec['DictionarySha256'].upper():
                raise ValueError('Official character dictionary mismatch')
        options = ort.SessionOptions()
        options.intra_op_num_threads = 1
        session = ort.InferenceSession(str(model), options, providers=['CPUExecutionProvider'])
        inp, out = session.get_inputs(), session.get_outputs()
        shape = lambda node: [x if isinstance(x, int) else -1 for x in node.shape]
        contract = spec['TensorContract']
        if len(inp) != 1 or len(out) != 1 or inp[0].type != 'tensor(float)' or out[0].type != 'tensor(float)' \
                or inp[0].name != contract['InputName'] or out[0].name != contract['OutputName'] \
                or shape(inp[0]) != contract['InputShape'] or shape(out[0]) != contract['OutputShape']:
            raise ValueError(f'{role}: ONNX tensor contract mismatch')
        del session
        manifests[role] = spec
    target.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='v6-prepare-', dir=target.parent) as temporary:
        prepared = Path(temporary) / 'v6-medium'
        for role, spec in manifests.items():
            folder = prepared / role
            folder.mkdir(parents=True)
            shutil.copyfile(args.source / role / 'model.onnx', folder / 'model.onnx')
            (folder / 'manifest.json').write_text(json.dumps(spec, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
        (prepared / 'rec/dictionary.txt').write_bytes(dictionary)
        prepared.rename(target)
    print(f'Prepared verified official PP-OCRv6 Medium: {target}. Existing defaults unchanged.')


if __name__ == '__main__':
    main()
