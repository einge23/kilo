"""Regenerate the workbook with reportlab; run from any working directory."""
from pathlib import Path
from html import escape
import re

from reportlab.lib import colors
from reportlab.lib.styles import ParagraphStyle, getSampleStyleSheet
from reportlab.platypus import (
    SimpleDocTemplate, Paragraph, Spacer, Preformatted, PageBreak,
    Table, TableStyle, Flowable,
)
from pypdf import PdfReader

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / 'docs/workout-tracker-plan.md'
OUTPUT = ROOT / 'output/pdf/workout-tracker-dotnet10-revised-plan.pdf'
NAVY = colors.HexColor('#172D42')
TEAL = colors.HexColor('#087F83')
GRAY = colors.HexColor('#536575')
PALE = colors.HexColor('#EFF5F6')
WIDTH = 504

styles = getSampleStyleSheet()
styles.add(ParagraphStyle('PlanTitle', fontName='Helvetica-Bold', fontSize=23,
    leading=27, textColor=NAVY, spaceAfter=18))
styles.add(ParagraphStyle('PlanHead', fontName='Helvetica-Bold', fontSize=12,
    leading=16, textColor=TEAL, spaceBefore=10, spaceAfter=6))
styles.add(ParagraphStyle('PlanBody', fontName='Helvetica', fontSize=9.7,
    leading=13.1, textColor=NAVY, spaceAfter=7, splitLongWords=True))
styles.add(ParagraphStyle('PlanList', parent=styles['PlanBody'], leftIndent=12,
    firstLineIndent=-12, spaceAfter=6))
styles.add(ParagraphStyle('PlanCell', parent=styles['PlanBody'], fontSize=8.7,
    leading=11.5, spaceAfter=0))
styles.add(ParagraphStyle('PlanCode', fontName='Courier', fontSize=8,
    leading=10, textColor=NAVY, backColor=PALE, borderPadding=8,
    spaceBefore=6, spaceAfter=10))


def inline(text):
    value = escape(text)
    value = re.sub(r'`([^`]+)`', r'<font name="Courier">\1</font>', value)
    # Readable primary-source URLs remain clickable in the PDF.
    value = re.sub(r'(https://[^\s]+)', r'<link href="\1" color="#087F83">\1</link>', value)
    return value


class Progress(Flowable):
    def __init__(self, number):
        super().__init__()
        self.number = number
        self.width = WIDTH
        self.height = 158

    def draw(self):
        c = self.canv
        c.setFillColor(PALE)
        c.roundRect(0, 0, self.width, self.height, 7, fill=1, stroke=0)
        c.setFont('Helvetica-Bold', 9)
        c.setFillColor(TEAL)
        c.drawString(12, 139, 'PROGRESS / SLICE ' + self.number)
        prefix = 'slice_' + self.number
        for x, key, label in [(12, 'implemented', 'Implementation complete'),
                              (268, 'verified', 'Verification passed')]:
            c.acroForm.checkbox(name=prefix+'_'+key, tooltip=label,
                x=x, y=114, size=12, checked=False, relative=True,
                borderColor=GRAY, fillColor=colors.white, textColor=TEAL,
                buttonStyle='check', forceBorder=True)
            c.setFont('Helvetica', 9)
            c.setFillColor(NAVY)
            c.drawString(x+20, 116, label)
        c.setFont('Helvetica', 8)
        c.drawString(12, 98, 'Status: Planned / Building / Blocked / Done')
        c.drawString(268, 98, 'Review date (YYYY-MM-DD)')
        for x, key, value in [(12, 'status', 'Planned'), (268, 'review_date', '')]:
            c.acroForm.textfield(name=prefix+'_'+key, tooltip=key,
                x=x, y=74, width=224, height=20, value=value,
                relative=True, fontName='Helvetica', fontSize=9,
                borderColor=GRAY, fillColor=colors.white, textColor=NAVY,
                forceBorder=True)
        c.setFont('Helvetica', 8)
        c.drawString(12, 62, 'Evidence / failures / follow-ups')
        c.acroForm.textfield(name=prefix+'_notes', tooltip='Evidence and follow-ups',
            x=12, y=10, width=480, height=44, relative=True,
            fieldFlags='multiline', fontName='Helvetica', fontSize=9,
            maxlen=4000, borderColor=GRAY, fillColor=colors.white,
            textColor=NAVY, forceBorder=True)


class Workbook(SimpleDocTemplate):
    def afterFlowable(self, flowable):
        if isinstance(flowable, Paragraph) and flowable.style.name == 'PlanTitle':
            title = flowable.getPlainText()
            key = 'section_' + str(len(self.section_pages))
            self.canv.bookmarkPage(key)
            self.canv.addOutlineEntry(title, key, 0)
            self.section_pages.append((title, self.page))


