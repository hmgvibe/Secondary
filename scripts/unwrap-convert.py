#!/usr/bin/env python3
"""Remove `ChineseLocale.Convert(...)` wrappers now that source literals are Simplified.

A tiny lexer tracks C# string/char literals and comments so parentheses inside
text never confuse the matching of the closing call parenthesis.
"""
import io
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
NEEDLE = "ChineseLocale.Convert("


def unwrap(text):
    out = []
    pos = 0
    while True:
        idx = text.find(NEEDLE, pos)
        if idx < 0:
            out.append(text[pos:])
            break
        out.append(text[pos:idx])
        # walk forward from the opening paren, tracking depth and literals
        start = idx + len(NEEDLE)
        depth = 1
        i = start
        state = "code"
        while i < len(text) and depth:
            c = text[i]
            if state == "code":
                if c == '"':
                    state = "string"
                elif c == "'":
                    state = "char"
                elif c == '/' and i + 1 < len(text) and text[i + 1] == '/':
                    state = "line"
                elif c == '(':
                    depth += 1
                elif c == ')':
                    depth -= 1
                    if depth == 0:
                        break
            elif state == "string":
                if c == '\\':
                    i += 1
                elif c == '"':
                    state = "code"
            elif state == "char":
                if c == '\\':
                    i += 1
                elif c == "'":
                    state = "code"
            elif state == "line":
                if c == '\n':
                    state = "code"
            i += 1
        if depth:
            out.append(text[idx:])
            break
        inner = text[start:i]
        # Parenthesise to preserve precedence when the argument is an expression.
        out.append("(" + inner + ")" if inner.strip() else inner)
        pos = i + 1
    return "".join(out)


def main():
    dry = "--dry" in sys.argv
    changed = 0
    for base in ("src", "tests"):
        for folder, dirs, files in os.walk(os.path.join(ROOT, base)):
            dirs[:] = [d for d in dirs if d not in ("obj", "bin", ".git")]
            for name in files:
                if not name.endswith((".cs", ".xaml", ".axaml")):
                    continue
                path = os.path.join(folder, name)
                with io.open(path, encoding="utf-8") as handle:
                    original = handle.read()
                if NEEDLE not in original:
                    continue
                converted = original
                for _ in range(8):
                    nxt = unwrap(converted)
                    if nxt == converted:
                        break
                    converted = nxt
                if NEEDLE in converted:
                    print("  WARNING residual Convert in " + os.path.relpath(path, ROOT))
                changed += 1
                if not dry:
                    with io.open(path, "w", encoding="utf-8", newline="") as handle:
                        handle.write(converted)
                print("  " + ("DRY " if dry else "") + os.path.relpath(path, ROOT))
    print("files with Convert unwrapped:", changed)


if __name__ == "__main__":
    main()
