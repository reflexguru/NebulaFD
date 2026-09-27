using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Image = Nebula.Core.Data.Chunks.BankChunks.Images.Image;

#pragma warning disable CA1416

namespace Nebula.Tools.GameDumper
{
    /// <summary>
    /// Image.GetBitmap() converts ImageData in-place to 24-bit RGBMasked (GraphicMode 4) and
    /// caches a Bitmap. DisposeBmp() drops that cache, so the next GetBitmap re-decodes the
    /// converted buffer with mode-4 row padding. Frames that were originally 32-bit (mode 8)
    /// with odd width then come out fully transparent or scrambled — which is why some sprite
    /// sheets and padded tiles look empty or like garbage while even-width neighbours are fine.
    /// Snapshot the raw buffer around every decode and blit pixels without a GDI+ DrawImage roundtrip.
    /// </summary>
    static class MfaImageEncoder
    {
        public sealed class Decoded
        {
            public byte[] Bgra = Array.Empty<byte>();
            public int Width;
            public int Height;
        }

        public static bool TryDecode(Image img, out Decoded decoded)
        {
            decoded = new Decoded();
            if (img.Width <= 0 || img.Height <= 0 || img.ImageData == null || img.ImageData.Length == 0)
                return false;

            byte[] originalData = (byte[])img.ImageData.Clone();
            byte originalMode = img.GraphicMode;
            bool originalMasked = img.IsMasked;
            uint originalFlags = img.Flags.Value;
            try
            {
                Bitmap bmp = img.GetBitmap();
                decoded.Width = bmp.Width;
                decoded.Height = bmp.Height;
                decoded.Bgra = CopyBgra(bmp);
                return decoded.Bgra.Length == decoded.Width * decoded.Height * 4;
            }
            catch
            {
                return false;
            }
            finally
            {
                img.DisposeBmp();
                img.ImageData = originalData;
                img.GraphicMode = originalMode;
                img.IsMasked = originalMasked;
                img.Flags.Value = originalFlags;
            }
        }

        public static byte[] ToPng(Image img)
        {
            if (!TryDecode(img, out var decoded))
                return Array.Empty<byte>();
            return ToPng(decoded);
        }

        public static byte[] ToPng(Decoded decoded)
        {
            using var bmp = BitmapFromBgra(decoded.Bgra, decoded.Width, decoded.Height);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        public static bool TryLoadPng(string path, out Decoded decoded)
        {
            decoded = new Decoded();
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                using var ms = new MemoryStream(bytes);
                using var bmp = new Bitmap(ms);
                decoded.Width = bmp.Width;
                decoded.Height = bmp.Height;
                decoded.Bgra = CopyBgra(bmp);
                return decoded.Bgra.Length == decoded.Width * decoded.Height * 4;
            }
            catch
            {
                return false;
            }
        }

        public static void Blit(byte[] src, int srcW, int srcH, byte[] dst, int dstW, int dstH, int dx, int dy)
        {
            int copyW = Math.Min(srcW, dstW - dx);
            int copyH = Math.Min(srcH, dstH - dy);
            if (copyW <= 0 || copyH <= 0 || dx < 0 || dy < 0)
                return;
            int srcStride = srcW * 4;
            int dstStride = dstW * 4;
            int copyBytes = copyW * 4;
            for (int y = 0; y < copyH; y++)
                Buffer.BlockCopy(src, y * srcStride, dst, (dy + y) * dstStride + dx * 4, copyBytes);
        }

        static byte[] CopyBgra(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] bgra = new byte[w * h * 4];
                int dstStride = w * 4;
                int row = Math.Min(Math.Abs(data.Stride), dstStride);
                for (int y = 0; y < h; y++)
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bgra, y * dstStride, row);
                return bgra;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        static Bitmap BitmapFromBgra(byte[] bgra, int width, int height)
        {
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int srcStride = width * 4;
                int row = Math.Min(Math.Abs(data.Stride), srcStride);
                for (int y = 0; y < height; y++)
                    Marshal.Copy(bgra, y * srcStride, IntPtr.Add(data.Scan0, y * data.Stride), row);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }
    }
}
