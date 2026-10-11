"""
Generates ZDepth's UI assets (standard library only):

  EmbeddedResources/ZDepth.ico    plug-in icon
  dist/icon.png                   package icon
  ZDepth.rui                      toolbar: ZDepth (right-click: ZDepthRange) and ZDepthCapture

Run from the repository root:  python3 tools/make_ui.py
GUIDs are fixed so Rhino keeps the toolbar's position and visibility across updates.
"""
import base64
import os
import struct
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


# --- tiny PNG / ICO writer --------------------------------------------------

def png(w, h, rgba):
    raw = b''.join(b'\x00' + rgba[y * w * 4:(y + 1) * w * 4] for y in range(h))

    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)

    return (b'\x89PNG\r\n\x1a\n'
            + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
            + chunk(b'IDAT', zlib.compress(raw, 9))
            + chunk(b'IEND', b''))


def ico(images):
    """images: list of (size, png bytes)."""
    header = struct.pack('<HHH', 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries = b''
    for size, data in images:
        entries += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    return header + entries + b''.join(d for _, d in images)


# --- icon drawing (unit square, y down) -------------------------------------

def hexc(s):
    return tuple(int(s[i:i + 2], 16) for i in (1, 3, 5))


BLACK, WHITE = hexc('#000000'), hexc('#FFFFFF')


def grey(level, v):
    """A depth grey, slightly lighter towards the bottom (nearer) of the shape."""
    g = max(0, min(255, round(level + (v - 0.5) * 70)))
    return (g, g, g)


def in_rounded_square(x, y, r=0.2):
    cx = min(max(x, r), 1 - r)
    cy = min(max(y, r), 1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def shapes(x, y, box=(0.0, 0.0, 1.0, 1.0)):
    """Clown ID's motif as a depth pass: far shapes dark, near shapes light."""
    x0, y0, x1, y1 = box
    u, v = (x - x0) / (x1 - x0), (y - y0) / (y1 - y0)
    col = None
    if 0.13 <= u <= 0.60 and 0.44 <= v <= 0.87:
        col = grey(85, v)
    if 0.29 <= u <= 0.51 and 0.13 <= v <= 0.62:
        col = grey(150, v)
    if (u - 0.69) ** 2 + (v - 0.60) ** 2 <= 0.19 ** 2:
        col = grey(225, v)
    return col


def mode_icon(x, y):
    if not in_rounded_square(x, y):
        return None
    return shapes(x, y) or BLACK


def capture_icon(x, y):
    if not in_rounded_square(x, y):
        return None
    # viewfinder corners
    a, b, t = 0.11, 0.37, 0.10
    for cx, cy in ((a, a), (1 - a, a), (a, 1 - a), (1 - a, 1 - a)):
        dx, dy = abs(x - cx), abs(y - cy)
        sx = 1 if cx < 0.5 else -1
        sy = 1 if cy < 0.5 else -1
        along_x = 0 <= (x - cx) * sx <= b - a and 0 <= (y - cy) * sy <= t
        along_y = 0 <= (y - cy) * sy <= b - a and 0 <= (x - cx) * sx <= t
        if along_x or along_y:
            return WHITE
    return shapes(x, y, (0.2, 0.2, 0.8, 0.8)) or BLACK


def render(fn, n, ss=8):
    out = bytearray()
    for py in range(n):
        for px in range(n):
            acc, hits = [0, 0, 0], 0
            for sy in range(ss):
                for sx in range(ss):
                    c = fn((px + (sx + .5) / ss) / n, (py + (sy + .5) / ss) / n)
                    if c:
                        acc = [acc[i] + c[i] for i in range(3)]
                        hits += 1
            out += bytes([acc[i] // hits if hits else 0 for i in range(3)] + [round(255 * hits / ss / ss)])
    return bytes(out)


def strip(icons, n):
    """Horizontal strip of icons, n×n each, as one PNG."""
    tiles = [render(fn, n) for fn in icons]
    rows = b''.join(b''.join(t[y * n * 4:(y + 1) * n * 4] for t in tiles) for y in range(n))
    return png(n * len(icons), n, rows)


# --- toolbar file ------------------------------------------------------------

FILE_ID = '6fd50dca-9fc2-4cce-96b4-15623f71bfeb'
MENU_ID = '9f8945c3-ef96-4c8f-b62e-59f429aa0cb3'
GROUP_ID = 'afaf8e51-712a-4a9d-8443-4d12c24f326e'
DOCK_ID = '5f6341be-1168-415e-8b27-ad7273a2059a'
GROUP_ITEM_ID = 'fc827b84-b49e-4c70-bdfa-1ed9e198ffd2'
TOOLBAR_ID = '06e2c6c6-280a-406b-bee9-bbfb3d1620e9'

BUTTONS = [
    # (item id, macro id, bitmap id, icon, script, name, tooltip)
    ('dc31e337-0f8f-4eaf-9e72-e9de09ac8e88', '6420923d-59d3-45e4-b9f5-8b50f995b552',
     'f3e936d4-a1de-4a9a-8390-f40196ea4e9c', mode_icon, '! _ZDepth', 'Z Depth',
     'Switch the active viewport to Z Depth and back'),
    ('e9e207dc-3997-4d3e-babe-963b04530613', 'c706c1f9-358a-4362-bb2f-ebba50791989',
     '852e9120-76a6-4cb5-a43c-b5e6f69fdde3', capture_icon, '! _ZDepthCapture', 'Z Depth Capture',
     'Save the active view as a depth map: PNG, 16-bit PNG or EXR, at any size'),
]

# Right-click on a button: (button index, macro id, script, name, tooltip)
RIGHT_MACROS = [
    (0, '490f5a7d-18e8-472f-86ed-4d1b8e3d411c', '! _ZDepthRange', 'Z Depth Range',
     'Set the depth range (auto or fixed near/far) and which end is white'),
]

def text(tag, value, indent):
    pad = ' ' * indent
    return f'{pad}<{tag}>\n{pad}  <locale_1033>{value}</locale_1033>\n{pad}</{tag}>\n'


def rui():
    x = '<?xml version="1.0" encoding="utf-8"?>\n'
    x += (f'<RhinoUI major_ver="5" minor_ver="0" guid="{FILE_ID}" localize="False" '
          f'default_language_id="1033" dpi_scale="100">\n')
    x += '  <extend_rhino_menus>\n'
    x += f'    <menu guid="{MENU_ID}">\n' + text('text', 'Extend Rhino Menus', 6) + '    </menu>\n'
    x += '  </extend_rhino_menus>\n'
    x += '  <menus />\n'
    x += '  <tool_bar_groups>\n'
    x += (f'    <tool_bar_group guid="{GROUP_ID}" dock_bar_guid64="{DOCK_ID}" '
          f'active_tool_bar_group="{TOOLBAR_ID}" single_file="True" '
          f'hide_single_tab="True" point_floating="400,200">\n')
    x += text('text', 'ZDepth', 6)
    x += f'      <tool_bar_group_item guid="{GROUP_ITEM_ID}" major_version="1" minor_version="1">\n'
    x += text('text', 'ZDepth', 8)
    x += f'        <tool_bar_id>{TOOLBAR_ID}</tool_bar_id>\n'
    x += '      </tool_bar_group_item>\n'
    x += ('      <dock_bar_info dpi_scale="100" dock_bar="False" docking="True" horz="True" visible="True" '
          'floating="True" mru_float_style="8192" bar_id="59650" mru_width="100" point_pos="-2,-2" '
          'float_point="400,200" dock_location_u="59419" dock_location="top" float_size="90,46" />\n')
    x += '    </tool_bar_group>\n'
    x += '  </tool_bar_groups>\n'
    x += '  <tool_bars>\n'
    x += f'    <tool_bar guid="{TOOLBAR_ID}" bitmap_id="{BUTTONS[0][2]}">\n'
    x += text('text', 'ZDepth', 6)
    right = {i: m for i, m, *_ in RIGHT_MACROS}
    for i, (item_id, macro_id, *_) in enumerate(BUTTONS):
        x += f'      <tool_bar_item guid="{item_id}">\n'
        x += f'        <left_macro_id>{macro_id}</left_macro_id>\n'
        if i in right:
            x += f'        <right_macro_id>{right[i]}</right_macro_id>\n'
        x += '      </tool_bar_item>\n'
    x += '    </tool_bar>\n'
    x += '  </tool_bars>\n'
    x += '  <macros>\n'
    for _, macro_id, bitmap_id, _, script, name, tip in BUTTONS:
        x += f'    <macro_item guid="{macro_id}" bitmap_id="{bitmap_id}">\n'
        x += text('text', name, 6) + text('tooltip', tip, 6) + text('help_text', tip, 6)
        x += text('button_text', name, 6) + text('menu_text', name, 6)
        x += f'      <script>{script}</script>\n'
        x += '    </macro_item>\n'
    for i, macro_id, script, name, tip in RIGHT_MACROS:
        x += f'    <macro_item guid="{macro_id}" bitmap_id="{BUTTONS[i][2]}">\n'
        x += text('text', name, 6) + text('tooltip', tip, 6) + text('help_text', tip, 6)
        x += text('button_text', name, 6) + text('menu_text', name, 6)
        x += f'      <script>{script}</script>\n'
        x += '    </macro_item>\n'
    x += '  </macros>\n'
    x += '  <bitmaps>\n'
    for tag, n in (('small_bitmap', 16), ('normal_bitmap', 24), ('large_bitmap', 32)):
        x += f'    <{tag} item_width="{n}" item_height="{n}">\n'
        for i, b in enumerate(BUTTONS):
            x += f'      <bitmap_item guid="{b[2]}" index="{i}" />\n'
        data = base64.b64encode(strip([b[3] for b in BUTTONS], n)).decode()
        x += f'      <bitmap>{data}</bitmap>\n'
        x += f'    </{tag}>\n'
    x += '  </bitmaps>\n'
    x += '  <scripts />\n'
    x += '</RhinoUI>\n'
    return x


def main():
    big = render(mode_icon, 256, 4)
    with open(os.path.join(ROOT, 'dist', 'icon.png'), 'wb') as f:
        f.write(png(256, 256, big))
    sizes = [16, 24, 32, 48, 256]
    images = [(s, png(s, s, big if s == 256 else render(mode_icon, s))) for s in sizes]
    with open(os.path.join(ROOT, 'EmbeddedResources', 'ZDepth.ico'), 'wb') as f:
        f.write(ico(images))
    with open(os.path.join(ROOT, 'ZDepth.rui'), 'w', encoding='utf-8') as f:
        f.write(rui())
    print('wrote dist/icon.png, EmbeddedResources/ZDepth.ico, ZDepth.rui')


if __name__ == '__main__':
    main()
