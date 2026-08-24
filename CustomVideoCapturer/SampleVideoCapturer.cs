using OpenTok;
using System;
using System.Timers;
using System.Drawing;

namespace CustomVideoCapturer
{
    public class SampleVideoCapturer : IVideoCapturer
    {
        private IVideoFrameConsumer frameConsumer;
        private Timer timer;
        private int WIDTH = 320;
        private int HEIGHT = 240;

        public void Destroy()
        {
            timer?.Stop();
            timer?.Dispose();
            timer = null;
        }

        public VideoCaptureSettings GetCaptureSettings()
        {
            VideoCaptureSettings videoCaptureSettings = new VideoCaptureSettings();
            videoCaptureSettings.Width = WIDTH;
            videoCaptureSettings.Height = HEIGHT;
            videoCaptureSettings.Fps = 1;
            videoCaptureSettings.MirrorOnLocalRender = false;
            videoCaptureSettings.PixelFormat = PixelFormat.FormatYuv420p;
            return videoCaptureSettings;
        }

        public void Init(IVideoFrameConsumer frameConsumer)
        {
            this.frameConsumer = frameConsumer;
        }

        public void Start()
        {
            timer = new Timer(1000);
            timer.Elapsed += new ElapsedEventHandler(OnTimedEvent);
            timer.Enabled = true;
        }

        private void OnTimedEvent(object source, ElapsedEventArgs e)
        {
            using (var bitmap = new Bitmap(WIDTH, HEIGHT))
            using (var gfx = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(Color.FromArgb(255, 0, 255, 0)))
            {
                gfx.FillRectangle(brush, 0, 0, WIDTH, HEIGHT);
                using (var frame = VideoFrame.CreateYuv420pFrameFromBitmap(bitmap))
                {
                    frameConsumer.Consume(frame);
                }
            }
        }

        public void SetVideoContentHint(VideoContentHint contentHint)
        {
            if (frameConsumer == null)
                throw new InvalidOperationException("Content hint can only be set after constructing the " +
                    "Publisher and Capturer.");
            frameConsumer.SetVideoContentHint(contentHint);
        }

        public VideoContentHint GetVideoContentHint()
        {
            if (frameConsumer != null)
                return frameConsumer.GetVideoContentHint();
            return VideoContentHint.NONE;
        }

        public void Stop()
        {
            timer.Stop();
        }
    }
}
