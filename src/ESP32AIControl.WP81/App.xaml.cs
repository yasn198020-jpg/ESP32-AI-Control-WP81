using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.UI.Xaml;

namespace ESP32AIControl.WP81
{
    public sealed partial class App : Application
    {
        public App()
        {
            this.InitializeComponent();
            this.Resuming += App_Resuming;
        }

        private void App_Resuming(object sender, object e)
        {
            var rootFrame = Window.Current.Content as Windows.UI.Xaml.Controls.Frame;
            var page = rootFrame == null ? null : rootFrame.Content as MainPage;
            if (page != null)
                page.OnApplicationResuming();
        }

        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            var rootFrame = Window.Current.Content as Windows.UI.Xaml.Controls.Frame;
            if (rootFrame == null)
            {
                rootFrame = new Windows.UI.Xaml.Controls.Frame();
                Window.Current.Content = rootFrame;
            }

            if (rootFrame.Content == null)
            {
                rootFrame.Navigate(typeof(MainPage));
            }

            Window.Current.Activate();
        }
    }
}
