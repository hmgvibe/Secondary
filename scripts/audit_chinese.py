#!/usr/bin/env python3
"""Audit all authored Chinese UI strings: convert with current rules, flag residue.

Finds:
  (A) traditional characters still present after conversion (OpenCC table misses)
  (B) strings whose converted form still uses non-Mainland wording (blacklist scan)
"""
import os, re, sys, collections

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CORE = os.path.join(ROOT, 'src', 'Secondary.Core')
DATA = open(os.path.join(CORE, 'ChineseData.cs'), encoding='utf-8').read()
LOCALE = open(os.path.join(CORE, 'ChineseLocale.cs'), encoding='utf-8').read()

def grab_const(name):
    m = re.search(r'const string ' + name + r'\s*=\s*"([^"]*)"', DATA)
    return m.group(1)

def grab_array(name):
    m = re.search(r'string\[\] ' + name + r'\s*=\s*new\[\]\s*\{(.*?)\};', DATA, re.S)
    return re.findall(r'"((?:[^"\\]|\\.)*)"', m.group(1))

char_keys = grab_const('CharKeys')
char_vals = grab_const('CharValues')
phrase_keys = grab_array('PhraseKeys')
phrase_vals = grab_array('PhraseValues')

char_map = {}
for k, v in zip(char_keys, char_vals):
    char_map[k] = v

# Mainland phrases from ChineseLocale.cs (block only)
m = re.search(r'MainlandPhrases\s*=\s*\{(.*?)\n\s*\};', LOCALE, re.S)
mainland = re.findall(r'\{\s*"([^"]+)",\s*"([^"]+)"\s*\}', m.group(1)) if m else []

phrase_index = collections.defaultdict(list)
for k, v in zip(phrase_keys, phrase_vals):
    if k:
        phrase_index[k[0]].append((k, v))
for k, v in mainland:
    phrase_index[k[0]].append((k, v))
for lst in phrase_index.values():
    lst.sort(key=lambda p: -len(p[0]))

def to_simplified(text):
    out = []
    i = 0
    n = len(text)
    while i < n:
        if text[i] in phrase_index:
            hit = False
            for key, val in phrase_index[text[i]]:
                if text.startswith(key, i):
                    out.append(val); i += len(key); hit = True; break
            if hit:
                continue
        out.append(char_map.get(text[i], text[i]))
        i += 1
    return ''.join(out)

# Collect authored Chinese strings from source (exclude obj/bin, generated)
CJK = re.compile(r'[\u3400-\u4dbf\u4e00-\u9fff\uf900-\ufaff]{2,}')
found = collections.defaultdict(set)  # original string -> set of file paths
for dirpath, dirnames, filenames in os.walk(os.path.join(ROOT, 'src')):
    dirnames[:] = [d for d in dirnames if d not in ('obj', 'bin')]
    for fn in filenames:
        if not fn.endswith(('.cs', '.xaml', '.axaml')):
            continue
        if fn.endswith('.g.cs') or 'AssemblyInfo' in fn or fn == 'ChineseData.cs':
            continue
        path = os.path.join(dirpath, fn)
        try:
            txt = open(path, encoding='utf-8').read()
        except Exception:
            continue
        for m in CJK.finditer(txt):
            s = m.group(0)
            found[s].add(os.path.relpath(path, ROOT))

# Residue: any simplified char that is a traditional key (OpenCC missed it)
trad_keys = set(char_keys)
residue = []
for s in sorted(found):
    conv = to_simplified(s)
    leftover = sorted({c for c in conv if c in trad_keys})
    if leftover:
        residue.append((s, conv, ''.join(leftover)))

