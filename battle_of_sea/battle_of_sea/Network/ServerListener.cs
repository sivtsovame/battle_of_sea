using System.Net;
using System.Net.Sockets;

namespace battle_of_sea.Network;

public class ServerListener
{
    private readonly int _port;
    private TcpListener _listener;

    public ServerListener(int port)
    {
        _port = port;
    }

    public async Task StartAsync()
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();

        Console.WriteLine($"Server started on port {_port}");

        while (true)
        {
            var client = await _listener.AcceptTcpClientAsync();
            Console.WriteLine("Client connected");

            var connection = new ClientConnection(client);
            _ = connection.HandleAsync(); // fire-and-forget
        }
    }
}
