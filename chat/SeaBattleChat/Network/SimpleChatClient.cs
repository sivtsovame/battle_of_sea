using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace SeaBattleChat.Network
{
    public class SimpleChatClient : IDisposable
    {
        private TcpClient _tcpClient;
        private NetworkStream _stream;
        private bool _isConnected = false;

        // События для уведомлений
        public event Action<string> MessageReceived;     // Пришло сообщение
        public event Action<string> StatusChanged;       // Изменился статус
        public event Action<bool> ConnectionChanged;     // Изменилось подключение

        public bool IsConnected => _isConnected && (_tcpClient?.Connected ?? false);

        public async Task<bool> ConnectAsync(string server, int port)
        {
            try
            {
                StatusChanged?.Invoke($"Подключение к {server}:{port}...");

                _tcpClient = new TcpClient();
                await _tcpClient.ConnectAsync(server, port);
                _stream = _tcpClient.GetStream();
                _isConnected = true;

                ConnectionChanged?.Invoke(true);
                StatusChanged?.Invoke($"✅ Успешно подключено к {server}:{port}");

                // Запускаем фоновую задачу для чтения сообщений
                _ = Task.Run(ReceiveMessagesAsync);

                return true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"❌ Ошибка подключения: {ex.Message}");
                Disconnect();
                return false;
            }
        }

        public async Task SendMessageAsync(string message)
        {
            if (!IsConnected)
            {
                StatusChanged?.Invoke("❌ Нет подключения к серверу");
                return;
            }

            try
            {
                byte[] data = Encoding.UTF8.GetBytes(message + "\n");
                await _stream.WriteAsync(data, 0, data.Length);
                StatusChanged?.Invoke($"📤 Отправлено: {message}");
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"❌ Ошибка отправки: {ex.Message}");
                Disconnect();
            }
        }

        private async Task ReceiveMessagesAsync()
        {
            byte[] buffer = new byte[4096];
            StringBuilder messageBuilder = new StringBuilder();

            try
            {
                while (IsConnected)
                {
                    int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);

                    if (bytesRead == 0)
                    {
                        // Сервер отключился
                        StatusChanged?.Invoke("📡 Сервер разорвал соединение");
                        Disconnect();
                        break;
                    }

                    string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    messageBuilder.Append(chunk);

                    // Обрабатываем каждое сообщение (разделитель - новая строка)
                    string data = messageBuilder.ToString();
                    int newLineIndex;

                    while ((newLineIndex = data.IndexOf('\n')) >= 0)
                    {
                        string message = data.Substring(0, newLineIndex).Trim();
                        data = data.Substring(newLineIndex + 1);

                        if (!string.IsNullOrEmpty(message))
                        {
                            MessageReceived?.Invoke(message);
                        }
                    }

                    messageBuilder.Clear();
                    messageBuilder.Append(data);
                }
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"📡 Ошибка приема: {ex.Message}");
                Disconnect();
            }
        }

        public void Disconnect()
        {
            if (_isConnected)
            {
                _isConnected = false;
                _stream?.Close();
                _tcpClient?.Close();

                ConnectionChanged?.Invoke(false);
                StatusChanged?.Invoke("🔌 Отключено от сервера");
            }
        }

        public void Dispose()
        {
            Disconnect();
        }
    }
}