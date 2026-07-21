using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Linq;
using System.Net.Http;
using Newtonsoft.Json;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Version;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.ProcessBuilder;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using System.Windows.Media.Media3D;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Controls;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using System.Text;
using System.Threading;
using Brushes = System.Windows.Media.Brushes;
using System.Windows.Interop;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using System.Windows.Documents;

namespace KVANTLauncher;

/// <summary>
/// Логика взаимодействия для MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MinecraftPath _path;
    private readonly MinecraftLauncher _launcher;
    private string? _currentSkinPath;
    private NotifyIcon? _trayIcon;
    private string? _currentViewedScreenshot;
    private Dictionary<string, int> _conflictStats = new Dictionary<string, int>();
    private int _restartCount = 0;
    private CancellationTokenSource? _launchCts;
    private bool _isSettingsLoading = false;
    private MSession? _session;
    private string _currentAuthType = "Offline";
    private HashSet<string> _availableOptiFineVersions = new();
    private HashSet<string> _availableNeoForgeVersions = new();
    private HashSet<string> _availableFabricVersions = new();


    // --- Параметры для обновлений и хостинга ---
    private const string CurrentVersion = "1.2.0 Alpha"; // Текущая версия
    // Ссылка на файл с информацией об обновлениях (замените на свою на GitHub)
    private const string UpdateInfoUrl = "https://raw.githubusercontent.com/ваш_ник/KVANTLauncher/main/update_info.json";
    // ------------------------------------------

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private class MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        public MEMORYSTATUSEX()
        {
            this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    // ===== DWM Dark Title Bar =====
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private void ApplyDarkTitleBar()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int useDarkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDarkMode, sizeof(int));
        }
        catch { }
    }

    public MainWindow()
    {
        InitializeComponent();
        Title = "KVANT Launcher v1.2.0 Alpha";
        Console.WriteLine("[SYSTEM] Лаунчер запущен (v1.1.0-FIX-CAT)");
        ApplyDarkTitleBar();
        
        // 1. Инициализация пути: папка "minecraft" ПРЯМО ТУТ (возле лаунчера)
        // Используем BaseDirectory для надежности (чтобы не зависеть от рабочей папки)
        var localPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "minecraft");
        _path = new MinecraftPath(localPath);
        
        // 2. Инициализация лаунчера
        _launcher = new MinecraftLauncher(_path);
        
        // 3. Добавление обработчиков событий для прогресса (4.x стиль)
        _launcher.FileProgressChanged += (s, e) =>
        {
            Dispatcher.Invoke(() =>
            {
                TxtStatus.Text = $"{e.Name} - {e.ProgressedTasks}/{e.TotalTasks}";
            });
        };
        _launcher.ByteProgressChanged += (s, e) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (e.TotalBytes > 0)
                    PbStatus.Value = (double)e.ProgressedBytes / e.TotalBytes * 100;
            });
        };

        EnsureStandardFolders();
        Loaded += MainWindow_Loaded;
        Loaded += async (s, e) => await CheckForUpdates(); // Проверка обновлений при запуске
        TxtNickname.TextChanged += TxtNickname_TextChanged;
        this.MouseMove += MainWindow_MouseMove;

        // Установка иконки окна программно для надежности (Taskbar & Alt-Tab)
        try {
            string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.ico");
            if (System.IO.File.Exists(icoPath))
            {
                using (var stream = new System.IO.FileStream(icoPath, System.IO.FileMode.Open, System.IO.FileAccess.Read))
                {
                    this.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(stream, System.Windows.Media.Imaging.BitmapCreateOptions.None, System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
                }
            }
            else
            {
                string assemblyName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name ?? "KVANTLauncher";
                this.Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri($"pack://application:,,,/{assemblyName};component/logo_green.png"));
            }
        } catch { }
    }

    // ===== Custom Title Bar Handlers =====
    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // Double-click to maximize/restore
            this.WindowState = this.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }
        else
        {
            this.DragMove();
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
    }

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
    {
        this.WindowState = this.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        this.Close();
    }

    private void MainWindow_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // Эффект параллакса - фон движется чуть-чуть вслед за мышкой
        var pos = e.GetPosition(this);
        double centerX = this.ActualWidth / 2;
        double centerY = this.ActualHeight / 2;

        double offX = (pos.X - centerX) / 40;
        double offY = (pos.Y - centerY) / 40;

        BgTranslate.X = -offX;
        BgTranslate.Y = -offY;
    }

    private System.Timers.Timer? _nicknameTimer;
    private void TxtNickname_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_isSettingsLoading) return; // Игнорируем авто-смену при загрузке конфига
        
        _nicknameTimer?.Stop();
        _nicknameTimer = new System.Timers.Timer(800);
        _nicknameTimer.Elapsed += (s, ev) =>
        {
            _nicknameTimer.Stop();
            Dispatcher.Invoke(() => UpdateAvatarByName(TxtNickname.Text ?? "Player"));
        };
        _nicknameTimer.Start();
    }

    private void UpdateAvatarByName(string nickname)
    {
        if (string.IsNullOrEmpty(nickname)) nickname = "Player";
        if (!string.IsNullOrEmpty(_currentSkinPath) && System.IO.File.Exists(_currentSkinPath)) return;

        // Если активен Ely.by, сразу берем полный скин и вырезаем лицо
        // т.к. их старое API аватарок ( /avatars/nickname ) часто возвращает 404
        if (_currentAuthType == "ElyBy") 
        {
            string url = $"http://skinsystem.ely.by/skins/{nickname}.png";
            TxtStatus.Text = $"Загрузка скина Ely.by: {nickname}...";
            UpdateSkinByUrl(url, nickname);
        }
        else
        {
            string url = $"https://mc-heads.net/avatar/{nickname}/100";
            TxtStatus.Text = $"Загрузка образа: {nickname}...";
            UpdateSkinByUrl(url, nickname);
        }
    }

    private async void UpdateSkinByUrl(string url, string nickname)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            client.Timeout = TimeSpan.FromSeconds(5);
            
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                HandleSkinError(url, nickname);
                return;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length < 300 && !url.Contains("mc-heads"))
            {
                HandleSkinError(url, nickname);
                return;
            }

            Dispatcher.Invoke(() => {
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                using (var ms = new System.IO.MemoryStream(bytes))
                {
                    bitmap.BeginInit();
                    bitmap.StreamSource = ms;
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                }

                ImageSource finalSource = bitmap;
                
                // Если это файл скина (размеры 64x64, 64x32 или кратные), вырезаем лицо
                if (bitmap.PixelWidth >= 64 && (bitmap.PixelWidth == bitmap.PixelHeight || bitmap.PixelWidth == bitmap.PixelHeight * 2))
                {
                    finalSource = GetFaceFromSkin(bitmap);
                }

                ImgSkinHead.Source = finalSource;
                BtnSkin.Tag = finalSource;
                TxtStatus.Text = "Скин получен";
            });
        }
        catch 
        {
            Dispatcher.Invoke(() => HandleSkinError(url, nickname));
        }
    }

    private ImageSource GetFaceFromSkin(System.Windows.Media.Imaging.BitmapSource skin)
    {
        try
        {
            // В Minecraft скинах лицо находится в области 8,8 размером 8x8 (относительно 64x64)
            int w = skin.PixelWidth;
            int scale = w / 64;
            int faceSize = 8 * scale;
            
            // Вырезаем основное лицо
            var face = new System.Windows.Media.Imaging.CroppedBitmap(skin, new Int32Rect(8 * scale, 8 * scale, faceSize, faceSize));
            
            // Пытаемся наложить второй слой (шлем/очки), если он есть
            // Он находится в 40,8
            try
            {
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawImage(face, new Rect(0, 0, faceSize, faceSize));
                    var hat = new System.Windows.Media.Imaging.CroppedBitmap(skin, new Int32Rect(40 * scale, 8 * scale, faceSize, faceSize));
                    context.DrawImage(hat, new Rect(0, 0, faceSize, faceSize));
                }
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(faceSize, faceSize, 96, 96, PixelFormats.Pbgra32);
                rtb.Render(visual);
                return rtb;
            }
            catch { return face; }
        }
        catch { return skin; }
    }

    private void HandleSkinError(string currentUrl, string nickname)
    {
        // Последовательно перебираем возможные источники
        if (currentUrl.Contains("skin.ely.by") && !currentUrl.Contains("nickname")) 
            UpdateSkinByUrl($"https://skin.ely.by/avatars/face/nickname/{nickname}", nickname);
        else if (currentUrl.Contains("skin.ely.by"))
            UpdateSkinByUrl($"https://mc-heads.net/avatar/{nickname}/100", nickname);
        else 
            LoadDefaultSkin();
    }

    private void LoadDefaultSkin()
    {
        try
        {
            // Используем CRAVATAR для дефолта (Steve)
            string defaultUrl = "https://cravatar.eu/helm/Steve/100";
            var bitmap = new System.Windows.Media.Imaging.BitmapImage(new Uri(defaultUrl));
            ImgSkinHead.Source = bitmap;
            BtnSkin.Tag = bitmap;
        }
        catch { }
    }

    private void EnsureStandardFolders()
    {
        string[] folders = { "mods", "resourcepacks", "shaderpacks", "screenshots", "saves" };
        foreach (var folder in folders)
        {
            var path = System.IO.Path.Combine(_path.BasePath, folder);
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SetupRamSlider();
        LoadSettings();
        LblVersion.Text = $"v{CurrentVersion}";
        
        // Запускаем загрузку модов в фоне, чтобы не заставлять пользователя ждать
        _ = FetchModListsAsync(); 
        
        await LoadVersions(); // Грузим версии (с фолбэками если списки еще не готовы)
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveSettings(); // Сохраняем при закрытии
        base.OnClosing(e);
    }

    private void SaveSettings()
    {
        try
        {
            var config = new LauncherConfig
            {
                Nickname = TxtNickname.Text,
                SelectedVersion = ComboVersions.SelectedItem?.ToString() ?? "",
                RAM = (int)SliderRAM.Value,
                LauncherBehavior = ComboLauncherBehavior.SelectedIndex,
                ShowSnapshots = CheckShowSnapshots.IsChecked ?? false,
                ShowModded = CheckShowModded.IsChecked ?? true,
                ShowOld = CheckShowOld.IsChecked ?? false,
                FastLaunch = CheckFastLaunch.IsChecked ?? true,
                SkinPath = _currentSkinPath,
                ConflictStats = _conflictStats,
                AuthType = _currentAuthType,
                ElyByToken = _session?.AccessToken,
                ElyByUsername = _session?.Username,
                ElyByUuid = _session?.UUID
            };

            string json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText("launcher_config.json", json);
        }
        catch { /* Игнорируем ошибки сохранения */ }
    }

    private void LoadSettings()
    {
        _isSettingsLoading = true; // Блокируем таймер
        try
        {
            if (File.Exists("launcher_config.json"))
            {
                string json = File.ReadAllText("launcher_config.json");
                var config = JsonConvert.DeserializeObject<LauncherConfig>(json);

                if (config != null)
                {
                    TxtNickname.Text = config.Nickname;
                    SliderRAM.Value = config.RAM;
                    ComboLauncherBehavior.SelectedIndex = config.LauncherBehavior;
                    CheckShowSnapshots.IsChecked = config.ShowSnapshots;
                    CheckShowModded.IsChecked = config.ShowModded;
                    CheckShowOld.IsChecked = config.ShowOld;
                    CheckFastLaunch.IsChecked = config.FastLaunch;
                    _currentSkinPath = config.SkinPath;
                    _conflictStats = config.ConflictStats ?? new Dictionary<string, int>();
                    _currentAuthType = config.AuthType ?? "Offline";

                    if (_currentAuthType == "ElyBy" && !string.IsNullOrEmpty(config.ElyByToken))
                    {
                        _session = new MSession(config.ElyByUsername, config.ElyByToken, config.ElyByUuid);
                        TxtNickname.Text = _session.Username;
                        BtnLogin.Content = "ELY.BY";
                        BtnLogin.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                        BtnLogin.Foreground = Brushes.White;
                        BtnLogin.FontWeight = FontWeights.Bold;
                    }
                    else if (_currentAuthType == "Microsoft")
                    {
                        BtnLogin.Content = "MICROSOFT";
                        BtnLogin.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                        BtnLogin.Foreground = Brushes.White;
                        BtnLogin.FontWeight = FontWeights.Bold;
                    }

                    // После восстановления всего — один раз обновляем скин
                    UpdateAvatarByName(TxtNickname.Text ?? "Player");

                    if (!string.IsNullOrEmpty(config.SelectedVersion))
                    {
                        foreach (var item in ComboVersions.Items)
                        {
                            if (item.ToString() == config.SelectedVersion)
                            {
                                ComboVersions.SelectedItem = item;
                                break;
                            }
                        }
                    }
                }
            }
        }
        catch { }
        finally
        {
            _isSettingsLoading = false;
        }
    }

    private void SetupRamSlider()
    {
        var memStatus = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(memStatus))
        {
            // Получаем общий объем ОЗУ в МБ (из байтов)
            int totalRamMb = (int)(memStatus.ullTotalPhys / (1024 * 1024));
            
            // Округляем до ближайшего кратного 512 вниз для слайдера
            int maxSliderRam = (totalRamMb / 512) * 512;
            
            SliderRAM.Maximum = maxSliderRam;
            
            // Если текущее значение больше максимума (мало ли), сбрасываем на 2ГБ или максимум
            if (SliderRAM.Value > maxSliderRam)
                SliderRAM.Value = Math.Min(2048, maxSliderRam);
        }
    }

    private async Task LoadVersions()
    {
        TxtStatus.Text = "Загрузка списка версий...";
        try
        {
            // Используем Task.Run для выполнения в фоновом потоке
            var allVersions = await Task.Run(async () => await _launcher.GetAllVersionsAsync());
            UpdateVersionList(allVersions);
            TxtStatus.Text = "Готов к работе";
        }
        catch (Exception ex)
        {
            // Mojang заблокирован или нет интернета.
            Console.WriteLine("[ERROR] Не удалось загрузить список версий: " + ex.Message);
            TxtStatus.Text = "Ошибка сети: Сервера Mojang недоступны. Проверьте интернет или DNS.";
        }
    }

    private void UpdateVersionList(System.Collections.IEnumerable versions)
    {
        try 
        {
            var currentSelected = ComboVersions.SelectedItem?.ToString();
            ComboVersions.Items.Clear();
            
            bool showSnapshots = CheckShowSnapshots.IsChecked ?? false;
            bool showModded = CheckShowModded.IsChecked ?? true;
            bool showOld = CheckShowOld.IsChecked ?? false;
            
            // Собираем все версии в упорядоченный список
            var orderedItems = new List<string>();
            var alreadyModded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (object item in versions)
            {
                dynamic? v = item;
                if (item is System.Collections.DictionaryEntry entry) v = entry.Value;
                if (v == null) continue;

                    string? name = v.Name;
                    string type = v.Type.ToString();
                    if (string.IsNullOrEmpty(name)) continue;

                // Фильтрация по типу
                    if (type.Equals("snapshot", StringComparison.OrdinalIgnoreCase) && !showSnapshots) continue;
                    if ((type.Equals("oldalpha", StringComparison.OrdinalIgnoreCase) || 
                         type.Equals("oldbeta", StringComparison.OrdinalIgnoreCase)) && !showOld) continue;
                    
                    bool isModded = name.Contains("forge", StringComparison.OrdinalIgnoreCase) || 
                                   name.Contains("fabric", StringComparison.OrdinalIgnoreCase) || 
                                   name.Contains("quilt", StringComparison.OrdinalIgnoreCase) || 
                                   name.Contains("neoforge", StringComparison.OrdinalIgnoreCase) || 
                                   name.Contains("optifine", StringComparison.OrdinalIgnoreCase);

                    if (isModded && !showModded) continue;

                    // Добавляем префикс для красоты
                    string displayName = name;
                if (name.Contains("neoforge", StringComparison.OrdinalIgnoreCase))     displayName = "⚡ NeoForge " + name;
                else if (name.Contains("forge", StringComparison.OrdinalIgnoreCase))   displayName = "🛠 Forge " + name;
                    else if (name.Contains("fabric", StringComparison.OrdinalIgnoreCase)) displayName = "🧵 Fabric " + name;
                    else if (name.Contains("optifine", StringComparison.OrdinalIgnoreCase)) displayName = "✨ OptiFine " + name;

                if (isModded) alreadyModded.Add(name);

                orderedItems.Add(displayName);

                // Если это чистый Release — сразу вставляем Forge, NeoForge и Fabric под ним
                if (showModded && type.Equals("release", StringComparison.OrdinalIgnoreCase) && !isModded)
                {
                    string forgeName    = "🛠 Forge " + name;
                    string neoForgeName = "⚡ NeoForge " + name;
                    string fabricName   = "🧵 Fabric " + name;
                    string optiFine     = "✨ OptiFine " + name;

                    if (!alreadyModded.Contains("forge " + name))
                        orderedItems.Add(forgeName);

                    // NeoForge: только если реально существует
                    if (IsNeoForgeVersion(name) && !alreadyModded.Contains("neoforge " + name))
                        orderedItems.Add(neoForgeName);

                    if (IsFabricVersion(name) && !alreadyModded.Contains("fabric " + name))
                        orderedItems.Add(fabricName);

                    // OptiFine: только если реально существует
                    if (IsOptiFineVersion(name))
                        orderedItems.Add(optiFine);
                }
            }

            bool any = false;
            foreach (var displayName in orderedItems)
            {
                    if (!ComboVersions.Items.Contains(displayName))
                    {
                        ComboVersions.Items.Add(displayName);
                        any = true;
                    }
                }

            if (any)
            {
                // Пытаемся восстановить прошлый выбор (с эмодзи или без)
                if (!string.IsNullOrEmpty(currentSelected))
                {
                    foreach (var item in ComboVersions.Items)
                    {
                        if (item.ToString() == currentSelected)
                        {
                            ComboVersions.SelectedItem = item;
                            break;
                        }
                    }
                }
                
                if (ComboVersions.SelectedItem == null)
                    ComboVersions.SelectedIndex = 0;
                    
                TxtStatus.Text = "Готов к работе";
            }
            else
            {
                TxtStatus.Text = "Список версий пуст";
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Ошибка отображения: " + ex.Message;
        }
    }

    // Обработчик изменения значения слайдера RAM
    private void SliderRAM_ValueChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtRAM == null) return;
        
        int ramMB = (int)SliderRAM.Value;
        TxtRAM.Text = $"{ramMB} MB";
    }

    private void VersionFilter_Changed(object sender, RoutedEventArgs e)
    {
        // Перезагружаем список версий, чтобы применить фильтры
        _ = LoadVersions();
    }

    private void BtnSelectSkin_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Skin files (*.png)|*.png",
            Title = "Выберите ваш скин"
        };

        if (dialog.ShowDialog() == true)
        {
            _currentSkinPath = dialog.FileName;
            UpdateSkinPreview(_currentSkinPath);
            SaveSettings();
        }
    }

    private void BtnResetSkin_Click(object sender, RoutedEventArgs e)
    {
        _currentSkinPath = null;
        if (BtnResetSkin != null) BtnResetSkin.Visibility = Visibility.Collapsed;
        UpdateAvatarByName(TxtNickname.Text ?? "Player");
        SaveSettings();
    }

    private void UpdateSkinPreview(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                if (BtnResetSkin != null) BtnResetSkin.Visibility = Visibility.Collapsed;
                return;
            }

            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            ImgSkinHead.Source = bitmap;
            BtnSkin.Tag = bitmap;
            
            if (BtnResetSkin != null) BtnResetSkin.Visibility = Visibility.Visible;
        }
        catch { }
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
            MainBar.Visibility = Visibility.Visible;
            BackgroundElements.Visibility = Visibility.Visible;
        }
        else
        {
            SettingsOverlay.Visibility = Visibility.Visible;
            MainBar.Visibility = Visibility.Collapsed;
            BackgroundElements.Visibility = Visibility.Collapsed;
        }
    }

    private void BtnAbout_Click(object sender, RoutedEventArgs e)
    {
        AboutOverlay.Visibility = Visibility.Visible;
    }

    private void BtnAbout_Close_Click(object sender, RoutedEventArgs e)
    {
        AboutOverlay.Visibility = Visibility.Collapsed;
    }

    private void TxtWallet_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
        {
            System.Windows.Clipboard.SetText(tb.Text);
            string oldText = tb.Text;
            string oldFg = tb.Foreground.ToString();
            tb.Foreground = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // #10b981
            var tip = new System.Windows.Controls.ToolTip { Content = "Скопировано!", PlacementTarget = tb };
            tip.IsOpen = true;
            tb.Text = "✓ Скопировано!";
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                Dispatcher.Invoke(() =>
                {
                    tb.Text = oldText;
                    tb.Foreground = new SolidColorBrush(Color.FromRgb(212, 212, 216)); // #d4d4d8
                    tip.IsOpen = false;
                });
            });
        }
    }

    private void BtnOptimize_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var memStatus = new MEMORYSTATUSEX();
            if (GlobalMemoryStatusEx(memStatus))
            {
                // Получаем общий объем ОЗУ в МБ
                long totalRamMb = (long)(memStatus.ullTotalPhys / (1024 * 1024));
                
                // Рассчитываем оптимальный RAM (50% от общего, но не менее 2ГБ и не более 8ГБ для начала)
                int optimizedRam = (int)(totalRamMb / 2);
                
                // Minecraft обычно хорошо работает с 2-4ГБ для ванилы и 4-8ГБ для модов
                if (optimizedRam < 2048) optimizedRam = 1024;
                if (optimizedRam > 8192) optimizedRam = 8192;
                
                // Округляем до ближайшего кратного 512
                optimizedRam = (optimizedRam / 512) * 512;

                // Применяем настройки
                SliderRAM.Value = optimizedRam;
                CheckFastLaunch.IsChecked = true;
                CheckShowModded.IsChecked = true;
                
                // Если ОЗУ много, можно включить и другие фишки
                if (totalRamMb > 8192)
                {
                    CheckShowSnapshots.IsChecked = false; // Обычно мешают
                }

                SaveSettings();
                MessageBox.Show($"Оптимизация завершена!\n\n" +
                                $"• Выделено памяти: {optimizedRam} MB (из {totalRamMb} MB)\n" +
                                $"• Включен быстрый запуск\n" +
                                $"• Включено отображение модов\n\n" +
                                $"Настройки сохранены.", "KVANT Optimizer", MessageBoxButton.OK, MessageBoxImage.Information);
                
                TxtStatus.Text = "Настройки оптимизированы";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка оптимизации: {ex.Message}");
        }
    }

    // Метод удален по просьбе пользователя, диагностика теперь только автоматическая


    private List<string> GetLogAnalysis()
    {
        List<string> issues = new List<string>();
        try
        {
            string logPath = System.IO.Path.Combine(_path.BasePath, "logs", "latest.log");
            if (!File.Exists(logPath)) return issues;

            using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    // Конфликты (Mixin / Несовместимость)
                    var conflictMatch = System.Text.RegularExpressions.Regex.Match(line, @"mixin .* from mod ([^ ]+) conflicts with mod ([^ ]+)|Mod '([^']+)' .* is incompatible with mod '([^']+)'", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (conflictMatch.Success)
                    {
                        string modA = conflictMatch.Groups[1].Success ? conflictMatch.Groups[1].Value : conflictMatch.Groups[3].Value;
                        string modB = conflictMatch.Groups[2].Success ? conflictMatch.Groups[2].Value : conflictMatch.Groups[4].Value;
                        modA = modA.Trim('\'', '\"');
                        modB = modB.Trim('\'', '\"');

                        string culprit = SelectWorseMod(modA, modB);
                        RecordConflict(culprit);
                        
                        string disabledFile = TryAutoFixConflict(culprit);
                        if (!string.IsNullOrEmpty(disabledFile))
                        {
                            string msg = $"🛠 Мод **{culprit}** вызывал слишком много ошибок и конфликтов, поэтому я его отключил ({disabledFile}.disabled), чтобы игра могла запуститься.";
                            issues.Add(msg);
                            // Показываем сразу, чтобы игрок видел действие
                            Dispatcher.Invoke(() => MessageBox.Show(msg.Replace("**", ""), "Авто-исправление", MessageBoxButton.OK, MessageBoxImage.Information));
                        }
                        else
                        {
                            issues.Add($"⚠️ Моды `{modA}` и `{modB}` не могут работать вместе. Попробуйте удалить один из них вручную.");
                        }
                        continue;
                    }

                    // Дубликаты
                    var dupMatch = System.Text.RegularExpressions.Regex.Match(line, @"Duplicate mod ID found: '([^']+)'", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (dupMatch.Success)
                    {
                        string modId = dupMatch.Groups[1].Value;
                        string disabledFile = TryAutoFixConflict(modId);
                        string msg = $"🚫 Найдена лишняя копия мода **{modId}**. Я её отключил ({disabledFile}.disabled).";
                        issues.Add(msg);
                        Dispatcher.Invoke(() => MessageBox.Show(msg.Replace("**", ""), "Удаление дубликата", MessageBoxButton.OK, MessageBoxImage.Information));
                        continue;
                    }

                    // Ошибки загрузки
                    if (line.Contains("Failed to create mod instance") || line.Contains("Error during mod identification"))
                    {
                        var modIdMatch = System.Text.RegularExpressions.Regex.Match(line, @"ModID: ([^ ]+)|mod ([^ ]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        if (modIdMatch.Success)
                        {
                            string mId = modIdMatch.Groups[1].Success ? modIdMatch.Groups[1].Value : modIdMatch.Groups[2].Value;
                            string disabledFile = TryAutoFixConflict(mId);
                            issues.Add($"❌ Мод `{mId}` не смог запуститься из-за внутренней ошибки и был отключен.");
                        }
                        continue;
                    }

                    // Отсутствующие файлы/зависимости
                    if (line.Contains("requires version") && line.Contains("of fabric,"))
                    {
                        issues.Add("📦 Для работы модов не хватает важного компонента: **Fabric API**. Скачайте его и положите в папку mods.");
                        continue;
                    }

                    var depMatches = System.Text.RegularExpressions.Regex.Matches(line, @"requires (?:version )?([^ ]+) or later of ([^, ]+)|requires ([^ ]+) \(([^)]+)\), which is missing!", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    foreach (System.Text.RegularExpressions.Match m in depMatches)
                    {
                        string ver = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[4].Value;
                        string mId = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                        issues.Add($"📥 Для запуска игры вам нужно вручную скачать и установить мод **{mId}** (версия {ver} или новее).");
                    }

                    // Память и Java
                    if (line.Contains("java.lang.UnsupportedClassVersionError"))
                    {
                        issues.Add("☕ У вас установлена слишком старая версия Java. Пожалуйста, обновите её до Java 21.");
                        continue;
                    }

                    if (line.Contains("java.lang.OutOfMemoryError"))
                    {
                        issues.Add("🧠 Игре не хватает оперативной памяти! Попробуйте выделить больше в настройках лаунчера.");
                        continue;
                    }
                }
            }
        }
        catch { }
        SaveSettings();
        return issues.Distinct().ToList();
    }

    private string SelectWorseMod(string modA, string modB)
    {
        _conflictStats.TryGetValue(modA, out int countA);
        _conflictStats.TryGetValue(modB, out int countB);
        
        // Отключаем того, у кого больше записей в логе конфликтов
        return countA >= countB ? modA : modB;
    }

    private void RecordConflict(string modId)
    {
        if (_conflictStats.ContainsKey(modId))
            _conflictStats[modId]++;
        else
            _conflictStats[modId] = 1;
    }

    private string TryAutoFixConflict(string modId)
    {
        try
        {
            string modsPath = System.IO.Path.Combine(_path.BasePath, "mods");
            if (!Directory.Exists(modsPath)) return "";

            var files = Directory.GetFiles(modsPath, "*.jar");
            string? targetFile = null;

            foreach (var file in files)
            {
                string fileName = System.IO.Path.GetFileName(file).ToLower();
                // Ищем по ID или части имени
                if (fileName.Contains(modId.ToLower()) || modId.ToLower().Contains(fileName.Replace(".jar", "")))
                {
                    targetFile = file;
                    break;
                }
            }

            if (targetFile != null)
            {
                string disabledPath = targetFile + ".disabled";
                if (System.IO.File.Exists(disabledPath)) System.IO.File.Delete(disabledPath);
                System.IO.File.Move(targetFile, disabledPath);
                return System.IO.Path.GetFileName(targetFile);
            }
        }
        catch { }
        return "";
    }

    private void BtnGallery_Click(object sender, RoutedEventArgs e)
    {
        HomeView.Visibility = Visibility.Collapsed;
        GalleryView.Visibility = Visibility.Visible;
        LoadScreenshots();
    }

    private void BtnServer_Click(object sender, RoutedEventArgs e)
    {
        var serverWin = new ServerWindow(TxtNickname.Text);
        serverWin.Topmost = true; // Поверх всех окон по умолчанию
        serverWin.Show();
    }

    private void CloseSubViews_Click(object sender, RoutedEventArgs e)
    {
        GalleryView.Visibility = Visibility.Collapsed;
        HomeView.Visibility = Visibility.Visible;
    }

    private async void LoadScreenshots()
    {
        ScreenshotsPanel.Children.Clear();
        var screenshotsPath = System.IO.Path.Combine(_path.BasePath, "screenshots");
        if (!Directory.Exists(screenshotsPath)) return;

        var files = Directory.GetFiles(screenshotsPath, "*.png");
        
        // Загружаем скриншоты асинхронно для ускорения
        foreach (var file in files)
        {
            await Task.Run(() => CreateScreenshotPreview(file));
        }
    }

    private void CreateScreenshotPreview(string file)
    {
        Dispatcher.Invoke(() =>
        {
            var container = new System.Windows.Controls.Grid { Margin = new Thickness(5), Width = 190, Height = 130 };
            
            var btn = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("SecondaryButtonStyle"),
                ToolTip = System.IO.Path.GetFileName(file)
            };

            // Оптимизированная загрузка изображения
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(file);
            bitmap.DecodePixelWidth = 190; // Загружаем только нужный размер
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze(); // Делаем immutable для многопоточности

            var img = new System.Windows.Controls.Image
            {
                Source = bitmap,
                Stretch = System.Windows.Media.Stretch.UniformToFill
            };
            btn.Content = img;
            btn.Click += (s, e) => OpenScreenshotViewer(file);

            // Кнопка удаления поверх
            var delBtn = new System.Windows.Controls.Button
            {
                Content = "🗑",
                Width = 30, Height = 30,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Margin = new Thickness(5),
                Background = new SolidColorBrush(Color.FromArgb(180, 239, 68, 68)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                ToolTip = "Удалить"
            };
            delBtn.Click += (s, e) => 
            {
                e.Handled = true;
                DeleteScreenshot(file);
            };

            // Кнопка копирования поверх
            var copyBtn = new System.Windows.Controls.Button
            {
                Content = "📋",
                Width = 30, Height = 30,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment = System.Windows.VerticalAlignment.Top,
                Margin = new Thickness(0, 5, 40, 0), // Отступ справа 40px (слева от кнопки удаления)
                Background = new SolidColorBrush(Color.FromArgb(180, 16, 185, 129)),
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                ToolTip = "Копировать"
            };
            copyBtn.Click += (s, e) => 
            {
                e.Handled = true;
                CopyScreenshotToClipboard(file);
            };

            container.Children.Add(btn);
            container.Children.Add(copyBtn);
            container.Children.Add(delBtn);
            ScreenshotsPanel.Children.Add(container);
        });
    }

    private void OpenScreenshotViewer(string path)
    {
        _currentViewedScreenshot = path;
        TxtScreenshotName.Text = System.IO.Path.GetFileName(path);
        
        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        bitmap.BeginInit();
        bitmap.UriSource = new Uri(path);
        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmap.EndInit();
        
        ImgViewer.Source = bitmap;
        SliderZoom.Value = 1.0;
        ScreenshotViewerOverlay.Visibility = Visibility.Visible;
        UpdateViewerSize();
    }

    private void BtnCloseViewer_Click(object sender, RoutedEventArgs e)
    {
        ScreenshotViewerOverlay.Visibility = Visibility.Collapsed;
        ImgViewer.Source = null;
        _currentViewedScreenshot = null;
    }

    private void SliderZoom_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateViewerSize();
    }

    private void ScreenShotViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateViewerSize();
    }

    private void UpdateViewerSize()
    {
        if (ImgViewer == null || ScreenShotViewer == null || SliderZoom == null) return;
        
        double zoom = SliderZoom.Value;
        // Базовый размер - это размер ScrollViewer минус отступы
        double baseW = ScreenShotViewer.ActualWidth - 100;
        double baseH = ScreenShotViewer.ActualHeight - 100;

        if (baseW > 0) ImgViewer.Width = baseW * zoom;
        if (baseH > 0) ImgViewer.Height = baseH * zoom;
    }

    private void BtnViewDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentViewedScreenshot))
        {
            DeleteScreenshot(_currentViewedScreenshot);
            BtnCloseViewer_Click(null!, null!);
        }
    }

    private void BtnViewCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentViewedScreenshot))
        {
            try
            {
                // Загружаем изображение в буфер обмена
                var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(_currentViewedScreenshot);
                bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bitmap.EndInit();

                System.Windows.Clipboard.SetImage(bitmap);
                TxtStatus.Text = $"Скриншот скопирован в буфер обмена: {System.IO.Path.GetFileName(_currentViewedScreenshot)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при копировании: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void DeleteScreenshot(string path)
    {
        var result = MessageBox.Show($"Вы уверены, что хотите удалить этот скриншот?\n\n{System.IO.Path.GetFileName(path)}", 
                                     "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        
        if (result == MessageBoxResult.Yes)
        {
            try
            {
                // Очищаем источник если файл открыт в превью
                if (_currentViewedScreenshot == path) ImgViewer.Source = null;
                
                System.IO.File.Delete(path);
                LoadScreenshots(); // Обновляем галерею
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при удалении: {ex.Message}");
            }
        }
    }

    private void CopyScreenshotToClipboard(string path)
    {
        try
        {
            var bitmap = new System.Windows.Media.Imaging.BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            System.Windows.Clipboard.SetImage(bitmap);
            TxtStatus.Text = $"Скриншот скопирован: {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при копировании: {ex.Message}", "Ошибка");
        }
    }


    private void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        if (_session != null || _currentAuthType != "Offline")
        {
            // Уже залогинен — показываем панель аккаунта
            TxtLoggedInName.Text = _session?.Username ?? TxtNickname.Text;
            TxtLoggedInType.Text = _currentAuthType switch
            {
                "Microsoft" => "Лицензионный аккаунт Microsoft",
                "ElyBy" => "Аккаунт Ely.by",
                _ => "Локальный режим"
            };
            AuthChoicePanel.Visibility = Visibility.Collapsed;
            AccountPanel.Visibility = Visibility.Visible;
        }
        else
        {
            // Не залогинен — показываем выбор способа входа
            AccountPanel.Visibility = Visibility.Collapsed;
            AuthChoicePanel.Visibility = Visibility.Visible;
        }
        AuthOverlay.Visibility = Visibility.Visible;
    }

    private void BtnSwitchAccount_Click(object sender, RoutedEventArgs e)
    {
        // Переключаем на панель выбора
        AccountPanel.Visibility = Visibility.Collapsed;
        AuthChoicePanel.Visibility = Visibility.Visible;
    }

    private void BtnLogout_Click(object sender, RoutedEventArgs e)
    {
        _session = null;
        _currentAuthType = "Offline";
        BtnLogin.Content = "ВОЙТИ";
        BtnLogin.Background = Brushes.Transparent;
        BtnLogin.Foreground = new SolidColorBrush(Color.FromRgb(161, 161, 170)); // Сброс на серый цвет (#a1a1aa)
        TxtStatus.Text = "Вышли из аккаунта";
        AuthOverlay.Visibility = Visibility.Collapsed;
        AccountPanel.Visibility = Visibility.Collapsed;
        AuthChoicePanel.Visibility = Visibility.Visible;
        SaveSettings();
        UpdateAvatarByName(TxtNickname.Text ?? "Player");
    }

    private void BtnLoginClose_Click(object sender, RoutedEventArgs e)
    {
        AuthOverlay.Visibility = Visibility.Collapsed;
        // Сбрасываем панели в исходное состояние
        AccountPanel.Visibility = Visibility.Collapsed;
        AuthChoicePanel.Visibility = Visibility.Visible;
    }

    private async void BtnMicrosoftAuth_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            TxtStatus.Text = "Авторизация Microsoft...";
            var loginHandler = JELoginHandlerBuilder.BuildDefault();
            var session = await loginHandler.AuthenticateInteractively();
            
            _session = session;
            _session.UserType = "msa"; // Критически важно для 1.16.5+
            _currentAuthType = "Microsoft";
            TxtNickname.Text = session.Username;
            BtnLogin.Content = "MICROSOFT";
            BtnLogin.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // #10b981
            BtnLogin.Foreground = Brushes.White;
            BtnLogin.FontWeight = FontWeights.Bold;
            
            AuthOverlay.Visibility = Visibility.Collapsed;
            UpdateAvatarByName(session.Username ?? "Player");
            TxtStatus.Text = $"Вход выполнен: {session.Username}";
            SaveSettings(); // Сохраняем сессию Microsoft
        }
        catch (OperationCanceledException)
        {
            // Пользователь сам закрыл окно входа — это нормально
            TxtStatus.Text = "Готов к работе";
            AuthOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase) ||
                                    ex.Message.Contains("отмен", StringComparison.OrdinalIgnoreCase))
        {
            TxtStatus.Text = "Готов к работе";
            AuthOverlay.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка MS Auth: {ex.Message}");
            TxtStatus.Text = "Ошибка входа Microsoft";
        }
    }

    private void BtnShowElyLogin_Click(object sender, RoutedEventArgs e)
    {
        var loginWin = new ElyLoginWindow();
        loginWin.Owner = this;
        if (loginWin.ShowDialog() == true)
        {
            _session = loginWin.Session;
            _currentAuthType = "ElyBy";
            
            // Если сервер вернул ник, обновляем его в поле ввода
            if (!string.IsNullOrEmpty(_session?.Username))
            {
                TxtNickname.Text = _session.Username;
            }
            BtnLogin.Content = "ELY.BY";
            BtnLogin.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // #10b981
            BtnLogin.Foreground = Brushes.White;
            BtnLogin.FontWeight = FontWeights.Bold;
            
            AuthOverlay.Visibility = Visibility.Collapsed;
            UpdateAvatarByName(_session?.Username ?? "Player");
            TxtStatus.Text = $"Вход выполнен через Ely.by: {_session?.Username}";
            SaveSettings(); // Сразу сохраняем
        }
    }

    private void BtnOfflineAuth_Click(object sender, RoutedEventArgs e)
    {
        _session = null;
        _currentAuthType = "Offline";
        BtnLogin.Content = "ВОЙТИ";
        BtnLogin.Background = Brushes.Transparent;
        TxtStatus.Text = "Переключено на локальный режим";
        AuthOverlay.Visibility = Visibility.Collapsed;
        UpdateAvatarByName(TxtNickname.Text ?? "Player");
    }

    private void BtnFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_path.BasePath))
        {
            Process.Start("explorer.exe", _path.BasePath);
        }
        else
        {
            Directory.CreateDirectory(_path.BasePath);
            Process.Start("explorer.exe", _path.BasePath);
        }
    }

    private void BtnFolderMenu_Click(object sender, RoutedEventArgs e)
    {
        // Открываем контекстное меню при клике на кнопку
        BtnFolderMenu.ContextMenu.PlacementTarget = BtnFolderMenu;
        BtnFolderMenu.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        BtnFolderMenu.ContextMenu.IsOpen = true;
    }

    private void OpenScreenshotsFolder_Click(object sender, RoutedEventArgs e)
    {
        var screenshotsPath = System.IO.Path.Combine(_path.BasePath, "screenshots");
        EnsureAndOpenFolder(screenshotsPath, "Скриншоты");
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        var modsPath = System.IO.Path.Combine(_path.BasePath, "mods");
        EnsureAndOpenFolder(modsPath, "Моды");
    }

    private void OpenShadersFolder_Click(object sender, RoutedEventArgs e)
    {
        var shadersPath = System.IO.Path.Combine(_path.BasePath, "shaderpacks");
        EnsureAndOpenFolder(shadersPath, "Шейдеры");
    }


    private void EnsureAndOpenFolder(string path, string folderName)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            TxtStatus.Text = $"Папка {folderName} создана";
        }
        else
        {
            TxtStatus.Text = $"Открыта папка: {folderName}";
        }
        Process.Start("explorer.exe", path);
    }

    private async void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        // Если уже идет запуск - кнопка работает как "ОТМЕНА"
        if (BtnPlay.Content.ToString() == "ОТМЕНА")
        {
            _launchCts?.Cancel();
            TxtStatus.Text = "Отмена запуска...";
            BtnPlay.IsEnabled = false; // Временно выключаем, пока идет процесс отмены
            return;
        }

        // Обнуляем попытки при новом нажатии
        _restartCount = 0;
        
        var nick = TxtNickname.Text;
        var versionText = ComboVersions.SelectedItem?.ToString();

        if (string.IsNullOrWhiteSpace(nick))
        {
            MessageBox.Show("Введите никнейм!");
            return;
        }

        if (string.IsNullOrWhiteSpace(versionText))
        {
            MessageBox.Show("Выберите версию!");
            return;
        }

        // Меняем текст на ОТМЕНА
        BtnPlay.Content = "ОТМЕНА";
        SaveSettings();

        _launchCts = new CancellationTokenSource();

        try
        {
            string realVersionName = versionText;
            bool isForge    = versionText.StartsWith("🛠 Forge ");
            bool isNeoForge = versionText.StartsWith("⚡ NeoForge ");
            bool isFabric   = versionText.StartsWith("🧵 Fabric ");
            bool isOptiFine = versionText.StartsWith("✨ OptiFine ");

            if (isForge || isFabric || isNeoForge)
            {
                // Определяем длину префикса: "🛠 Forge " = 9 символов (emoji+пробел+слово+пробел)
                int prefixLen = isNeoForge ? 10 : 9; // "⚡ NeoForge " длиннее
                string mcVersion = versionText.Substring(prefixLen).Trim();
                
                var localVersions = await _launcher.GetAllVersionsAsync();
                bool exists = false;
                string modTag = isForge ? "forge" : (isNeoForge ? "neoforge" : "fabric");
                foreach (var v in localVersions)
                {
                    if (v.Name.Contains(mcVersion, StringComparison.OrdinalIgnoreCase) && 
                        v.Name.Contains(modTag, StringComparison.OrdinalIgnoreCase))
                    {
                        realVersionName = v.Name;
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    if (isForge)
                    {
                        await InstallForge(mcVersion);
                    }
                    else if (isNeoForge)
                    {
                        await InstallNeoForge(mcVersion);
                    }
                    else
                    {
                        await InstallFabric(mcVersion);
                    }
                    
                    await LoadVersions();
                    var updatedVersions = await _launcher.GetAllVersionsAsync();
                    foreach (var v in updatedVersions)
                    {
                        if (v.Name.Contains(mcVersion, StringComparison.OrdinalIgnoreCase) && 
                            v.Name.Contains(modTag, StringComparison.OrdinalIgnoreCase))
                        {
                            realVersionName = v.Name;
                            break;
                        }
                    }
                }
            }
            else if (isOptiFine)
            {
                // "✨ OptiFine 1.20.4" -> "1.20.4"
                string mcVersion = versionText.Substring("✨ OptiFine ".Length).Trim();
                realVersionName = await EnsureOptiFine(mcVersion);
                if (string.IsNullOrEmpty(realVersionName))
                {
                    MessageBox.Show(
                        "Не удалось установить OptiFine для этой версии.\n" +
                        "Возможно, OptiFine ещё не вышел для неё, или нет интернета.",
                        "OptiFine", MessageBoxButton.OK, MessageBoxImage.Warning);
                    ResetPlayButton();
                    return;
                }
            }

            await LaunchGame(nick, realVersionName, _launchCts.Token);
        }
        catch (OperationCanceledException)
        {
            TxtStatus.Text = "Запуск отменен";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка: {ex.Message}");
            TxtStatus.Text = "Ошибка";
        }
        finally
        {
            ResetPlayButton();
        }
    }

    private void ResetPlayButton()
    {
        BtnPlay.Content = "ЗАПУСТИТЬ";
        BtnPlay.IsEnabled = true;
        _launchCts?.Dispose();
        _launchCts = null;
    }

    private async Task InstallFabric(string mcVersion)
    {
        try 
        {
            TxtStatus.Text = $"Установка Fabric для {mcVersion}...";
            
            using var client = new HttpClient();
            // 1. Получаем список загрузчиков для этой версии игры
            string loadersUrl = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}";
            var response = await client.GetStringAsync(loadersUrl);
            var loaders = JsonConvert.DeserializeObject<dynamic[]>(response);
            
            if (loaders == null || loaders.Length == 0)
                throw new Exception("Не удалось найти Fabric для этой версии игры.");
                
            string loaderVersion = loaders[0].loader.version;
            
            // 2. Получаем полный профиль версии
            string profileUrl = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json";
            var profileJson = await client.GetStringAsync(profileUrl);
            
            // 3. Сохраняем в папку versions
            string fabricVersionName = $"fabric-loader-{loaderVersion}-{mcVersion}";
            string versionPath = System.IO.Path.Combine(_path.BasePath, "versions", fabricVersionName);
            Directory.CreateDirectory(versionPath);
            
            string jsonFileName = System.IO.Path.Combine(versionPath, $"{fabricVersionName}.json");
            await File.WriteAllTextAsync(jsonFileName, profileJson);
            
            TxtStatus.Text = $"Fabric {mcVersion} успешно установлен!";
        }
        catch (Exception ex)
        {
            throw new Exception($"Ошибка при установке Fabric: {ex.Message}");
        }
    }

    private async Task InstallNeoForge(string mcVersion)
    {
        TxtStatus.Text = $"Поиск NeoForge для {mcVersion}...";
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
            client.Timeout = TimeSpan.FromMinutes(5);

            bool is1201 = mcVersion == "1.20.1";
            string artifact = is1201 ? "forge" : "neoforge";
            string group    = is1201 ? "net/neoforged/forge" : "net/neoforged/neoforge";
            string apiUrl   = $"https://maven.neoforged.net/api/maven/versions/releases/{group.Replace('/', '.')}";

            // 1. Получаем список релизных версий
            string json = await client.GetStringAsync(
                $"https://maven.neoforged.net/api/maven/versions/releases/{group}");
            var data = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json);
            var allVers = data?["versions"]?.ToObject<List<string>>() ?? new List<string>();

            // NeoForge версии для 1.20.2+ имеют вид "20.2.xxx", для 1.20.1 — "1.20.1-47.x.x"
            string prefix = is1201 ? $"{mcVersion}-" : mcVersion.Substring(2) + ".";
            string? loaderVer = allVers
                .Where(v => v.StartsWith(prefix))
                .Where(v => !v.Contains("beta") && !v.Contains("alpha"))
                .OrderByDescending(v => v)
                .FirstOrDefault();

            if (string.IsNullOrEmpty(loaderVer))
                throw new Exception($"NeoForge ещё не вышел для Minecraft {mcVersion}.");

            // 2. Скачиваем installer.jar
            string installerUrl = $"https://maven.neoforged.net/releases/{group}/{loaderVer}/{artifact}-{loaderVer}-installer.jar";
            TxtStatus.Text = $"Загрузка NeoForge {loaderVer} installer...";
            byte[] installerBytes = await client.GetByteArrayAsync(installerUrl);

            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KVANT_NEOFORGE");
            Directory.CreateDirectory(tempDir);
            string installerPath = System.IO.Path.Combine(tempDir, $"neoforge-{loaderVer}-installer.jar");
            await File.WriteAllBytesAsync(installerPath, installerBytes);

            // 3. Запускаем: java -jar installer.jar --installClient <mcDir>
            // Используем ту же Java, что и для игры
            string javaExe = GetInstallerJavaPath();
            TxtStatus.Text = $"Установка NeoForge {loaderVer}...";

            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName  = javaExe,
                    Arguments = $"-jar \"{installerPath}\" --installClient \"{_path.BasePath}\"",
                    WorkingDirectory      = tempDir,
                    UseShellExecute       = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow        = true
                }
            };
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) Dispatcher.Invoke(() => TxtStatus.Text = e.Data.Length > 80 ? e.Data[..80] : e.Data); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            await proc.WaitForExitAsync();

            if (proc.ExitCode != 0)
                throw new Exception($"Установщик NeoForge завершился с кодом {proc.ExitCode}.");

            TxtStatus.Text = $"NeoForge {loaderVer} успешно установлен!";
        }
        catch (Exception ex)
        {
            throw new Exception($"Ошибка при установке NeoForge: {ex.Message}");
        }
    }

    // Возвращает реальное имя установленной версии OptiFine, или пустую строку если не удалось
    private async Task<string> EnsureOptiFine(string mcVersion)
    {
        TxtStatus.Text = $"Поиск OptiFine для {mcVersion}...";
        try
        {
            // Сначала проверяем — может OptiFine уже установлен?
            var localVersions = await _launcher.GetAllVersionsAsync();
            foreach (var v in localVersions)
            {
                if (v.Name.Contains("OptiFine", StringComparison.OrdinalIgnoreCase) &&
                    v.Name.Contains(mcVersion, StringComparison.OrdinalIgnoreCase))
                    return v.Name;
            }

            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            client.Timeout = TimeSpan.FromMinutes(5);

            // Перебираем суффиксы от новых к старым
            var suffixes = new[] { "J2","J1","J","I9","I8","I7","I6","I5","I4","I3","I2","I1","I","H9","H8","H7","H6","H5" };
            string? downloadUrl = null;
            string? jarName = null;

            // Зеркало 1: Официальный adloadx
            foreach (var s in suffixes)
            {
                string name = $"OptiFine_{mcVersion}_HD_U_{s}.jar";
                string url  = $"https://optifine.net/adloadx?f={name}";
                try
                {
                    var r = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                    if (r.IsSuccessStatusCode && r.Content.Headers.ContentLength > 100_000)
                    { downloadUrl = url; jarName = name; break; }
                }
                catch { }
            }

            // Зеркало 2: bmclapi (популярное китайское зеркало, работает глобально)
            if (downloadUrl == null)
            {
                foreach (var s in suffixes)
                {
                    string name = $"OptiFine_{mcVersion}_HD_U_{s}.jar";
                    string url  = $"https://bmclapi2.bangbang93.com/optifine/{mcVersion}/HD_U/{s}";
                    try
                    {
                        var r = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                        if (r.IsSuccessStatusCode)
                        { downloadUrl = url; jarName = name; break; }
                    }
                    catch { }
                }
            }

            if (downloadUrl == null || jarName == null)
                throw new Exception($"Не удалось найти OptiFine для Minecraft {mcVersion}.");

            TxtStatus.Text = $"Загрузка {jarName}...";
            byte[] bytes = await client.GetByteArrayAsync(downloadUrl);
            if (bytes.Length < 100_000)
                throw new Exception("Скачан пустой файл OptiFine.");

            string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KVANT_OPTIFINE");
            Directory.CreateDirectory(tempDir);
            string jarPath = System.IO.Path.Combine(tempDir, jarName);
            await File.WriteAllBytesAsync(jarPath, bytes);

            // Запускаем: java -jar OptiFine.jar <mcDir> — это официальный headless-режим
            string javaExe = GetInstallerJavaPath();
            TxtStatus.Text = "Установка OptiFine...";
            var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName   = javaExe,
                    Arguments  = $"-jar \"{jarPath}\" \"{_path.BasePath}\"",
                    WorkingDirectory       = tempDir,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                }
            };
            proc.Start();
            string outTxt = await proc.StandardOutput.ReadToEndAsync();
            string errTxt = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            // Проверяем появление версии
            await LoadVersions();
            var updated = await _launcher.GetAllVersionsAsync();
            foreach (var v in updated)
            {
                if (v.Name.Contains("OptiFine", StringComparison.OrdinalIgnoreCase) &&
                    v.Name.Contains(mcVersion, StringComparison.OrdinalIgnoreCase))
                {
                    TxtStatus.Text = $"OptiFine {v.Name} установлен!";
                    return v.Name;
                }
            }

            // Если тихий режим не сработал — открываем GUI-установщик
            Dispatcher.Invoke(() =>
            {
                var res = MessageBox.Show(
                    $"Тихая установка OptiFine не сработала.\n" +
                    $"Сейчас откроется установщик — просто нажмите \"Install\" и он сам найдёт папку.\n\n" +
                    $"После установки снова нажмите ЗАПУСТИТЬ.",
                    "OptiFine", MessageBoxButton.OK, MessageBoxImage.Information);
                Process.Start(new ProcessStartInfo { FileName = javaExe, Arguments = $"-jar \"{jarPath}\"", UseShellExecute = false });
            });
            return "";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = "Ошибка OptiFine: " + ex.Message;
            return "";
        }
    }

    private string _javaRuntimeDir => System.IO.Path.Combine(_path.BasePath, "java-runtime");

    /// <summary>Возвращает путь к java.exe для запуска инсталляторов (использует Java из CmlLib или системную)</summary>
    private string GetInstallerJavaPath()
    {
        // Пробуем найти ту же Java, что CmlLib использовал последний раз
        if (!string.IsNullOrEmpty(_cachedJavaPath) && File.Exists(_cachedJavaPath))
            return _cachedJavaPath;

        // Ищем java.exe в стандартных местах
        var candidates = new[]
        {
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            System.IO.Path.Combine(_path.BasePath, "..", "runtime"),  // CmlLib скачивает Java сюда
            _javaRuntimeDir, // Наша скачанная Java
        };
        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;
            var found = Directory.GetFiles(dir, "java.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (found != null) return found;
        }
        return "java"; // Фоллбэк: системный PATH
    }

    /// <summary>Определяет, какая версия Java нужна для Minecraft</summary>
    private int GetRequiredJavaVersion(string mcVersion)
    {
        if (string.IsNullOrEmpty(mcVersion)) return 8;

        // Minecraft 1.17+ требует Java 16+
        // Minecraft 1.18+ требует Java 17+
        // Minecraft 1.20.5+ требует Java 21+
        if (mcVersion.Contains("1.20.5") || mcVersion.Contains("1.20.6") || mcVersion.Contains("1.21") || mcVersion.Contains("1.22"))
            return 21;
        if (mcVersion.Contains("1.18") || mcVersion.Contains("1.19") || mcVersion.Contains("1.20"))
            return 17;
        if (mcVersion.Contains("1.17"))
            return 16;

        return 8; // Все версии до 1.17 работают на Java 8
    }

    /// <summary>Скачивает Java с Adoptium если нужной версии нет</summary>
    private async Task<string> EnsureJavaAvailable(int requiredVersion)
    {
        // Сначала проверяем системную Java
        string existingJava = FindJavaWithVersion(requiredVersion);
        if (!string.IsNullOrEmpty(existingJava))
            return existingJava;

        // Java не найдена — скачиваем
        TxtStatus.Text = $"Скачивание Java {requiredVersion}...";

        try
        {
            string downloadUrl = $"https://api.adoptium.net/v3/binary/latest/{requiredVersion}/ga/windows/x64/jdk/hotspot/normal/eclipse";
            string zipPath = System.IO.Path.Combine(_path.BasePath, $"java-{requiredVersion}.zip");
            string extractDir = System.IO.Path.Combine(_javaRuntimeDir, $"java-{requiredVersion}");

            if (!Directory.Exists(extractDir))
            {
                Directory.CreateDirectory(_javaRuntimeDir);

                // Скачиваем
                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromMinutes(10);
                    var response = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    long? totalBytes = response.Content.Headers.ContentLength;
                    using (var stream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                    {
                        byte[] buffer = new byte[8192];
                        long downloaded = 0;
                        int bytesRead;

                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead);
                            downloaded += bytesRead;

                            if (totalBytes > 0)
                            {
                                int progress = (int)(downloaded * 100 / totalBytes.Value);
                                TxtStatus.Text = $"Скачивание Java {requiredVersion}... {progress}%";
                            }
                        }
                    }
                }

                // Распаковываем
                TxtStatus.Text = $"Распаковка Java {requiredVersion}...";
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir);

                // Удаляем архив
                try { File.Delete(zipPath); } catch { }
            }

            // Находим java.exe в распакованной папке
            string javaExe = Directory.GetFiles(extractDir, "java.exe", SearchOption.AllDirectories).FirstOrDefault() ?? "";
            if (!string.IsNullOrEmpty(javaExe))
            {
                TxtStatus.Text = $"Java {requiredVersion} установлена!";
                return javaExe;
            }
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Ошибка скачивания Java: {ex.Message}";
        }

        return "java"; // Фоллбэк
    }

    /// <summary>Ищет Java нужной версии в стандартных папках</summary>
    private string FindJavaWithVersion(int requiredVersion)
    {
        var candidates = new[]
        {
            _javaRuntimeDir,
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            System.IO.Path.Combine(_path.BasePath, "..", "runtime"),
        };

        foreach (var dir in candidates)
        {
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                var javaFiles = Directory.GetFiles(dir, "java.exe", SearchOption.AllDirectories);
                foreach (var java in javaFiles)
                {
                    int ver = GetJavaVersion(java);
                    if (ver >= requiredVersion)
                        return java;
                }
            }
        }
        return "";
    }

    private async Task InstallForge(string mcVersion)
    {
        TxtStatus.Text = $"Установка Forge для {mcVersion}... (это может занять несколько минут)";
        try
        {
            // CmlLib автоматически скачивает и запускает официальный Forge-установщик
            var forgeInstaller = new ForgeInstaller(_launcher);
            var versionName = await forgeInstaller.Install(mcVersion);
            TxtStatus.Text = $"Forge {versionName} установлен!";
        }
        catch (Exception ex)
        {
            throw new Exception($"Ошибка при установке Forge: {ex.Message}");
        }
    }



    private async Task LaunchGame(string nickname, string versionName, CancellationToken cancellationToken = default)
    {
        var session = _session ?? MSession.CreateOfflineSession(nickname);
        int selectedRAM = (int)SliderRAM.Value;
        
        // Убеждаемся, что инжектор для скинов загружен (если не Microsoft)
        if (_currentAuthType != "Microsoft")
        {
            TxtStatus.Text = "Проверка системы скинов...";
            await EnsureAuthlibInjector();
        }

        // ПЫТАЕМСЯ ПОЛУЧИТЬ РЕАЛЬНЫЙ UUID ОТ ELY.BY (Для скинов в мультиплеере)
        if (_currentAuthType == "Offline" || _currentAuthType == "ElyBy")
        {
            TxtStatus.Text = "Синхронизация профиля...";
            string realUuid = await GetElyByUuid(nickname);
            if (!string.IsNullOrEmpty(realUuid) && realUuid.Length >= 32)
            {
                // Minecraft ожидает UUID с дефисами. Если их нет - добавим.
                if (!realUuid.Contains("-")) {
                    realUuid = $"{realUuid.Substring(0,8)}-{realUuid.Substring(8,4)}-{realUuid.Substring(12,4)}-{realUuid.Substring(16,4)}-{realUuid.Substring(20)}";
                }
                session = new MSession(nickname, session.AccessToken ?? "offline", realUuid);
            }
        }

        // ХАК: Принудительно ставим тип "msa" через Reflection
        try
        {
            var type = session.GetType();
            var userTypeProp = type.GetProperty("UserType");
            if (userTypeProp != null) userTypeProp.SetValue(session, "msa");
            
            // Ставим XUID только если его нет, чтобы не перетереть реальный (если есть)
            var xuidProp = type.GetProperty("Xuid");
            if (xuidProp != null && string.IsNullOrEmpty(session.Xuid)) 
                xuidProp.SetValue(session, "1234567890");
        }
        catch { /* Если свойств нет, игра попробует использовать JVM-флаги */ }

        // Убеждаемся, что инжектор для скинов загружен (если не Microsoft)
        if (_currentAuthType != "Microsoft")
        {
            await EnsureAuthlibInjector();
        
            string injectorPath = System.IO.Path.Combine(_path.BasePath, "authlib-injector.jar");
            if (!System.IO.File.Exists(injectorPath))
            {
                 MessageBox.Show("Критическая ошибка: Не удалось загрузить систему скинов. Проверьте интернет или антивирус.", "Ошибка KVANT");
                 BtnPlay.IsEnabled = true;
                 return;
            }
        }

        var launchOption = new MLaunchOption
        {
            MaximumRamMb = selectedRAM,
            Session = session
        };

        // Находим путь к Java, который будет использовать CmlLib
        var mcVersion = await _launcher.GetVersionAsync(versionName);

        // Определяем какая Java нужна и скачиваем если нужно
        int requiredJava = GetRequiredJavaVersion(versionName);
        string javaPath = await EnsureJavaAvailable(requiredJava);
        launchOption.JavaPath = javaPath;

        // ПРОВЕРКА JAVA
        int javaVer = GetJavaVersion(javaPath);

        if (javaVer < requiredJava)
        {
            var res = MessageBox.Show(
                $"Minecraft {versionName} требует Java {requiredJava}.\n" +
                $"У вас обнаружена Java {javaVer}.\n\n" +
                $"Авто-скачивание не удалось. Установите Java {requiredJava} вручную.\n" +
                $"Продолжить запуск на свой страх и риск?",
                "Неподходящая версия Java",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            
            if (res == MessageBoxResult.No) return;
        }

        // Устанавливаем JVM аргументы с учетом найденной версии Java
        launchOption.ExtraJvmArguments = GetJvmArguments(selectedRAM, javaVer);

        while (_restartCount < 5)
        {
            TxtStatus.Text = "Подготовка файлов...";
            await _launcher.InstallAsync(versionName, cancellationToken: cancellationToken);

            // УЛЬТИМАТИВНЫЙ ФИКС СКИНОВ: Устанавливаем и настраиваем CustomSkinLoader для модов
            await EnsureCustomSkinLoader(versionName);
            
            // "ЖЁСТКИЙ РЕЖИМ": Очистка кэша (чтобы старые скины Стива не висели)
            ClearMinecraftCache();
            // PatchVersionJsonForthlib(versionName); // УДАЛЕНО: Это мешает работе authlib-injector
            
            TxtStatus.Text = "Запуск Minecraft...";
            var process = await _launcher.CreateProcessAsync(versionName, launchOption);
            
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow = true;

            StringBuilder gameOutput = new StringBuilder();
            process.OutputDataReceived += (s, e) => { if (e.Data != null) gameOutput.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) gameOutput.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            TxtStatus.Text = "Игра запущена!";
            bool killedByWatchdog = false;

            // WATCHDOG: Мгновенно убиваем процесс при обнаружении критической ошибки Fabric
            _ = Task.Run(async () =>
            {
                while (!process.HasExited)
                {
                    string output = gameOutput.ToString();
                    if (output.Contains("Incompatible mods found!") || 
                        output.Contains("requires version") && output.Contains("which is missing!"))
                    {
                        killedByWatchdog = true;
                        // Даем логам время "дотечь", чтобы мы могли их прочитать
                        await Task.Delay(1500); 
                        if (!process.HasExited) { try { process.Kill(); } catch { } }
                        break;
                    }
                    await Task.Delay(500);
                }
            });

            await process.WaitForExitAsync();

            // Если вышли без ошибок — цикл окончен
            if (process.ExitCode == 0 && !killedByWatchdog) 
            {
                TxtStatus.Text = "Игра закрыта";
                break;
            }

            // АНАЛИЗ И ИСПРАВЛЕНИЕ
            var issues = GetLogAnalysis();
            string fullOutput = gameOutput.ToString();
            
            var consoleDepMatch = System.Text.RegularExpressions.Regex.Matches(fullOutput, @"requires (?:version )?([^ ]+) or later of ([^, ]+)|requires ([^ ]+) \(([^)]+)\), which is missing!", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in consoleDepMatch)
            {
                string ver = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[4].Value;
                string mId = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
                issues.Add($"📥 Для запуска игры вам нужно вручную скачать и установить мод **{mId}** (версия {ver} или новее).");
            }

            if (issues.Count > 0)
            {
                _restartCount++;
                string summary = string.Join("\n", issues.Distinct());
                TxtStatus.Text = $"Авто-исправление: Попытка {_restartCount}/5...";

                // Если есть только информация о нехватке модов (без авто-отключений), то просто показываем список и прерываемся
                bool onlyMissing = issues.All(i => i.Contains("нужно вручную скачать"));
                bool hasFixes = summary.Contains("отключен") || summary.Contains("дубликат");

                if (hasFixes)
                {
                    // Если мы что-то отключили, пробуем перезапустить
                    await Task.Delay(2000); 
                    continue; 
                }
                else
                {
                    // Если только нехватка модов — показываем список и останавливаем цикл
                    MessageBox.Show($"Для запуска игры требуется ваше вмешательство:\n\n{summary}", "Требуется установка модов", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
                }
            }
            else
            {
                // Если ошибок не найдено, выводим сырой лог ошибки из консоли
                string lastLines = fullOutput.Length > 500 ? "..." + fullOutput.Substring(fullOutput.Length - 500) : fullOutput;
                MessageBox.Show($"Игра закрылась с кодом {process.ExitCode}.\nЛоги не содержат известных ошибок.\n\n**ПОСЛЕДНИЕ СТРОКИ КОНСОЛИ:**\n{lastLines}", "Критический вылет");
                break;
            }
        }

        if (_restartCount >= 5)
        {
            var finalIssues = GetLogAnalysis();
            string report = finalIssues.Count > 0 ? string.Join("\n", finalIssues) : "Неизвестная ошибка (проверьте новейшие моды).";
            MessageBox.Show($"Тут мои полномочия всё...\nИгра не запустилась даже после 5 попыток исправления.\n\n**ФИНАЛЬНЫЙ ОТЧЕТ:**\n{report}", "Предел попыток", MessageBoxButton.OK, MessageBoxImage.Stop);
            _restartCount = 0;
        }

        int behavior = 0;
        Dispatcher.Invoke(() => behavior = ComboLauncherBehavior.SelectedIndex);
        if (behavior == 0) Application.Current.Shutdown();
    }

    private void MinimizeToTray()
    {
        // Создаем иконку в трее
        _trayIcon = new NotifyIcon
        {
            Visible = true,
            Text = "KVANT Launcher - Игра запущена"
        };

        // Тщательно загружаем иконку
        try
        {
            // 1. Пытаемся из ресурсов
            var iconUri = new Uri("pack://application:,,,/logo_green.png");
            var info = System.Windows.Application.GetResourceStream(iconUri);
            if (info != null)
            {
                using (var stream = info.Stream)
                using (var bitmap = new System.Drawing.Bitmap(stream))
                {
                    _trayIcon.Icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon());
                }
            }
            // 2. Пытаемся из файла на диске (строго по пути установки)
            else 
            {
                string iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo_green.png");
                // Также пробуем .ico если он есть
                string icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logo.ico");

                if (System.IO.File.Exists(icoPath))
                {
                    _trayIcon.Icon = new System.Drawing.Icon(icoPath);
                }
                else if (System.IO.File.Exists(iconPath))
                {
                    using (var bitmap = new System.Drawing.Bitmap(iconPath))
                    {
                        _trayIcon.Icon = System.Drawing.Icon.FromHandle(bitmap.GetHicon());
                    }
                }
                else
                {
                    _trayIcon.Icon = System.Drawing.SystemIcons.Application;
                }
            }
        }
        catch
        {
            _trayIcon.Icon = System.Drawing.SystemIcons.Application;
        }
        
        _trayIcon.DoubleClick += (s, e) => RestoreFromTray();
        
        // Скрываем окно
        this.Hide();
    }

    private void RestoreFromTray()
    {
        // Показываем окно обратно
        this.Show();
        this.WindowState = WindowState.Normal;
        this.Activate();
        
        // Удаляем иконку из трея
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
    }

    private async Task CheckForUpdates()
    {
        try
        {
            using var client = new System.Net.Http.HttpClient();
            // Запрашиваем JSON с вашего хостинга (например, GitHub)
            var response = await client.GetStringAsync(UpdateInfoUrl);
            var updateInfo = JsonConvert.DeserializeObject<UpdateData>(response);

            if (updateInfo != null && updateInfo.Version != CurrentVersion)
            {
                var result = MessageBox.Show(
                    $"Доступна новая версия: {updateInfo.Version}\n\nЧто нового:\n{updateInfo.Changelog}\n\nОбновить сейчас?",
                    "Обновление KVANT Launcher",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);

                if (result == MessageBoxResult.Yes)
                {
                    // Открываем ссылку на скачивание архива (GitHub Releases)
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = updateInfo.DownloadUrl,
                        UseShellExecute = true
                    });
                    // В идеале здесь можно скачать updater.exe, который заменит основной файл
                }
            }
        }
        catch { /* Ошибка проверки (нет интернета или файла) */ }
    }
    private bool IsFabricVersion(string version)
    {
        // Честная проверка через список доступных версий
        return _availableFabricVersions.Contains(version);
    }

    // NeoForge: официально поддерживает 1.20.2+, а для 1.20.1 — форк под именем «forge»
    private bool IsNeoForgeVersion(string version)
    {
        // Честная проверка: есть ли эта версия в списке доступных на Maven
        return _availableNeoForgeVersions.Contains(version);
    }

    // OptiFine: поддерживает 1.8 - 1.21
    private bool IsOptiFineVersion(string version)
    {
        // Честная проверка: есть ли эта версия на BMCLAPI
        return _availableOptiFineVersions.Contains(version);
    }

    private async Task FetchModListsAsync()
    {
        TxtStatus.Text = "Подключение к API модов...";
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("KVANTLauncher", "1.1"));
            client.Timeout = TimeSpan.FromSeconds(15);

            // 1. OptiFine (BMCLAPI)
            try
            {
                var optiResp = await client.GetStringAsync("https://bmclapi2.bangbang93.com/optifine/metadata");
                var optiArray = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JArray>(optiResp);
                if (optiArray != null)
                {
                    foreach (var item in optiArray)
                    {
                        string? mcVer = item["mcversion"]?.ToString();
                        if (!string.IsNullOrEmpty(mcVer)) _availableOptiFineVersions.Add(mcVer);
                    }
                }
            }
            catch { }

            // 2. NeoForge (Maven)
            try
            {
                _availableNeoForgeVersions.Add("1.20.1"); // Стабильный форк
                string neoApi = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";
                var neoResp = await client.GetStringAsync(neoApi);
                var neoData = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(neoResp);
                var versions = neoData?["versions"]?.ToObject<List<string>>();
                if (versions != null)
                {
                    foreach (var v in versions)
                    {
                        var parts = v.Split('.');
                        if (parts.Length >= 2)
                        {
                            string mcVer = $"1.{parts[0]}.{parts[1]}";
                            if (parts[1] == "0") mcVer = $"1.{parts[0]}";
                            _availableNeoForgeVersions.Add(mcVer);
                        }
                    }
                }
            }
            catch { }

            // 3. Fabric (Meta Meta API)
            try
            {
                var fabricResp = await client.GetStringAsync("https://meta.fabricmc.net/v2/versions/game");
                var fabricArray = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JArray>(fabricResp);
                if (fabricArray != null)
                {
                    foreach (var item in fabricArray)
                    {
                        if (item["stable"]?.ToObject<bool>() == true)
                        {
                            string? mcVer = item["version"]?.ToString();
                            if (!string.IsNullOrEmpty(mcVer)) _availableFabricVersions.Add(mcVer);
                        }
                    }
                }
            }
            catch { }

            // ФОЛБЭКИ: Если интернета нет или API лежат, добавляем популярные версии
            if (_availableOptiFineVersions.Count == 0)
            {
                string[] common = { "1.7.10", "1.8.8", "1.8.9", "1.12.2", "1.14.4", "1.15.2", "1.16.5", "1.17.1", "1.18.2", "1.19.2", "1.19.4", "1.20.1", "1.20.4", "1.21" };
                foreach (var v in common) _availableOptiFineVersions.Add(v);
            }
            if (_availableFabricVersions.Count == 0)
            {
                string[] common = { "1.14.4", "1.15.2", "1.16.5", "1.17.1", "1.18.2", "1.19.2", "1.20.1", "1.20.4", "1.21" };
                foreach (var v in common) _availableFabricVersions.Add(v);
            }
            if (_availableNeoForgeVersions.Count <= 1)
            {
                string[] common = { "1.20.1", "1.20.2", "1.20.4", "1.21" };
                foreach (var v in common) _availableNeoForgeVersions.Add(v);
            }

            // Обновляем список версий, если они уже были загружены
            var allVers = await _launcher.GetAllVersionsAsync();
            Dispatcher.Invoke(() => UpdateVersionList(allVers));
        }
        catch { }
    }

    private void ClearMinecraftCache()
    {
        try
        {
            string[] cachePaths = {
                System.IO.Path.Combine(_path.BasePath, "assets", "skins"),
                System.IO.Path.Combine(_path.BasePath, "webcache"),
                System.IO.Path.Combine(_path.BasePath, "launcher_accounts.json")
            };

            foreach (var p in cachePaths)
            {
                if (System.IO.File.Exists(p)) System.IO.File.Delete(p);
                else if (Directory.Exists(p)) Directory.Delete(p, true);
            }

        }
        catch { }
    }

    private void PatchVersionJsonForthlib(string versionName)
    {
        try
        {
            string jsonPath = System.IO.Path.Combine(_path.BasePath, "versions", versionName, $"{versionName}.json");
            if (!System.IO.File.Exists(jsonPath)) return;

            string content = System.IO.File.ReadAllText(jsonPath);
            
            string elyApi = "https://authserver.ely.by";
            // Список хостов (и http, и https для верности)
            string[] mojangHosts = { 
                "https://authserver.mojang.com", "http://authserver.mojang.com",
                "https://sessionserver.mojang.com", "http://sessionserver.mojang.com",
                "https://api.mojang.com", "http://api.mojang.com",
                "https://api.minecraftservices.com", "http://api.minecraftservices.com"
            };

            bool changed = false;
            if (_currentAuthType == "ElyBy")
            {
                foreach (var host in mojangHosts)
                {
                    if (content.Contains(host))
                    {
                        content = content.Replace(host, elyApi);
                        changed = true;
                    }
                }
            }
            else
            {
                if (content.Contains(elyApi))
                {
                    content = content.Replace(elyApi, "https://authserver.mojang.com");
                    changed = true;
                }
            }
            
            if (changed) System.IO.File.WriteAllText(jsonPath, content);
        }
        catch { }
    }

    private IEnumerable<MArgument> GetJvmArguments(int ram, int jv = -1)
    {
        var extraArgs = new List<string>();

        if (_currentAuthType == "ElyBy" || _currentAuthType == "Offline")
        {
            string originalPath = System.IO.Path.Combine(_path.BasePath, "authlib-injector.jar");
            if (System.IO.File.Exists(originalPath))
            {
                string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KVANT_LAUNCHER");
                if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
                string tempJarPath = System.IO.Path.Combine(tempDir, "authlib-injector.jar");
                
                try { System.IO.File.Copy(originalPath, tempJarPath, true); } catch { tempJarPath = originalPath; }

                string elyUrl = "https://ely.by";
                // 1. УЛЬТИМАТИВНАЯ ТОЧКА ВХОДА (Обязательно в кавычках для путей с пробелами)
                extraArgs.Add($"-javaagent:\"{tempJarPath}\"={elyUrl}");
                
                // 2. Редиректы для инжектора
                extraArgs.Add($"-Dauthlibinjector.mojang.url={elyUrl}");
                extraArgs.Add($"-Dauthlibinjector.yggdrasil.url={elyUrl}");
                extraArgs.Add("-Dauthlibinjector.side=client");
                
                // 3. ФИКСЫ СОВМЕСТИМОСТИ (Без api.env=custom, это ломает инжектор)
                extraArgs.Add("-Dminecraft.user.checkChatSignature=false");
                extraArgs.Add("-Dminecraft.user.checkChatSecurity=false");
                extraArgs.Add("-Djava.net.preferIPv4Stack=true");
                extraArgs.Add("-Dminecraft.user.checkChatSignature=false");
                extraArgs.Add("-Dminecraft.user.checkChatSecurity=false");

                // 5. Фикс для Java 16+ (1.16+)
                // Добавляем --add-opens только если Java 9 или новее, 
                // иначе Java 8 (Minecraft 1.16.5) вылетит с ошибкой "Unrecognized option"
                string selectedVersion = "";
                Dispatcher.Invoke(() => { selectedVersion = ComboVersions.SelectedItem?.ToString() ?? ""; });
                
                if (jv == -1) jv = GetJavaVersion();
                if (jv >= 9 && (selectedVersion.Contains("1.16") || selectedVersion.Contains("1.17") || 
                                selectedVersion.Contains("1.18") || selectedVersion.Contains("1.19") || 
                                selectedVersion.Contains("1.20") || selectedVersion.Contains("1.21")))
                {
                    extraArgs.Add("--add-opens"); extraArgs.Add("java.base/java.lang=ALL-UNNAMED");
                    extraArgs.Add("--add-opens"); extraArgs.Add("java.base/java.net=ALL-UNNAMED");
                    extraArgs.Add("--add-opens"); extraArgs.Add("java.base/java.io=ALL-UNNAMED");
                    extraArgs.Add("--add-opens"); extraArgs.Add("java.base/java.util=ALL-UNNAMED");
                }
            }
        }

        var args = new List<string>
        {
            "-XX:+UseG1GC",
            "-XX:+ParallelRefProcEnabled",
            "-XX:MaxGCPauseMillis=200",
            "-XX:+UnlockExperimentalVMOptions"
        };
        
        args.InsertRange(0, extraArgs);

        return args.Select(x => new MArgument(x));
    }

    private async Task EnsureCustomSkinLoader(string versionName)
    {
        if (_currentAuthType == "Microsoft") return; // Лицензионщикам не портим игру

        bool isModded = versionName.ToLower().Contains("forge") || 
                        versionName.ToLower().Contains("fabric") || 
                        versionName.ToLower().Contains("quilt");
        
        if (!isModded) return;

        try 
        {
            string minecraftPath = _path.BasePath;
            string modsDir = System.IO.Path.Combine(minecraftPath, "mods");
            if (!Directory.Exists(modsDir)) Directory.CreateDirectory(modsDir);

            // 1. СОЗДАЕМ КОНФИГ ДЛЯ CSL (Чтобы он знал про Ely.by)
            string cslConfigDir = System.IO.Path.Combine(minecraftPath, "CustomSkinLoader");
            if (!Directory.Exists(cslConfigDir)) Directory.CreateDirectory(cslConfigDir);
            
            string extraListPath = System.IO.Path.Combine(cslConfigDir, "ExtraList.json");
            // Всегда обновляем конфиг, чтобы Ely.by был первым
            string configJson = @"{
  ""version"": 1,
  ""list"": [
    {
      ""name"": ""Ely.by"",
      ""type"": ""CustomSkinAPI"",
      ""root"": ""https://ely.by/api/users/skin/""
    }
  ]
}";
            await File.WriteAllTextAsync(extraListPath, configJson);

            // 2. ПРОВЕРЯЕМ НАЛИЧИЕ МОДА
            var existingMods = Directory.GetFiles(modsDir, "*CustomSkinLoader*.jar");
            if (existingMods.Length > 0) return;

            TxtStatus.Text = "Установка патча скинов (CSL)...";
            
            // Ссылка на стабильную версию мода (универсальная для 1.16-1.21)
            // В идеале тут должен быть выбор под версию, но для начала поставим самую совместимую
            string downloadUrl = "https://github.com/xfl03/CustomSkinLoader/releases/download/14.19/CustomSkinLoader_Forge-14.19.jar";
            if (versionName.ToLower().Contains("fabric"))
                downloadUrl = "https://github.com/xfl03/CustomSkinLoader/releases/download/14.19/CustomSkinLoader_Fabric-14.19.jar";

            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            var bytes = await client.GetByteArrayAsync(downloadUrl);
            await File.WriteAllBytesAsync(System.IO.Path.Combine(modsDir, "CustomSkinLoader-KVANT-FIX.jar"), bytes);
        }
        catch { /* Ошибка установки мода не должна вешать кнопку "Играть" */ }
    }

    private async Task<string> GetElyByUuid(string nickname)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            // API Ely.by для получения UUID по нику
            var resp = await client.GetAsync($"https://ely.by/api/users/by/username/{nickname}");
            if (resp != null && resp.IsSuccessStatusCode && resp.Content != null)
            {
                var json = await resp.Content.ReadAsStringAsync();
                var data = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json);
                string? uuid = data?["uuid"]?.ToString();
                if (!string.IsNullOrEmpty(uuid))
                {
                    return uuid;
                }
            }
        }
        catch { }
        return "";
    }

    private int _cachedJavaVer = -1;
    private string _cachedJavaPath = "";
    private int GetJavaVersion(string javaPath = "java")
    {
        if (_cachedJavaVer != -1 && _cachedJavaPath == javaPath) return _cachedJavaVer;

        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = javaPath,
                    Arguments = "-version",
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string output = process.StandardError.ReadToEnd();
            process.WaitForExit();

            _cachedJavaPath = javaPath;

            // Примеры вывода:
            // "java version "1.8.0_391"" -> Java 8
            // "openjdk version "17.0.1" 2021-10-19" -> Java 17
            
            if (output.Contains("version \"1.8")) return _cachedJavaVer = 8;
            
            var match = System.Text.RegularExpressions.Regex.Match(output, @"version ""(\d+)\.");
            if (match.Success) return _cachedJavaVer = int.Parse(match.Groups[1].Value);
            
            return _cachedJavaVer = 8;
        }
        catch { return _cachedJavaVer = 8; }
    }

    private async Task EnsureAuthlibInjector()
    {
        string path = System.IO.Path.Combine(_path.BasePath, "authlib-injector.jar");
        
        // Если файл есть и он нормального размера, не качаем
        if (System.IO.File.Exists(path) && new FileInfo(path).Length > 100000) return;

        try
        {
            string dir = System.IO.Path.GetDirectoryName(path) ?? "";
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            
            // Пробуем несколько источников
            string[] urls = {
                "https://authlib-injector.ely.by/artifact/latest/authlib-injector.jar",
                "https://bmclapi2.bangbang93.com/mirrors/authlib-injector/artifact/latest/authlib-injector.jar",
                "https://github.com/yushijinhun/authlib-injector/releases/download/v1.2.5/authlib-injector-1.2.5.jar"
            };

            foreach (var url in urls)
            {
                try {
                    TxtStatus.Text = "Загрузка системы скинов...";
                    var response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        var bytes = await response.Content.ReadAsByteArrayAsync();
                        if (bytes.Length > 100000) {
                            await File.WriteAllBytesAsync(path, bytes);
                            return;
                        }
                    }
                } catch { continue; }
            }
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Не удалось загрузить authlib-injector: {ex.Message}");
        }
    }
}

public class UpdateData
{
    public string Version { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Changelog { get; set; } = "";
}

public class LauncherConfig
{
    public string Nickname { get; set; } = "Player";
    public string SelectedVersion { get; set; } = "";
    public int RAM { get; set; } = 2048;
    public int LauncherBehavior { get; set; } = 0;
    public bool ShowSnapshots { get; set; } = false;
    public bool ShowModded { get; set; } = true;
    public bool ShowOld { get; set; } = false;
    public bool FastLaunch { get; set; } = true;
    public string? SkinPath { get; set; }
    public Dictionary<string, int> ConflictStats { get; set; } = new Dictionary<string, int>();
    public string AuthType { get; set; } = "Offline"; // Offline, Microsoft, ElyBy
    public string? MicrosoftSessionJson { get; set; } // Сохраненная сессия Microsoft
    public string? ElyByToken { get; set; }
    public string? ElyByUsername { get; set; }
    public string? ElyByUuid { get; set; }
}
