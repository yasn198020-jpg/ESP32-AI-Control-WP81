using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace ESP32AIControl.UWP
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
            Resuming += OnResuming;
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame == null)
            {
                frame = new Frame();
                Window.Current.Content = frame;
            }

            if (frame.Content == null)
                frame.Navigate(typeof(MainPage), e.Arguments);

            Window.Current.Activate();
        }

        private void OnSuspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                var frame = Window.Current.Content as Frame;
                var page = frame == null ? null : frame.Content as MainPage;
                if (page != null) page.OnAppSuspending();
            }
            finally
            {
                deferral.Complete();
            }
        }

        private void OnResuming(object sender, object e)
        {
            var frame = Window.Current.Content as Frame;
            var page = frame == null ? null : frame.Content as MainPage;
            if (page != null) page.OnAppResuming();
        }
    }
}
