using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Open.Nat;

namespace KVANTLauncher
{
    public partial class ServerWindow : Window
    {
        private string _nickname;
        private string _virtualIp;
        private string _realIp = "";
        private HubConnection? _connection;
        private IHost? _host; // ASP.NET Core host
        private static readonly HttpClient _httpClient = new HttpClient();
        private NatDevice? _device;
        private Mapping? _currentMapping;
        private int _hubPort = 5000;

        public ServerWindow(string nickname)
        {
            InitializeComponent();
            _nickname = string.IsNullOrWhiteSpace(nickname) ? "User_" + Environment.MachineName : nickname;
            
            if (TxtUserNick != null) TxtUserNick.Text = _nickname;
            
            _virtualIp = GenerateStaticIP(_nickname); // Default IP
            if (TxtVirtualIP != null) TxtVirtualIP.Text = _virtualIp;

            FetchPublicIP();
            InitializeNetwork();
        }

        private async void FetchPublicIP()
        {
            try
            {
                _realIp = await _httpClient.GetStringAsync("https://api.ipify.org");
                Dispatcher.Invoke(() => {
                    if (TxtPublicIP != null) TxtPublicIP.Text = "Real IP: " + _realIp;
                    if (BtnCopyRealIP != null) BtnCopyRealIP.Visibility = Visibility.Visible;
                });
            }
            catch { }
        }

        private string GenerateStaticIP(string input, string networkContext = "LOCAL")
        {
            using (MD5 md5 = MD5.Create())
            {
                // Если сеть не выбрана, используем дефолтный 26.174.x.y (как Radmin)
                byte[] netHash = md5.ComputeHash(Encoding.UTF8.GetBytes(networkContext));
                byte netByte = (byte)(netHash[0] % 250 + 1);

                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
                return $"26.{netByte}.{hash[0]}.{hash[1]}";
            }
        }

        private async void InitializeNetwork()
        {
            // Мы больше не подключаемся автоматически при запуске к внешнему серверу,
            // вместо этого мы ждем пока пользователь создаст или войдет в сеть.
        }

        private async Task ConnectToHubAsync(string url)
        {
            try
            {
                if (_connection != null) await _connection.DisposeAsync();

                _connection = new HubConnectionBuilder()
                    .WithUrl(url) 
                    .WithAutomaticReconnect()
                    .Build();

                _connection.On<string, string, string>("PlayerConnected", (room, nick, vip) => {
                    Dispatcher.Invoke(() => AddPlayerToList(nick, true, vip));
                });

                await _connection.StartAsync();
            }
            catch (Exception ex) 
            {
                 System.Windows.MessageBox.Show("Ошибка подключения к комнате: " + ex.Message);
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left) DragMove();
        }

        private async void BtnPower_Checked(object sender, RoutedEventArgs e)
        {
            if (EntranceContainer == null) return;
            EntranceContainer.Opacity = 1;
            EntranceContainer.IsEnabled = true;
            FetchPublicIP();
            InitializeNetwork();
        }

        private async void BtnPower_Unchecked(object sender, RoutedEventArgs e)
        {
            if (EntranceContainer == null) return;
            EntranceContainer.Opacity = 0.3;
            EntranceContainer.IsEnabled = false;
            
            // Если мы были в комнате, выходим
            if (ServerActivePanel.Visibility == Visibility.Visible)
            {
                BtnLeaveRoom_Click(null!, null!);
            }

            if (_connection != null)
            {
                try { await _connection.StopAsync(); } catch { }
            }
            
            _realIp = "";
            if (TxtPublicIP != null) TxtPublicIP.Text = "Offline";
            if (BtnCopyRealIP != null) BtnCopyRealIP.Visibility = Visibility.Collapsed;
        }

        private void CheckOverlay_Checked(object sender, RoutedEventArgs e) => Topmost = true;
        private void CheckOverlay_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;

        private void BtnShowCreatePanel_Click(object sender, RoutedEventArgs e)
        {
            ServerCreatePanel.Visibility = Visibility.Visible;
            ServerJoinPanel.Visibility = Visibility.Collapsed;
        }