# Non-Mainland wording blacklist (Taiwan/HK terms that glyph-conversion leaves intact)
# Taiwan/HK terms whose glyph-simplified form is still NOT natural Mainland wording.
# key = authored Traditional term, value = natural Mainland Simplified term.
# Only include terms where glyph conversion alone leaves a non-Mainland word.
NON_MAINLAND = {
    '訊息':'消息', '選單':'菜单', '資料夾':'文件夹', '記錄檔':'日志文件',
    '快取':'缓存', '捷徑':'快捷方式', '全域':'全局', '逾時':'超时',
    '感測器':'传感器', '視訊':'视频', '光碟':'光盘', '位元組':'字节',
    '位元':'位', '功能表':'菜单', '狀態列':'状态栏', '檔案總管':'文件资源管理器',
    '主電腦':'主机', '行動裝置':'移动设备', '匯入':'导入', '匯出':'导出',
    '網際網路':'互联网', '回傳':'返回', '回应':'响应', '影片':'视频',
    '微調':'微调',
    '硬碟':'硬盘', '軟碟':'软盘', '光碟機':'光驱', '燒錄':'刻录', '隨身碟':'U盘',
    '列印':'打印', '數據機':'调制解调器', '監視器':'显示器', '喇叭':'音箱',
    '麥克風':'麦克风', '數位':'数字', '類比':'模拟', '位元':'位',
    '檔案總管':'文件资源管理器', '控制台':'控制面板', '連絡':'联系',
    '訊號':'信号', '號碼':'号码', '運算子':'运算符', '變數':'变量',
    '註解':'注释', '字串':'字符串', '陣列':'数组', '函式':'函数',
    '迴圈':'循环', '除錯':'调试', '演算法':'算法', '物件':'对象',
    '屬性':'属性', '方法':'方法', '模組':'模块', '套件':'包',
    # already in MainlandPhrases - kept so audit flags anything the longer-phrase pass misses
    '設定':'设置', '儲存':'保存', '連線':'连接', '離線':'离线', '網路':'网络',
    '軟體':'软件', '硬體':'硬件', '資訊':'信息', '專案':'项目', '介面':'界面',
    '螢幕':'屏幕', '滑鼠':'鼠标', '印表機':'打印机', '程式':'程序', '檔案':'文件',
    '資料庫':'数据库', '記憶體':'内存', '視窗':'窗口', '工具列':'工具栏',
    '圖示':'图标', '驅動程式':'驱动程序', '支援':'支持', '預設':'默认',
    '選項':'选项', '選擇':'选择', '服務':'服务', '品質':'质量', '效能':'性能',
    '解析度':'分辨率', '更新率':'刷新率', '登入':'登录', '登出':'退出',
    '警示':'警告', '便箋':'便签', '系統匣':'系统托盘', '跟隨系統':'跟随系统',
}
mainland_keys = {k for k, _ in mainland}
nonmain = []
# A residue is a conversion result that still contains a Taiwan/HK-specific Simplified form.
# `glyph` is the glyph-only Simplified form of the TW term; if the MainlandPhrases table maps
# this term, conversion produces the Mainland form instead, so it is NOT a residue.
for s in sorted(found):
    conv = to_simplified(s)
    for tw, cn in NON_MAINLAND.items():
        if not tw:
            continue
        glyph = to_simplified(tw)  # glyph-only simplified form (e.g. 選單 -> 选单)
        if not glyph or tw in mainland_keys:
            continue  # already mapped to natural Mainland wording
        if glyph in conv:
            nonmain.append((s, conv, tw, cn))
            break

out_dir = os.path.join(ROOT, 'artifacts', 'audit')
os.makedirs(out_dir, exist_ok=True)

with open(os.path.join(out_dir, 'all-strings.txt'), 'w', encoding='utf-8') as f:
    for s in sorted(found):
        f.write(f'{s}\t=>\t{to_simplified(s)}\n')

with open(os.path.join(out_dir, 'residue.txt'), 'w', encoding='utf-8') as f:
    for s, conv, left in residue:
        f.write(f'{s}\t=>\t{conv}\t[残留:{left}]\n')

with open(os.path.join(out_dir, 'nonmainland.txt'), 'w', encoding='utf-8') as f:
    for s, conv, tw, cn in nonmain:
        f.write(f'{s}\t=>\t{conv}\t[疑似港台:{tw} 应为:{cn}]\n')

print(f'唯一文案片段: {len(found)}')
print(f'残留繁体字片段: {len(residue)}')
print(f'疑似港台用词片段: {len(nonmain)}')
print(f'MainlandPhrases 现有条目: {len(mainland)}')
print(f'输出目录: {out_dir}')
