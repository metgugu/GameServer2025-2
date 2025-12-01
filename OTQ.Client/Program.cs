using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions; 
using System.Linq; 

// (한글 문제 해결 1) CP949(EUC-KR) 인코딩을 .NET에서 사용 가능하도록 등록
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

// (입력 씹힘 방지 1) 콘솔에 동시 접근(출력/디버그)을 막기 위한 잠금 객체
object consoleLock = new();

// C# 프로그램이 터미널에 '출력'할 때는 UTF-8을 사용하도록 강제
Console.OutputEncoding = Encoding.UTF8;

// ===================================================
// UI/GAME STATE VARIABLES 
// ===================================================
string? globalNickname = string.Empty;

string currentQuestioner = "미정";
int currentTurn = 0; 
int currentQuestionIndex = 0; 
int myTotalScore = 0; 

// 질문/답변 기록을 저장할 리스트: (질문자, 질문 내용, 답변 내용)
List<(string Questioner, string Question, string Answer)> qaHistory = new();

// 현재 고개에서 '나'의 질문/답변을 임시로 저장 (qaHistory에 추가되기 전)
(string Question, string Reply)? myCurrentQuestion = null;

// 현재 고개에서 획득한 (내 질문 + 선택 질문) 기록을 임시 보관
List<(string Questioner, string Question, string Answer)> currentGogaeHints = new();


Console.WriteLine("Enter Server IP (default 127.0.0.1):");
string ip = Console.ReadLine() ?? "127.0.0.1";
if (string.IsNullOrWhiteSpace(ip))
{
    ip = "127.0.0.1";
}

Console.WriteLine("Enter your nickname(띄어쓰기불가능):");
string? nicknameInput = Console.ReadLine();
while (string.IsNullOrWhiteSpace(nicknameInput))
{
    Console.WriteLine("Nickname cannot be empty. Enter your nickname:");
    nicknameInput = Console.ReadLine();
}
globalNickname = nicknameInput;

TcpClient client = new TcpClient();
StreamReader? networkReader = null;
StreamWriter? networkWriter = null;
StreamReader? consoleInputReader = null;

try
{
    await client.ConnectAsync(ip, 9000);
    SafeWriteLine("Connected to server! (Type 'exit' or /cls to quit)");

    NetworkStream stream = client.GetStream();

    networkReader = new StreamReader(stream, Encoding.UTF8);
    networkWriter = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
    consoleInputReader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);

    // 서버에 닉네임 전송
    await networkWriter.WriteLineAsync(globalNickname);

    Task receiverTask = ReceiveMessagesAsync(networkReader, client);
    Task senderTask = SendMessagesAsync(networkWriter, client, consoleInputReader);

    await Task.WhenAny(receiverTask, senderTask);
}
catch (Exception ex)
{
    SafeWriteLine($"Error: {ex.Message}");
}
finally
{
    client.Close();
    networkReader?.Close();
    networkWriter?.Close();
    consoleInputReader?.Close();
}


// ===================================================
// UI/UX FUNCTIONS 
// ===================================================

/// <summary>
/// 모든 콘솔 출력을 담당하는 함수 (출력 충돌 방지 및 색상 제거)
/// </summary>
void SafeWriteLine(string message)
{
    lock (consoleLock)
    {
        Console.WriteLine(message);
    }
}

/// <summary>
/// 서버에서 온 메시지가 "단계 전환"을 의미하는지 검사
/// </summary>
bool ShouldClearScreenFor(string msg)
{
    if (msg.Contains("게임을 시작합니다!")) return true;
    if (msg.Contains("번째 턴을 시작합니다")) return true;
    
    // 'X번째 고개를 시작합니다' 메시지가 올 때 갱신 (기록 추가 후)
    if (msg.Contains("번째 고개를 시작합니다")) return true; 
    
    if (msg.Contains("모든 플레이어의 질문이 등록되었습니다")) return true;
    if (msg.Contains("출제자가 모든 질문에 답변했습니다! 이제 힌트를 선택할 차례입니다")) return true;
    if (msg.Contains("모든 플레이어가 힌트 선택을 완료했습니다!")) return true;
    
    // [최종 힌트] 블록 시작 시 갱신 조건은 제거됨 (다음 고개 시작 시 갱신)
    
    if (msg.Contains("5번의 고개가 모두 끝났습니다! 턴을 종료하고 점수를 계산합니다")) return true;
    if (msg.Contains("로비로 돌아왔습니다")) return true;

    return false;
}

