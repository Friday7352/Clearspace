from pathlib import Path
from html import escape
from html.parser import HTMLParser
from urllib.parse import urlsplit, unquote
from shutil import copyfile
from docx import Document

source=Path('C:/Users/Payton/OneDrive/Desktop/CS499/Module 7/CS 499 Professional Self Assessment.docx')
document=Document(source)
headings={'Professional Growth and Career Direction','Collaboration and Communication','Algorithms, Software Engineering, and Databases','Security and Professional Responsibility','Introduction to the Portfolio'}
download='assets/self-assessment/CS_499_Professional_Self_Assessment.docx'
parts=['  <section id="about" aria-labelledby="self-assessment-heading">',
       '    <h2 id="self-assessment-heading">Professional Self-Assessment</h2>',
       f'    <p><a class="repo-link" href="{download}">Download my professional self-assessment (Word)</a></p>']
published=[]
for p in document.paragraphs:
    text=p.text.strip()
    if not text or text in ('CS 499 Professional Self Assessment','View the Clearspace source repository'):
        continue
    tag='h3' if text in headings else 'p'
    parts.append(f'    <{tag}>{escape(text)}</{tag}>')
    if tag=='p': published.append(text)
parts.extend(['    <p><a class="repo-link" href="https://github.com/Friday7352/Clearspace">View the Clearspace source repository</a></p>', '  </section>'])
section='\n'.join(parts)
for name in ('portfolio-repository','eportfolio-publish'):
    root=Path('output')/name
    page=root/'index.html'
    html=page.read_text(encoding='utf-8')
    start=html.index('  <section id="about"')
    end=html.index('</section>',start)+len('</section>')
    html=html[:start]+section+html[end:]
    html=html.replace('<a href="#about">About</a>','<a href="#about">Self-Assessment</a>')
    old='Clearspace was built mainly independently. Instructor or peer feedback and the resulting decisions still need to be documented to strengthen this outcome.'
    new='My professional IT support work and Unity collaboration provide experience coordinating responsibilities and communicating with contributors. The self-assessment explains this background; Clearspace itself was developed independently.'
    html=html.replace(old,new)
    page.write_text(html,encoding='utf-8')
    target=root/download
    target.parent.mkdir(parents=True,exist_ok=True)
    copyfile(source,target)
    assert target.read_bytes()==source.read_bytes()
    assert all(escape(text) in html for text in published)
    assert html.index('id="about"')<html.index('id="outcomes"')
    print(name,':',len(published),'paragraphs and',len(headings),'headings published; Word copy verified')

class Links(HTMLParser):
    def __init__(self): super().__init__(); self.links=[]; self.ids=set()
    def handle_starttag(self,tag,attrs):
        for key,value in attrs:
            if key in ('href','src'): self.links.append(value)
            if key=='id': self.ids.add(value)
for name in ('portfolio-repository','eportfolio-publish'):
    root=Path('output')/name
    checked=0
    for page in root.glob('*.html'):
        parser=Links(); parser.feed(page.read_text(encoding='utf-8'))
        for link in parser.links:
            url=urlsplit(link)
            if url.scheme or url.netloc: continue
            target=page.parent/unquote(url.path) if url.path else page
            assert target.exists(),(page,link)
            if url.fragment and target.suffix=='.html':
                other=Links(); other.feed(target.read_text(encoding='utf-8'))
                assert url.fragment in other.ids,(page,link)
            checked+=1
    print(name,':',checked,'local links verified')
