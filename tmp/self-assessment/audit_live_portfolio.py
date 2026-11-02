from pathlib import Path
from html.parser import HTMLParser
from urllib.parse import urljoin,urlsplit
from urllib.request import Request,urlopen
from urllib.error import HTTPError
from concurrent.futures import ThreadPoolExecutor
import json

base='https://friday7352.github.io/'
class Page(HTMLParser):
    def __init__(self): super().__init__(); self.links=[]; self.ids=set(); self.text=[]
    def handle_starttag(self,tag,attrs):
        for key,value in attrs:
            if key=='href': self.links.append(value)
            if key=='id': self.ids.add(value)
    def handle_data(self,data): self.text.append(data)
pages={}; urls=set()
for name in ['index.html','artifact-one.html','artifact-two.html','artifact-three.html','career-reflection.html','module-five-journal.html']:
    with urlopen(Request(urljoin(base,name),headers={'User-Agent':'PortfolioRequirementsCheck'}),timeout=25) as r:
        html=r.read().decode('utf-8')
    p=Page();p.feed(html); pages[name]=p
    for link in p.links:
        u=urlsplit(urljoin(base,link))
        urls.add(u._replace(fragment='').geturl())
    print('PAGE',name,'loaded',len(p.links),'links')

def check(url):
    try:
        with urlopen(Request(url,method='HEAD',headers={'User-Agent':'Mozilla/5.0 PortfolioCheck'}),timeout=20) as r:
            return {'url':url,'status':r.status,'size':r.headers.get('Content-Length')}
    except HTTPError as e: return {'url':url,'status':e.code}
    except Exception as e: return {'url':url,'error':str(e)}
with ThreadPoolExecutor(max_workers=6) as pool: results=list(pool.map(check,sorted(urls)))
dest=Path('output/final-project-review');dest.mkdir(parents=True,exist_ok=True)
(dest/'live-link-results.json').write_text(json.dumps(results,indent=2),encoding='utf-8')
print('UNIQUE URLS',len(results))
print('NON-SUCCESS',json.dumps([r for r in results if r.get('status')!=200],indent=2))
for r in results:
    if '/assets/artifacts/' in r['url']:print('ARTIFACT',r)
print('SELF ASSESSMENT FIRST',pages['index.html'].ids)
