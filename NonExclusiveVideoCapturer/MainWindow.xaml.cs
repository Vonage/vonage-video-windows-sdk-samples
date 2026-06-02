using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using OpenTok;

namespace NonExclusiveVideoCapturer
{
    public partial class MainWindow : Window
    {
        private const string APP_ID = "";
        private const string SESSION_ID = "";
        private const string TOKEN = "";

        private Context? context;
        private Session? session;
        private VideoCapturer? videoCapturer;
        private Publisher? publisher;
        
        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            context = new Context(new WPFDispatcher());
                        
            videoCapturer = new VideoCapturer(new WPFDispatcher());
            videoCapturer.Event += VideoCapturer_Event;

            publisher = new Publisher.Builder(context)
            {
                Capturer = videoCapturer,
                Renderer = PublisherVideo
            }.Build();
            publisher.StreamCreated += Publisher_StreamCreated;

            session = new Session.Builder(context, APP_ID, SESSION_ID).Build();
            session.Connected += Session_Connected;
            session.Disconnected += Session_Disconnected;
            session.Error += Session_Error;
            session.StreamReceived += Session_StreamReceived;
            session.Connect(TOKEN);            
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            session?.Dispose();
            publisher?.Dispose();
        }

        private void Session_Connected(object? sender, System.EventArgs e)
        {
            Debug.Assert(publisher != null);
            session?.Publish(publisher);            
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
            Debug.Assert(context != null);
            Debug.Assert(session != null);
            Subscriber subscriber = new Subscriber.Builder(context, e.Stream)
            {
                Renderer = SubscriberVideo
            }.Build();
            session?.Subscribe(subscriber);
        }

        private void VideoCapturer_Event(object? sender, VideoCapturer.EventArgs e)
        {
            /* We can subscribe to video capturer events to check for changes in the camera control availability */
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

            /* Let's test some of the video capturer capabilities */

            await Task.Delay(3000);

            /* Try to change capture resolution. This will only work if camera was opened in exclusive mode (no other app was using the camera) */
            if (videoCapturer.ExclusiveControl)
                videoCapturer.FrameFormat = new FrameFormat(320, 240, 30);

            await Task.Delay(3000);

            /* We can hot-switch to any of the available video capture devices. SInce index 0 is selected by default, let's pick last index now (if available) */
            IReadOnlyList<VideoCapturerDevice> devices = await VideoCapturer.GetDevicesAsync();
            if (devices.Count > 0)
                videoCapturer.DeviceId = devices[devices.Count-1].Id;         
        }
    }
}