        private void BtnShowJoinPanel_Click(object sender, RoutedEventArgs e)
        {
            ServerCreatePanel.Visibility = Visibility.Collapsed;
            ServerJoinPanel.Visibility = Visibility.Visible;
        }

        private void BtnShowServerMain_Click(object sender, RoutedEventArgs e)
        {
            EntranceContainer.Visibility = Visibility.Visible;
            ServerActivePanel.Visibility = Visibility.Collapsed;
            ServerCreatePanel.Visibility = Visibility.Collapsed;
            ServerJoinPanel.Visibility = Visibility.Collapsed;
        }

        private async void BtnConfirmCreate_Click(object sender, RoutedEventArgs e)
        {
            string name = TxtNewRoomName.Text;
            if (string.IsNullOrWhiteSpace(name)) name = "MyNetwork";
            
            BtnConfirmCreate.Content = "ЗАПУСК СЕРВЕРА...";
            BtnConfirmCreate.IsEnabled = false;

            // 1. Запускаем встроенный SignalR сервер (Hub)
            await StartInternalHubAsync();

            // 2. Открываем порты через UPnP (Minecraft и наш Hub)
            bool success = await SetupUPnPAsync();

            if (success)
            {
                // Подключаемся к своему же Хабу
                await ConnectToHubAsync($"http://localhost:{_hubPort}/chat");
                EnterActiveMode(name);
            }
            else
            {
                BtnConfirmCreate.Content = "ОТКРЫТЬ ДОСТУП В ИНТЕРНЕТ";
                BtnConfirmCreate.IsEnabled = true;
                System.Windows.MessageBox.Show("Не удалось автоматически пробросить порты через UPnP.\nПроверьте, включен ли UPnP в настройках вашего роутера.", "Ошибка UPnP", MessageBoxButton.OK, MessageBoxImage.Warning);
                
                // Т.к. сервер стартовал на localhost, мы все равно можем войти (для игры по локалке)
                await ConnectToHubAsync($"http://localhost:{_hubPort}/chat");
                EnterActiveMode(name);
            }
        }

        private async Task StartInternalHubAsync()
        {
            try
            {
                if (_host != null) await _host.StopAsync();

                var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
                builder.Logging.ClearProviders();
                builder.Services.AddSignalR();
                
                var app = builder.Build();
                app.MapHub<KvantHub>("/chat");
                
                _ = app.RunAsync($"http://0.0.0.0:{_hubPort}");
                _host = app;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[HUB ERROR] " + ex.Message);
            }
        }

        private async Task<bool> SetupUPnPAsync()
        {
            try
            {
                var discoverer = new NatDiscoverer();
                var cts = new System.Threading.CancellationTokenSource(3000);
                _device = await discoverer.DiscoverDeviceAsync(PortMapper.Upnp, cts);
                
                if (_device == null) return false;

                // Пробрасываем порт 25565 (Minecraft по умолчанию) и 5000 (наш Hub)
                _currentMapping = new Mapping(Protocol.Tcp, 25565, 25565, "KVANT MC Server");
                await _device.CreatePortMapAsync(_currentMapping);
                await _device.CreatePortMapAsync(new Mapping(Protocol.Udp, 25565, 25565, "KVANT MC Server"));
                
                // Пробрасываем порт для SignalR
                await _device.CreatePortMapAsync(new Mapping(Protocol.Tcp, _hubPort, _hubPort, "KVANT Lobby Hub"));

                var ip = await _device.GetExternalIPAsync();
                Dispatcher.Invoke(() => {
                    if (TxtHostPublicAddress != null) TxtHostPublicAddress.Text = $"{ip}:25565";
                    if (HostAddressBanner != null) HostAddressBanner.Visibility = Visibility.Visible;
                });

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[UPnP ERROR] " + ex.Message);
                return false;
            }
        }

