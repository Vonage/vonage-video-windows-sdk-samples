using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using OpenTok;

namespace NonExclusiveVideoCapturer
{
    public partial class MainWindow : Window
    {
        private const string APP_ID = "47797141";
        private const string SESSION_ID = "2_MX40Nzc5NzE0MX5-MTc3OTQ1OTQyMjkzN356SFlSdktJWDJsM0YySnEyNnpad2NPenN-fn4";
        private const string TOKEN = "T1==cGFydG5lcl9pZD00Nzc5NzE0MSZzZGtfdmVyc2lvbj0mc2lnPTU2YzQyNmIzZTcyNjQ2NzIzZjkzODljN2Q1YmI1YmMyNTk5OTZhNmY6c2Vzc2lvbl9pZD0yX01YNDBOemM1TnpFME1YNS1NVGMzT1RRMU9UUXlNamt6TjM1NlNGbFNka3RKV0RKc00wWXlTbkV5Tm5wYWQyTlBlbk4tZm40JmNyZWF0ZV90aW1lPTE3Nzk0NTk0MjImZXhwaXJlX3RpbWU9MTc4MjA1MTQyMiZyb2xlPW1vZGVyYXRvciZub25jZT04OWRjNTA0ZS1lYWEzLTQ2NDktYjQ1Yy04YTFkMjRkNjY3ODg=";

        private Context? Context;
        private Session? Session;
        private VideoCapturer? videoCapturer;
        private Publisher? Publisher;
        
        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Context = new Context(new WPFDispatcher());
                        
            videoCapturer = new VideoCapturer(new WPFDispatcher());
            videoCapturer.Event += VideoCapturer_Event;

            Publisher = new Publisher.Builder(Context)
            {
                Capturer = videoCapturer,
                Renderer = PublisherVideo
            }.Build();
            Publisher.StreamCreated += Publisher_StreamCreated;

            Session = new Session.Builder(Context, APP_ID, SESSION_ID).Build();
            Session.Connected += Session_Connected;
            Session.Disconnected += Session_Disconnected;
            Session.Error += Session_Error;
            Session.StreamReceived += Session_StreamReceived;
            Session.Connect(TOKEN);            
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            Session?.Dispose();
            Publisher?.Dispose();
        }

        private void Session_Connected(object? sender, System.EventArgs e)
        {
            Session?.Publish(Publisher);            
        }

        private void Session_Disconnected(object? sender, System.EventArgs e)
        {
            Trace.WriteLine("Session disconnected.");
        }

        private void Session_Error(object? sender, Session.ErrorEventArgs e)
        {
            Trace.WriteLine("Session error:" + e.ErrorCode);
        }

        private void Session_StreamReceived(object? sender, Session.StreamEventArgs e)
        {
            Subscriber subscriber = new Subscriber.Builder(Context, e.Stream)
            {
                Renderer = SubscriberVideo
            }.Build();
            Session?.Subscribe(subscriber);
        }

        private void VideoCapturer_Event(object? sender, VideoCapturer.EventArgs e)
        {
            Debug.Assert(videoCapturer != null);
            if (e.Type == VideoCapturer.EventType.ExclusiveControlNotAvailable)
                videoCapturer.ExclusiveControl = false; /* Hot-switch video capturer to non-exclusive mode */
            else if (e.Type == VideoCapturer.EventType.ExclusiveControlAvailable)
                videoCapturer.ExclusiveControl = true; /* Camera has become free. Hot-switch video capturer to exclusive mode */
            else
                Console.WriteLine("Video Capturer Error: " + e.Type.ToString());
        }

        private async void Publisher_StreamCreated(object? sender, Publisher.StreamEventArgs e)
        {
            Debug.Assert(videoCapturer != null);

            /* Let's test some of the vidoe capturer capabilities */
            await Task.Delay(1000);

            /* We can hot-switch to any of the available video capture devices */
            IReadOnlyList<VideoCapturerDevice> devices = await VideoCapturer.GetDevicesAsync();
            if (devices.Count > 0)
                videoCapturer.DeviceId = devices[0].Id;

            await Task.Delay(1000);

            /* Now try to change capture resolution. This will only work if camera was opened in exclusive mode (no other app was using the camera) */
            if (videoCapturer.ExclusiveControl)
                videoCapturer.FrameFormat = new FrameFormat(320, 240, 30);
        }
    }
}