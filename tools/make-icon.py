# 生成应用图标 src/IpInfo.App/app.ico（渐变圆角方块 + 白色 IP 字母）
# 用法：python tools\make-icon.py
from PIL import Image, ImageDraw, ImageFont

SIZE = 1024
OUT_ICO = "src/IpInfo.App/app.ico"
OUT_PREVIEW = "docs/app-icon-preview.png"

# 渐变底色（靛蓝 → 紫色），与界面主色一致
top = (79, 70, 229)
bottom = (168, 85, 247)
gradient = Image.new("RGB", (SIZE, SIZE))
draw = ImageDraw.Draw(gradient)
for y in range(SIZE):
    t = y / float(SIZE - 1)
    color = (
        int(top[0] + (bottom[0] - top[0]) * t),
        int(top[1] + (bottom[1] - top[1]) * t),
        int(top[2] + (bottom[2] - top[2]) * t),
    )
    draw.line([(0, y), (SIZE, y)], fill=color)

# 圆角遮罩
mask = Image.new("L", (SIZE, SIZE), 0)
mask_draw = ImageDraw.Draw(mask)
mask_draw.rounded_rectangle(
    [0, 0, SIZE - 1, SIZE - 1], radius=int(SIZE * 0.225), fill=255
)

icon = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
icon.paste(gradient, (0, 0), mask)

# 顶部柔和高光
highlight = Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0))
highlight_draw = ImageDraw.Draw(highlight)
highlight_draw.ellipse(
    [-int(SIZE * 0.25), -int(SIZE * 0.55), int(SIZE * 1.25), int(SIZE * 0.42)],
    fill=(255, 255, 255, 34),
)
icon = Image.alpha_composite(icon, Image.composite(highlight, Image.new("RGBA", (SIZE, SIZE), (0, 0, 0, 0)), mask))

# 白色 IP 字样
font = ImageFont.truetype(r"C:\Windows\Fonts\segoeuib.ttf", int(SIZE * 0.46))
text = "IP"
text_draw = ImageDraw.Draw(icon)
bbox = text_draw.textbbox((0, 0), text, font=font)
x = (SIZE - (bbox[2] - bbox[0])) / 2 - bbox[0]
y = (SIZE - (bbox[3] - bbox[1])) / 2 - bbox[1] - int(SIZE * 0.015)
text_draw.text((x, y), text, font=font, fill=(255, 255, 255, 255))

icon.save(
    OUT_ICO,
    format="ICO",
    sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)],
)
icon.resize((256, 256), Image.LANCZOS).save(OUT_PREVIEW)
print("saved", OUT_ICO, "and", OUT_PREVIEW)
