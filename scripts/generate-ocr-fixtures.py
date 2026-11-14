"""Original synthetic fixtures. No game screenshots or third-party image assets."""
from PIL import Image, ImageDraw, ImageFont
from pathlib import Path
import json, argparse, random
p=argparse.ArgumentParser(); p.add_argument('--output',type=Path,required=True); p.add_argument('--japanese-font',type=Path,required=True); args=p.parse_args()
args.output.mkdir(parents=True,exist_ok=True)
cases=[]
def make(name,language,lines,size=36,mode='NVL',vertical=False,ruby=False,busy=False):
    im=Image.new('RGB',(960,600),(28,37,54)); d=ImageDraw.Draw(im)
    if busy:
        rng=random.Random(19)
        for _ in range(65):
            x,y=rng.randrange(960),rng.randrange(600); d.ellipse((x,y,x+80,y+80),fill=(rng.randrange(25,65),rng.randrange(25,65),rng.randrange(25,65)))
    font=ImageFont.truetype(str(args.japanese_font) if language=='ja' else r'C:\Windows\Fonts\arial.ttf',size)
    regions=[]
    for i,text in enumerate(lines):
        if vertical:
            x=760-i*180; y=80
            for j,c in enumerate(text): d.text((x,y+j*(size+6)),c,font=font,fill='white')
            regions.append(dict(X=x,Y=y,Width=size+8,Height=len(text)*(size+6)))
        else:
            x=90; y=110+i*(size+30)+(110 if mode=='NVL' and i>0 else 0)
            d.text((x,y),text,font=font,fill='white')
            b=d.textbbox((x,y),text,font=font); regions.append(dict(X=b[0],Y=b[1],Width=b[2]-b[0],Height=b[3]-b[1]))
            if ruby and i==0:
                small=ImageFont.truetype(str(args.japanese_font),12);d.text((x,y-12),'きょう',font=small,fill='white')
    im.save(args.output/(name+'.png'))
    cases.append(dict(Name=name,File=name+'.png',Language=language,Mode=mode,Vertical=vertical,Expected=('\n\n' if mode=='NVL' else '\n').join(lines),Regions=regions))
make('english','en',['The journey begins with a single step.'])
make('small-english','en',['Open the door and follow the light.'],size=16)
make('japanese','ja',['今日は晴れです'])
make('vertical','ja',['今日は晴れです','明日も元気です'],vertical=True)
make('furigana','ja',['今日は晴れです'],ruby=True)
make('background','en',['Welcome to the ancient forest.'],busy=True)
make('adv','en',['We must leave before sunrise.','Follow me to the old bridge.'],mode='ADV')
make('nvl','en',['The forest was silent.','A new story begins here.'])
make('japanese-multiline','ja',['扉を開けてください','一緒に行きましょう'],mode='ADV')
make('reading-order','en',['First we find the key.','Then we open the door.'])
# Pixel-identical to the QA screen capture that exposed overlapping DB fragments.
Image.open(args.output/'english.png').crop((101,96,851,256)).save(args.output/'desktop-crop.png')
cases.append(dict(Name='desktop-crop',File='desktop-crop.png',Language='en',Mode='ADV',Vertical=False,
    Expected='The journey begins with a single step.',Regions=[dict(X=0,Y=21,Width=588,Height=33)]))
(args.output/'cases.json').write_text(json.dumps(cases,ensure_ascii=False,indent=2),encoding='utf-8')
