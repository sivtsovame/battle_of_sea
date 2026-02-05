# === FULL BATTLE TEST SCRIPT ===

$serverHost = "127.0.0.1"
$serverPort = 5000

function Connect-Player($name) {
    $tcp = New-Object System.Net.Sockets.TcpClient
    $tcp.Connect($serverHost, $serverPort)
    $stream = $tcp.GetStream()
    $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8)
    $writer = New-Object System.IO.StreamWriter($stream, [System.Text.Encoding]::UTF8)
    $writer.AutoFlush = $true

    # Отправляем connect
    $msg = @{ type="connect"; payload=@{ playerName=$name } } | ConvertTo-Json -Compress
    $writer.WriteLine($msg)

    $response = $reader.ReadLine()
    $playerId = ($response | ConvertFrom-Json).Payload.playerId
    Write-Host "$name connected -> $response"

    return [PSCustomObject]@{
        Name = $name
        Tcp = $tcp
        Reader = $reader
        Writer = $writer
        Id = $playerId
    }
}

function Shoot($shooter, $x, $y) {
    $msg = @{ type="shoot"; payload=@{ x=$x; y=$y } } | ConvertTo-Json -Compress
    $shooter.Writer.WriteLine($msg)
    $response = $shooter.Reader.ReadLine()
    Write-Host "$($shooter.Name) shoots [$x,$y]"
    Write-Host "$($shooter.Name) result -> $response"
}

# === Подключаем игроков ===
$player1 = Connect-Player "Alex"
$player2 = Connect-Player "Bob"

# === Ручная расстановка кораблей на сервере ===
# Для теста на сервере должно быть реализовано: Board.PlaceShip(x, y, size, horizontal)
# Предположим, что сервер уже расставляет 1-4 палубные корабли

Write-Host "`n=== SHOOTING PHASE ===`n"

# Сценарий стрельбы
# Alex стреляет по пустой клетке
Shoot $player1 9 9
# Bob стреляет по пустой клетке
Shoot $player2 9 8

# Alex стреляет по клетке с 4-палубным кораблём
Shoot $player1 0 0
Shoot $player1 0 1
Shoot $player1 0 2
Shoot $player1 0 3

# Bob стреляет по клеткам, чтобы показать, что ход передаётся корректно
Shoot $player2 5 5
Shoot $player2 6 6

# После последнего попадания Alex должен получить "game_over"

Write-Host "`n=== TEST FINISHED ===`n"

# Закрываем соединения
$player1.Tcp.Close()
$player2.Tcp.Close()