using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace CharliVision;

internal sealed class LyricMotion
{
    private const double FocusY = 154;
    private const double ViewportHeight = 352;
    private const int Radius = 3;
    private const int VisibleRadius = 2;
    private static readonly FontFamily LyricFont = new("Segoe UI Variable Display,Segoe UI");

    private readonly record struct Pose(double Center, double Scale, double Opacity)
    {
        public Pose At(Pose target, double movement, double fade) => new(
            Center + (target.Center - Center) * movement,
            Scale + (target.Scale - Scale) * movement,
            Opacity + (target.Opacity - Opacity) * fade);
    }

    private sealed class Row
    {
        internal readonly Grid Host = new() { VerticalAlignment = VerticalAlignment.Top };
        internal readonly TextBlock Text = new()
        {
            FontFamily = LyricFont,
            FontWeight = FontWeights.ExtraBold,
            TextWrapping = TextWrapping.Wrap
        };
        internal readonly WrapPanel Words = new();
        internal readonly ScaleTransform Scale = new();
        internal readonly TranslateTransform Offset = new();
        internal readonly DropShadowEffect Glow = new() { BlurRadius = 7, ShadowDepth = 0 };
        internal readonly KaraokeWords Karaoke;
        internal int Index = -1;
        internal bool Timed;
        internal double Height;
        internal Pose From, Target, Current;

        internal Row()
        {
            Host.Opacity = 0;
            Host.Children.Add(Text);
            Host.Children.Add(Words);
            Host.RenderTransform = new TransformGroup { Children = { Scale, Offset } };
            Karaoke = new KaraokeWords(Words);
        }

        internal void Apply(Pose pose)
        {
            if (Current == pose) return;
            Current = pose;
            Scale.ScaleX = Scale.ScaleY = pose.Scale;
            Offset.X = 12 * (1 - pose.Scale);
            Offset.Y = pose.Center - Height * pose.Scale / 2;
            Host.Opacity = pose.Opacity;
        }
    }

    private readonly Row[] _rows = new Row[2 * Radius + 1];
    private IReadOnlyList<LyricLine>? _source;
    private Row? _active;
    private double _start, _duration, _width, _size, _glow;
    private bool _highContrast, _reduceWordMotion;
    private AlbumPalette _palette = AlbumPalette.Neutral;
    private Brush _primary = Brushes.White, _muted = Brushes.LightGray;

    internal LyricMotion(Canvas surface)
    {
        for (int i = 0; i < _rows.Length; i++)
        {
            _rows[i] = new Row();
            surface.Children.Add(_rows[i].Host);
        }
    }

    internal bool HasVisibleText { get; private set; }
    internal string ActiveText => _active is { Timed: false } row ? row.Text.Text : "";
    internal int WordCount => _active is { Timed: true } row ? row.Words.Children.Count : 0;
    internal int CurrentWordIndex => _active?.Karaoke.CurrentWordIndex ?? -1;
    internal bool HasContext => _active != null &&
        ContextIsVisible(_active.Index - 1) && ContextIsVisible(_active.Index + 1);

    private bool ContextIsVisible(int index) => index >= 0 &&
        _rows[index % _rows.Length] is { } row && row.Index == index && row.Target.Opacity > 0 &&
        !string.IsNullOrWhiteSpace(row.Text.Text);

    internal bool Configure(double width, double size, bool highContrast, bool reduceWordMotion, double glow)
    {
        bool layoutChanged = _width != width || _size != size || _highContrast != highContrast;
        if (layoutChanged) Clear();
        _width = width;
        _size = size;
        _highContrast = highContrast;
        _reduceWordMotion = reduceWordMotion;
        _glow = glow;
        foreach (var row in _rows)
        {
            row.Karaoke.HighContrast = highContrast;
            row.Karaoke.ReducedMotion = reduceWordMotion;
            row.Karaoke.GlowStrength = glow;
            row.Glow.Opacity = KaraokeWords.VisualGlowOpacity(glow);
        }
        return layoutChanged;
    }

    internal void ApplyTheme(AlbumPalette palette, Brush primary, Brush muted)
    {
        _palette = palette;
        _primary = primary;
        _muted = muted;
        foreach (var row in _rows)
        {
            row.Text.Foreground = row == _active ? primary : muted;
            row.Glow.Color = palette.Accent;
            if (row.Timed && row.Index >= 0)
                row.Karaoke.ApplyTheme(palette);
        }
    }

    internal void Clear()
    {
        _source = null;
        _active = null;
        _duration = 0;
        HasVisibleText = false;
        foreach (var row in _rows)
        {
            row.Index = -1;
            row.Apply(default);
            row.Karaoke.Clear();
            row.Text.Text = "";
            row.Text.Effect = null;
            row.Timed = false;
        }
    }

