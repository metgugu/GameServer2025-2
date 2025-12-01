using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions; 
using System.Linq; 

// 한글 깨짐 방지
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// 콘솔 충돌 방지 락
object consoleLock = new();

Console.OutputEncoding = Encoding.UTF8;

// ===================================================
// UI/GAME STATE VARIABLES 
// ===================================================
string? globalNickname = string.Empty;
string currentQuestioner = "미정";
int currentTurn = 0; 
int currentQuestionIndex = 0; 
int myTotalScore = 0; 

// [핵심] 채팅 기록을 저장할 리스트 (화면 갱신 시 복구용)
List<string> chatLogHistory = new List<string>();
const int MaxChatHistory = 15; 

// [핵심] 사용자가 현재 치고 있는 글자 버퍼
StringBuilder inputBuffer = new StringBuilder();

List<(string Questioner, string Question, string Answer)> qaHistory = new();
(string Question, string Reply)? myCurrentQuestion = null;
List<(string Questioner, string Question, string Answer)> currentGogaeHints = new();

// ===================================================
// 접속 정보 입력
// ===================================================
Console.Clear();
Console.WriteLine("==============================================");
Console.WriteLine("      [ 온라인 스무고개 클라이언트 ]");
Console.WriteLine("==============================================");

Console.Write("접속할 서버 IP (기본값 127.0.0.1): ");
string? inputIp = Console.ReadLine();
string ip = string.IsNullOrWhiteSpace(inputIp) ? "127.0.0.1" : inputIp.Trim();

Console.Write("닉네임 입력 (띄어쓰기 불가): ");
string? nicknameInput = Console.ReadLine();
while (string.IsNullOrWhiteSpace(nicknameInput))
{
    Console.Write("닉네임을 입력해야 합니다: ");
    nicknameInput = Console.ReadLine();
}
globalNickname = nicknameInput.Trim();

// ===================================================
// 서버 연결
// ===================================================
TcpClient client = new TcpClient();
StreamReader? networkReader = null;
StreamWriter? networkWriter = null;

try
{
    Console.WriteLine($"서버({ip}:9000)에 연결 시도 중...");
    await client.ConnectAsync(ip, 9000);
    Console.WriteLine("서버 연결 성공!");

    NetworkStream stream = client.GetStream();
    networkReader = new StreamReader(stream, Encoding.UTF8);
    networkWriter = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };

    await networkWriter.WriteLineAsync(globalNickname);

    // 비동기 태스크 시작
    var receiverTask = ReceiveMessagesAsync(networkReader, client);
    var senderTask = InputLoopAsync(networkWriter, client);

    await Task.WhenAny(receiverTask, senderTask);
}
catch (Exception ex)
{
    Console.WriteLine($"[오류] {ex.Message}");
}
finally
{
    client.Close();
    networkReader?.Close();
    networkWriter?.Close();
}

// ===================================================
// UI 함수 
// ===================================================

/// <summary>
/// 메시지를 받으면 기록에 저장하고 화면에 출력하는 함수
/// </summary>
void SafeWriteLine(string message)
{
    lock (consoleLock)
    {
        // 1. 채팅 기록 리스트에 추가
        chatLogHistory.Add(message);
        if (chatLogHistory.Count > MaxChatHistory) chatLogHistory.RemoveAt(0);

        // 2. 현재 입력 중이던 줄 지우기
        ClearCurrentInputLine();

        // 3. 메시지 출력
        Console.WriteLine(message);

        // 4. 입력 중이던 내용 복구
        RedrawInputBuffer();
    }
}

void ClearCurrentInputLine()
{
    Console.SetCursorPosition(0, Console.CursorTop);
    Console.Write(new string(' ', Console.WindowWidth - 1));
    Console.SetCursorPosition(0, Console.CursorTop);
}

void RedrawInputBuffer()
{
    // 프롬프트 없이 내용만 출력
    Console.Write(inputBuffer.ToString());
}

