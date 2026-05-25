#!/usr/bin/env python3
"""One-off cleaner for excel_Asistant.md — removes accidental backslash escapes
and collapses excessive blank lines that broke YAML frontmatter parsing.
"""
import re
import sys

path = "dotnet/Api/Skills/excel_Asistant.md" if len(sys.argv) < 2 else sys.argv[1]

with open(path, encoding="utf-8") as f:
    raw = f.read()

# Strip backslash escapes before common markdown chars
def unescape(m):
    return m.group(1)

cleaned = re.sub(r"\\(---|##|#|-|\*|&)", unescape, raw)

# Normalize line endings
cleaned = cleaned.replace("\r\n", "\n").replace("\r", "\n")

# Collapse 3+ blank lines to 2
cleaned = re.sub(r"\n{3,}", "\n\n", cleaned)

# Fix frontmatter (keys separated by blank lines)
m = re.match(r"^---\s*\n(.+?)\n---", cleaned, re.DOTALL)
if m:
    pairs = [l.strip() for l in m.group(1).split("\n") if l.strip() and ":" in l]
    cleaned = "---\n" + "\n".join(pairs) + "\n---" + cleaned[m.end():]

# Collapse blank lines between consecutive list items
lines = cleaned.split("\n")
out = []
i = 0
while i < len(lines):
    out.append(lines[i])
    if i + 2 < len(lines):
        cur = lines[i].strip()
        nxt = lines[i + 2].strip()
        is_list = lambda s: s.startswith("- ") or s.startswith("* ") or bool(re.match(r"^\d+\.\s", s))
        if lines[i + 1].strip() == "" and is_list(cur) and is_list(nxt):
            i += 2
            continue
    i += 1
cleaned = "\n".join(out)

with open(path, "w", encoding="utf-8", newline="\n") as f:
    f.write(cleaned)

print(f"Wrote {len(cleaned)} bytes, {cleaned.count(chr(10))} lines")
print("\n=== First 20 lines ===")
print("\n".join(cleaned.split("\n")[:20]))
