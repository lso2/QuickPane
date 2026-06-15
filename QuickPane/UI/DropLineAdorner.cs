using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace QuickPane.UI
{
    /// <summary>A thin horizontal line drawn over a panel to show where a dragged item will drop.</summary>
    internal sealed class DropLineAdorner : Adorner
    {
        private double _y;
        private bool _visible;
        private readonly Pen _pen;

        public DropLineAdorner(UIElement adorned) : base(adorned)
        {
            IsHitTestVisible = false;
            var color = (Application.Current != null
                ? Application.Current.TryFindResource("AccentBrush") as SolidColorBrush
                : null) ?? new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
            _pen = new Pen(new SolidColorBrush(color.Color), 2);
            _pen.Freeze();
        }

        public void Update(double y)
        {
            _y = y;
            _visible = true;
            InvalidateVisual();
        }

        public void Hide()
        {
            _visible = false;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (!_visible) return;
            double w = ((FrameworkElement)AdornedElement).ActualWidth;
            dc.DrawLine(_pen, new Point(6, _y), new Point(w - 6, _y));
        }
    }
}
