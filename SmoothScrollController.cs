using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CharliVision;

/// <summary>
/// V4: interruptible pixel-based wheel scrolling; native scrollbar, touchpad,
/// keyboard and touch manipulation retain their own WPF input paths.
/// One short-lived compositor subscription, never a repeated layout timer.
/// </summary>
public sealed class SmoothScrollController : IDisposable
{
    private readonly ScrollViewer _viewer;
    private readonly Func<bool> _reducedMotion;
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private double _target,_lastTime,_lastIssuedOffset;
    private bool _active,_disposed,_internalScroll;

    public SmoothScrollController(ScrollViewer viewer,Func<bool> reducedMotion)
    {
        _viewer=viewer;
        _reducedMotion=reducedMotion;
        _target=viewer.VerticalOffset;
        _viewer.PreviewMouseWheel+=OnWheel;
        _viewer.ScrollChanged+=OnScrollChanged;
        _viewer.Unloaded+=OnUnloaded;
    }
    private static bool InteractiveChild(DependencyObject? current,
        DependencyObject limit)
    {
        while(current is not null && current!=limit){
            if(current is Slider or ScrollBar or ComboBox or TextBoxBase or ListBox)
                return true;
            current=current is Visual?VisualTreeHelper.GetParent(current):null;
        }
        return false;
    }
    private void SetOffset(double pixels)
    {
        // WPF often raises ScrollChanged AFTER ScrollToVerticalOffset returns.
        // Remember our intended offset across the deferred layout callback.
        _lastIssuedOffset=Math.Clamp(pixels,
            0,Math.Max(0,_viewer.ScrollableHeight));
        _internalScroll=true;
        try{_viewer.ScrollToVerticalOffset(_lastIssuedOffset);}
        finally{_internalScroll=false;}
    }
    private void OnScrollChanged(object sender,ScrollChangedEventArgs e)
    {
        if(_disposed)return;
        // Native scrollbar drag, PageUp/PageDown, touchpad manipulation and
        // keyboard input must override an unfinished wheel animation.
        if(!_internalScroll && Math.Abs(e.VerticalChange)>.001){
            bool ourDeferredScroll=_active &&
                Math.Abs(_viewer.VerticalOffset-_lastIssuedOffset)<1.5;
            if(!ourDeferredScroll){
                Stop();
                _target=_viewer.VerticalOffset;
            }
        }
        _target=Math.Clamp(_target,0,Math.Max(0,_viewer.ScrollableHeight));
    }
    private void OnWheel(object sender,MouseWheelEventArgs e)
    {
        if(_disposed||_viewer.ScrollableHeight<.5||e.Delta==0 ||
           InteractiveChild(e.OriginalSource as DependencyObject,_viewer))
            return;
        double current=_active?_target:_viewer.VerticalOffset;
        double requested=Math.Clamp(current-e.Delta*.72,0,_viewer.ScrollableHeight);
        e.Handled=true;
        _target=requested;
        if(_reducedMotion()){
            Stop();
            SetOffset(requested);
            return;
        }
        if(!_active){
            _active=true;
            _lastTime=_clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering+=OnRender;
        }
    }
    private void OnRender(object? sender,EventArgs e)
    {
        if(!_active||_disposed)return;
        if(!_viewer.IsLoaded){Stop();return;}
        double now=_clock.Elapsed.TotalSeconds;
        double dt=Math.Clamp(now-_lastTime,0,.080);
        _lastTime=now;
        double target=Math.Clamp(_target,0,Math.Max(0,_viewer.ScrollableHeight));
        double delta=target-_viewer.VerticalOffset;
        if(Math.Abs(delta)<.35||_viewer.ScrollableHeight<.5){
            SetOffset(target);Stop();return;
        }
        // 95% within ~195ms; re-target immediately on new wheel input.
        double eased=1-Math.Exp(-dt/.065);
        SetOffset(_viewer.VerticalOffset+delta*eased);
    }
    public void ResetToTop()
    {
        if(_disposed)return;
        Stop();
        _target=0;
        SetOffset(0);
    }
    private void OnUnloaded(object sender,RoutedEventArgs e)=>Stop();
    private void Stop()
    {
        if(!_active)return;
        _active=false;
        CompositionTarget.Rendering-=OnRender;
    }
    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        Stop();
        _viewer.PreviewMouseWheel-=OnWheel;
        _viewer.ScrollChanged-=OnScrollChanged;
        _viewer.Unloaded-=OnUnloaded;
    }
}
