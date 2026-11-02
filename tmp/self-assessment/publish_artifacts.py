from pathlib import Path
from zipfile import ZipFile
from shutil import copyfile
from hashlib import sha256
from html.parser import HTMLParser
from urllib.parse import urlsplit,unquote
import json,re

base=Path('C:/Users/Payton/OneDrive/Desktop/CS499')
archives=[('Module 3/Clearspace-before-enhancements.zip','Clearspace-before-enhancements.zip'),('Module 3/Clearspace-ArtifactOne.zip','Clearspace-ArtifactOne.zip'),('Module 4/Clearspace-ArtifactTwo.zip','Clearspace-ArtifactTwo.zip'),('Module 5/Clearspace-ArtifactThree.zip','Clearspace-ArtifactThree.zip')]
manifest=[]
for rel,name in archives:
    p=base/rel
    with ZipFile(p) as z:
        assert z.testzip() is None
        count=sum(not n.endswith('/') for n in z.namelist())
    manifest.append({'file':name,'bytes':p.stat().st_size,'sha256':sha256(p.read_bytes()).hexdigest(),'file_count':count})
for folder in ('portfolio-repository','eportfolio-publish'):
    root=Path('output')/folder
    dest=root/'assets/artifacts'; dest.mkdir(parents=True,exist_ok=True)
    for (rel,name),info in zip(archives,manifest):
        copyfile(base/rel,dest/name)
        assert sha256((dest/name).read_bytes()).hexdigest()==info['sha256']
    (dest/'manifest.json').write_text(json.dumps({'description':'Unmodified artifact archives supplied by Payton Castle','archives':manifest},indent=2),encoding='utf-8')
    with ZipFile(base/archives[-1][0]) as z:
        report=next(n for n in z.namelist() if n.endswith('/milestone-four-final.trx'))
        (root/'assets/databases/milestone-four-final.trx').write_bytes(z.read(report))
    index=(root/'index.html').read_text(encoding='utf-8')
    mappings=[('software-design','Clearspace-before-enhancements.zip','Clearspace-ArtifactOne.zip'),('algorithms','Clearspace-ArtifactOne.zip','Clearspace-ArtifactTwo.zip'),('databases','Clearspace-ArtifactTwo.zip','Clearspace-ArtifactThree.zip')]
    for i,(category,original,enhanced) in enumerate(mappings,1):
        start=index.index(f'<section id="{category}"'); end=index.index('</section>',start)
        block=index[start:end]
        links=f'''<li><a class="repo-link" href="assets/artifacts/{original}">Download original source before this enhancement (ZIP)</a></li>
          <li><a class="repo-link" href="assets/artifacts/{enhanced}">Download enhanced Artifact {i} source (ZIP)</a></li>
          '''
        block=block.replace('<ul class="artifact-links">','<ul class="artifact-links">\n          '+links,1)
        if category=='databases':
            old='''The final narrative describes expanded file and folder locking, automatic
          re-locking, File Explorer integration, and password changes, and reports
          a 283-test suite. The source ZIP and 251-test report below preserve the
          earlier October 1 milestone snapshot; they do not include every later
          enhancement described in the final narrative.'''
            new='''The supplied Artifact Three archive includes the newer file and folder
          locking implementation described in the final narrative. Its saved final
          test report records 283 tests: 280 passed and 3 failed. The earlier
          October 1 report records 251 passing tests. These are archived results;
          the supplied ZIP has not been retested as part of portfolio publishing.'''
            assert old in block
            block=block.replace(old,new)
            block=re.sub(r'\s*<li><a class="repo-link" href="assets/databases/Payton_Castle_CS499_Milestone_Four_Code.zip">.*?</a></li>','',block)
            block=block.replace('<li><a class="repo-link" href="assets/databases/milestone-four.trx">', '<li><a class="repo-link" href="assets/databases/milestone-four-final.trx">Download final verification report — 280 passed, 3 failed (TRX)</a></li>\n          <li><a class="repo-link" href="assets/databases/milestone-four.trx">')
            block=block.replace('Download the 251-test verification report (TRX)','Download earlier October 1 verification — 251 passed (TRX)')
        index=index[:start]+block+index[end:]
        page=root/f'artifact-{["one","two","three"][i-1]}.html'
        html=page.read_text(encoding='utf-8')
        downloads=f'<p><a href="assets/artifacts/{original}">Download original source (ZIP)</a> · <a href="assets/artifacts/{enhanced}">Download enhanced source (ZIP)</a></p>'
        html=html.replace('</header>',downloads+'</header>',1)
        if category=='databases':
            note='<aside class="snapshot-note"><p><strong>Verification:</strong> The enhanced source download is the supplied Artifact Three archive and includes the newer locking code. Its <a href="assets/databases/milestone-four-final.trx">saved final report</a> records 280 passes and 3 failures out of 283 tests. Failures concern the older-password message assertion, locked-folder cleanup, and index retry timing. The archive has not been retested for this publication.</p></aside>'
            html=re.sub(r'<aside class="snapshot-note">.*?</aside>',note,html,count=1)
        page.write_text(html,encoding='utf-8')
    (root/'index.html').write_text(index,encoding='utf-8')
    print(folder,': four exact archives and final test report added')

class Links(HTMLParser):
    def __init__(self): super().__init__(); self.links=[]; self.ids=set()
    def handle_starttag(self,tag,attrs):
        for k,v in attrs:
            if k in ('href','src'): self.links.append(v)
            if k=='id': self.ids.add(v)
for folder in ('portfolio-repository','eportfolio-publish'):
    root=Path('output')/folder; checked=0
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
            checked+=1
    print(folder,':',checked,'local links verified')
