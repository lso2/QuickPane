using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace QuickPane.UI
{
    /// <summary>A translucent snapshot of the dragged element that follows the cursor, so reordering
    /// feels like picking the item up and moving it rather than nudging a handle.</summary>
    internal sealed class DragGhostAdorner : Adorner
    {
        private readonly Brush _brush;
        private readonly Size _size;
        private Point _pos;
        private bool _visible;

        public DragGhostAdorner(UIElement adorned, Visual source, Size size) : base(adorned)
        {
            IsHitTestVisible = false;
            _size = size;
            _brush = Snapshot(source, size) ??
                     new VisualBrush(source) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        }

        /// <summary>
        /// A picture taken once, rather than a brush that keeps reading the element. The list hides the
        /// row it is dragging so a spacer can travel in its place, and a brush painting from a hidden
        /// element paints nothing, which leaves the cursor carrying an empty rectangle.
        /// </summary>
        private static Brush Snapshot(Visual source, Size size)
        {
            try
            {
                if (size.Width <= 0 || size.Height <= 0) return null;
                var dpi = VisualTreeHelper.GetDpi(source);
                int w = (int)System.Math.Ceiling(size.Width * dpi.DpiScaleX);
                int h = (int)System.Math.Ceiling(size.Height * dpi.DpiScaleY);
                if (w <= 0 || h <= 0) return null;

                var frame = new DrawingVisual();
                using (var dc = frame.RenderOpen())
                {
                    dc.DrawRectangle(
                        new VisualBrush(source) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                        null, new Rect(size));
                }

                var bmp = new RenderTargetBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                bmp.Render(frame);
                bmp.Freeze();
                return new ImageBrush(bmp) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            }
            catch { return null; }
        }

        public void SetPosition(Point p)
        {
            _pos = p;
            _visible = true;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (!_visible) return;
            dc.PushOpacity(0.72);
            dc.DrawRectangle(_brush, null, new Rect(_pos, _size));
            dc.Pop();
        }
    }
}