def chrome(canvas, doc):
    canvas.saveState()
    canvas.setStrokeColor(TEAL)
    canvas.setLineWidth(2)
    canvas.line(54, 751, 558, 751)
    canvas.setFont('Helvetica-Bold', 8)
    canvas.setFillColor(GRAY)
    canvas.drawString(54, 763, 'KILO / .NET 10 / CUMULATIVE IMPLEMENTATION')
    canvas.setFont('Helvetica', 8)
    canvas.drawString(54, 31, 'REVISED OCTOBER 7, 2026   /   BUILD -> VERIFY -> RECORD')
    canvas.drawRightString(558, 31, str(doc.page))
    canvas.restoreState()


def table(lines):
    rows = [[c.strip() for c in line.strip().strip('|').split('|')] for line in lines]
    rows = [r for r in rows if not all(re.fullmatch(r'[:\- ]+', c) for c in r)]
    if rows[0][0] == 'Slice' and len(rows[0]) == 3:
        widths = [38, 282, 184] if 'Method' in rows[0][1] else [42, 350, 112]
    elif rows[0][0] == 'First used':
        widths = [68, 180, 256]
    else:
        widths = [WIDTH/len(rows[0])] * len(rows[0])
    cells = [[Paragraph(inline(c), styles['PlanCell']) for c in row] for row in rows]
    t = Table(cells, colWidths=widths, repeatRows=1, hAlign='LEFT')
    t.setStyle(TableStyle([
        ('BACKGROUND', (0, 0), (-1, 0), PALE),
        ('VALIGN', (0, 0), (-1, -1), 'TOP'),
        ('LINEBELOW', (0, 0), (-1, 0), 1, TEAL),
        ('LINEBELOW', (0, 1), (-1, -1), .3, colors.HexColor('#D9E2E5')),
        ('LEFTPADDING', (0, 0), (-1, -1), 6),
        ('RIGHTPADDING', (0, 0), (-1, -1), 6),
        ('TOPPADDING', (0, 0), (-1, -1), 4),
        ('BOTTOMPADDING', (0, 0), (-1, -1), 4),
    ]))
    return t


def parse(text):
    story = []
    lines = text.splitlines()
    i = 0
    while i < len(lines):
        line = lines[i]
        if not line.strip():
            i += 1
            continue
        if line == '---page---':
            story.append(PageBreak())
        elif line.startswith('# '):
            story.append(Paragraph(inline(line[2:]), styles['PlanTitle']))
        elif line.startswith('## '):
            story.append(Paragraph(inline(line[3:]), styles['PlanHead']))
        elif line.startswith('@progress '):
            story += [Spacer(1, 8), Progress(line.split()[1])]
        elif line.startswith('```'):
            i += 1
            code = []
            while i < len(lines) and not lines[i].startswith('```'):
                code.append(lines[i])
                i += 1
            from reportlab.pdfbase.pdfmetrics import stringWidth
            assert all(stringWidth(s, 'Courier', 8) <= WIDTH-16 for s in code), 'Code too wide'
            story.append(Preformatted('\n'.join(code), styles['PlanCode']))
        elif line.startswith('|'):
            block = []
            while i < len(lines) and lines[i].startswith('|'):
                block.append(lines[i])
                i += 1
            story.append(table(block))
            continue
        else:
            is_list = line.startswith('- ') or bool(re.match(r'^\d+\. ', line))
            story.append(Paragraph(inline(line), styles['PlanList' if is_list else 'PlanBody']))
        i += 1
    return story


def main():
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    document = Workbook(str(OUTPUT), pagesize=(612, 792), leftMargin=54,
        rightMargin=54, topMargin=57, bottomMargin=52,
        title='Kilo - Cumulative .NET 10 Implementation Workbook',
        author='Kilo project', subject='20 slices with cumulative implementation notes')
    document.section_pages = []
    story = parse(SOURCE.read_text(encoding='utf-8'))
    expected_sections = sum(isinstance(item, Paragraph) and item.style.name == 'PlanTitle'
                            for item in story)
    document.build(story, onFirstPage=chrome, onLaterPages=chrome)
    reader = PdfReader(OUTPUT)
    fields = reader.get_fields()
    assert len(fields) == 100, f'Expected 100 progress fields, got {len(fields)}'
    assert len(document.section_pages) == expected_sections
    print(f'Created {len(reader.pages)} pages, {len(fields)} form fields, {len(document.section_pages)} bookmarks.')
    for title, page in document.section_pages:
        print(f'{page:02d} {title}')


if __name__ == '__main__':
    main()
