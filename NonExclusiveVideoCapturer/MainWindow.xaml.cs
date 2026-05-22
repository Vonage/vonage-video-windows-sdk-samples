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

        private async void Publisher_StreamCreated(object? sender, Publisher.StreamEventArgs e)
        {
            /* We can hot-switch to any of the available video capture devices */
            IReadOnlyList<VideoCapturerDevice> devices = await VideoCapturer.GetDevicesAsync();
            if (videoCapturer != null && devices.Count > 0)
                videoCapturer.DeviceId = devices[0].Id;
        }
    }
}