/// <summary>
/// 화면을 지우고 게임 상태를 표시합니다. (획득 점수 추가, 질문자 제거, 답변 통합 출력)
/// </summary>
void DrawTitleScreen()
{
    lock (consoleLock)
    {
        Console.Clear();

        // 상단 타이틀
        Console.WriteLine("==============================================");
        Console.WriteLine("          >> Battle 20 Questions <<          "); 
        Console.WriteLine("==============================================");

        // 현재 진행 상황 및 나의 총점 표시
        Console.WriteLine($"  출제자 : {currentQuestioner}");
        Console.WriteLine($"  턴     : {currentTurn}번째 턴");
        Console.WriteLine($"  고개   : {currentQuestionIndex}번째 고개");
        Console.WriteLine($"  나의 총점: {myTotalScore}점");
        Console.WriteLine("==============================================");

        // 지금까지 한 질문 / 답 
        Console.WriteLine("  --- 지금까지 한 질문 / 답 ---");

        if (qaHistory != null && qaHistory.Count > 0)
        {
            int idx = 1;
            foreach (var qa in qaHistory)
            {
                // 질문자 닉네임 제거 및 답변 통합 출력
                Console.WriteLine($"    {idx}. Q: {qa.Question} (A: {qa.Answer})");
                idx++;
            }
        }
        else
        {
            Console.WriteLine("    (아직 질문이 없습니다.)");
        }

        Console.WriteLine("  ------------------------------------------");

        Console.WriteLine("[ 서버 메시지/채팅 ]");
    }
}


// ===================================================
// ASYNC TASKS 
// ===================================================

/// <summary>
/// 서버로부터 메시지를 '수신'하고 게임 상태를 업데이트하는 작업 전용 루프
/// </summary>
async Task ReceiveMessagesAsync(StreamReader networkReader, TcpClient client)
{
    try
    {
        while (client.Connected)
        {
            string? message = await networkReader.ReadLineAsync();
            if (message == null)
            {
                SafeWriteLine("Server disconnected.");
                break;
            }

            // 1. 상태 변수 업데이트를 시도
            UpdateGameState(message);
            
            // 2. 🔹 서버에서 온 메시지가 "단계 전환"이면 클라이언트가 스스로 화면 지우기 및 UI 갱신
            if (ShouldClearScreenFor(message))
            {
                // UI 갱신 (DrawTitleScreen 내부에서 Console.Clear()와 lock 처리)
                DrawTitleScreen();
                // UI를 새로 그린 후, 전환 메시지도 표시해줍니다.
                SafeWriteLine(message); 
            }
            else
            {
                // 3. 일반 메시지 출력
                SafeWriteLine(message); 
            }
        }
    }
    catch (Exception)
    {
        SafeWriteLine("Connection lost.");
    }
}

