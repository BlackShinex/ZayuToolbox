using System.Windows;
using System.Windows.Controls;

namespace IpInfo.App
{
    public partial class PageHeader : UserControl
    {
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.Register("Title", typeof(string), typeof(PageHeader),
                new PropertyMetadata(string.Empty, OnTitleChanged));

        public static readonly DependencyProperty SubtitleProperty =
            DependencyProperty.Register("Subtitle", typeof(string), typeof(PageHeader),
                new PropertyMetadata(string.Empty, OnSubtitleChanged));

        public PageHeader()
        {
            InitializeComponent();
        }

        public string Title
        {
            get { return (string)GetValue(TitleProperty); }
            set { SetValue(TitleProperty, value); }
        }

        public string Subtitle
        {
            get { return (string)GetValue(SubtitleProperty); }
            set { SetValue(SubtitleProperty, value); }
        }

        private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            PageHeader header = d as PageHeader;
            if (header != null) header.TitleText.Text = e.NewValue as string;
        }

        private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            PageHeader header = d as PageHeader;
            if (header != null) header.SubtitleText.Text = e.NewValue as string;
        }
    }
}