bool ShouldClearScreenFor(string msg)
{
    if (msg.Contains("게임을 시작합니다!")) return true;
    if (msg.Contains("번째 턴을 시작합니다")) return true;
    if (msg.Contains("번째 고개를 시작합니다")) return true; 
    if (msg.Contains("모든 질문 등록 완료")) return true;
    if (msg.Contains("답변 완료! 이제 힌트를 선택할 차례입니다")) return true;
    if (msg.Contains("힌트 선택 완료! 정답 유추 단계입니다")) return true;
    if (msg.Contains("5번의 고개가 모두 끝났습니다")) return true;
    if (msg.Contains("로비로 돌아왔습니다")) return true;
    return false;
}

void DrawTitleScreen()
{
    lock (consoleLock)
    {
        Console.Clear();

        Console.WriteLine("==============================================");
        Console.WriteLine("          >> Battle 20 Questions <<           "); 
        Console.WriteLine("==============================================");
        Console.WriteLine($"  출제자 : {currentQuestioner}");
        Console.WriteLine($"  턴     : {currentTurn}번째 턴");
        Console.WriteLine($"  고개   : {currentQuestionIndex}번째 고개");
        Console.WriteLine($"  나의 총점: {myTotalScore}점");
        Console.WriteLine("==============================================");
        Console.WriteLine("  --- 지금까지 한 질문 / 답 ---");

        if (qaHistory != null && qaHistory.Count > 0)
        {
            int idx = 1;
            foreach (var qa in qaHistory)
            {
                Console.WriteLine($"    {idx}. Q: {qa.Question} (A: {qa.Answer})");
                idx++;
            }
        }
        else
        {
            Console.WriteLine("    (아직 질문이 없습니다.)");
        }

        Console.WriteLine("  ------------------------------------------");
        Console.WriteLine("[ 채팅 기록 ]");
        
        // 채팅 기록 복구
        foreach (var log in chatLogHistory)
        {
            Console.WriteLine(log);
        }
        
        // 입력 중이던 내용 복구
        RedrawInputBuffer();
    }
}

// ===================================================
// 비동기 작업
// ===================================================

async Task ReceiveMessagesAsync(StreamReader networkReader, TcpClient client)
{
    try
    {
        while (client.Connected)
        {
            string? message = await networkReader.ReadLineAsync();
            if (message == null) break;

            UpdateGameState(message);
            
            if (ShouldClearScreenFor(message))
            {
                // 화면 갱신 시에도 기록은 저장해야 함
                lock(consoleLock)
                {
                    chatLogHistory.Add(message);
                    if (chatLogHistory.Count > MaxChatHistory) chatLogHistory.RemoveAt(0);
                }
                DrawTitleScreen();
            }
            else
            {
                SafeWriteLine(message); 
            }
        }
    }
    catch (Exception)
    {
        // 종료
    }
}

void UpdateGameState(string message)
{
    var turnMatch = Regex.Match(message, @"\[서버\] (\d+)번째 턴을 시작합니다\. 출제자: \[ (.+?) \]");
    if (turnMatch.Success)
    {
        currentTurn = int.Parse(turnMatch.Groups[1].Value);
        currentQuestioner = turnMatch.Groups[2].Value;
        currentQuestionIndex = 0; 
        qaHistory.Clear(); 
        myCurrentQuestion = null;
        currentGogaeHints.Clear();
        return;
    }
    
    var gogaeMatch = Regex.Match(message, @"(\d+)번째 고개를 시작합니다");
    if (gogaeMatch.Success)
    {
        if (currentGogaeHints.Count > 0)
        {
            foreach (var hint in currentGogaeHints)
            {
                if (!qaHistory.Any(qa => qa.Questioner == hint.Questioner && qa.Question == hint.Question))
                {
                    qaHistory.Add(hint); 
                }
            }
            currentGogaeHints.Clear();
        }
        currentQuestionIndex = int.Parse(gogaeMatch.Groups[1].Value);
        myCurrentQuestion = null;
        return;
    }
    
    var myDataMatch = Regex.Match(message, @"\[내 질문\] \[(.+?)\]: (.+?) -> \((.+?)\)");
    if (myDataMatch.Success && myDataMatch.Groups[1].Value == globalNickname)
    {
        myCurrentQuestion = (myDataMatch.Groups[2].Value, myDataMatch.Groups[3].Value); 
        return;
    }
    
    var finalChoiceMatch = Regex.Match(message, @"\[선택 질문\] \[(.+?)\]: (.+?) -> \((.+?)\)");
    if (finalChoiceMatch.Success)
    {
        string questioner = finalChoiceMatch.Groups[1].Value;
        string question = finalChoiceMatch.Groups[2].Value;
        string reply = finalChoiceMatch.Groups[3].Value;
        
        if (myCurrentQuestion.HasValue)
        {
            var myQ = myCurrentQuestion.Value;
            if (!currentGogaeHints.Any(h => h.Questioner == globalNickname && h.Question == myQ.Question))
            {
                currentGogaeHints.Add((globalNickname!, myQ.Question, myQ.Reply));
            }
            myCurrentQuestion = null;
        }

        if (questioner != globalNickname)
        {
            if (!currentGogaeHints.Any(h => h.Questioner == questioner && h.Question == question))
            {
                currentGogaeHints.Add((questioner, question, reply));
            }
        }
        return;
    }

    var scoreMatch = Regex.Match(message, @"\[결과\] (\S+)(?:\s\(.+?\))?: .* 총 (\d+)점");
    if (scoreMatch.Success)
    {
        string nickname = scoreMatch.Groups[1].Value;
        if (nickname == globalNickname)
        {
            myTotalScore = int.Parse(scoreMatch.Groups[2].Value);
        }
        return;
    }
}

