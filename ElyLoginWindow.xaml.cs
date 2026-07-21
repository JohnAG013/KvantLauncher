using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Newtonsoft.Json;
using CmlLib.Core.Auth;

namespace KVANTLauncher
{
    public partial class ElyLoginWindow : Window
    {
        public MSession? Session { get; private set; }

        public ElyLoginWindow()
        {
            InitializeComponent();
            this.Loaded += (s, e) =>
            {
                if (Owner != null)
                {
                    this.Left = Owner.Left + (Owner.Width - this.Width) / 2;
                    this.Top = Owner.Top + (Owner.Height - this.Height) / 2;
                }
            };
            this.ContentRendered += ElyLoginWindow_ContentRendered;
        }

        private void ElyLoginWindow_ContentRendered(object? sender, EventArgs e)
        {
            var sb = new System.Windows.Media.Animation.Storyboard();

            // Opacity анимируем на самом Window
            var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280));
            fadeIn.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            System.Windows.Media.Animation.Storyboard.SetTarget(fadeIn, this);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(fadeIn, new PropertyPath(UIElement.OpacityProperty));

            // Scale анимируем на RootBorder
            var scaleX = new System.Windows.Media.Animation.DoubleAnimation(0.85, 1.0, TimeSpan.FromMilliseconds(280));
            scaleX.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            System.Windows.Media.Animation.Storyboard.SetTarget(scaleX, RootBorder);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleX, new PropertyPath("RenderTransform.ScaleX"));

            var scaleY = new System.Windows.Media.Animation.DoubleAnimation(0.85, 1.0, TimeSpan.FromMilliseconds(280));
            scaleY.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            System.Windows.Media.Animation.Storyboard.SetTarget(scaleY, RootBorder);
            System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleY, new PropertyPath("RenderTransform.ScaleY"));

            sb.Children.Add(fadeIn);
            sb.Children.Add(scaleX);
            sb.Children.Add(scaleY);
            sb.Begin();
        }

        private void Border_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
                this.DragMove();
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            string email = TxtEmail.Text;
            string password = TxtPassword.Password;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
            {
                System.Windows.MessageBox.Show("Заполните все поля!");
                return;
            }

            LoadingGrid.Visibility = Visibility.Visible;
            BtnLogin.IsEnabled = false;

            try
            {
                using var client = new HttpClient();
                var authPayload = new
                {
                    agent = new { name = "Minecraft", version = 1 },
                    username = email,
                    password = password,
                    clientToken = Guid.NewGuid().ToString()
                };

                var content = new StringContent(JsonConvert.SerializeObject(authPayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("https://authserver.ely.by/auth/authenticate", content);
                var responseString = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    var authData = Newtonsoft.Json.Linq.JObject.Parse(responseString);
                    var profile = authData["selectedProfile"];
                    var token = authData["accessToken"]?.ToString();

                    if (profile != null && token != null)
                    {
                        string uuid = profile["id"]?.ToString()?.Replace("-", "") ?? "";
                        Session = new MSession(
                            profile["name"]?.ToString() ?? "Player",
                            token,
                            uuid
                        );
                        this.DialogResult = true;
                        this.Close();
                    }
                    else
                    {
                        System.Windows.MessageBox.Show("Ошибка: Неверный формат ответа Ely.by");
                    }
                }
                else
                {
                    var errorData = Newtonsoft.Json.Linq.JObject.Parse(responseString);
                    string errorMsg = errorData["errorMessage"]?.ToString() ?? "Неверный логин или пароль";
                    System.Windows.MessageBox.Show($"Ошибка: {errorMsg}");
                }
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Ошибка сети: {ex.Message}");
            }
            finally
            {
                LoadingGrid.Visibility = Visibility.Collapsed;
                BtnLogin.IsEnabled = true;
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }
}
