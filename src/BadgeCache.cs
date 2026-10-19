using System;
using System.Collections.Generic;
using System.Drawing;

namespace MintClicker
{
    // Caches the numbered point badges. Rebuilding the list used to allocate one bitmap
    // per row on every edit; after a delete or reorder most numbers still exist, so the
    // cache turns that work into dictionary lookups.
    internal sealed class BadgeCache : IDisposable
    {
        private readonly Dictionary<string, Bitmap> images = new Dictionary<string, Bitmap>(StringComparer.Ordinal);
        private readonly HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);

        internal Bitmap Get(int number, int button, int size)
        {
            string key = number + "/" + button + "/" + size;
            used.Add(key);
            Bitmap image;
            if (images.TryGetValue(key, out image)) return image;
            image = PointBadge.Image(number, button, size);
            images.Add(key, image);
            return image;
        }

        internal void BeginRefresh() { used.Clear(); }

        internal void Trim()
        {
            List<string> stale = new List<string>();
            foreach (KeyValuePair<string, Bitmap> item in images)
                if (!used.Contains(item.Key)) stale.Add(item.Key);
            foreach (string key in stale)
            {
                images[key].Dispose();
                images.Remove(key);
            }
        }

        public void Dispose()
        {
            foreach (Bitmap image in images.Values) image.Dispose();
            images.Clear();
            used.Clear();
        }
    }
}
