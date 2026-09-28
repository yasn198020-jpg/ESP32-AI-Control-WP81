# WP81 XAML smoke test

This project intentionally contains only the minimum WP8.1 XAML application surface.

Purpose:
- prove that the GitHub Actions Windows runner can install the WP8.1/Windows SDK toolchain;
- prove that the legacy WP8.1 XAML compiler can compile App.xaml and MainPage.xaml;
- separate toolchain failures from ESP32-AI-Control application failures.

A failure here means we should not modify the application code yet.
