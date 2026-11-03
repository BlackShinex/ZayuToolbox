# 生成应用图标 src/Zayu.App/app.ico
# 使用与界面左上角一致的 Sunshine 角色图，放到浅色圆角玻璃底板上。
# 用法：python tools\make-icon.py
from PIL import Image, ImageDraw, ImageFilter

SIZE = 1024
LOGO_PATH = "src/Zayu.App/Assets/sunshine-logo.png"
OUT_ICO = "src/Zayu.App/app.ico"
OUT_PREVIEW = "docs/app-icon-preview.png"

# 与界面一致的浅色玻璃底：白色到淡紫
top = (255, 255, 255)
bottom = (244, 239, 255)
background = Image.new("RGB", (SIZE, SIZE))
draw = ImageDraw.Draw(background)
for y in range(SIZE):
    t = y / float(SIZE - 1)
    color = (
        int(top[0] + (bottom[0] - top[0]) * t),
        int(top[1] + (bottom[1] - top[1]) * t),
        int(top[2] + (bottom[2] - top[2]) * t),
    )
    draw.line([(0, y), (SIZE, y)], fill=color)

mask = Image.new("L", (SIZE, SIZE), 0)
mask_draw = ImageDraw.Draw(mask)
mask_draw.rounded_rectangle(
    [0, 0, SIZE - 1, SIZE - 1], radius=int(SIZE * 0.225), fill=255
)

icon = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
icon.paste(background, (0, 0), mask)

# 极淡的紫色光晕，让底板更有玻璃层次
glow = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
glow_draw = ImageDraw.Draw(glow)
glow_draw.ellipse(
    [int(SIZE * 0.35), int(SIZE * 0.42), int(SIZE * 1.1), int(SIZE * 1.1)],
    fill=(185, 170, 255, 55),
)
glow = glow.filter(ImageFilter.GaussianBlur(int(SIZE * 0.12)))
icon = Image.alpha_composite(icon, Image.composite(glow, Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0)), mask))

# 内描边，和卡片高光边框呼应
border = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
border_draw = ImageDraw.Draw(border)
border_draw.rounded_rectangle(
    [int(SIZE * 0.035), int(SIZE * 0.035), int(SIZE * 0.965), int(SIZE * 0.965)],
    radius=int(SIZE * 0.20), outline=(214, 204, 250, 130), width=max(2, int(SIZE * 0.008)),
)
icon = Image.alpha_composite(icon, border)

# 放入 Sunshine 角色，保留透明背景
logo = Image.open(LOGO_PATH).convert("RGBA")
logo_width = int(SIZE * 0.74)
logo_height = int(logo_width * logo.height / logo.width)
logo = logo.resize((logo_width, logo_height), Image.LANCZOS)
logo_x = (SIZE - logo_width) // 2
logo_y = (SIZE - logo_height) // 2 + int(SIZE * 0.015)
icon.alpha_composite(logo, (logo_x, logo_y))

icon.save(
    OUT_ICO,
    format="ICO",
    sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (128, 128), (256, 256)],
)
icon.resize((256, 256), Image.LANCZOS).save(OUT_PREVIEW)
print("saved", OUT_ICO, "and", OUT_PREVIEW)
