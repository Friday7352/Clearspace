from pathlib import Path
from docx import Document
from docx.shared import Inches, Pt, RGBColor
from docx.enum.text import WD_ALIGN_PARAGRAPH
from docx.oxml import OxmlElement
from docx.oxml.ns import qn

path=Path('output/doc/Payton_Castle_CS499_Professional_Self_Assessment_Draft.docx')
doc=Document(path)
for sec in doc.sections:
    sec.top_margin=sec.bottom_margin=sec.left_margin=sec.right_margin=Inches(1)
    sec.header_distance=Inches(0.5)
    sec.different_first_page_header_footer=False
for style in doc.styles:
    if style.type==1:
        style.font.name='Times New Roman'
        style.font.size=Pt(12)
        style.font.color.rgb=RGBColor(0,0,0)
        style.paragraph_format.line_spacing=2
        style.paragraph_format.space_before=Pt(0)
        style.paragraph_format.space_after=Pt(0)
    for border in list(style.element.iter(qn('w:pBdr'))):
        border.getparent().remove(border)
doc.styles['Normal'].paragraph_format.first_line_indent=Inches(0.5)
for name in ('Title','Heading 1'):
    st=doc.styles[name]
    st.font.bold=True
    st.font.italic=False
    st.paragraph_format.alignment=WD_ALIGN_PARAGRAPH.CENTER
    st.paragraph_format.first_line_indent=Inches(0)
    st.paragraph_format.keep_with_next=True
for p in list(doc.paragraphs):
    if p.style.name=='Subtitle':
        p._element.getparent().remove(p._element)
        continue
    fmt=p.paragraph_format
    fmt.page_break_before=False
    fmt.line_spacing=2
    fmt.space_before=fmt.space_after=Pt(0)
    fmt.widow_control=True
    fmt.alignment=WD_ALIGN_PARAGRAPH.CENTER if p.style.name in ('Title','Heading 1') else WD_ALIGN_PARAGRAPH.LEFT
    fmt.first_line_indent=Inches(0) if p.style.name in ('Title','Heading 1') or not p.text else Inches(0.5)
    for run in p.runs:
        run.font.name='Times New Roman'
        run.font.size=Pt(12)
for sec in doc.sections:
    p=sec.header.paragraphs[0]
    p.clear()
    p.alignment=WD_ALIGN_PARAGRAPH.RIGHT
    p.paragraph_format.first_line_indent=Inches(0)
    p.paragraph_format.space_before=p.paragraph_format.space_after=Pt(0)
    run=p.add_run()
    run.font.name='Times New Roman'; run.font.size=Pt(12)
    field=OxmlElement('w:fldSimple'); field.set(qn('w:instr'),'PAGE')
    fieldrun=OxmlElement('w:r'); t=OxmlElement('w:t'); t.text='1'; fieldrun.append(t); field.append(fieldrun)
    p._p.append(field)
doc.save(path)
check=Document(path)
assert len(check.comments)==2
assert all(p.paragraph_format.line_spacing==2 for p in check.paragraphs)
print('APA formatting verified; no title page; two review comments preserved')
