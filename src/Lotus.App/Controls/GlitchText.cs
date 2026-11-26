using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Lotus.Ui
{
    /// <summary>
    /// Text with a cyan and a pink ghost behind it that twitch now and then, like
    /// a bad video signal. Used for the big headings.
    /// </summary>
    public sealed class GlitchText : Grid
    {
        private static readonly Random Random = new Random();

        /// <summary>Set to false to keep every GlitchText still, for screenshots.</summary>
        public static bool AnimationsEnabled = true;

        private readonly TextBlock cyan = new TextBlock();
        private readonly TextBlock pink = new TextBlock();
        private readonly TextBlock main = new TextBlock();
        private readonly TranslateTransform cyanShift = new TranslateTransform(-2, 0);
        private readonly TranslateTransform pinkShift = new TranslateTransform(2, 1);
        private readonly TranslateTransform mainShift = new TranslateTransform(0, 0);
        private DispatcherTimer timer;
        private int burst;

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(GlitchText), new PropertyMetadata("", OnAppearanceChanged));

        public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
            nameof(FontSize), typeof(double), typeof(GlitchText), new PropertyMetadata(48.0, OnAppearanceChanged));

        public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
            nameof(Foreground), typeof(Brush), typeof(GlitchText),
            new PropertyMetadata(Brushes.White, OnAppearanceChanged));

        public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
            nameof(FontFamily), typeof(FontFamily), typeof(GlitchText),
            new PropertyMetadata(new FontFamily("Bahnschrift, Impact, Arial Narrow"), OnAppearanceChanged));

        public static readonly DependencyProperty GhostsProperty = DependencyProperty.Register(
            nameof(Ghosts), typeof(bool), typeof(GlitchText), new PropertyMetadata(true, OnAppearanceChanged));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public double FontSize
        {
            get => (double)GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public Brush Foreground
        {
            get => (Brush)GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        public FontFamily FontFamily
        {
            get => (FontFamily)GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        /// <summary>Shows the coloured ghosts. Turn off for a plain heading that only twitches.</summary>
        public bool Ghosts
        {
            get => (bool)GetValue(GhostsProperty);
            set => SetValue(GhostsProperty, value);
        }

        public GlitchText()
        {
            cyan.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xF0, 0xFF));
            pink.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x2D, 0x95));
            cyan.RenderTransform = cyanShift;
            pink.RenderTransform = pinkShift;
            main.RenderTransform = mainShift;
            foreach (TextBlock block in new[] { pink, cyan, main })
            {
                block.FontWeight = FontWeights.Bold;
                block.FontStretch = FontStretches.Condensed;
                block.IsHitTestVisible = false;
                Children.Add(block);
            }
            Apply();
            Loaded += (s, e) => Start();
            Unloaded += (s, e) => Stop();
        }

        private static void OnAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((GlitchText)d).Apply();
        }

        private void Apply()
        {
            foreach (TextBlock block in new[] { pink, cyan, main })
            {
                block.Text = Text;
                block.FontSize = FontSize;
                block.FontFamily = FontFamily;
            }
            main.Foreground = Foreground;
            cyan.Visibility = pink.Visibility = Ghosts ? Visibility.Visible : Visibility.Hidden;
        }

        private void Start()
        {
            if (!AnimationsEnabled || timer != null) return;
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = NextPause() };
            timer.Tick += OnTick;
            timer.Start();
        }

        private void Stop()
        {
            if (timer == null) return;
            timer.Stop();
            timer.Tick -= OnTick;
            timer = null;
            Rest();
        }

        private static TimeSpan NextPause()
        {
            return TimeSpan.FromMilliseconds(1800 + Random.Next(4200));
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (burst == 0) burst = 2 + Random.Next(3);
            burst--;
            if (burst > 0)
            {
                // Mid-twitch: throw the layers apart for one frame.
                cyanShift.X = -2 - Random.Next(7);
                cyanShift.Y = Random.Next(-2, 3);
                pinkShift.X = 2 + Random.Next(7);
                pinkShift.Y = Random.Next(-2, 3);
                mainShift.X = Random.Next(-3, 4);
                timer.Interval = TimeSpan.FromMilliseconds(45 + Random.Next(50));
            }
            else
            {
                Rest();
                timer.Interval = NextPause();
            }
        }

        private void Rest()
        {
            cyanShift.X = -2;
            cyanShift.Y = 0;
            pinkShift.X = 2;
            pinkShift.Y = 1;
            mainShift.X = 0;
        }
    }
}
