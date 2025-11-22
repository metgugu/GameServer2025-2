using System.Net.Sockets;
using System.Text;

// (한글 문제 해결 1) CP949(EUC-KR) 인코딩을 .NET에서 사용 가능하도록 등록
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
/// 서버에서 온 메시지가 "단계 전환"을 의미하는지 검사해서,
/// true면 화면을 지우도록 사용하는 헬퍼 함수
/// </summary>
bool ShouldClearScreenFor(string msg)
{
    // 서버에서 보내는 안내 문구 기준 (원본 서버 코드 참고)
    if (msg.Contains("게임을 시작합니다!")) return true;
    if (msg.Contains("번째 턴을 시작합니다")) return true;
    if (msg.Contains("번째 고개를 시작합니다")) return true;
    if (msg.Contains("모든 플레이어의 질문이 등록되었습니다")) return true; // 답변 단계 시작
    if (msg.Contains("출제자가 모든 질문에 답변했습니다! 이제 힌트를 선택할 차례입니다")) return true;
    if (msg.Contains("모든 플레이어가 힌트 선택을 완료했습니다!")) return true; // 정답 추측 단계 시작
    if (msg.Contains("고개가 종료되었습니다")) return true; // 다음 고개 시작
    if (msg.Contains("5번의 고개가 모두 끝났습니다! 턴을 종료하고 점수를 계산합니다")) return true;
    if (msg.Contains("게임이 종료되었습니다")) return true;
    if (msg.Contains("로비로 돌아왔습니다")) return true;

    return false;
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

            // 🔹 서버에서 온 메시지가 "단계 전환"이면 클라이언트가 스스로 화면 지우기
            if (ShouldClearScreenFor(message))
            {
                lock (consoleLock)
                {
                    Console.Clear();
                    // 필요하면 여기서 타이틀 같은 것도 다시 그려줄 수 있음
                    // 예) DrawTitleScreen();
                }
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

            string lower = message.ToLower();

            // 🔹 로컬에서만 화면 지우기: /cls (서버에는 안 보내고 내 콘솔만 클리어)
            if (lower == "/cls")
            {
                lock (consoleLock)
                {
                    Console.Clear();
                    // 여기서도 원하면 타이틀 출력 가능
                    // DrawTitleScreen();
                }
                continue;
            }

            // (입력 씹힘 개선) DEBUG 출력도 lock 안에서 처리
            lock (consoleLock)
            {
                Console.WriteLine($"[DEBUG] Sending: {message}");
            }

            if (lower == "exit")
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
