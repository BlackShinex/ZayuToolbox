using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;

namespace IpInfo.App
{
    /// <summary>
    /// 离线二维码渲染：QRCoder 只负责生成模块矩阵，WPF 负责绘制像素。
    /// 生成的是 Pbgra32 位图，可直接赋给 Image.Source。
    /// </summary>
    public static class QrCodeRenderer
    {
        /// <summary>
        /// 把文本渲染为二维码位图（白底黑块，含静区）。
        /// </summary>
        /// <param name="text">二维码内容，不能为空。</param>
        /// <param name="moduleSize">每个二维码模块的像素边长，必须大于 0。</param>
        /// <returns>已冻结、可跨线程使用的位图。</returns>
        public static BitmapSource Create(string text, int moduleSize)
        {
            if (string.IsNullOrEmpty(text)) throw new ArgumentException("二维码内容不能为空。", "text");
            if (moduleSize <= 0) throw new ArgumentOutOfRangeException("moduleSize", "模块尺寸必须大于 0。");

            using (QRCodeData data = QRCodeGenerator.GenerateQrCode(text, QRCodeGenerator.ECCLevel.M))
            {
                List<BitArray> matrix = data.ModuleMatrix;
                int modules = matrix.Count;
                int pixelSize = modules * moduleSize;
                int stride = pixelSize * 4;
                byte[] pixels = new byte[stride * pixelSize];

                // 先铺满白色不透明背景，再把黑色模块写进去。
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = 0xFF;
                    pixels[i + 1] = 0xFF;
                    pixels[i + 2] = 0xFF;
                    pixels[i + 3] = 0xFF;
                }

                for (int row = 0; row < modules; row++)
                {
                    BitArray rowBits = matrix[row];
                    for (int column = 0; column < modules; column++)
                    {
                        if (!rowBits[column]) continue;
                        for (int y = 0; y < moduleSize; y++)
                        {
                            int offset = (row * moduleSize + y) * stride + column * moduleSize * 4;
                            for (int x = 0; x < moduleSize; x++)
                            {
                                int index = offset + x * 4;
                                pixels[index] = 0x00;
                                pixels[index + 1] = 0x00;
                                pixels[index + 2] = 0x00;
                                pixels[index + 3] = 0xFF;
                            }
                        }
                    }
                }

                BitmapSource source = BitmapSource.Create(pixelSize, pixelSize, 96, 96,
                    PixelFormats.Pbgra32, null, pixels, stride);
                source.Freeze();
                return source;
            }
        }
    }
}
