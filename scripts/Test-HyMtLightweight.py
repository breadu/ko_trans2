"""Explicit model experiment only; no download, GUI, settings or external AI.
Runs the existing LocalWorker protocol, with identical prompt content/settings.
Only model-specific framing differs, as required by each frozen GGUF template.
"""
import argparse, ctypes as C, ctypes.wintypes as W, hashlib, json, pathlib, queue, subprocess, threading, time

def digest(path):
    with open(path, 'rb') as f: return hashlib.file_digest(f, 'sha256').hexdigest()

class Memory(C.Structure):
    _fields_ = [('cb',W.DWORD),('faults',W.DWORD)]+[(n,C.c_size_t) for n in
        ['peakRss','rss','peakPaged','paged','peakNonpaged','nonpaged','pagefile','peakPagefile','private']]

class CounterValue(C.Structure):
    _fields_ = [('status',W.DWORD),('value',C.c_longlong)]

class CounterItem(C.Structure):
    _fields_ = [('name',W.LPWSTR),('value',CounterValue)]

class Sampler:
    def __init__(self, process):
        self.process=process; self.rss=0; self.private=0; self.vram=None; self.done=threading.Event()
        self.pdh=C.WinDLL('pdh'); self.query=W.HANDLE(); self.counter=W.HANDLE()
        self.pdh.PdhOpenQueryW(None,0,C.byref(self.query))
        self.counter_ok=self.pdh.PdhAddEnglishCounterW(self.query,
            f'\\GPU Process Memory(pid_{process.pid}_*)\\Dedicated Usage',0,C.byref(self.counter))==0
        self.thread=threading.Thread(target=self.run,daemon=True); self.thread.start()
    def run(self):
        while not self.done.wait(.1):
            m=Memory(); m.cb=C.sizeof(m)
            if C.windll.psapi.GetProcessMemoryInfo(W.HANDLE(int(self.process._handle)),C.byref(m),m.cb):
                self.rss=max(self.rss,m.peakRss); self.private=max(self.private,m.private)
            if self.counter_ok:
                self.pdh.PdhCollectQueryData(self.query)
                size=W.DWORD(); count=W.DWORD()
                self.pdh.PdhGetFormattedCounterArrayW(self.counter,0x400,C.byref(size),C.byref(count),None)
                if 0 < size.value < 1024*1024:
                    buf=C.create_string_buffer(size.value)
                    if self.pdh.PdhGetFormattedCounterArrayW(self.counter,0x400,C.byref(size),C.byref(count),buf)==0:
                        items=C.cast(buf,C.POINTER(CounterItem))
                        values=[items[i].value.value for i in range(count.value) if items[i].value.status in (0,1)]
                        if values: self.vram=max(self.vram or 0,sum(values))
    def close(self):
        self.done.set(); self.thread.join(); self.pdh.PdhCloseQuery(self.query)

def prompt(source, history, light):
    background='\n'.join(history[-4:]) or 'No preceding dialogue is available.'
    content='[Background Information]\n'+background+'\n\nPlease translate the following text into Korean, taking the provided background information into consideration.\n\n[Source Text]\n'+source
    return ('<｜hy_begin▁of▁sentence｜><｜hy_User｜>'+content+'<｜hy_Assistant｜>' if light
        else '<|startoftext|>'+content+'<|extra_0|>')

