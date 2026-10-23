using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Lemonize.Core;
using Orientation = System.Windows.Controls.Orientation;

namespace Lemonize.UI;

/// <summary>"Lemonize 1.0.1 · developed by [wild]lab", with the logo linking to the developer's page.</summary>
public sealed class CreditLine : StackPanel
{
    public CreditLine()
    {
        Orientation = Orientation.Horizontal;
        var dim = (Style)Application.Current.FindResource("Dim");
        Children.Add(new TextBlock { Text = $"Lemonize {AppInfo.Version}", Style = dim, VerticalAlignment = VerticalAlignment.Center });
        Children.Add(new TextBlock { Text = "  ·  " + Loc.T("developed by") + "  ", Style = dim, VerticalAlignment = VerticalAlignment.Center });

        var logo = new Image
        {
            Source = (ImageSource)Application.Current.FindResource("WildlabLogo"),
            Height = 13,
            Stretch = Stretch.Uniform,
        };
        RenderOptions.SetEdgeMode(logo, EdgeMode.Unspecified);
        var link = new Button
        {
            Content = logo,
            Style = (Style)Application.Current.FindResource("LinkButton"),
            ToolTip = AppInfo.DeveloperUrl,
            VerticalAlignment = VerticalAlignment.Center,
        };
        link.Click += (_, _) => Commands.Open(AppInfo.DeveloperUrl);
        Children.Add(link);
    }
}
