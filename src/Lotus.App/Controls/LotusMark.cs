using System.Windows;
using System.Windows.Media;

namespace Lotus.Ui
{
    /// <summary>
    /// The LOTUS mark: an angular lotus flower on a dark tile. The shapes match
    /// mark in the .ico file.
    /// </summary>
    public sealed class LotusMark : FrameworkElement
    {
        public static readonly DependencyProperty ShowTileProperty = DependencyProperty.Register(
            nameof(ShowTile), typeof(bool), typeof(LotusMark),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Draws the dark tile behind the flower. Turn it off on dark backgrounds.</summary>
        public bool ShowTile
        {
            get => (bool)GetValue(ShowTileProperty);
            set => SetValue(ShowTileProperty, value);
        }

        private static readonly double[] Tile = { 4, 4, 78, 4, 96, 22, 96, 96, 4, 96 };

        private static readonly string[] Colors =
        {
            "#FF2D95", "#FF2D95", "#00F0FF", "#00F0FF", "#F5F5F5", "#FCEE0A"
        };

        private static readonly double[][] Shapes =
        {
            new double[] { 10, 52, 28, 60, 44, 77, 21, 76 }, // outer left petal
            new double[] { 90, 52, 72, 60, 56, 77, 79, 76 }, // outer right petal
            new double[] { 24, 28, 41, 44, 45, 75, 28, 60 }, // inner left petal
            new double[] { 76, 28, 59, 44, 55, 75, 72, 60 }, // inner right petal
            new double[] { 50, 14, 62, 44, 50, 77, 38, 44 }, // centre petal
            new double[] { 22, 82, 78, 82, 73, 89, 27, 89 }  // water line
        };

        protected override void OnRender(DrawingContext dc)
        {
            double size = ActualWidth < ActualHeight ? ActualWidth : ActualHeight;
            if (size <= 0) return;
            double scale = size / 100.0;
            double offsetX = (ActualWidth - size) / 2;
            double offsetY = (ActualHeight - size) / 2;

            if (ShowTile) dc.DrawGeometry(Brush("#0B0B10"), null, Polygon(Tile, scale, offsetX, offsetY));
            for (int i = 0; i < Shapes.Length; i++)
            {
                dc.DrawGeometry(Brush(Colors[i]), null, Polygon(Shapes[i], scale, offsetX, offsetY));
            }
        }

        private static Brush Brush(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static Geometry Polygon(double[] flat, double scale, double dx, double dy)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(flat[0] * scale + dx, flat[1] * scale + dy), true, true);
                for (int i = 2; i < flat.Length; i += 2)
                {
                    ctx.LineTo(new Point(flat[i] * scale + dx, flat[i + 1] * scale + dy), true, false);
                }
            }
            geometry.Freeze();
            return geometry;
        }
    }
}
