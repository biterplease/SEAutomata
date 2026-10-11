#!/usr/bin/env python3
"""Markdown -> Steam BBCode (the subset used by mod-guide.md).

Usage: python tools/md2bbcode.py mod-guide.md mod-guide-bbcode.txt

Supported: # / ## / ### headings, paragraphs (soft-wrapped lines are joined), **bold**, *italic*,
`code` (-> italic: Steam has no inline monospace), [links](url), nested - / * / 1. lists,
pipe tables, ``` fenced code, > quotes, --- rules.
Not supported: images, HTML, reference links, multi-paragraph list items.
"""
import re
import sys

LIST_RE = re.compile(r'^(\s*)([-*+]|\d+[.)])\s+(.*)$')
HEAD_RE = re.compile(r'^(#{1,6})\s+(.*?)\s*#*\s*$')
HR_RE = re.compile(r'^\s*([-*_])(\s*\1){2,}\s*$')
TABLE_SEP_RE = re.compile(r'^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$')


def inline(text):
    codes = []

    def keep(m):
        codes.append(m.group(1))
        return '\x00%d\x00' % (len(codes) - 1)

    text = re.sub(r'`([^`]+)`', keep, text)
    text = re.sub(r'\[([^\]]+)\]\(([^)\s]+)\)', r'[url=\2]\1[/url]', text)
    text = re.sub(r'\*\*(.+?)\*\*', r'[b]\1[/b]', text)
    text = re.sub(r'__(.+?)__', r'[b]\1[/b]', text)
    text = re.sub(r'(?<![\w*])\*(?!\s)(.+?)(?<!\s)\*(?![\w*])', r'[i]\1[/i]', text)
    text = re.sub(r'(?<!\w)_(?!\s)(.+?)(?<!\s)_(?!\w)', r'[i]\1[/i]', text)
    return re.sub(r'\x00(\d+)\x00', lambda m: '[i]' + codes[int(m.group(1))] + '[/i]', text)


def split_row(line):
    line = line.strip()
    if line.startswith('|'):
        line = line[1:]
    if line.endswith('|'):
        line = line[:-1]
    return [c.strip() for c in line.split('|')]


def convert(md):
    lines = md.replace('\r\n', '\n').split('\n')
    out = []
    i, n = 0, len(lines)

    def block(text):
        if out and out[-1] != '':
            out.append('')
        out.append(text)

    while i < n:
        line = lines[i]
        if not line.strip():
            i += 1
            continue

        if line.lstrip().startswith('```'):
            i += 1
            body = []
            while i < n and not lines[i].lstrip().startswith('```'):
                body.append(lines[i])
                i += 1
            i += 1
            block('[code]\n' + '\n'.join(body) + '\n[/code]')
            continue

        m = HEAD_RE.match(line)
        if m:
            level = min(len(m.group(1)), 3)   # Steam has h1-h3
            block('[h%d]%s[/h%d]' % (level, inline(m.group(2)), level))
            i += 1
            continue

        if HR_RE.match(line):
            block('[hr][/hr]')
            i += 1
            continue

        if '|' in line and i + 1 < n and TABLE_SEP_RE.match(lines[i + 1]):
            rows = ['[tr]' + ''.join('[th]%s[/th]' % inline(c) for c in split_row(line)) + '[/tr]']
            i += 2
            while i < n and lines[i].strip() and '|' in lines[i]:
                rows.append('[tr]' + ''.join('[td]%s[/td]' % inline(c) for c in split_row(lines[i])) + '[/tr]')
                i += 1
            block('[table]\n' + '\n'.join(rows) + '\n[/table]')
            continue

        if line.lstrip().startswith('>'):
            body = []
            while i < n and lines[i].lstrip().startswith('>'):
                body.append(lines[i].lstrip()[1:].strip())
                i += 1
            block('[quote]' + inline(' '.join(body)) + '[/quote]')
            continue

        if LIST_RE.match(line):
            res, stack = [], []   # stack: (indent, closing tag)
            while i < n:
                cur = lines[i]
                m = LIST_RE.match(cur)
                if m:
                    indent = len(m.group(1).expandtabs(4))
                    ordered = m.group(2)[0].isdigit()
                    while stack and indent < stack[-1][0]:
                        res.append(stack.pop()[1])
                    if not stack or indent > stack[-1][0]:
                        res.append('[olist]' if ordered else '[list]')
                        stack.append((indent, '[/olist]' if ordered else '[/list]'))
                    res.append('[*]' + inline(m.group(3)))
                    i += 1
                elif cur.strip() and cur[:1].isspace():   # continuation of the item above
                    res[-1] += ' ' + inline(cur.strip())
                    i += 1
                else:
                    break
            while stack:
                res.append(stack.pop()[1])
            block('\n'.join(res))
            continue

        para = []
        while i < n and lines[i].strip() and not (HEAD_RE.match(lines[i]) or LIST_RE.match(lines[i])
                                                  or HR_RE.match(lines[i]) or lines[i].lstrip().startswith(('```', '>'))):
            para.append(lines[i].strip())
            i += 1
        block(inline(' '.join(para)))

    return '\n'.join(out) + '\n'


if __name__ == '__main__':
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    with open(sys.argv[1], encoding='utf-8') as f:
        md = f.read()
    with open(sys.argv[2], 'w', encoding='utf-8', newline='\n') as f:
        f.write(convert(md))