    internal void Show(IReadOnlyList<LyricLine> lines, int index, double now, double duration)
    {
        Tick(now);
        bool sameSource = ReferenceEquals(_source, lines);
        if (!sameSource) Clear();
        _source = lines;
        int anchor = index;
        if (index < 0)
        {
            // Some LRCs start with a timestamped blank cue. Preview the first
            // real text without assigning that cue the active role.
            int upcoming = 0;
            while (upcoming < lines.Count && string.IsNullOrWhiteSpace(lines[upcoming].Text))
                upcoming++;
            anchor = upcoming - 1;
        }
        int first = Math.Max(0, anchor - VisibleRadius);
        int last = Math.Min(lines.Count - 1, anchor + VisibleRadius);
        var previous = _active;
        _active = index >= 0 ? Prepare(lines, index) : null;
        if (previous != _active)
            previous?.Karaoke.SetContext();

        for (int lineIndex = first; lineIndex <= last; lineIndex++)
        {
            var row = Prepare(lines, lineIndex);
            row.Text.Foreground = row == _active ? _primary : _muted;
            row.Text.Effect = row == _active && !row.Timed ? row.Glow : null;
        }

        var center = new Pose(FocusY, SizeAt(_active, 0), _active == null ? 0 : 1);
        if (_active != null)
            Target(_active, center, duration);
        for (int direction = -1; direction <= 1; direction += 2)
        {
            double edge = FocusY + direction * (_active == null ? 20 : _active.Height * center.Scale / 2);
            for (int distance = 1; distance <= VisibleRadius; distance++)
            {
                int lineIndex = anchor + direction * distance;
                if (lineIndex < first || lineIndex > last) break;
                var row = _rows[lineIndex % _rows.Length];
                double scale = SizeAt(row, distance);
                double half = row.Height * scale / 2;
                double opacity = distance switch
                {
                    1 when _highContrast => .88,
                    1 => direction < 0 ? .40 : .60,
                    2 => _highContrast ? .42 : .23,
                    _ => 0
                };
                edge += direction * (distance == 1 ? 20 : 16);
                double y = edge + direction * half;
                // Keep full letters inside the viewport. A long active lyric
                // takes precedence over context that would otherwise be clipped.
                if (y - half < 2 || y + half > ViewportHeight - 2) opacity = 0;
                Target(row, new Pose(y, scale, opacity), duration);
                edge += direction * half * 2;
            }
        }

        foreach (var row in _rows)
        {
            if (row.Index >= first && row.Index <= last) continue;
            Target(row, row.Current with { Opacity = 0 }, duration);
        }
        _start = now;
        _duration = duration;
        HasVisibleText = _active != null
            ? !string.IsNullOrWhiteSpace(_active.Text.Text)
            : anchor + 1 < lines.Count;
    }

    private Row Prepare(IReadOnlyList<LyricLine> lines, int index)
    {
        var row = _rows[index % _rows.Length];
        if (row.Index == index) return row;

        var line = lines[index];
        row.Index = index;
        row.Apply(default);
        row.From = row.Target = default;
        row.Host.Width = _width;
        row.Text.Text = line.Text;
        row.Text.FontSize = (line.Text.Length switch
        {
            <23 => 50, <39 => 43, <59 => 36, <100 => 29, _ => 23
        }) * _size;
        row.Karaoke.SizeScale = _size;
        row.Karaoke.HighContrast = _highContrast;
        row.Karaoke.ReducedMotion = _reduceWordMotion;
        row.Karaoke.GlowStrength = _glow;
        row.Timed = row.Karaoke.Setup(line);
        row.Text.Visibility = row.Timed ? Visibility.Collapsed : Visibility.Visible;
        if (row.Timed) row.Karaoke.ApplyTheme(_palette);
        row.Host.Measure(new Size(_width, double.PositiveInfinity));
        row.Height = row.Host.DesiredSize.Height;
        return row;
    }

    internal void PrepareNextEdge(IReadOnlyList<LyricLine> lines, int activeIndex)
    {
        int index = activeIndex + Radius;
        if (ReferenceEquals(_source, lines) && activeIndex >= 0 && index < lines.Count)
            Prepare(lines, index);
    }

    private static double SizeAt(Row? row, int distance)
    {
        if (row == null) return 1;
        double scale = distance switch { 0 => 1, 1 => .68, _ => .53 };
        double maximumHeight = distance switch { 0 => 142, 1 => 54, _ => 34 };
        return Math.Min(scale, maximumHeight / Math.Max(1, row.Height));
    }

    private static void Target(Row row, Pose target, double duration)
    {
        row.From = row.Current.Opacity == 0 ? target with { Opacity = 0 } : row.Current;
        row.Target = target;
        if (duration <= 0) row.Apply(target);
    }

    internal void UpdateWord(TimeSpan position) => _active?.Karaoke.Update(position);

    internal void Tick(double now)
    {
        if (_duration <= 0) return;
        double t = Math.Clamp((now - _start) / _duration, 0, 1);
        double movement = 1 - Math.Pow(1 - t, 3);
        double fade = 1 - Math.Pow(1 - Math.Min(1, t * 1.25), 2);
        foreach (var row in _rows)
            row.Apply(row.From.At(row.Target, movement, fade));
        if (t >= 1) _duration = 0;
    }
}
