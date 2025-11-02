using System.Net.Sockets;
using System.Text;

// (한글 문제 해결 1) CP949(EUC-KR) 인코딩을 .NET에서 사용 가능하도록 등록
// (chcp 65001을 사용할 것이므로 이 줄은 사실상 필요 없으나, 호환성을 위해 남겨둡니다.)
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// (입력 씹힘 방지 1) 콘솔에 동시 접근(출력/디버그)을 막기 위한 잠금 객체
object consoleLock = new();

// C# 프로그램이 터미널에 '출력'할 때는 UTF-8을 사용하도록 강제
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
StreamReader? networkReader = null; // 서버로부터 읽기
StreamWriter? networkWriter = null; // 서버로 쓰기
StreamReader? consoleInputReader = null; // 사용자 키보드로부터 읽기

try
{
    await client.ConnectAsync(ip, 9000); 
    Console.WriteLine("Connected to server! (Type 'exit' to quit)");

    NetworkStream stream = client.GetStream();
    
    // 네트워크 통신은 항상 UTF-8로 통일
    networkReader = new StreamReader(stream, Encoding.UTF8);
    networkWriter = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
    
    // (한글 문제 해결 2) 터미널이 UTF-8('chcp 65001' 모드)로 보낸다고 가정하고,
    // C#도 UTF-8로 입력을 받도록 설정합니다.
    consoleInputReader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

    // 서버에 닉네임 전송
    await networkWriter.WriteLineAsync(nickname);

    // '메시지 수신'과 '메시지 전송' 작업을 동시에 비동기로 실행
    Task receiverTask = ReceiveMessagesAsync(networkReader);
    Task senderTask = SendMessagesAsync(networkWriter, client, consoleInputReader);

    // 둘 중 하나라도 끝나면 프로그램 종료 (예: 서버가 끊기거나, 사용자가 exit 입력)
    await Task.WhenAny(receiverTask, senderTask);
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
}
finally
{
    // 프로그램 종료 시 모든 리소스 정리
    client.Close();
    networkReader?.Close();
    networkWriter?.Close();
    consoleInputReader?.Close(); 
}

/// <summary>
/// 서버로부터 메시지를 '수신'하는 작업 전용 루프
/// </summary>
async Task ReceiveMessagesAsync(StreamReader networkReader)
{
    try
    {
        while (true)
        {
            string? message = await networkReader.ReadLineAsync();
            if (message == null)
            {
                // 다른 스레드가 콘솔에 출력하는 것을 방지 (입력 씹힘 개선)
                lock (consoleLock)
                {
                    Console.WriteLine("Server disconnected.");
                }
                break;
            }
            
            // 다른 스레드가 콘솔에 출력하는 것을 방지 (입력 씹힘 개선)
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

/// <summary>
/// 사용자 키보드 입력을 서버로 '전송'하는 작업 전용 루프
/// </summary>
async Task SendMessagesAsync(StreamWriter writer, TcpClient client, StreamReader consoleInputReader)
{
    try
    {
        while (client.Connected)
        {
            // 한글 처리가 설정된 'consoleInputReader'로부터 입력을 받음
            string? message = await consoleInputReader.ReadLineAsync(); 

            if (string.IsNullOrWhiteSpace(message)) continue;

            // (입력 씹힘 개선) DEBUG 출력도 lock 안에서 처리
            lock (consoleLock)
            {
                Console.WriteLine($"[DEBUG] Sending: {message}");
            }

            if (message.ToLower() == "exit")
            {
                break; // 이 루프를 종료하면 프로그램이 종료됨
            }
        
            // 서버로 메시지 전송
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