using System;
using System.Windows;
using System.Windows.Controls;

namespace Secondary.App.Presentation;

public sealed partial class HomeUtilityWidget
{
    // Dimensions come from the viewport, never ScrollViewer's unbounded measure.
    // Updating existing elements preserves editor drafts, focus and running timers.
    private void UpdateResponsiveLayout()
    {
        if (contentViewport is null || contentViewport.ActualWidth <= 0 || contentViewport.ActualHeight <= 0) return;
        var width = Math.Max(1, contentViewport.ActualWidth - 24);
        var height = Math.Max(1, contentViewport.ActualHeight - 16);
        var scale = Math.Clamp(Math.Min(width / 272, height / 232), .65, 2.4);
        status.FontSize = Math.Clamp(11 * scale, 11, 15);
        if (note is not null)
        {
            note.Height = Math.Max(40, height - 82);
            note.FontSize = Math.Clamp(13 * scale, 12, 20);
        }
        if (todoScroll is not null)
        {
            todoScroll.Height = Math.Max(36, height - 82);
            foreach (var line in todoList!.Children)
                if (line is DockPanel panel)
                    foreach (var child in panel.Children)
                        if (child is Button { Content: TextBlock text }) text.FontSize = Math.Clamp(13 * scale, 12, 20);
        }
        if (calendarGrid is not null)
        {
            calendarGrid.Height = Math.Max(96, height - 92);
            var font = Math.Clamp(Math.Min(width / 7 * .45, calendarGrid.Height / 7 * .55), 11, 30);
            foreach (var label in calendarLabels) label.FontSize = font;
            calendarHeading!.FontSize = Math.Clamp(17 * scale, 13, 25);
        }
        if (Settings.Kind is "Countdown" or "Stopwatch" or "Counter")
        {
            var reserved = Settings.Kind == "Stopwatch" ? 106 : 150;
            numberHost.Height = Math.Clamp(height - reserved, 40, 180);
            numberHost.MaxHeight = 180;
            readout.Margin = new Thickness(0);
            body.Margin = new Thickness(0, Math.Max(0, (height - reserved - numberHost.Height) / 2), 0, 0);
        }
        if (progressDate is not null)
        {
            progressDate.FontSize = Math.Clamp(17 * scale, 13, 30);
            var gap = Math.Clamp((height - 170 * scale) / 4, 4, 44);
            for (var i = 0; i < progressLabels.Length; i++)
            {
                progressLabels[i].FontSize = Math.Clamp(13 * scale, 12, 26);
                progressTracks[i].Height = Math.Clamp(6 * scale, 5, 14);
                progressTracks[i].Margin = new Thickness(0, 0, 0, gap);
            }
        }
        if (mediaHero is not null)
        {
            var cover = Math.Clamp(Math.Min(width * .30, height - 145), 44, 168);
            mediaArtwork!.Width = mediaArtwork.Height = cover;
            mediaHero.ColumnDefinitions[0].Width = new GridLength(cover + 10);
            mediaHero.MinHeight = cover;
            mediaArtworkIcon!.FontSize = Math.Clamp(cover * .45, 22, 60);
            if (mediaArtwork.Child is Image image) { image.Width = image.Height = cover; image.Clip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, cover, cover), 12, 12); }
            mediaTitle!.FontSize = Math.Clamp(19 * scale, 14, 32);
            mediaArtist!.FontSize = Math.Clamp(12 * scale, 11, 19);
            mediaTitle.MaxHeight = Math.Max(42, cover * .65);
        }
        if (weatherHero is not null)
        {
            var heroHeight = Math.Clamp(height - 162, 52, 200);
            weatherHero.MinHeight = heroHeight;
            weatherHero.ColumnDefinitions[0].Width = new GridLength(Math.Clamp(width * .26, 44, 130));
            weatherReading!.FontSize = Math.Clamp(Math.Min((width * .68) / 4.3, heroHeight * .72), 24, 92);
            weatherIcon!.FontSize = Math.Clamp(heroHeight * .70, 30, 88);
            weatherDetails!.FontSize = Math.Clamp(15 * scale, 13, 25);
            weatherMeta!.FontSize = Math.Clamp(12 * scale, 11, 18);
            weatherCity!.FontSize = Math.Clamp(15 * scale, 13, 22);
        }
        UpdateUsageLayout(width, height, scale);
        if (eventReading is not null)
        {
            eventReading.FontSize = Math.Clamp(Math.Min(width / Math.Max(3, eventReading.Text.Length) * 1.5, (height - 66) * .45), 24, 110);
            eventReading.Margin = new Thickness(0, Math.Clamp((height - eventReading.FontSize - 60) / 3, 32, 100), 0, 6);
            eventDescription!.FontSize = Math.Clamp(13 * scale, 12, 22);
        }
    }
}