// [핵심] ReadKey를 이용한 입력 루프 + 한글 지움 처리
async Task InputLoopAsync(StreamWriter writer, TcpClient client)
{
    try
    {
        while (client.Connected)
        {
            ConsoleKeyInfo keyInfo = Console.ReadKey(intercept: true);

            lock(consoleLock) 
            {
                // 1. 엔터 키 (전송)
                if (keyInfo.Key == ConsoleKey.Enter)
                {
                    string message = inputBuffer.ToString();
                    
                    // 내가 쓴 글을 화면에서 지움 (중복 방지)
                    ClearCurrentInputLine();
                    
                    if (!string.IsNullOrWhiteSpace(message))
                    {
                        if (message.ToLower() == "/cls")
                        {
                            DrawTitleScreen();
                        }
                        else if (message.ToLower() == "exit")
                        {
                            break; 
                        }
                        else
                        {
                            _ = writer.WriteLineAsync(message); 
                        }
                    }

                    inputBuffer.Clear();
                }
                // 2. 백스페이스 (지우기)
                else if (keyInfo.Key == ConsoleKey.Backspace)
                {
                    if (inputBuffer.Length > 0)
                    {
                        // 지우려는 마지막 글자를 가져옴
                        char lastChar = inputBuffer[inputBuffer.Length - 1];
                        
                        // 그 글자의 화면 폭(1칸 혹은 2칸)을 계산
                        int width = GetTextWidth(lastChar);

                        // 버퍼에서 삭제
                        inputBuffer.Length--;

                        // 화면에서 삭제 (너비만큼 백스페이스)
                        if (width == 2)
                        {
                            Console.Write("\b\b  \b\b"); // 한글: 2칸 뒤로 -> 공백 2개 -> 2칸 뒤로
                        }
                        else
                        {
                            Console.Write("\b \b");     // 영어: 1칸 뒤로 -> 공백 1개 -> 1칸 뒤로
                        }
                    }
                }
                // 3. 일반 문자 입력
                else if (!char.IsControl(keyInfo.KeyChar))
                {
                    inputBuffer.Append(keyInfo.KeyChar);
                    Console.Write(keyInfo.KeyChar);
                }
            }
        }
    }
    catch (Exception)
    {
        // 종료
    }
}

// 글자가 화면에서 몇 칸을 차지하는지 계산하는 헬퍼 함수
int GetTextWidth(char c)
{
    // 한글 범위 체크 (대략적인 범위)
    if (c >= 0x2E80 || (c >= 0x1100 && c <= 0x11FF) || (c >= 0xAC00 && c <= 0xD7A3))
    {
        return 2; // 한글 등 전각 문자는 2칸
    }
    return 1; // 영어, 숫자, 특수기호는 1칸
}