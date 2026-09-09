"""Report figures: one layout produces both SVG and Word fallback PNG."""
from pathlib import Path
from xml.sax.saxutils import escape
from PIL import Image, ImageDraw, ImageFont


class Figure:
    def __init__(self, title, height=900):
        self.height = height
        self.image = Image.new('RGB', (1800, height), 'white')
        self.draw = ImageDraw.Draw(self.image)
        self.svg = [f'<svg xmlns="http://www.w3.org/2000/svg" width="1800" height="{height}" viewBox="0 0 1800 {height}" role="img"><title>{escape(title)}</title><rect width="1800" height="{height}" fill="white"/>']
        self.text(60, 65, title, 46)

    def text(self, x, y, value, size=38, color='#334155', center=False):
        font_path = Path('C:/Windows/Fonts/yugothm.ttc')
        if not font_path.exists():
            font_path = Path('C:/Windows/Fonts/meiryo.ttc')
        font = ImageFont.truetype(str(font_path), size)
        for i, line in enumerate(value.split('\n')):
            baseline = y + i * (size + 14)
            self.draw.text((x, baseline), line, font=font, fill=color, anchor='ms' if center else 'ls')
            anchor = 'middle' if center else 'start'
            self.svg.append(f'<text x="{x}" y="{baseline}" font-family="Yu Gothic, Meiryo, sans-serif" font-size="{size}" text-anchor="{anchor}" fill="{color}">{escape(line)}</text>')

    def box(self, x, y, w, h, title, subtitle='', accent=False):
        fill = '#EEF5F5' if accent else '#F5F7FA'
        self.draw.rounded_rectangle((x, y, x+w, y+h), radius=14, fill=fill, outline='#A8B6C5', width=2)
        self.svg.append(f'<rect x="{x}" y="{y}" width="{w}" height="{h}" rx="14" fill="{fill}" stroke="#A8B6C5" stroke-width="2"/>')
        self.text(x+w/2, y+58, title, 42, '#17324D', True)
        self.text(x+w/2, y+115, subtitle, 38, center=True)

    def arrow(self, x1, y1, x2, y2, label=''):
        import math
        a = math.atan2(y2-y1, x2-x1)
        points = [(x2, y2), (x2-16*math.cos(a-.45), y2-16*math.sin(a-.45)), (x2-16*math.cos(a+.45), y2-16*math.sin(a+.45))]
        self.draw.line((x1, y1, x2, y2), fill='#64748B', width=3)
        self.draw.polygon(points, fill='#64748B')
        self.svg.append(f'<path d="M{x1} {y1} L{x2} {y2}" stroke="#64748B" stroke-width="3"/><polygon points="'+' '.join(f'{x},{y}' for x,y in points)+'" fill="#64748B"/>')
        if label:
            self.text((x1+x2)/2, (y1+y2)/2-20, label, 34, center=True)

    def save(self, directory, name):
        png, svg = directory / (name+'.png'), directory / (name+'.svg')
        self.image.save(png, dpi=(240, 240))
        svg.write_text('\n'.join(self.svg+['</svg>']), encoding='utf-8')
        return png, svg


def build_diagrams(directory):
    directory.mkdir(parents=True, exist_ok=True)
    result = {}
    f = Figure('Watashiの構成と接続経路', 1040)
    f.box(60, 355, 320, 220, 'WPF Client', '利用者端末\nBearer token')
    f.box(560, 355, 360, 250, 'Central Server', '認証・認可・監査\n経路選択・SQLite', True)
    f.arrow(380, 465, 560, 465, 'HTTP(S)')
    for y, label in [(130, 'Direct'), (425, '単一Agent'), (755, 'Gateway')]:
        f.text(1000, y-28, label, 36)
    f.box(1460, 130, 280, 160, 'SMB', '共有ファイル', True)
    f.arrow(920, 390, 980, 210)
    f.arrow(980, 210, 1460, 210, 'SMB資格情報')
    f.box(1000, 425, 300, 180, 'Agent', 'SMB実行')
    f.box(1460, 425, 280, 180, 'SMB', '共有ファイル', True)
    f.arrow(920, 505, 1000, 505)
    f.arrow(1300, 505, 1460, 505, 'SMB')
    f.text(1120, 650, 'HTTP(S) ＋ mTLS / 共有秘密', 34, center=True)
    f.box(680, 755, 300, 180, 'Gateway', '一段中継')
    f.box(1100, 755, 300, 180, 'Target Agent', 'SMB実行')
    f.box(1510, 755, 230, 180, 'SMB', '隔離網', True)
    f.arrow(740, 605, 830, 755)
    f.arrow(980, 845, 1100, 845)
    f.arrow(1400, 845, 1510, 845, 'SMB')
    f.text(1010, 995, 'Server → Gateway → Target Agent は HTTP(S) ＋ 共有秘密', 34, center=True)
    f.text(65, 750, '通信区間ごとに\n資格情報と\n保護方式を設定', 38)
    result['architecture'] = f.save(directory, 'architecture')
    f = Figure('ファイル操作の共通処理', 900)
    stages = [('1  認証', 'JWT・利用者状態'), ('2  パス解決', '正規化・境界'), ('3  認可', '共有・サブパス・操作'), ('4  経路選択', 'Direct / Agent'), ('5  実行', 'SMB操作'), ('6  監査・応答', '結果を記録して返却')]
    positions = [(60,160),(660,160),(1260,160),(1260,490),(660,490),(60,490)]
    for (title, sub), (x,y) in zip(stages, positions):
        f.box(x,y,480,200,title,sub)
    for coords in [(540,260,660,260),(1140,260,1260,260),(1500,360,1500,490),(1260,590,1140,590),(660,590,540,590)]:
        f.arrow(*coords)
    f.text(60, 790, '認可後に経路を選択。拒否・障害時も実装対象の監査処理へ進む。', 38)
    f.text(60, 850, 'メンテナンス中は業務APIの受付を停止し、転送を待機として保持する。', 38)
    result['operation-flow'] = f.save(directory, 'operation-flow')
    f = Figure('Windows認証による初回パスワード設定', 1080)
    stages = [('1  GID入力','利用者が入力'),('2  Windows認証','IIS / Negotiate'),('3  本人照合','ドメイン ＋ GID'),('4  状態確認','初回設定待ち\n期限・ロック'),('5  本人が設定','ポリシー確認\nトークン発行')]
    positions = [(60,160),(660,160),(1260,160),(1260,490),(660,490)]
    for (title,sub),(x,y) in zip(stages,positions):
        f.box(x,y,480,200,title,sub)
    for coords in [(540,260,660,260),(1140,260,1260,260),(1500,360,1500,490),(1260,590,1140,590)]:
        f.arrow(*coords)
    f.arrow(1500,690,1500,820)
    f.box(60,820,1680,160,'本人確認不可・Windows認証不可・条件不成立','共通のパスワード入力へ戻る。必要時は管理者発行の一時パスワードを利用。',True)
    f.text(60,1040,'不明なID・設定済みユーザーも同じ応答へそろえ、ユーザー列挙を抑える。',38)
    result['windows-first-setup'] = f.save(directory,'windows-first-setup')
    return result
