from pathlib import Path
from html import escape
from html.parser import HTMLParser
from urllib.parse import urlsplit,unquote
from shutil import copyfile
from docx import Document
import re

entries=[
 ('one','software-design','Software Design and Engineering','Module 3/Milestone 2 Assignment.docx','Payton_Castle_CS499_Milestone_Two_Narrative.docx'),
 ('two','algorithms','Algorithms and Data Structures','Module 4/Milestone 3 Assignment.docx','Payton_Castle_CS499_Milestone_Three_Narrative.docx'),
 ('three','databases','Databases','Module 5/Milestone Four Assignment.docx','Payton_Castle_CS499_Milestone_Four_Narrative.docx')]
headings={'Artifact Background','Why I Chose Clearspace and What I Improved','Reason for Selection and Improvements','Reflection on Learning and Challenges','Course Outcomes','References'}
def clean(text):
    # Repair replacement characters whose meaning is unambiguous in the source.
    text=text.replace('O(n \ufffd d)','O(n × d)').replace('folder\ufffds',"folder's")
    text=text.replace('\ufffdthe index covers this folder\ufffd','“the index covers this folder”').replace('\ufffdthe index can answer this query.\ufffd','“the index can answer this query.”')
    text=text.replace('Asking \ufffdwhat if\ufffd','Asking “what if”').replace('an early \ufffdnothing happens\ufffd','an early “nothing happens”')
    return text
def linked(text):
    return re.sub(r'https?://[^\s<]+',lambda m:f'<a href="{escape(m.group())}">{escape(m.group())}</a>',escape(text))

for folder in ('portfolio-repository','eportfolio-publish'):
    root=Path('output')/folder
    index=(root/'index.html').read_text(encoding='utf-8')
    css=index.split('<style>',1)[1].split('</style>',1)[0]
    for number,category,label,relative,filename in entries:
        source=Path('C:/Users/Payton/OneDrive/Desktop/CS499')/relative
        d=Document(source)
        texts=[clean(p.text.strip()) for p in d.paragraphs if p.text.strip()]
        title=texts[0]
        first=next(i for i,t in enumerate(texts) if t in headings)
        metadata=texts[1:first]
        content=[]
        for text in texts[first:]:
            tag='h2' if text in headings else 'p'
            content.append(f'<{tag}>{linked(text)}</{tag}>')
        download=f'assets/{category}/{filename}'
        target=root/download; target.parent.mkdir(parents=True,exist_ok=True)
        copyfile(source,target)
        assert target.read_bytes()==source.read_bytes()
        note=''
        if category=='databases':
            note='<aside class="snapshot-note"><p><strong>Source snapshot:</strong> This narrative describes later enhancements and reports 283 tests. The source ZIP and 251-test report linked on the homepage document the earlier October 1 milestone snapshot.</p></aside>'
        page=f'''<!DOCTYPE html>
<html lang="en"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>{escape(title)} | Payton</title><style>{css}
main a, header a {{ color: var(--accent); }}
main h2 {{ margin-top: 32px; font-size: 1.35rem; }}
.snapshot-note {{ padding: 4px 20px; background: var(--panel); border-left: 3px solid var(--accent); margin-bottom: 28px; }}
p {{ overflow-wrap: anywhere; }}
@media(max-width:600px) {{ header {{ padding-top:32px; }} header h1 {{ font-size:1.9rem; }} }}
</style></head><body><header><a href="index.html#{category}">&larr; Back to ePortfolio</a>
<h1>{escape(title)}</h1><p class="tagline">{escape(' · '.join(metadata))}</p>
<p><a href="{download}">Download the narrative (Word)</a></p></header>
<main>{note}{''.join(content)}<p><a href="index.html#{category}">Return to {escape(label)}</a></p></main>
<footer>&copy; 2026 Payton &middot; CS 499 Capstone ePortfolio</footer></body></html>'''
        assert '\ufffd' not in page
        (root/f'artifact-{number}.html').write_text(page,encoding='utf-8')
        if number!='three':
            anchor=f'<li><a class="repo-link" href="{download}">'
            assert anchor in index
            index=index.replace(anchor,f'<li><a class="repo-link" href="artifact-{number}.html">Read the {escape(label.lower())} narrative &rarr;</a></li>\n          '+anchor,1)
    old='''The October 1 Milestone Four verification passed 251 Release tests covering
          migration, constraints, rollback, persistence, wrong passwords, tampering,
          and failures around file replacement. A separate dialog check confirmed
          that unlocking restored the original bytes. This portfolio presents that
          verified submission snapshot; later application changes are outside this package.'''
    new='''The final narrative describes expanded file and folder locking, automatic
          re-locking, File Explorer integration, and password changes, and reports
          a 283-test suite. The source ZIP and 251-test report below preserve the
          earlier October 1 milestone snapshot; they do not include every later
          enhancement described in the final narrative.'''
    assert old in index
    index=index.replace(old,new)
    index=index.replace('Download original and enhanced source with evidence (ZIP)','Download October 1 original and enhanced source with evidence (ZIP)')
    (root/'index.html').write_text(index,encoding='utf-8')
    print(folder,': three narrative pages and exact Word copies prepared')

class Links(HTMLParser):
    def __init__(self): super().__init__(); self.links=[]; self.ids=set()
    def handle_starttag(self,tag,attrs):
        for k,v in attrs:
            if k in ('href','src'): self.links.append(v)
            if k=='id': self.ids.add(v)
for folder in ('portfolio-repository','eportfolio-publish'):
    root=Path('output')/folder; n=0
    for page in root.glob('*.html'):
        p=Links(); p.feed(page.read_text(encoding='utf-8'))
        for link in p.links:
            u=urlsplit(link)
            if u.scheme or u.netloc: continue
            target=page.parent/unquote(u.path) if u.path else page
            assert target.exists(),(page,link)
            if u.fragment:
                q=Links(); q.feed(target.read_text(encoding='utf-8'))
                assert u.fragment in q.ids,(page,link)
            n+=1
    print(folder,':',n,'local links verified')
