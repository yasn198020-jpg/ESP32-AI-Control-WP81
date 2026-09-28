using Windows.UI.Xaml;

namespace WP81Smoke
{
    sealed partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(Windows.ApplicationModel.Activation.LaunchActivatedEventArgs e)
        {
            Window.Current.Content = new MainPage();
            Window.Current.Activate();
        }
    }
}