        private async void BtnConfirmJoin_Click(object sender, RoutedEventArgs e)
        {
            string hostIP = TxtRoomId.Text; // Теперь в поле ID вводим IP хоста
            if (string.IsNullOrWhiteSpace(hostIP))
            {
                System.Windows.MessageBox.Show("Введите IP хоста (ID сети)");
                return;
            }

            await ConnectToHubAsync($"http://{hostIP}:{_hubPort}/chat");
            EnterActiveMode(hostIP);
        }

        private async void EnterActiveMode(string title)
        {
            EntranceContainer.Visibility = Visibility.Collapsed;
            ServerActivePanel.Visibility = Visibility.Visible;
            
            // Обновляем IP под текущую сеть
            _virtualIp = GenerateStaticIP(_nickname, title);
            if (TxtVirtualIP != null) TxtVirtualIP.Text = _virtualIp;

            PlayerListContainer.Children.Clear();
            AddPlayerToList(_nickname + " (Вы)", true, _virtualIp);
            
            // Сообщаем всем в этой "сети" через SignalR
            if (_connection?.State == HubConnectionState.Connected)
            {
                try { 
                    await _connection.InvokeAsync("JoinNetwork", title, _nickname, _virtualIp); 
                } catch { }
            }
            else
            {
                // Симуляция: если сервера нет, добавим "разработчика" для вида
                AddPlayerToList("Friend_Developer", true, GenerateStaticIP("Dev", title));
            }
        }

        private async void BtnLeaveRoom_Click(object sender, RoutedEventArgs e)
        {
            if (_device != null && _currentMapping != null)
            {
                try { await _device.DeletePortMapAsync(_currentMapping); } catch { }
                try { await _device.DeletePortMapAsync(new Mapping(Protocol.Tcp, _hubPort, _hubPort, "KVANT Lobby Hub")); } catch { }
                _device = null;
            }

            if (_host != null) { await _host.StopAsync(); _host = null; }
            if (_connection != null) { await _connection.DisposeAsync(); _connection = null; }

            HostAddressBanner.Visibility = Visibility.Collapsed;
            BtnShowServerMain_Click(null!, null!);
        }

        private void AddPlayerToList(string nick, bool isOnline, string vIp = "")
        {
            var border = new Border 
            { 
                Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 24, 27)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 6),
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 39, 42)),
                BorderThickness = new Thickness(1)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            sp.Children.Add(new System.Windows.Shapes.Ellipse { 
                Width = 10, Height = 10, 
                Fill = isOnline ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)) : System.Windows.Media.Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 12, 0)
            });
            sp.Children.Add(new TextBlock { 
                Text = nick, 
                Foreground = System.Windows.Media.Brushes.White, 
                FontSize = 13, 
                FontWeight = FontWeights.SemiBold 
            });
            
            grid.Children.Add(sp);

            if (!string.IsNullOrEmpty(vIp))
            {
                var tip = new TextBlock { 
                    Text = vIp, 
                    Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(82, 82, 91)),
                    FontSize = 11,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                    Margin = new Thickness(0,0,5,0)
                };
                Grid.SetColumn(tip, 1);
                grid.Children.Add(tip);
            }

            border.Child = grid;
            PlayerListContainer.Children.Add(border);
        }

        private void BtnSendMessage_Click(object sender, RoutedEventArgs e) { }
        private void TxtChatMessage_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { }

        private void BtnCopyRealIP_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_realIp))
            {
                System.Windows.Clipboard.SetText(_realIp);
                System.Windows.MessageBox.Show("Реальный IP скопирован!", "KVANT", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnCopyIP_Click(object sender, RoutedEventArgs e)
        {
            System.Windows.Clipboard.SetText(_virtualIp);
        }
    }

    // Встроенный SignalR хаб, который будет работать прямо в лаунчере
    public class KvantHub : Hub
    {
        public async Task JoinNetwork(string room, string nick, string vip)
        {
            // Мы просто пересылаем всем остальным игрокам в этой "комнате" (на этом сервере) данные о новом игроке
            await Clients.Others.SendAsync("PlayerConnected", room, nick, vip);
        }
    }

    public class ChatMessageModel
    {
        public string Sender { get; set; } = "";
        public string Message { get; set; } = "";
        public DateTime Time { get; set; }
    }
}
