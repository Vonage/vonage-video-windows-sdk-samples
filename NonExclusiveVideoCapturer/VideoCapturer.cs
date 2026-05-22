using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Graphics.Imaging;
using System.Diagnostics;
using OpenTok;

namespace NonExclusiveVideoCapturer
{
    public enum CameraLocation
    {
        Unknown,
        Front,
        Back,
        Top,
        Bottom,
        Left,
        Right
    }

    public class VideoCapturerDevice
    {
        public string Id { get; }
        public string Name { get; }

        internal VideoCapturerDevice(string id, string name)
        {
            Id = id;
            Name = name;
        }
    }

    public class FrameSource
    {
        public string Id { get; }
        public IReadOnlyList<FrameFormat> Formats { get; }

        internal FrameSource(string id, IReadOnlyList<FrameFormat> formats)
        {
            Id = id;
            Formats = formats;
        }
    }

    public class FrameFormat
    {
        public ushort Width { get; }
        public ushort Height { get; }
        public float FrameRate { get; }
        public PixelFormat PixelFormat { get; }
        internal long Quantifier { get; }

        public FrameFormat(ushort width, ushort height, float frameRate, PixelFormat pixelFormat = PixelFormat.FormatYuv420p)
        {
            Width = width;
            Height = height;
            FrameRate = frameRate;
            PixelFormat = pixelFormat;
            Quantifier = ((long)Width) << 48 | ((long)Height) << 32 | ((long)FrameRate) << 16 | ((long)PixelFormat);
        }
    }

    public class VideoCapturer : IVideoCapturer
    {
        public enum EventType
        {
            InternalError,
            NoDevices,
            NoFrameSources,
            InvalidDevice,
            DeviceError,
            ExclusiveControlNotAvailable,
            ExclusiveControlAvailable,
            InvalidFrameSource,
        }

        public class EventArgs : System.EventArgs
        {
            public EventType Type { get; }

            public EventArgs(EventType type)
            {
                Type = type;
            }
        }

        public static async Task<IReadOnlyList<VideoCapturerDevice>> GetDevicesAsync(CameraLocation? location = null)
        {
            DeviceInformationCollection deviceInformationCollection = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture).AsTask().ConfigureAwait(false);

            List<VideoCapturerDevice> devices = [];
            if (deviceInformationCollection != null)
            {
                foreach (DeviceInformation deviceInformation in deviceInformationCollection)
                {
                    if (!deviceInformation.IsEnabled)
                        continue;
                    if (location.HasValue && deviceInformation.EnclosureLocation != null && CameraLocationToPanel(location.Value) != deviceInformation.EnclosureLocation.Panel)
                        continue;
                    devices.Add(new VideoCapturerDevice(deviceInformation.Id, deviceInformation.Name));
                }
            }
            return devices;
        }