def run(worker, model, backend, cases, output, light):
    worker=worker.resolve(); model=model.resolve()
    output=pathlib.Path(output); output.parent.mkdir(parents=True,exist_ok=True)
    records=[]; started=time.perf_counter()
    with open(str(output)+'.native.txt','w',encoding='utf-8') as native:
        proc=subprocess.Popen([str(worker),'--backend',backend,'--model',str(model)],cwd=str(worker.parent),
            stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=native,text=True,encoding='utf-8',creationflags=subprocess.CREATE_NO_WINDOW)
        sampler=Sampler(proc); replies=queue.Queue()
        def read():
            for line in proc.stdout:
                try: replies.put(json.loads(line))
                except ValueError: replies.put({'protocolError':True})
            replies.put(None)
        threading.Thread(target=read,daemon=True).start()
        def send(value): proc.stdin.write(json.dumps(value,ensure_ascii=False)+'\n'); proc.stdin.flush()
        def receive():
            value=replies.get(timeout=180)
            if value is None: raise RuntimeError('Owned worker ended')
            return value
        result={'model':'1.8B' if light else '7B','backend':backend,'records':records}
        try:
            ready=receive(); result['loadMs']=(time.perf_counter()-started)*1000
            if not ready.get('Ready'): raise RuntimeError('Model load failed: '+json.dumps(ready))
            for case in cases:
                before=time.perf_counter(); send({'Id':case['id'],'Type':'translate','Prompt':prompt(case['source'],case.get('history',[]),light),'MaxTokens':case.get('maxTokens',256)})
                reply=receive(); records.append({'id':case['id'],'kind':case.get('kind',''),
                    'source':case['source'],'history':case.get('history',[]),'wallMs':(time.perf_counter()-before)*1000,**reply})
                output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
                print(f"{result['model']}/{backend}/{case['id']}: {reply.get('Stop',reply.get('Error'))}, {reply.get('Milliseconds',0):.0f}ms",flush=True)
            # Boundaries: output limit, cancellation/drain, reuse, normal shutdown.
            send({'Id':'limit','Type':'translate','Prompt':prompt('The bridge will collapse unless we repair it before the next storm.',[],light),'MaxTokens':1})
            result['limit']=receive()
            send({'Id':'cancel','Type':'translate','Prompt':prompt(' '.join(['Tell the others to wait for us.']*20),[],light),'MaxTokens':256})
            time.sleep(.03); send({'Id':'cancel','Type':'cancel'}); result['cancel']=receive()
            send({'Id':'restart','Type':'translate','Prompt':prompt('Hello.',[],light),'MaxTokens':256}); result['restart']=receive()
            send({'Id':'','Type':'shutdown'}); proc.wait(timeout=20); result['exitCode']=proc.returncode
        finally:
            if proc.poll() is None:
                try: send({'Id':'','Type':'shutdown'}); proc.wait(timeout=10)
                except (OSError,subprocess.TimeoutExpired): proc.kill(); proc.wait()
            sampler.close(); result.update(peakWorkingSetBytes=sampler.rss,peakPrivateBytes=sampler.private,peakDedicatedGpuBytes=sampler.vram)
            output.write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
    return result

if __name__=='__main__':
    p=argparse.ArgumentParser(); p.add_argument('--worker',type=pathlib.Path,required=True); p.add_argument('--model',type=pathlib.Path,required=True)
    p.add_argument('--light',action='store_true'); p.add_argument('--backend',choices=['cpu','vulkan'],required=True)
    p.add_argument('--cases',type=pathlib.Path,default=pathlib.Path(__file__).parent.parent/'tests/fixtures/hymt-lightweight-synthetic.json')
    p.add_argument('--short',action='store_true'); p.add_argument('--output',required=True); a=p.parse_args()
    expected=('dc5f44fcf1fa496ee7ad725982c0c8c553a4de00259b53af84c4b89fb0c06699',1133080448) if a.light else ('9f96256500f3fc1ab4d64336b58f52a949a95ad7516b0c229476eef782f9f77b',4624648896)
    if a.model.stat().st_size != expected[1] or digest(a.model) != expected[0]: raise ValueError('Frozen model integrity mismatch')
    cases=json.loads(a.cases.read_text(encoding='utf-8-sig'))
    if a.short: cases=[c for c in cases if c['id'] in ['ja01','ja16','ja17','en01']]
    result=run(a.worker,a.model,a.backend,cases,a.output,a.light)
    if result.get('exitCode')!=0 or any(r.get('Error') or r.get('Stop')!='EOG' or not any('\uac00'<=c<='\ud7a3' for c in r.get('Text','')) for r in result['records']): raise RuntimeError('Translation/termination failure; inspect local result')
    if result['limit'].get('Stop')!='limit' or result['cancel'].get('Error')!='cancelled' or result['restart'].get('Stop')!='EOG' or not result['restart'].get('Text'): raise RuntimeError('Limit/cancellation/reuse boundary failed')
    print('PASS: fixed model, Korean output, EOG and owned-worker shutdown; quality review is separate.',flush=True)
