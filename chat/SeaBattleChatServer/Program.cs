using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace SeaBattleChatServer
{
    class Program
    {
        private const int Port = 5000;

        // Храним всех подключенных клиентов
        private static readonly List<ClientHandler> clients = new List<ClientHandler>();
        private static readonly object lockObject = new object();

        static async Task Main(string[] args)
        {
            Console.WriteLine("сервер запущен");

            try
            {
                await StartServerAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Критическая ошибка: {ex.Message}");
                Console.ReadKey();
            }
        }

        static async Task StartServerAsync()
        {
            TcpListener listener = new TcpListener(IPAddress.Any, Port);
            listener.Start();

            Console.WriteLine($"✅ Сервер запущен на порту {Port}");
            Console.WriteLine($"📡 Ожидание игроков...");
            Console.WriteLine();

            while (true)
            {
                TcpClient client = await listener.AcceptTcpClientAsync();

                // Создаем обработчик для нового клиента
                var handler = new ClientHandler(client, BroadcastMessage, RemoveClient);
                lock (lockObject)
                {
                    clients.Add(handler);
                }

                // Запускаем обработку в отдельном потоке
                _ = Task.Run(() => handler.StartAsync());

                Console.WriteLine($"🔗 Игрок подключился. Всего игроков: {clients.Count}");
            }
        }

        // Отправка сообщения ВСЕМ клиентам
        static void BroadcastMessage(string message, ClientHandler sender = null)
        {
            lock (lockObject)
            {
                foreach (var client in clients)
                {
                    // Не отправляем обратно отправителю
                    if (sender == null || client != sender)
                    {
                        client.SendMessageAsync(message).ConfigureAwait(false);
                    }
                }
            }
        }

        // Отправка системного сообщения
        static void BroadcastSystemMessage(string message)
        {
            BroadcastMessage($"[СИСТЕМА] {message}");
        }

        // Удаление клиента из списка
        static void RemoveClient(ClientHandler client)
        {
            lock (lockObject)
            {
                if (clients.Remove(client))
                {
                    Console.WriteLine($"🔌 Игрок отключился. Осталось игроков: {clients.Count}");
                    if (clients.Count > 0)
                    {
                        BroadcastSystemMessage($"👋 Игрок {client.ClientName} покинул чат. Осталось игроков: {clients.Count}");
                    }
                }
            }
        }
    }

    // Класс для обработки каждого клиента
    class ClientHandler
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly Action<string, ClientHandler> _broadcastCallback;
        private readonly Action<ClientHandler> _removeCallback;
        private string _clientName = "Игрок";

        public string ClientName => _clientName;

        public ClientHandler(TcpClient client, Action<string, ClientHandler> broadcastCallback, Action<ClientHandler> removeCallback)
        {
            _client = client;
            _stream = client.GetStream();
            _broadcastCallback = broadcastCallback;
            _removeCallback = removeCallback;
        }

        public async Task StartAsync()
        {
            try
            {
                // Отправляем приветствие
                await SendMessageAsync($"[СИСТЕМА] Добро пожаловать в чат Морского боя!");
                await SendMessageAsync($"[СИСТЕМА] Введите ваше имя:");

                byte[] buffer = new byte[1024];

                // Первое сообщение - имя игрока
                int bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead > 0)
                {
                    _clientName = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();
                    if (string.IsNullOrEmpty(_clientName))
                        _clientName = "Игрок";
                }

                await SendMessageAsync($"[СИСТЕМА] Привет, {_clientName}! Вы в чате. Пишите сообщения:");

                // Уведомляем всех о новом игроке
                _broadcastCallback?.Invoke($"[СИСТЕМА] {_clientName} присоединился к чату!", this);

                // Цикл чтения сообщений
                while (_client.Connected)
                {
                    bytesRead = await _stream.ReadAsync(buffer, 0, buffer.Length);

                    if (bytesRead == 0)
                        break;

                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead).Trim();

                    if (!string.IsNullOrEmpty(message))
                    {
                        Console.WriteLine($"💬 {_clientName}: {message}");

                        // Отправляем сообщение ВСЕМ клиентам (кроме отправителя)
                        string formattedMessage = $"{_clientName}: {message}";
                        _broadcastCallback?.Invoke(formattedMessage, this);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Ошибка с {_clientName}: {ex.Message}");
            }
            finally
            {
                _removeCallback?.Invoke(this);
                _client.Close();
            }
        }

        public async Task SendMessageAsync(string message)
        {
            if (_client.Connected)
            {
                byte[] data = Encoding.UTF8.GetBytes(message + "\n");
                await _stream.WriteAsync(data, 0, data.Length);
            }
        }
    }
}