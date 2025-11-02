using System.Net.Sockets;
using System.Text;

// [씹힘 방지 1] 콘솔에 동시 접근을 막기 위한 잠금(lock) 객체
object consoleLock = new();

// [한글 패치 1] C#은 무조건 UTF-8만 사용하도록 고정
Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("Enter Server IP (default 127.0.0.1):");
string ip = Console.ReadLine();
if (string.IsNullOrWhiteSpace(ip))
{
    ip = "127.0.0.1";
}

Console.WriteLine("Enter your nickname:");
string? nickname = Console.ReadLine();
while (string.IsNullOrWhiteSpace(nickname))
{
    Console.WriteLine("Nickname cannot be empty. Enter your nickname:");
    nickname = Console.ReadLine();
}

TcpClient client = new TcpClient();
StreamReader? networkReader = null; 
StreamWriter? networkWriter = null; 
StreamReader? consoleInputReader = null; 

try
{
    await client.ConnectAsync(ip, 9000); 
    Console.WriteLine("Connected to server! (Type 'exit' to quit)");

    NetworkStream stream = client.GetStream();
    networkReader = new StreamReader(stream, Encoding.UTF8);
    networkWriter = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
    
    // [한글 패치 2] 콘솔 입력도 UTF-8로 읽도록 고정
    consoleInputReader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

    await networkWriter.WriteLineAsync(nickname);

    Task receiverTask = ReceiveMessagesAsync(networkReader);
    Task senderTask = SendMessagesAsync(networkWriter, client, consoleInputReader);

    await Task.WhenAny(receiverTask, senderTask);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
finally
{
    client.Close();
    networkReader?.Close();
    networkWriter?.Close();
    consoleInputReader?.Close(); 
}

async Task ReceiveMessagesAsync(StreamReader networkReader)
{
    try
    {
        while (true)
        {
            string? message = await networkReader.ReadLineAsync();
            if (message == null)
            {
                lock (consoleLock)
                {
                    Console.WriteLine("Server disconnected.");
                }
                break;
            }
            
            lock (consoleLock)
            {
                Console.WriteLine(message); 
            }
        }
    }
    catch (Exception)
    {
        lock (consoleLock)
        {
            Console.WriteLine("Connection lost.");
        }
    }
}

async Task SendMessagesAsync(StreamWriter writer, TcpClient client, StreamReader consoleInputReader)
{
    try
    {
        while (client.Connected)
        {
            string? message = await consoleInputReader.ReadLineAsync(); 

            if (string.IsNullOrWhiteSpace(message)) continue;

            lock (consoleLock)
            {
                Console.WriteLine($"[DEBUG] Sending: {message}");
            }

            if (message.ToLower() == "exit")
            {
                break; 
            }
        
            await writer.WriteLineAsync(message);
        }
    }
    catch (Exception)
    {
        lock (consoleLock)
        {
            Console.WriteLine("Failed to send message. Server may be down.");
        }
    }
}