/// <summary>
/// 서버 메시지를 분석하여 UI에 필요한 전역 변수를 업데이트합니다. 
/// </summary>
void UpdateGameState(string message)
{
    // A. 턴 시작 정보 파싱 (턴이 바뀔 때만 전체 초기화)
    var turnMatch = Regex.Match(message, @"\[서버\] (\d+)번째 턴을 시작합니다\. 이번 출제자는 \[ (.+?) \]님입니다\.");
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
    
    // B. 고개 시작 정보 파싱 (이 시점에 이전 고개 기록을 qaHistory에 반영)
    var gogaeMatch = Regex.Match(message, @"(\d+)번째 고개를 시작합니다");
    if (gogaeMatch.Success)
    {
        // 🚨 다음 고개 인덱스 업데이트 전에, 이전 고개의 기록을 qaHistory에 추가합니다.
        if (currentGogaeHints.Count > 0)
        {
            foreach (var hint in currentGogaeHints)
            {
                if (!qaHistory.Any(qa => qa.Questioner == hint.Questioner && qa.Question == hint.Question))
                {
                    qaHistory.Add(hint); 
                }
            }
            currentGogaeHints.Clear(); // 임시 저장소 비우기
        }

        currentQuestionIndex = int.Parse(gogaeMatch.Groups[1].Value);
        myCurrentQuestion = null;
        return;
    }
    
    // C. 질문/답변 기록 업데이트 (내 질문/답변 수신 시, 임시 저장만)
    var myDataMatch = Regex.Match(message, @"\[내 질문\] \[(.+?)\]: (.+?) -> \((.+?)\)");
    if (myDataMatch.Success && myDataMatch.Groups[1].Value == globalNickname)
    {
        string questioner = myDataMatch.Groups[1].Value;
        string question = myDataMatch.Groups[2].Value;
        string reply = myDataMatch.Groups[3].Value;
        
        myCurrentQuestion = (question, reply); 
        
        return;
    }
    
    // D. 힌트 선택 목록 업데이트 (무시)
    var otherChoiceMatch = Regex.Match(message, @"\d+\. \[(.+?)\]: (.+)");
    if (otherChoiceMatch.Success)
    {
        return;
    }
    
    // E. 최종 선택된 힌트 업데이트 (currentGogaeHints에 임시 저장)
    var finalChoiceMatch = Regex.Match(message, @"\[선택 질문\] \[(.+?)\]: (.+?) -> \((.+?)\)");
    if (finalChoiceMatch.Success)
    {
        string questioner = finalChoiceMatch.Groups[1].Value;
        string question = finalChoiceMatch.Groups[2].Value;
        string reply = finalChoiceMatch.Groups[3].Value;
        
        // 1. 내 질문을 임시 저장소에 추가
        if (myCurrentQuestion.HasValue)
        {
            var myQ = myCurrentQuestion.Value;
            if (!currentGogaeHints.Any(h => h.Questioner == globalNickname && h.Question == myQ.Question))
            {
                currentGogaeHints.Add((globalNickname!, myQ.Question, myQ.Reply));
            }
            myCurrentQuestion = null; // 사용 후 초기화
        }

        // 2. 선택 질문을 임시 저장소에 추가
        bool isMyQuestion = questioner == globalNickname;
        
        if (!isMyQuestion)
        {
            if (!currentGogaeHints.Any(h => h.Questioner == questioner && h.Question == question))
            {
                currentGogaeHints.Add((questioner, question, reply));
            }
        }
        
        return;
    }

    // 🚨 F. 턴 결과 및 점수 업데이트 파싱 (새로 추가)
    // 예시: [결과] PlayerB: 마지막 라운드로부터 5라운드 연속 정답 유지! (+10점, 총 10점)
    var scoreMatch = Regex.Match(message, @"\[결과\] (\S+)(?:\s\(.+?\))?: .* 총 (\d+)점");
    if (scoreMatch.Success)
    {
        // 캡처 그룹 1은 이제 순수한 닉네임('a' 또는 'b')만 담습니다.
        string nickname = scoreMatch.Groups[1].Value;
        int totalScore = int.Parse(scoreMatch.Groups[2].Value); 
        
        // 내 닉네임과 일치하면 점수를 업데이트합니다.
        if (nickname == globalNickname)
        {
            myTotalScore = totalScore;
        }
        return;
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
            string? message = await consoleInputReader.ReadLineAsync();

            if (string.IsNullOrWhiteSpace(message)) continue;

            string lower = message.ToLower();

            // 🔹 로컬에서만 화면 지우기: /cls 
            if (lower == "/cls")
            {
                DrawTitleScreen();
                continue;
            }

            SafeWriteLine($"[DEBUG] Sending: {message}");

            if (lower == "exit")
            {
                break;
            }

            // 서버로 메시지 전송
            await writer.WriteLineAsync(message);
        }
    }
    catch (Exception)
    {
        SafeWriteLine("Failed to send message. Server may be down.");
    }
}