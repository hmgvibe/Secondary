#!/usr/bin/env python3
"""One-shot refactor helper: convert authored Traditional literals to Simplified.

Uses the already-vetted OpenCC mapping checked into ChineseData.cs plus the
Mainland vocabulary overrides in ChineseLocale.cs. Phrases are matched
longest-first so vocabulary (e.g. 設定 -> 设置) wins over glyph-only mapping.
"""
import io
import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def read(path):
    with io.open(path, encoding="utf-8") as handle:
        return handle.read()


def write(path, text):
    with io.open(path, "w", encoding="utf-8", newline="") as handle:
        handle.write(text)


def build_maps():
    data = read(os.path.join(ROOT, "src", "Secondary.Core", "ChineseData.cs"))
    locale = read(os.path.join(ROOT, "src", "Secondary.Core", "ChineseLocale.cs"))

    char_keys = re.search(r'CharKeys\s*=\s*"([^"]*)"', data).group(1)
    char_values = re.search(r'CharValues\s*=\s*"([^"]*)"', data).group(1)
    assert len(char_keys) == len(char_values), "char maps misaligned"

    phrase_block = re.search(r'PhraseKeys\s*=\s*new\[\]\s*\{(.*?)\n\s*\};', data, re.S).group(1)
    value_block = re.search(r'PhraseValues\s*=\s*new\[\]\s*\{(.*?)\n\s*\};', data, re.S).group(1)
    phrase_keys = re.findall(r'"([^"]*)"', phrase_block)
    phrase_values = re.findall(r'"([^"]*)"', value_block)
    assert len(phrase_keys) == len(phrase_values), "phrase maps misaligned"

    phrases = {key: value for key, value in zip(phrase_keys, phrase_values) if key}
    # Mainland vocabulary overrides glyph-only OpenCC entries.
    for key, value in re.findall(r'\{\s*"([^"]+)",\s*"([^"]+)"\s*\}', locale):
        phrases[key] = value

    char_map = dict(zip(char_keys, char_values))
    return phrases, char_map


def make_converter(phrases, char_map):
    max_phrase = max(len(k) for k in phrases)
    starts = set(k[0] for k in phrases)

    def convert(text):
        out = []
        i = 0
        size = len(text)
        while i < size:
            if text[i] in starts:
                matched = False
                for length in range(min(max_phrase, size - i), 1, -1):
                    value = phrases.get(text[i:i + length])
                    if value is not None:
                        out.append(value)
                        i += length
                        matched = True
                        break
                if matched:
                    continue
            out.append(char_map.get(text[i], text[i]))
            i += 1
        return "".join(out)

    return convert


def main():
    dry = "--dry" in sys.argv
    phrases, char_map = build_maps()
    convert = make_converter(phrases, char_map)
    extensions = (".cs", ".xaml", ".axaml")
    skip = {
        os.path.join("src", "Secondary.Core", "ChineseData.cs"),
        os.path.join("src", "Secondary.Core", "ChineseLocale.cs"),
    }
    changed = 0
    for base in ("src", "tests"):
        for folder, dirs, files in os.walk(os.path.join(ROOT, base)):
            dirs[:] = [d for d in dirs if d not in ("obj", "bin", ".git")]
            for name in files:
                if not name.endswith(extensions):
                    continue
                path = os.path.join(folder, name)
                rel = os.path.relpath(path, ROOT)
                if rel in skip:
                    continue
                original = read(path)
                converted = convert(original)
                if converted != original:
                    changed += 1
                    if not dry:
                        write(path, converted)
                    print("  " + ("DRY " if dry else "") + rel)
    print("converted files:", changed)


if __name__ == "__main__":
    main()
