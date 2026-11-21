using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CharliVision;

/// <summary>
/// Short-lived inertial scrolling shared by the two settings surfaces.
/// No perpetual timers and no costly re-layout beyond standard ScrollViewer work.
/// </summary>
public sealed class SmoothScrollController : IDisposable
{
    private readonly ScrollViewer _viewer;
    private readonly Func<bool> _reducedMotion;
    private readonly Stopwatch _clock=Stopwatch.StartNew();
    private double _target;
    private double _lastTime;
    private bool _active,_disposed;
    public SmoothScrollController(ScrollViewer viewer,Func<bool> reducedMotion)
    {
        _viewer=viewer;
        _reducedMotion=reducedMotion;
        _target=viewer.VerticalOffset;
        _viewer.PreviewMouseWheel+=OnWheel;
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
    private void OnWheel(object sender,MouseWheelEventArgs e)
    {
        if(_disposed || _viewer.ScrollableHeight<1 ||
           InteractiveChild(e.OriginalSource as DependencyObject,_viewer))
            return;
        // WPF wheel Delta=120 for a detent. Use pixel-based scrolling.
        double desired=-e.Delta*.70;
        _target=Math.Clamp((_active?_target:_viewer.VerticalOffset)+desired,
            0,_viewer.ScrollableHeight);
        e.Handled=true;
        if(_reducedMotion()){
            Stop();
            _viewer.ScrollToVerticalOffset(_target);
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
        if(!_active || _disposed)return;
        double now=_clock.Elapsed.TotalSeconds;
        double dt=Math.Clamp(now-_lastTime,0,.10);
        _lastTime=now;
        double delta=_target-_viewer.VerticalOffset;
        if(Math.Abs(delta)<.32 || _viewer.ScrollableHeight<.1){
            _viewer.ScrollToVerticalOffset(
                Math.Clamp(_target,0,_viewer.ScrollableHeight));
            Stop();return;
        }
        double eased=1-Math.Exp(-dt/.072);
        _viewer.ScrollToVerticalOffset(Math.Clamp(
            _viewer.VerticalOffset+delta*eased,0,_viewer.ScrollableHeight));
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
        _viewer.Unloaded-=OnUnloaded;
    }
}