        public VideoCapturer(IDispatcher dispatcher)
        {
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public event EventHandler<EventArgs>? Event;

        public string? DeviceId
        {
            get => deviceId;
            set
            {
                if (deviceId == value) return;
                deviceId = value;
                ScheduleReset();
            }
        }
        public bool ExclusiveControl
        {
            get => exclusiveControl;
            set
            {
                if (exclusiveControl == value) return;
                exclusiveControl = value;
                ScheduleReset();
            }
        }
        public string? FrameSourceId
        {
            get => frameSourceId;
            set
            {
                if (frameSourceId == value) return;
                frameSourceId = value;
                ScheduleReset();
            }
        }
        public FrameFormat? FrameFormat
        {
            get => frameFormat;
            set
            {
                if (frameFormat == value) return;
                frameFormat = value;
                ScheduleReset();
            }
        }

        public IReadOnlyList<FrameSource>? FrameSources { get; private set; }

        private const uint ErrorCodeHardwareResources = 0xC00D3704;
        private const uint ErrorCodeHardwareInUse = 0x80070020;
        private static readonly FrameFormat DefaultFrameFormat = new FrameFormat(1280, 720, 30, PixelFormat.FormatYuv420p);
        private readonly IDispatcher dispatcher;
        private readonly object mutex = new();
        private string? deviceId;
        private bool exclusiveControl = true;
        private string? frameSourceId;
        private FrameFormat? frameFormat;
        private IVideoFrameConsumer? videoFrameConsumer;
        private MediaCapture? mediaCapture;
        private MediaFrameSource? mediaFrameSource;
        private MediaFrameReader? mediaFrameReader;
        private VideoCaptureSettings? videoCaptureSettings;
        private byte[]? imageBytes;
        private GCHandle pinnedImageBytes;
        private int resetScheduled;
        private int isStarted;
        private VideoContentHint videoContentHint = VideoContentHint.MOTION;

        private void DispatchEvent(EventType eventType)
        {
            EventHandler<EventArgs>? eventHandler = Event;
            if (eventHandler == null) return;
            dispatcher.DispatchEvent(this, eventHandler, new EventArgs(eventType));
        }

        private void OnErrorEvent(EventType eventType)
        {
            InternalStop();
            DispatchEvent(eventType);
        }

        private void ScheduleReset()
        {
            if (Interlocked.Exchange(ref resetScheduled, 1) == 1) return;
            _ = Task.Run(() =>
            {
                lock (mutex)
                {
                    Interlocked.Exchange(ref resetScheduled, 0);
                    if (Volatile.Read(ref isStarted) == 0) return;

                    InternalStop();

                    /* Make sure there's at least one valid video device */
                    IReadOnlyList<VideoCapturerDevice> devices = GetDevicesAsync().Result;
                    if (devices.Count == 0)
                    {
                        OnErrorEvent(EventType.NoDevices);
                        return;
                    }

                    /* If no device is selected or the selected device does not exist, use the first one in the list */
                    string? usedDeviceId = null;
                    foreach (VideoCapturerDevice device in devices)
                    {
                        if (usedDeviceId == null || deviceId == device.Id)
                            usedDeviceId = device.Id;
                    }

                    /* Initialize a new media capture with selected settings */
                    mediaCapture = new MediaCapture();
                    MediaCaptureInitializationSettings settings = new MediaCaptureInitializationSettings()
                    {
                        VideoDeviceId = usedDeviceId,
                        StreamingCaptureMode = StreamingCaptureMode.Video,
                        SharingMode = exclusiveControl ? MediaCaptureSharingMode.ExclusiveControl : MediaCaptureSharingMode.SharedReadOnly,
                        MemoryPreference = MediaCaptureMemoryPreference.Cpu, // If Auto, Frames can arrive as SoftwareBitmap or as IDirect3DSurface. TODO: study if hardware support is an option
                    };

                    mediaCapture.Failed += (s, e) =>
                    {
                        /* If exclusive control was requested and device is already in use, temporarily initialize the device in ReadOnly mode */
                        if (exclusiveControl && e.Code == ErrorCodeHardwareResources)
                            OnErrorEvent(EventType.ExclusiveControlNotAvailable);
                        else if (e.Code == ErrorCodeHardwareInUse)
                            OnErrorEvent(EventType.DeviceError);
                        else
                            OnErrorEvent(EventType.InternalError);
                    };

                    mediaCapture.InitializeAsync(settings).Wait();

                    /* If exclusive control could not be granted, listen for device changes until the device exclusive control becomes available */
                    if (!exclusiveControl)
                    {
                        mediaCapture.CaptureDeviceExclusiveControlStatusChanged += (s, e) =>
                        {
                            if (e.Status == MediaCaptureDeviceExclusiveControlStatus.ExclusiveControlAvailable)
                                DispatchEvent(EventType.ExclusiveControlAvailable);
                        };
                    }

                    /* Make sure there's at least one valid frame source */
                    if (mediaCapture.FrameSources == null || mediaCapture.FrameSources.Count == 0)
                    {
                        OnErrorEvent(EventType.NoFrameSources);
                        return;
                    }

                    /* List Frame Sources for initialized device */
                    List<FrameSource> frameSources = [];
                    foreach (KeyValuePair<string, MediaFrameSource> mediaframeSource in mediaCapture.FrameSources)
                    {
                        List<FrameFormat> frameFormats = [];
                        foreach (MediaFrameFormat supportedFormat in mediaframeSource.Value.SupportedFormats)
                            if (supportedFormat.MajorType.Equals("Video", StringComparison.InvariantCultureIgnoreCase))
                                frameFormats.Add(ConvertFrameFormat(supportedFormat));
                        frameSources.Add(new FrameSource(mediaframeSource.Key, frameFormats));
                    }
                    FrameSources = frameSources;

                    /* If no frame source is selected or the selected frame source does not exist, use the first one in the list */
                    foreach (KeyValuePair<string, MediaFrameSource> availableMediaFrameSource in mediaCapture.FrameSources)
                    {
                        if (mediaFrameSource == null || frameSourceId == availableMediaFrameSource.Key)
                            mediaFrameSource = availableMediaFrameSource.Value;
                    }
                    Debug.Assert(mediaFrameSource != null);

                    /* If exclusive control is granted, select the closest format possible to the requested one (or ther default one if none is selected) */
                    if (exclusiveControl)
                    {
                        /* Make sure there's at least one valid frame format */
                        if (mediaFrameSource.SupportedFormats == null || mediaFrameSource.SupportedFormats.Count == 0)
                        {
                            OnErrorEvent(EventType.InvalidFrameSource);
                            return;
                        }

                        frameFormat ??= DefaultFrameFormat;

                        long quantifierDistance = long.MaxValue;
                        MediaFrameFormat? selectedMediaFrameFormat = null;
                        foreach (MediaFrameFormat mediaFrameFormat in mediaFrameSource.SupportedFormats)
                        {
                            long newQuantiferDistance = Math.Abs(ConvertFrameFormat(mediaFrameFormat).Quantifier - frameFormat.Quantifier);
                            if (quantifierDistance < newQuantiferDistance) continue;
                            quantifierDistance = newQuantiferDistance;
                            selectedMediaFrameFormat = mediaFrameFormat;
                        }

                        Debug.Assert(selectedMediaFrameFormat != null);
                        mediaFrameSource.SetFormatAsync(selectedMediaFrameFormat).Wait();
                    }

                    Debug.Assert(mediaFrameSource.CurrentFormat != null);
                    frameFormat = ConvertFrameFormat(mediaFrameSource.CurrentFormat);

                    if (pinnedImageBytes.IsAllocated)
                        pinnedImageBytes.Free();
                    imageBytes = new byte[4 * frameFormat.Width * frameFormat.Height];
                    pinnedImageBytes = GCHandle.Alloc(imageBytes, GCHandleType.Pinned);

                    /* Get current format settings */
                    videoCaptureSettings = new VideoCaptureSettings()
                    {
                        PixelFormat = StringToPixelFormat(mediaFrameSource.CurrentFormat.Subtype),
                        Fps = (int)(mediaFrameSource.CurrentFormat.FrameRate.Numerator / mediaFrameSource.CurrentFormat.FrameRate.Denominator),
                        Height = (int)mediaFrameSource.CurrentFormat.VideoFormat.Height,
                        Width = (int)mediaFrameSource.CurrentFormat.VideoFormat.Width,
                        MirrorOnLocalRender = true // TODO: Use location
                    };

                    /* Create the frame reader */
                    mediaFrameReader = mediaCapture.CreateFrameReaderAsync(mediaFrameSource).AsTask().Result;
                    mediaFrameReader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;

                    /* Add the frame processing callback */
                    mediaFrameReader.FrameArrived += (r, e) =>
                    {
                        MediaFrameReference? mediaFrameReference = null;
                        SoftwareBitmap? softwareBitmap = null;
                        VideoFrame? videoFrame = null;

                        try
                        {
                            mediaFrameReference = r.TryAcquireLatestFrame();
                            softwareBitmap = mediaFrameReference?.VideoMediaFrame?.SoftwareBitmap;
                            if (softwareBitmap == null)
                                return;

                            PixelFormat pixelFormat = TranslatePixelFormat(softwareBitmap.BitmapPixelFormat);
                            if (pixelFormat == PixelFormat.Unknown)
                            {
                                SoftwareBitmap transformedSoftwareBitmap = SoftwareBitmap.Convert(softwareBitmap, BitmapPixelFormat.Rgba8);
                                softwareBitmap.Dispose();
                                softwareBitmap = transformedSoftwareBitmap;
                                pixelFormat = PixelFormat.FormatRgba32;
                            }

                            softwareBitmap.CopyToBuffer(imageBytes.AsBuffer());

                            /* This method calls otc_video_frame_new which makes a copy of the underlying buffer so ImageBytes is free for use in the next iteration. */
                            videoFrame = VideoFrame.CreateFrameFromBuffer(pixelFormat, softwareBitmap.PixelWidth, softwareBitmap.PixelHeight, pinnedImageBytes.AddrOfPinnedObject());
                            videoFrameConsumer?.Consume(videoFrame, 0);
                        }
                        catch (Exception ex)
                        {
                            /* TODO: Log Warning and maybe count number of failed frames to re-open camera at some point  */
                        }
                        finally
                        {
                            videoFrame?.Dispose();
                            softwareBitmap?.Dispose();
                            mediaFrameReference?.Dispose();
                        }
                    };

                    /* Start capturing frames */
                    MediaFrameReaderStartStatus status = mediaFrameReader.StartAsync().AsTask().Result;
                    if (status == MediaFrameReaderStartStatus.ExclusiveControlNotAvailable)
                        OnErrorEvent(EventType.ExclusiveControlNotAvailable);
                    else if (status != MediaFrameReaderStartStatus.Success)
                        OnErrorEvent(EventType.InternalError);
                }
            });
        }

        private void InternalStop()
        {
            lock (mutex)
            {
                if (mediaFrameReader != null)
                {
                    mediaFrameReader.StopAsync().Wait();
                    mediaFrameReader.Dispose();
                    mediaFrameReader = null;
                }

                if (pinnedImageBytes.IsAllocated)
                    pinnedImageBytes.Free();
                imageBytes = null;

                videoCaptureSettings = null;
                mediaFrameSource = null;

                if (mediaCapture != null)
                {
                    mediaCapture.Dispose();
                    mediaCapture = null;
                }

                FrameSources = null;
            }
        }

        void IVideoCapturer.Init(IVideoFrameConsumer frameConsumer)
        {
            videoFrameConsumer = frameConsumer;
            videoFrameConsumer.SetVideoContentHint(videoContentHint);
        }

        void IVideoCapturer.SetVideoContentHint(VideoContentHint contentHint)
        {
            if (videoContentHint == contentHint) return;
            videoContentHint = contentHint;
            if (videoFrameConsumer != null)
                videoFrameConsumer.SetVideoContentHint(videoContentHint);
        }
        VideoContentHint IVideoCapturer.GetVideoContentHint() { return videoContentHint; }

        void IVideoCapturer.Start()
        {
            if (Interlocked.Exchange(ref isStarted, 1) == 1) return;
            ScheduleReset();
        }

        void IVideoCapturer.Stop()
        {
            if (Interlocked.Exchange(ref isStarted, 0) == 0) return;
            InternalStop();
        }

        void IVideoCapturer.Destroy()
        {
            ((IVideoCapturer)this).Stop();
        }

        VideoCaptureSettings IVideoCapturer.GetCaptureSettings() => videoCaptureSettings ?? new VideoCaptureSettings();

        ~VideoCapturer()
        {
            ((IVideoCapturer)this).Stop();
        }

        private static PixelFormat StringToPixelFormat(string pixelFormat)
        {
            if (string.IsNullOrWhiteSpace(pixelFormat))
            {
                return PixelFormat.Unknown;
            }
            else if (pixelFormat.Equals("YUV420p", StringComparison.InvariantCultureIgnoreCase))
            {
                return PixelFormat.FormatYuv420p;
            }
            else if (pixelFormat.Equals("NV12", StringComparison.InvariantCultureIgnoreCase))
            {
                return PixelFormat.FormatNv12;
            }
            else if (pixelFormat.Equals("NV21", StringComparison.InvariantCultureIgnoreCase))
            {
                return PixelFormat.FormatNv21;
            }
            else if (pixelFormat.Equals("YUY2", StringComparison.InvariantCultureIgnoreCase))
            {
                return PixelFormat.FormatYuy2;
            }
            else if (pixelFormat.Equals("UYVY", StringComparison.InvariantCultureIgnoreCase))
            {
                return PixelFormat.FormatUyvy;
            }
            else if (pixelFormat.Equals("ARGB", StringComparison.InvariantCultureIgnoreCase))
            {
                return PixelFormat.FormatArgb32;
            }
            else if (pixelFormat.Equals("BGRA", StringComparison.InvariantCultureIgnoreCase))
            {
                return PixelFormat.FormatBgra32;
            }
            else if (pixelFormat.Equals("RGB", StringComparison.InvariantCultureIgnoreCase))
            {
                return PixelFormat.FormatRgb24;
            }
            else if (pixelFormat.Equals("ABGR", StringComparison.InvariantCultureIgnoreCase))
            {
                return PixelFormat.FormatAbgr32;
            }
            else if (pixelFormat.Equals("MJPG", StringComparison.InvariantCultureIgnoreCase))
            {
                return PixelFormat.FormatMjpeg;
            }
            else if (pixelFormat.Equals("RGBA", StringComparison.InvariantCultureIgnoreCase))
            {
                return PixelFormat.FormatRgba32;
            }
            else
            {
                return PixelFormat.Unknown;
            }
        }

        private static PixelFormat TranslatePixelFormat(BitmapPixelFormat pixelFormat)
        {
            switch (pixelFormat)
            {
                case BitmapPixelFormat.Rgba8:
                    return PixelFormat.FormatRgba32;
                case BitmapPixelFormat.Bgra8:
                    return PixelFormat.FormatBgra32;
                case BitmapPixelFormat.Nv12:
                    return PixelFormat.FormatNv12;
                case BitmapPixelFormat.Yuy2:
                    return PixelFormat.FormatYuy2;
                default:
                    return PixelFormat.Unknown;
            }
        }

        private static Panel CameraLocationToPanel(CameraLocation location)
        {
            return location switch
            {
                CameraLocation.Unknown => Panel.Unknown,
                CameraLocation.Front => Panel.Front,
                CameraLocation.Back => Panel.Back,
                CameraLocation.Top => Panel.Top,
                CameraLocation.Bottom => Panel.Bottom,
                CameraLocation.Left => Panel.Left,
                CameraLocation.Right => Panel.Right,
                _ => Panel.Unknown,
            };
        }

        private static FrameFormat ConvertFrameFormat(MediaFrameFormat mediaFrameFormat)
        {
            return new(
                (ushort)mediaFrameFormat.VideoFormat.Width,
                (ushort)mediaFrameFormat.VideoFormat.Height,
                mediaFrameFormat.FrameRate.Numerator / (float)mediaFrameFormat.FrameRate.Denominator,
                StringToPixelFormat(mediaFrameFormat.Subtype));
        }
    }
}