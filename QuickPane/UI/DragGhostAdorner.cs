using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

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
            var vb = new VisualBrush(source) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            _brush = vb;
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
