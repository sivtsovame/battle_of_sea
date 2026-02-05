using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SeaBattleChat.Network;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace SeaBattleChat
{
    public partial class MainWindow : Window
    {
        private ObservableCollection<ChatMessage> messages = new ObservableCollection<ChatMessage>();
        private bool isConnected = false;
        private string playerName = "Адмирал";

        // Сетевой клиент
        private SimpleChatClient _networkClient;

        // Элементы управления
        private TextBox? messageTextBox;
        private TextBox? serverTextBox;
        private TextBox? portTextBox;
        private TextBox? playerNameTextBox; // НОВОЕ: поле для ввода имени
        private Button? connectButton;
        private Button? disconnectButton;
        private Button? sendButton;
        private Button? clearButton;
        private TextBlock? statusText;
        private Border? statusIndicator;
        private ScrollViewer? messagesScrollViewer;
        private StackPanel? messagesPanel;

        public MainWindow()
        {
            InitializeComponent();
            this.Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            InitializeControls();
            InitializeChat();
        }

        private void InitializeControls()
        {
            try
            {
                // Находим все элементы управления по Name из XAML
                messageTextBox = this.FindControl<TextBox>("MessageTextBox");
                serverTextBox = this.FindControl<TextBox>("ServerTextBox");
                portTextBox = this.FindControl<TextBox>("PortTextBox");
                playerNameTextBox = this.FindControl<TextBox>("PlayerNameTextBox"); // НОВОЕ
                connectButton = this.FindControl<Button>("ConnectButton");
                disconnectButton = this.FindControl<Button>("DisconnectButton");
                sendButton = this.FindControl<Button>("SendButton");
                clearButton = this.FindControl<Button>("ClearButton");
                statusText = this.FindControl<TextBlock>("StatusText");
                statusIndicator = this.FindControl<Border>("StatusIndicator");
                messagesScrollViewer = this.FindControl<ScrollViewer>("MessagesScrollViewer");
                messagesPanel = this.FindControl<StackPanel>("MessagesPanel");

                // Проверяем, что все элементы найдены
                if (serverTextBox == null || portTextBox == null || connectButton == null ||
                    messageTextBox == null || playerNameTextBox == null) // Обновлено
                {
                    AddSystemMessage("⚠ Ошибка инициализации элементов управления!", true);
                }
            }
            catch (Exception ex)
            {
                AddSystemMessage($"⚠ Ошибка загрузки интерфейса: {ex.Message}", true);
            }
        }

        private void InitializeChat()
        {
            // Устанавливаем имя игрока из текстового поля
            if (playerNameTextBox != null)
            {
                playerName = playerNameTextBox.Text?.Trim() ?? "Адмирал";

                // Добавляем обработчик изменения имени
                playerNameTextBox.LostFocus += PlayerNameTextBox_LostFocus;
                playerNameTextBox.KeyDown += PlayerNameTextBox_KeyDown;
            }

            // Инициализируем сетевого клиента
            _networkClient = new SimpleChatClient();

            // Подписываемся на события сетевого клиента
            _networkClient.MessageReceived += OnNetworkMessageReceived;
            _networkClient.StatusChanged += OnNetworkStatusChanged;
            _networkClient.ConnectionChanged += OnNetworkConnectionChanged;

            // Обработчики кнопок
            if (connectButton != null)
                connectButton.Click += ConnectButton_Click;

            if (disconnectButton != null)
                disconnectButton.Click += DisconnectButton_Click;

            if (sendButton != null)
                sendButton.Click += SendButton_Click;

            if (clearButton != null)
                clearButton.Click += ClearButton_Click;

            // Обработка Enter в поле сообщения
            if (messageTextBox != null)
                messageTextBox.KeyDown += MessageTextBox_KeyDown;

            // Добавляем приветственные сообщения
            AddSystemMessage($"👤 Вы вошли как: {playerName}");
            AddSystemMessage("💬 Чат для переговоров во время морского сражения");
            AddSystemMessage("⚡ Подключитесь к серверу для общения с другими игроками");
        }

        // Обработчик изменения имени игрока (при потере фокуса)
        private void PlayerNameTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdatePlayerName();
        }

        // Обработчик изменения имени игрока (по Enter)
        private void PlayerNameTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                UpdatePlayerName();
                e.Handled = true;
            }
        }

        // Обновление имени игрока
        private void UpdatePlayerName()
        {
            if (playerNameTextBox != null)
            {
                string newName = playerNameTextBox.Text?.Trim() ?? "";
                if (!string.IsNullOrEmpty(newName) && newName != playerName)
                {
                    playerName = newName;
                    AddSystemMessage($"✏️ Вы сменили имя на: {playerName}");

                    // Если подключены к серверу, можно отправить команду смены имени
                    if (_networkClient.IsConnected)
                    {
                        AddSystemMessage("⚠ Переподключитесь, чтобы применить новое имя к серверу");
                    }
                }
            }
        }

        // Обработчик полученных сообщений от сервера
        private void OnNetworkMessageReceived(string message)
        {
            // Выполняем в UI потоке
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Определяем тип сообщения
                if (message.StartsWith("[СИСТЕМА]"))
                {
                    AddSystemMessage(message.Substring(9)); // Убираем "[СИСТЕМА] "
                }
                else if (message.Contains(":"))
                {
                    int colonIndex = message.IndexOf(':');
                    if (colonIndex > 0)
                    {
                        string sender = message.Substring(0, colonIndex).Trim();
                        string text = message.Substring(colonIndex + 1).Trim();

                        if (sender == playerName)
                            AddGameMessage(text);
                        else
                            AddOpponentMessage($"{sender}: {text}");
                    }
                    else
                    {
                        AddSystemMessage(message);
                    }
                }
                else
                {
                    AddSystemMessage(message);
                }
            });
        }

        // Обработчик изменения статуса сети
        private void OnNetworkStatusChanged(string status)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Показываем статус в чате
                AddSystemMessage(status);

                // Обновляем статус в UI
                if (statusText != null)
                {
                    if (status.Contains("✅") || status.Contains("Успешно"))
                        statusText.Text = "Подключено";
                    else if (status.Contains("❌") || status.Contains("Ошибка"))
                        statusText.Text = "Ошибка";
                    else if (status.Contains("🔌") || status.Contains("Отключено"))
                        statusText.Text = "Не подключено";
                    else if (status.Contains("Подключение"))
                        statusText.Text = "Подключение...";
                    else if (status.Contains("Отправлено"))
                        statusText.Text = "В сети";
                    else
                        statusText.Text = "Статус";
                }
            });
        }

        // Обработчик изменения подключения
        private void OnNetworkConnectionChanged(bool isConnected)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                this.isConnected = isConnected;

                // Обновляем состояние кнопок
                if (connectButton != null)
                {
                    connectButton.IsEnabled = !isConnected;
                    connectButton.Content = isConnected ? "✅ Подключено" : "Подключиться";
                }

                if (disconnectButton != null)
                    disconnectButton.IsEnabled = isConnected;

                // Обновляем индикатор статуса
                if (statusIndicator != null)
                {
                    statusIndicator.Background = new Avalonia.Media.SolidColorBrush(
                        isConnected ? 0xFF10B981 : 0xFFEF4444);
                }

                // Обновляем поле ввода сообщения
                if (messageTextBox != null)
                {
                    messageTextBox.IsEnabled = isConnected;
                    messageTextBox.Watermark = isConnected ?
                        "Введите сообщение для противника..." :
                        "Подключитесь к серверу...";
                }

                // Обновляем поле имени (можно менять только когда не подключены)
                if (playerNameTextBox != null)
                {
                    playerNameTextBox.IsEnabled = !isConnected;
                }

                if (sendButton != null)
                    sendButton.IsEnabled = isConnected;
            });
        }

        // Подключение к серверу
        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (serverTextBox == null || portTextBox == null || playerNameTextBox == null)
                return;

            string server = serverTextBox.Text ?? "";
            string portText = portTextBox.Text ?? "";
            string name = playerNameTextBox.Text?.Trim() ?? "";

            // Проверяем введенные данные
            if (string.IsNullOrWhiteSpace(name))
            {
                AddSystemMessage("⚠ Введите ваше имя!", true);
                return;
            }

            if (string.IsNullOrWhiteSpace(server))
            {
                AddSystemMessage("⚠ Введите адрес сервера!", true);
                return;
            }

            if (!int.TryParse(portText, out int port))
            {
                AddSystemMessage("⚠ Неверный порт!", true);
                return;
            }

            // Обновляем имя игрока
            playerName = name;

            // Временно меняем состояние кнопок
            if (connectButton != null)
            {
                connectButton.IsEnabled = false;
                connectButton.Content = "⏳ Подключение...";
            }

            if (disconnectButton != null)
                disconnectButton.IsEnabled = false;

            if (statusText != null)
                statusText.Text = "Подключение...";

            if (statusIndicator != null)
                statusIndicator.Background = new Avalonia.Media.SolidColorBrush(0xFFF59E0B);

            AddSystemMessage($"🌐 Подключаюсь к {server}:{port}...");

            // РЕАЛЬНОЕ ПОДКЛЮЧЕНИЕ К СЕРВЕРУ
            bool connected = await _networkClient.ConnectAsync(server, port);

            if (connected)
            {
                // После успешного подключения отправляем имя игрока
                await _networkClient.SendMessageAsync(playerName);
                AddSystemMessage($"🎮 Вы вошли как: {playerName}");
                AddSystemMessage("💬 Теперь вы можете общаться с другими игроками!");
            }
            else
            {
                // Если подключение не удалось, восстанавливаем состояние
                if (connectButton != null)
                {
                    connectButton.IsEnabled = true;
                    connectButton.Content = "Подключиться";
                }
            }
        }

        // Отключение от сервера
        private void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            _networkClient.Disconnect();
        }

        // Отправка сообщения (по кнопке)
        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            SendMessage();
        }

        // Отправка сообщения (по Enter)
        private void MessageTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SendMessage();
            }
        }

        private void SendMessage()
        {
            if (messageTextBox == null)
            {
                AddSystemMessage("Ошибка: поле сообщения не найдено", true);
                return;
            }

            string messageText = messageTextBox.Text?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(messageText))
            {
                // Визуальная обратная связь для пустого сообщения
                if (messageTextBox != null)
                {
                    var originalBorder = messageTextBox.BorderBrush;
                    messageTextBox.BorderBrush = new Avalonia.Media.SolidColorBrush(0xFFFF6B6B);

                    Task.Delay(300).ContinueWith(t =>
                    {
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            messageTextBox.BorderBrush = originalBorder;
                        });
                    });
                }
                return;
            }

            // ОТПРАВКА НА СЕРВЕР
            if (_networkClient.IsConnected)
            {
                // Отправляем просто текст (сервер сам добавит имя)
                _ = _networkClient.SendMessageAsync(messageText);

                // Локально показываем свое сообщение
                AddGameMessage(messageText);
            }
            else
            {
                // Локальное сообщение (без сервера)
                AddSystemMessage("⚠ Нет подключения к серверу");
                AddGameMessage(messageText);
            }

            // Очищаем поле ввода
            messageTextBox.Text = "";
            messageTextBox.Focus();

            // Прокрутка к последнему сообщению
            if (messagesScrollViewer != null)
                messagesScrollViewer.ScrollToEnd();
        }

        // Очистка чата
        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (messagesPanel != null)
                messagesPanel.Children.Clear();

            AddSystemMessage("🗑️ История чата очищена");

            // Восстанавливаем приветственное сообщение
            AddSystemMessage($"👤 Вы вошли как: {playerName}");
            AddSystemMessage("💬 Чат для переговоров во время морского сражения");
            AddSystemMessage("⚡ Подключитесь к серверу для общения с другими игроками");
        }

        // Добавить системное сообщение
        private void AddSystemMessage(string text, bool isError = false)
        {
            var message = new ChatMessage
            {
                Sender = isError ? "⚠ Система" : "ℹ Система",
                Text = text,
                Time = DateTime.Now,
                IsSystem = true,
                IsError = isError
            };

            AddMessageToUI(message);
        }

        // Добавить сообщение от игрока
        private void AddGameMessage(string text)
        {
            var message = new ChatMessage
            {
                Sender = playerName,
                Text = text,
                Time = DateTime.Now,
                IsMyMessage = true
            };

            AddMessageToUI(message);
        }

        // Добавить сообщение от противника
        private void AddOpponentMessage(string text)
        {
            var message = new ChatMessage
            {
                Sender = "Противник",
                Text = text,
                Time = DateTime.Now,
                IsMyMessage = false
            };

            AddMessageToUI(message);
        }

        private void AddMessageToUI(ChatMessage message)
        {
            if (messagesPanel == null)
                return;

            // Создаем UI элемент для сообщения
            var messageBorder = new Border
            {
                Padding = new Thickness(12),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 0, 0, 6),
                HorizontalAlignment = message.IsMyMessage ?
                    Avalonia.Layout.HorizontalAlignment.Right :
                    Avalonia.Layout.HorizontalAlignment.Left,
                MaxWidth = 400,
                BorderThickness = new Thickness(1)
            };

            // Выбор цвета фона
            if (message.IsError)
            {
                messageBorder.Background = new Avalonia.Media.SolidColorBrush(0xFFFEE2E2);
                messageBorder.BorderBrush = new Avalonia.Media.SolidColorBrush(0xFFFCA5A5);
            }
            else if (message.IsSystem)
            {
                messageBorder.Background = new Avalonia.Media.SolidColorBrush(0xFFF1F5F9);
                messageBorder.BorderBrush = new Avalonia.Media.SolidColorBrush(0xFFE2E8F0);
            }
            else if (message.IsMyMessage)
            {
                messageBorder.Background = new Avalonia.Media.SolidColorBrush(0xFFDBEAFE);
                messageBorder.BorderBrush = new Avalonia.Media.SolidColorBrush(0xFF93C5FD);
            }
            else
            {
                messageBorder.Background = new Avalonia.Media.SolidColorBrush(0xFFF0F9FF);
                messageBorder.BorderBrush = new Avalonia.Media.SolidColorBrush(0xFFBAE6FD);
            }

            var stackPanel = new StackPanel();

            // Заголовок с именем и временем
            var headerPanel = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 8
            };

            var senderText = new TextBlock
            {
                Text = message.Sender,
                FontWeight = Avalonia.Media.FontWeight.SemiBold,
                FontSize = 12,
                Foreground = message.IsError ?
                    new Avalonia.Media.SolidColorBrush(0xFFDC2626) :
                    (message.IsSystem ?
                        new Avalonia.Media.SolidColorBrush(0xFF475569) :
                        (message.IsMyMessage ?
                            new Avalonia.Media.SolidColorBrush(0xFF1E40AF) :
                            new Avalonia.Media.SolidColorBrush(0xFF0C4A6E)))
            };

            var timeText = new TextBlock
            {
                Text = message.Time.ToString("HH:mm:ss"),
                Foreground = new Avalonia.Media.SolidColorBrush(0xFF64748B),
                FontSize = 10,
                Margin = new Thickness(6, 0, 0, 0)
            };

            headerPanel.Children.Add(senderText);
            headerPanel.Children.Add(timeText);

            // Текст сообщения
            var messageText = new TextBlock
            {
                Text = message.Text,
                Margin = new Thickness(0, 6, 0, 0),
                FontSize = 13,
                Foreground = message.IsError ?
                    new Avalonia.Media.SolidColorBrush(0xFF991B1B) :
                    (message.IsSystem ?
                        new Avalonia.Media.SolidColorBrush(0xFF334155) :
                        (message.IsMyMessage ?
                            new Avalonia.Media.SolidColorBrush(0xFF1E3A8A) :
                            new Avalonia.Media.SolidColorBrush(0xFF075985))),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
            };

            stackPanel.Children.Add(headerPanel);
            stackPanel.Children.Add(messageText);

            messageBorder.Child = stackPanel;
            messagesPanel.Children.Add(messageBorder);

            // Прокрутка к последнему сообщению
            if (messagesScrollViewer != null)
                messagesScrollViewer.ScrollToEnd();
        }

        // Освобождение ресурсов при закрытии окна
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _networkClient?.Dispose();
        }

        // Модель сообщения
        public class ChatMessage
        {
            public string Sender { get; set; } = "";
            public string Text { get; set; } = "";
            public DateTime Time { get; set; } = DateTime.Now;
            public bool IsSystem { get; set; } = false;
            public bool IsMyMessage { get; set; } = false;
            public bool IsError { get; set; } = false;
        }
    }
}