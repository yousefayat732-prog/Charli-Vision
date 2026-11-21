using System;
using NAudio.Wave;
using NAudio.Dsp;

namespace CharliVision;

// Default Windows-output WASAPI loopback. It can include non-Spotify sounds.
public sealed class AudioEngine : IDisposable
{
    private const int FftSize=2048,BandCount=92;
    private static readonly Guid IeeeFloatSubtype=
        new("00000003-0000-0010-8000-00AA00389B71");
    private readonly object _sync=new();
    private readonly Complex[] _input=new Complex[FftSize];
    private readonly float[] _levels=new float[BandCount];
    private readonly float[] _raw=new float[BandCount];
    private readonly int[] _binLow=new int[BandCount],_binHigh=new int[BandCount];
    private readonly float[] _window=new float[FftSize];
    private WasapiLoopbackCapture? _capture;
    private int _position,_sampleRate=48000;
    private double _sumSquares,_autoGain=1;
    public double Sensitivity {get;set;}=1;
    public bool Running=>_capture!=null;
    public bool Available {get;private set;}
    public double LastRms {get;private set;}
    public double NormalizationGain=>_autoGain;

    public AudioEngine()
    {
        for(int i=0;i<FftSize;i++)
            _window[i]=(float)(.5-.5*Math.Cos(2*Math.PI*i/(FftSize-1)));
        UpdateBins();
    }
    private void UpdateBins()
    {
        for(int i=0;i<BandCount;i++){
            double loHz=50*Math.Pow(240,i/(double)BandCount);
            double hiHz=50*Math.Pow(240,(i+1d)/BandCount);
            int lo=Math.Clamp((int)(loHz*FftSize/_sampleRate),1,FftSize/2-1);
            int hi=Math.Clamp((int)(hiHz*FftSize/_sampleRate),lo+1,FftSize/2);
            _binLow[i]=lo;_binHigh[i]=hi;
        }
    }
    public void Start()
    {
        if(_capture!=null)return;
        try {
            var capture=new WasapiLoopbackCapture();
            _sampleRate=capture.WaveFormat.SampleRate;
            UpdateBins();
            capture.DataAvailable+=CaptureData;
            capture.RecordingStopped+=(_,_)=>Available=false;
            _capture=capture;
            capture.StartRecording();
            Available=true;
        }catch(Exception ex){SettingsStore.Log(ex);Stop();}
    }
    private void CaptureData(object? sender,WaveInEventArgs e)
    {
        var format=_capture?.WaveFormat;
        if(format is null)return;
        int bytes=format.BitsPerSample/8,frameSize=format.BlockAlign;
        if(bytes<2||frameSize<=0||format.Channels<=0)return;
        bool floating=format.Encoding==WaveFormatEncoding.IeeeFloat ||
            (format is WaveFormatExtensible extended &&
             extended.SubFormat==IeeeFloatSubtype);
        for(int offset=0;offset+frameSize<=e.BytesRecorded;offset+=frameSize) {
            float value=0;
            for(int ch=0;ch<format.Channels;ch++){
                int at=offset+ch*bytes;
                if(bytes==4){
                    value+=floating?BitConverter.ToSingle(e.Buffer,at):
                        BitConverter.ToInt32(e.Buffer,at)/2147483648f;
                }else if(bytes==2)value+=BitConverter.ToInt16(e.Buffer,at)/32768f;
                else if(bytes==3){
                    int raw=e.Buffer[at]|(e.Buffer[at+1]<<8)|(e.Buffer[at+2]<<16);
                    if((raw&0x800000)!=0)raw|=unchecked((int)0xFF000000);
                    value+=raw/8388608f;
                }
            }
            value/=format.Channels;
            if(!float.IsFinite(value))value=0;
            _sumSquares+=value*value;
            _input[_position].X=value*_window[_position];
            _input[_position].Y=0;
            if(++_position==FftSize){
                _position=0;
                double rms=Math.Sqrt(_sumSquares/FftSize);
                _sumSquares=0;
                LastRms=rms;
                // Don't amplify digitization noise during genuine silence.
                if(rms<.00015){
                    lock(_sync)Array.Clear(_levels,0,_levels.Length);
                    continue;
                }
                FastFourierTransform.FFT(true,11,_input);
                double peak=0;
                for(int i=0;i<BandCount;i++){
                    double maximum=0;
                    for(int bin=_binLow[i];bin<_binHigh[i];bin++){
                        var real=_input[bin].X;
                        var imaginary=_input[bin].Y;
                        double magnitude=Math.Sqrt(real*real+imaginary*imaginary);
                        if(magnitude>maximum)maximum=magnitude;
                    }
                    // Compressed amplitude lets quiet tracks retain a visible spectrum.
                    float band=(float)Math.Sqrt(maximum/(FftSize/2d))*3f;
                    _raw[i]=band;
                    peak=Math.Max(peak,band);
                }
                // Adaptive gain bounded to avoid visual clipping and runaway in silence.
                double desired=Math.Clamp(.46/Math.Max(peak,.005),1,55);
                _autoGain+=(desired-_autoGain)*(desired<_autoGain?.24:.14);
                lock(_sync){
                    for(int i=0;i<BandCount;i++){
                        double gated=_raw[i]<Math.Max(.0005,peak*.018)?0:_raw[i];
                        double normalized=Math.Clamp(gated*_autoGain*
                            Math.Clamp(Sensitivity,.5,2.5),0,1);
                        _levels[i]=(float)Math.Pow(normalized,.85);
                    }
                }
            }
        }
    }
    public float[] Snapshot()
    {
        lock(_sync)return (float[])_levels.Clone();
    }
    // Reuse caller-owned storage at compositor refresh rate: no array
    // allocation on the WPF UI frame path.
    public void SnapshotInto(float[] destination)
    {
        if(destination is null||destination.Length!=BandCount)
            throw new ArgumentException("Expected 92 bands",nameof(destination));
        lock(_sync)Array.Copy(_levels,destination,BandCount);
    }
    public void Stop()
    {
        var capture=_capture;_capture=null;
        if(capture!=null) {
            try{capture.StopRecording();}catch{}
            try{capture.Dispose();}catch{}
        }
        Available=false;LastRms=0;_autoGain=1;
        _position=0;_sumSquares=0;
        lock(_sync)Array.Clear(_levels,0,_levels.Length);
    }
    public void Dispose()=>Stop();
}