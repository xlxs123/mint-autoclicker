using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace MintClicker
{
    // Template images travel inside the profile as base64 PNG, so import/export keeps
    // working without side files.
    internal static class TemplateImage
    {
        private const int MaxSide = 200;

        internal static string Encode(Bitmap bitmap)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return Convert.ToBase64String(stream.ToArray());
            }
        }

        internal static string Encode(int[] pixels, int width, int height)
        {
            using (Bitmap bitmap = FromPixels(pixels, width, height)) return Encode(bitmap);
        }

        internal static Bitmap FromPixels(int[] pixels, int width, int height)
        {
            Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try { Marshal.Copy(pixels, 0, data.Scan0, Math.Min(pixels.Length, width * height)); }
            finally { bitmap.UnlockBits(data); }
            return bitmap;
        }

        // Decodes a stored template; returns false for empty, corrupt or oversized data.
        internal static bool TryDecode(string base64, out int[] pixels, out int width, out int height)
        {
            pixels = null;
            width = height = 0;
            if (String.IsNullOrEmpty(base64)) return false;
            if (base64.Length > 60000) return false;   // about 200x200 of PNG
            try
            {
                using (MemoryStream stream = new MemoryStream(Convert.FromBase64String(base64)))
                using (Bitmap bitmap = new Bitmap(stream))
                {
                    width = bitmap.Width;
                    height = bitmap.Height;
                    if (width < 8 || height < 8 || width > MaxSide || height > MaxSide) return false;
                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        pixels = new int[width * height];
                        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                    }
                    finally { bitmap.UnlockBits(data); }
                    return true;
                }
            }
            catch (Exception) { return false; }
        }

        internal static Bitmap Decode(string base64)
        {
            using (MemoryStream stream = new MemoryStream(Convert.FromBase64String(base64)))
                return new Bitmap(stream);
        }
    }

    // Coarse-to-fine template search over ARGB pixel buffers. Pure and therefore
    // covered by the test suite with synthetic images.
    internal static class TemplateMatcher
    {
        private const int SampleGrid = 5;

        internal static bool Find(int[] screen, int screenWidth, int screenHeight, int[] template, int templateWidth, int templateHeight, int tolerance, out Point location)
        {
            location = Point.Empty;
            if (screen == null || template == null) return false;
            if (templateWidth <= 0 || templateHeight <= 0 || screenWidth < templateWidth || screenHeight < templateHeight) return false;
            if (screen.Length < screenWidth * screenHeight || template.Length < templateWidth * templateHeight) return false;

            List<int> sampleOffsets = new List<int>();
            int stepX = Math.Max(1, templateWidth / SampleGrid), stepY = Math.Max(1, templateHeight / SampleGrid);
            for (int y = 0; y < templateHeight; y += stepY)
                for (int x = 0; x < templateWidth; x += stepX)
                    sampleOffsets.Add(y * templateWidth + x);

            int maxX = screenWidth - templateWidth, maxY = screenHeight - templateHeight;
            for (int y = 0; y <= maxY; y++)
                for (int x = 0; x <= maxX; x++)
                {
                    int screenOffset = y * screenWidth + x;
                    bool candidates = true;
                    for (int i = 0; i < sampleOffsets.Count; i++)
                    {
                        int offset = sampleOffsets[i];
                        if (!PixelMatches(screen[screenOffset + (offset / templateWidth) * screenWidth + offset % templateWidth], template[offset], tolerance))
                        {
                            candidates = false;
                            break;
                        }
                    }
                    if (!candidates) continue;
                    if (!Verify(screen, screenWidth, x, y, template, templateWidth, templateHeight, tolerance)) continue;
                    location = new Point(x, y);
                    return true;
                }
            return false;
        }

        // Position of the template's centre inside the searched area.
        internal static Point? FindCenter(int[] screen, int screenWidth, int screenHeight, int[] template, int templateWidth, int templateHeight, int tolerance)
        {
            Point location;
            if (!Find(screen, screenWidth, screenHeight, template, templateWidth, templateHeight, tolerance, out location)) return null;
            return new Point(location.X + templateWidth / 2, location.Y + templateHeight / 2);
        }

        private static bool Verify(int[] screen, int screenWidth, int x, int y, int[] template, int templateWidth, int templateHeight, int tolerance)
        {
            for (int ty = 0; ty < templateHeight; ty++)
            {
                int screenRow = (y + ty) * screenWidth + x;
                int templateRow = ty * templateWidth;
                for (int tx = 0; tx < templateWidth; tx++)
                    if (!PixelMatches(screen[screenRow + tx], template[templateRow + tx], tolerance)) return false;
            }
            return true;
        }

        private static bool PixelMatches(int left, int right, int tolerance)
        {
            int dr = Math.Abs(((left >> 16) & 0xFF) - ((right >> 16) & 0xFF));
            if (dr > tolerance) return false;
            int dg = Math.Abs(((left >> 8) & 0xFF) - ((right >> 8) & 0xFF));
            if (dg > tolerance) return false;
            int db = Math.Abs((left & 0xFF) - (right & 0xFF));
            return db <= tolerance;
        }
    }
}
