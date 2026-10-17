using System.Windows;
using System.Windows.Controls;

namespace Keepsake.Gui.Infrastructure
{
    /// <summary>
    /// Makes <see cref="PasswordBox.Password"/> bindable: <c>infra:PasswordBinding.Password="{Binding SftpPassword, Mode=TwoWay}"</c>.
    /// Only used for short-lived form values that are sent to the service and then cleared.
    /// </summary>
    public static class PasswordBinding
    {
        public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
            "Password", typeof(string), typeof(PasswordBinding),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnPasswordChanged));

        private static readonly DependencyProperty UpdatingProperty =
            DependencyProperty.RegisterAttached("Updating", typeof(bool), typeof(PasswordBinding));

        public static string GetPassword(DependencyObject d) => (string)d.GetValue(PasswordProperty);
        public static void SetPassword(DependencyObject d, string value) => d.SetValue(PasswordProperty, value);

        private static void OnPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is PasswordBox box))
                return;
            box.PasswordChanged -= OnBoxChanged;
            if (!(bool)box.GetValue(UpdatingProperty))
                box.Password = (string)e.NewValue ?? string.Empty;
            box.PasswordChanged += OnBoxChanged;
        }

        private static void OnBoxChanged(object sender, RoutedEventArgs e)
        {
            var box = (PasswordBox)sender;
            box.SetValue(UpdatingProperty, true);
            SetPassword(box, box.Password);
            box.SetValue(UpdatingProperty, false);
        }
    }
}
