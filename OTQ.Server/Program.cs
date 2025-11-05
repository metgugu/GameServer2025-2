using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Linq; 

// Windows 터미널에서 한글 입력을 올바르게 처리하기 위해 UTF-8을 기본으로 설정합니다.
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// --- 전역 변수: 서버의 현재 상태를 관리합니다 ---

/// <summary>
/// 서버에 접속한 모든 플레이어의 목록
/// </summary>
List<Player> players = new List<Player>();

/// <summary>
/// 게임이 시작되었는지 여부 (true가 되면 새로운 플레이어 입장 불가)
/// </summary>
bool isGameRunning = false; 

/// <summary>
/// 서버의 현재 상태를 나타내는 '상태 머신' 변수
/// </summary>
GameState currentGameState = GameState.Lobby; 

/// <summary>
/// 현재 턴의 출제자 인덱스 (players 리스트 기준)
/// </summary>
int currentTurnIndex = 0; 

/// <summary>
/// 현재 턴의 실제 정답
/// </summary>
string currentAnswer = ""; 

/// <summary>
/// 현재 '고개'에서 (질문한 사람, (질문 내용, 답변 내용))을 저장
/// </summary>
Dictionary<Player, (string Question, string Reply)> currentGogaeData = new();

/// <summary>
/// 질문한 사람들의 순서를 기억 (출제자가 답변할 순서)
/// </summary>
List<Player> questionAskers = new();

/// <summary>
/// 출제자가 현재 답변해야 할 질문의 인덱스 (questionAskers 리스트 기준)
/// </summary>
int currentReplyIndex = 0;

/// <summary>
/// 현재 몇 번째 고개인지 추적 (1~5)
/// </summary>
int currentGogaeNumber = 1; 
// ---------------

Console.WriteLine("Starting server...");
// "127.0.0.1" (localhost) 대신 "IPAddress.Any" (모든 네트워크)로 변경
TcpListener listener = new TcpListener(IPAddress.Any, 9000);
listener.Start();
// 서버가 어떤 IP에서 수신 대기 중인지 명확하게 표시 (예: 0.0.0.0:9000)
Console.WriteLine($"Server started. Listening on {listener.LocalEndpoint}...");

/// <summary>
/// 서버 메인 루프. 새로운 클라이언트의 접속을 계속 기다립니다.
/// </summary>
while (true)
{
    TcpClient client = await listener.AcceptTcpClientAsync();

    // 게임이 이미 시작되었다면 새 클라이언트를 받지 않고 연결을 거부합니다.
    if (isGameRunning)
    {
        Console.WriteLine("Client tried to connect, but game is running. Rejected.");
        client.Close(); 
        continue;
    }

    Console.WriteLine("Client connected, waiting for nickname...");
    
    // 새 플레이어 객체 생성 (첫 번째 접속자(index 0)가 호스트가 됨)
    Player newPlayer = new Player(client, players.Count == 0); 
    players.Add(newPlayer);
    
    // 이 클라이언트 전용 비동기 핸들러를 시작합니다.
    _ = HandleClientAsync(newPlayer);
}

/// <summary>
/// 클라이언트 한 명을 전담하여 메시지 수신 및 처리를 담당합니다.
/// </summary>
async Task HandleClientAsync(Player player)
{
    NetworkStream stream = player.Client.GetStream();
    StreamReader reader = new StreamReader(stream, Encoding.UTF8);

    try
    {
        // 1. 닉네임 설정
        // 클라이언트가 보낸 첫 번째 메시지는 닉네임으로 간주합니다.
        string? nickname = await reader.ReadLineAsync();
        if (string.IsNullOrEmpty(nickname))
        {
            throw new Exception("Invalid nickname");
        }
        player.Nickname = nickname;
        
        Console.WriteLine($"Player set nickname: {player.Nickname} (Host: {player.IsHost})");
        
        // 모든 플레이어에게 새 참가자 입장 알림
        string joinMessage = $"[서버] {player.Nickname}님이 입장했습니다.";
        Console.WriteLine($"Broadcasting: {joinMessage}"); 
        await BroadcastMessageAsync(joinMessage);

        // 방장(호스트)에게만 본인이 방장임을 알리는 1:1 메시지 전송
        if (player.IsHost)
        {
            await SendMessageToAsync(player, "[서버] 당신은 방장(호스트)입니다. 3~4명이 모이면 /start 를 입력하여 게임을 시작하세요.");
        }

        // 2. 채팅 및 게임 로직 처리 루프
        // 이 루프는 클라이언트가 연결되어 있는 동안 계속 실행됩니다.
        while (player.Client.Connected)
        {
            string? message = await reader.ReadLineAsync();
            if (message == null) break; // 클라이언트 연결 끊김

            Console.WriteLine($"[{player.Nickname}]: {message}"); // 수신 로그

            // 서버의 현재 '게임 상태'에 따라 수신된 메시지를 다르게 처리합니다.
            switch (currentGameState)
            {
                case GameState.Lobby:
                    await HandleLobbyMessageAsync(player, message);
                    break;
                case GameState.WaitingForAnswer:
                    await HandleAnswerInputAsync(player, message);
                    break;
                case GameState.WaitingForQuestions:
                    await HandleQuestionInputAsync(player, message);
                    break;
                case GameState.WaitingForReplies:
                    await HandleReplyInputAsync(player, message);
                    break;
                case GameState.WaitingForChoice:
                    await HandleChoiceInputAsync(player, message);
                    break;
                case GameState.WaitingForGuesses:
                    await HandleGuessInputAsync(player, message);
                    break;
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error handling player {player.Nickname}: {ex.Message}");
    }
    finally
    {
        // 클라이언트 연결 종료 시 처리
        players.Remove(player);
        player.Client.Close();
        Console.WriteLine($"Player {player.Nickname} disconnected.");
        
        string leaveMessage = $"[서버] {player.Nickname}님이 퇴장했습니다.";
        Console.WriteLine($"Broadcasting: {leaveMessage}"); 
        await BroadcastMessageAsync(leaveMessage);
        
        // TODO: 게임 중에 플레이어가 나가면 게임을 중지하는 로직 필요
    }
}

/// <summary>
/// (Lobby 상태) 로비에서의 채팅 및 /start 명령어 처리
/// </summary>
async Task HandleLobbyMessageAsync(Player player, string message)
{
    if (message.ToLower() == "/start")
    {
        // 1. 호스트(방장)만 시작 가능
        if (!player.IsHost)
        {
            await SendMessageToAsync(player, "[서버] 방장(호스트)만 게임을 시작할 수 있습니다.");
        }
        // 2. 기획안: 최소 3명
        else if (players.Count < 3) 
        {
            await SendMessageToAsync(player, $"[서버] 최소 3명의 플레이어가 필요합니다. (현재 {players.Count}명)");
        }
        // 3. 기획안: 최대 4명
        else if (players.Count > 4)
        {
            await SendMessageToAsync(player, $"[서버] 최대 4명의 플레이어만 가능합니다. (현재 {players.Count}명)");
        }
        else
        {
            // 게임 시작 조건 충족
            isGameRunning = true; // 새 플레이어 입장 차단
            currentGameState = GameState.WaitingForAnswer; // 상태 변경
            currentTurnIndex = 0; // 호스트부터 턴 시작
            
            // 게임 시작 시 모든 플레이어 점수 초기화
            foreach(var p in players) { p.TotalScore = 0; }

            string startMessage = $"[서버] 게임을 시작합니다! (총 {players.Count}명)";
            Console.WriteLine($"Broadcasting: {startMessage}");
            await BroadcastMessageAsync(startMessage);
            
            // 첫 번째 턴 시작 함수 호출
            await StartTurnAsync(); 
        }
    }
    else
    {
        // 로비 상태에서는 일반 채팅
        string chatMessage = $"[{player.Nickname}]: {message}";
        await BroadcastMessageAsync(chatMessage);
    }
}

/// <summary>
/// 새 턴을 시작합니다. (출제자 공지 및 턴 데이터 초기화)
/// </summary>
async Task StartTurnAsync()
{
    Player questionSetter = players[currentTurnIndex];
    currentAnswer = ""; // 정답 초기화
    
    // 새 턴이 시작되면 '이번 턴'의 추측/선택 기록을 모두 초기화합니다. (TotalScore는 유지)
    foreach(Player p in players)
    {
        p.Guesses.Clear();
        p.AvailableChoices.Clear();
        p.ChosenQuestion = null;
    }
    
    string turnMessage = $"[서버] {currentTurnIndex + 1}번째 턴을 시작합니다. 이번 출제자는 [ {questionSetter.Nickname} ]님입니다.";
    Console.WriteLine($"Broadcasting: {turnMessage}");
    await BroadcastMessageAsync(turnMessage);

    // 출제자에게만 정답 입력 요청
    await SendMessageToAsync(questionSetter, "[서버] 당신은 출제자입니다. 정답을 입력하세요 (테스트는 영어로): ");
}

/// <summary>
/// (WaitingForAnswer 상태) 출제자의 '정답' 입력을 처리합니다.
/// </summary>
async Task HandleAnswerInputAsync(Player player, string message)
{
    Player questionSetter = players[currentTurnIndex];

    // 출제자만 정답을 입력할 수 있습니다.
    if (player == questionSetter)
    {
        currentAnswer = message;
        Console.WriteLine($"[게임 로그] 출제자({questionSetter.Nickname})가 정답 설정: {currentAnswer}");

        await SendMessageToAsync(player, $"[서버] 정답이 '{currentAnswer}'(으)로 설정되었습니다.");
        
        currentGogaeNumber = 1; // 1고개부터 시작
        
        string notice = $"[서버] 출제자가 정답을 설정했습니다. {currentGogaeNumber}번째 고개를 시작합니다.";
        Console.WriteLine($"Broadcasting: {notice}");
        await BroadcastMessageAsync(notice);

        // 새 고개를 위해 질문/답변 데이터 초기화
        currentGogaeData.Clear(); 
        questionAskers.Clear(); 
        
        currentGameState = GameState.WaitingForQuestions; // '질문 대기' 상태로 전환
        
        string nextStepNotice = "[서버] 이제부터 출제자를 제외한 모든 플레이어는 '질문'을 입력해주세요. (한 사람당 1개, 영어로)";
        Console.WriteLine($"Broadcasting: {nextStepNotice}");
        await BroadcastMessageAsync(nextStepNotice);
    }
    else
    {
        // 출제자가 아닌 플레이어의 입력은 차단
        await SendMessageToAsync(player, "[서버] 지금은 출제자가 정답을 입력할 차례입니다. 잠시만 기다려주세요.");
    }
}

/// <summary>
/// (WaitingForQuestions 상태) 추리자들의 '질문' 입력을 처리합니다.
/// </summary>
async Task HandleQuestionInputAsync(Player player, string message)
{
    Player questionSetter = players[currentTurnIndex];

    // 출제자의 입력은 차단
    if (player == questionSetter)
    {
        await SendMessageToAsync(player, "[서버] 당신은 출제자입니다. 다른 플레이어들이 질문을 입력할 때까지 기다려주세요.");
        return;
    }
    
    // 이미 질문한 플레이어의 입력은 차단
    if (currentGogaeData.ContainsKey(player))
    {
        await SendMessageToAsync(player, "[서버] 이미 이번 고개에 질문을 제출했습니다. 답변을 기다려주세요.");
        return;
    }

    // 질문 저장 (질문 내용, 빈 답변)
    currentGogaeData.Add(player, (message, "")); 
    questionAskers.Add(player); // 질문자 순서 기록
    
    Console.WriteLine($"[게임 로그] {player.Nickname} 질문 등록: {message}");
    await SendMessageToAsync(player, "[서버] 질문이 등록되었습니다.");

    // 모든 추리자가 질문을 완료했는지 확인
    int requiredQuestions = players.Count - 1; // 출제자 제외
    int currentQuestions = currentGogaeData.Count;

    if (currentQuestions == requiredQuestions)
    {
        // 모든 질문이 모였으면 '답변 대기' 상태로 전환
        Console.WriteLine("[게임 로그] 모든 질문이 수집되었습니다. 답변 단계로 넘어갑니다.");
        await BroadcastMessageAsync("[서버] 모든 플레이어의 질문이 등록되었습니다. 출제자가 답변할 차례입니다.");
        
        currentReplyIndex = 0; // 첫 번째 질문부터 답변 시작
        currentGameState = GameState.WaitingForReplies;
        
        // 출제자에게 첫 번째 질문을 보내는 함수 호출
        await AskForNextReplyAsync(); 
    }
    else
    {
        // 아직 질문이 남았음
        int remaining = requiredQuestions - currentQuestions;
        await BroadcastMessageAsync($"[서버] 남은 질문: {remaining}개");
    }
}

/// <summary>
/// (WaitingForReplies 상태) 출제자에게 다음 질문에 대한 답변(y/n)을 요청합니다.
/// </summary>
async Task AskForNextReplyAsync()
{
    Player presenter = players[currentTurnIndex];
    Player asker = questionAskers[currentReplyIndex]; // 현재 답변할 질문을 한 사람
    string question = currentGogaeData[asker].Question;

    // 출제자에게 질문을 1:1로 보냄
    await SendMessageToAsync(presenter, $"---------- [질문 {currentReplyIndex + 1}/{questionAskers.Count}] ----------");
    await SendMessageToAsync(presenter, $"-> [{asker.Nickname}]: {question}");
    await SendMessageToAsync(presenter, "[서버] 'y'(예) 또는 'n'(아니오)로 답변하세요: (영어로)");

    // 나머지 추리자들에게는 대기 알림
    string waitMessage = $"[서버] 출제자가 [ {asker.Nickname} ]님의 질문에 답변하는 중입니다...";
    Console.WriteLine($"Broadcasting: {waitMessage}");
    
    foreach (Player p in players.Where(p => p != presenter))
    {
        await SendMessageToAsync(p, waitMessage);
    }
}

/// <summary>
/// (WaitingForReplies 상태) 출제자의 'y/n' 답변 입력을 처리합니다.
/// </summary>
async Task HandleReplyInputAsync(Player player, string message)
{
    Player presenter = players[currentTurnIndex];

    // 출제자가 아닌 플레이어 입력 차단
    if (player != presenter)
    {
        await SendMessageToAsync(player, "[서버] 지금은 출제자가 질문에 답변하는 중입니다. 잠시만 기다려주세요.");
        return;
    }

    // 'y' 또는 'n'만 유효한 입력으로 간주
    string input = message.ToLower();
    if (input != "y" && input != "n")
    {
        await SendMessageToAsync(presenter, "[서버] 잘못된 입력입니다. 'y' 또는 'n'으로만 답변해야 합니다: (영어로)");
        return;
    }
    
    // 답변 저장
    string reply = (input == "y") ? "Yes" : "No";
    Player asker = questionAskers[currentReplyIndex]; 
    currentGogaeData[asker] = (currentGogaeData[asker].Question, reply); // (질문, "Yes"/"No") 저장
    
    Console.WriteLine($"[게임 로그] 출제자가 {asker.Nickname}의 질문({currentGogaeData[asker].Question})에 '{reply}'로 답변함.");
    await SendMessageToAsync(presenter, $"[서버] '{reply}' (으)로 답변이 저장되었습니다.");

    // 다음 질문으로 인덱스 이동
    currentReplyIndex++;

    if (currentReplyIndex < questionAskers.Count)
    {
        // 아직 답변할 질문이 남음
        await AskForNextReplyAsync();
    }
    else
    {
        // 모든 답변이 완료됨 -> '답변 선택' 단계로 전환
        Console.WriteLine("[게임 로그] 모든 답변 수집 완료. '답변 선택' 단계로 넘어갑니다.");
        await BroadcastMessageAsync("[서버] 출제자가 모든 질문에 답변했습니다! 이제 힌트를 선택할 차례입니다.");
        
        currentGameState = GameState.WaitingForChoice; 
        
        Player questionSetter = players[currentTurnIndex];
        
        // 모든 '추리자'들에게 개인화된 선택지 전송
        foreach (Player askerPlayer in questionAskers)
        {
            // 1. 본인의 질문/답변은 기본으로 전송
            var myData = currentGogaeData[askerPlayer];
            await SendMessageToAsync(askerPlayer, "---------- [힌트 선택] ----------");
            await SendMessageToAsync(askerPlayer, $"[내 질문] [{askerPlayer.Nickname}]: {myData.Question} -> ({myData.Reply})");
            await SendMessageToAsync(askerPlayer, "[서버] 아래에서 추가로 확인할 질문 1개를 숫자로 선택하세요.");

            // 2. '다른' 플레이어의 질문 목록을 만들어 전송
            var otherAskers = questionAskers.Where(p => p != askerPlayer).ToList();
            
            // Player 객체에 '선택 가능한 목록'을 저장 (나중에 숫자를 검증하기 위해)
            askerPlayer.AvailableChoices = otherAskers;
            
            for (int i = 0; i < otherAskers.Count; i++)
            {
                var otherAsker = otherAskers[i];
                var otherData = currentGogaeData[otherAsker];
                await SendMessageToAsync(askerPlayer, $"{i + 1}. [{otherAsker.Nickname}]: {otherData.Question}");
            }
            await SendMessageToAsync(askerPlayer, "---------------------------------");
            await SendMessageToAsync(askerPlayer, $"숫자(1~{otherAskers.Count})를 입력하세요: ");
            
            // 선택 상태 초기화
            askerPlayer.ChosenQuestion = null;
        }
        
        await SendMessageToAsync(questionSetter, "[서버] 플레이어들이 힌트를 선택 중입니다. 잠시만 기다려주세요...");
    }
}

/// <summary>
/// (WaitingForChoice 상태) 추리자들의 '힌트 선택' 입력을 처리합니다.
/// </summary>
async Task HandleChoiceInputAsync(Player player, string message)
{
    Player presenter = players[currentTurnIndex];

    // 출제자 또는 질문 안 한 사람(관전자 등) 차단
    if (player == presenter || !questionAskers.Contains(player))
    {
        await SendMessageToAsync(player, "[서버] 지금은 힌트를 선택할 차례가 아닙니다.");
        return;
    }
    
    // 이미 선택한 사람 차단
    if (player.ChosenQuestion != null)
    {
        await SendMessageToAsync(player, "[서버] 이미 힌트 선택을 완료했습니다. 정답을 입력할 차례를 기다려주세요.");
        return;
    }

    // 유효한 숫자인지 검증 (1 ~ 선택지 개수)
    if (!int.TryParse(message, out int choiceIndex) || choiceIndex < 1 || choiceIndex > player.AvailableChoices.Count)
    {
        await SendMessageToAsync(player, $"[서버] 잘못된 입력입니다. 1부터 {player.AvailableChoices.Count} 사이의 숫자를 입력하세요.");
        return;
    }
    
    // 선택 저장
    Player chosenAsker = player.AvailableChoices[choiceIndex - 1]; // 0-based index
    player.ChosenQuestion = chosenAsker; // "이 플레이어가 누구의 질문을 골랐는지" 저장
    
    Console.WriteLine($"[게임 로그] {player.Nickname}가 {chosenAsker.Nickname}의 질문을 선택함.");
    await SendMessageToAsync(player, "[서버] 힌트 선택이 완료되었습니다. 다른 플레이어들을 기다립니다...");

    // 모든 추리자가 선택을 완료했는지 확인
    int requiredChoices = players.Count - 1;
    int currentChoices = questionAskers.Count(p => p.ChosenQuestion != null);

    if (currentChoices == requiredChoices)
    {
        // 모든 선택 완료 -> '정답 추측' 단계로 전환
        Console.WriteLine("[게임 로그] 모든 플레이어가 힌트 선택 완료. 정답 유추 단계로 넘어갑니다.");
        currentGameState = GameState.WaitingForGuesses;
        
        await BroadcastMessageAsync("[서버] 모든 플레이어가 힌트 선택을 완료했습니다!");

        // 각 플레이어에게 개인화된 "최종 힌트" 전송
        foreach (Player askerPlayer in questionAskers)
        {
            var myData = currentGogaeData[askerPlayer];
            Player selectedAsker = askerPlayer.ChosenQuestion!; // 위에서 null이 아님을 확인했음
            var chosenData = currentGogaeData[selectedAsker];
            
            await SendMessageToAsync(askerPlayer, "---------- [최종 힌트] ----------");
            await SendMessageToAsync(askerPlayer, $"[내 질문] [{askerPlayer.Nickname}]: {myData.Question} -> ({myData.Reply})");
            await SendMessageToAsync(askerPlayer, $"[선택 질문] [{selectedAsker.Nickname}]: {chosenData.Question} -> ({chosenData.Reply})");
            await SendMessageToAsync(askerPlayer, "---------------------------------");
            await SendMessageToAsync(askerPlayer, $"[서버] {currentGogaeNumber}번째 고개의 정답을 입력하세요. (영어로)");
        }
        
        await SendMessageToAsync(presenter, "[서버] 플레이어들이 정답을 추측 중입니다...");
    }
}


/// <summary>
/// (WaitingForGuesses 상태) 추리자들의 '정답 추측' 입력을 처리합니다.
/// </summary>
async Task HandleGuessInputAsync(Player player, string message)
{
    Player presenter = players[currentTurnIndex];

    // 출제자 또는 질문 안 한 사람 차단
    if (player == presenter || !questionAskers.Contains(player))
    {
        await SendMessageToAsync(player, "[서버] 지금은 정답을 추측할 차례가 아닙니다.");
        return;
    }

    // 이미 이번 고개에 추측한 사람 차단
    if (player.Guesses.Count == currentGogaeNumber)
    {
        await SendMessageToAsync(player, "[서버] 이미 이번 고개에 정답을 제출했습니다. 다음 고개를 기다려주세요.");
        return;
    }
    
    // (방어 코드) 이전 고개 추측을 건너뛴 사람 차단
    if (player.Guesses.Count != currentGogaeNumber - 1)
    {
        await SendMessageToAsync(player, "[서버] 오류: 이전 고개의 정답 기록이 없습니다.");
        return;
    }

    // 추측 기록 저장 (정답 비교 시 대소문자 무시)
    player.Guesses.Add(message.ToLower());
    Console.WriteLine($"[게임 로그] {player.Nickname} {currentGogaeNumber}고개 추측: {message.ToLower()}");
    await SendMessageToAsync(player, "[서버] 정답 추측이 등록되었습니다.");

    // 모든 추측이 다 모였는지 확인
    int requiredGuesses = players.Count - 1; // 출제자 제외
    int currentGuesses = questionAskers.Count(p => p.Guesses.Count == currentGogaeNumber);

    if (currentGuesses == requiredGuesses)
    {
        // 모든 추측이 모임
        Console.WriteLine($"[게임 로그] {currentGogaeNumber}고개의 모든 추측이 수집되었습니다.");
        
        if (currentGogaeNumber < 5)
        {
            // 5고개가 아님 -> 다음 고개로
            currentGogaeNumber++;
            
            await BroadcastMessageAsync($"[서버] {currentGogaeNumber-1}고개가 종료되었습니다. {currentGogaeNumber}번째 고개를 시작합니다.");
            
            // 다음 고개를 위해 데이터 초기화
            currentGogaeData.Clear(); 
            questionAskers.Clear(); 
            currentReplyIndex = 0;
            foreach(var p in players.Where(p => p != presenter)) { // 추리자들의 선택지만 초기화
                p.AvailableChoices.Clear();
                p.ChosenQuestion = null;
            }
            
            currentGameState = GameState.WaitingForQuestions; // '질문 대기' 상태로 복귀
            
            string nextStepNotice = "[서버] 이제부터 출제자를 제외한 모든 플레이어는 '질문'을 입력해주세요. (한 사람당 1개, 영어로)";
            await BroadcastMessageAsync(nextStepNotice);
        }
        else
        {
            // 5고개 완료! -> 턴 종료 및 점수 계산
            Console.WriteLine($"[게임 로그] 5고개가 모두 종료되었습니다. 턴을 종료합니다.");
            await EndTurnAndCalculateScoresAsync();
        }
    }
}


/// <summary>
/// 턴을 종료하고, 기획안의 "연속 정답" 규칙에 따라 점수를 계산합니다.
/// (새로운 점수 규칙 적용됨)
/// </summary>
async Task EndTurnAndCalculateScoresAsync()
{
    await BroadcastMessageAsync("[서버] 5번의 고개가 모두 끝났습니다! 턴을 종료하고 점수를 계산합니다.");
    await BroadcastMessageAsync($"[서버] 이번 턴의 정답은 [ {currentAnswer} ]였습니다!");

    Player presenter = players[currentTurnIndex];
    List<Player> guessers = players.Where(p => p != presenter).ToList();
    
    // (플레이어, 획득 점수)
    Dictionary<Player, int> turnScores = new Dictionary<Player, int>();
    int maxGuesserContinuousRounds = 0; // 출제자 점수 계산을 위한 '최고 응시자 연속 라운드 수'

    await BroadcastMessageAsync("---------- [턴 결과] ----------");

    // 1. 응시자 점수 계산
    foreach (Player guesser in guessers)
    {
        // 수정된 CalculateGuesserContinuousRounds 함수를 호출하여 연속 유지 라운드 수를 얻습니다.
        int continuousRounds = CalculateGuesserContinuousRounds(guesser, currentAnswer.ToLower());
        
        // 규칙: 본인이 정답을 마지막 라운드로부터 연속 유지한 라운드 수 * 2점
        int score = continuousRounds * 2;
        
        guesser.TotalScore += score;
        turnScores[guesser] = score;

        string resultMessage;
        if (continuousRounds > 0)
        {
            resultMessage = $"[결과] {guesser.Nickname}: 마지막 라운드로부터 {continuousRounds}라운드 연속 정답 유지! (+{score}점, 총 {guesser.TotalScore}점)";
            
            // 출제자 점수를 위한 최고 기록 갱신
            if (continuousRounds > maxGuesserContinuousRounds)
            {
                maxGuesserContinuousRounds = continuousRounds;
            }
        }
        else
        {
            resultMessage = $"[결과] {guesser.Nickname}: 정답 맞추기 실패 (+0점, 총 {guesser.TotalScore}점)";
        }
        await BroadcastMessageAsync(resultMessage);
        Console.WriteLine(resultMessage);
    }
    
    // 2. 출제자 점수 계산
    // 규칙: 응시자 중 가장 오래 연속 유지한 라운드 수 * 1점
    int presenterScore = maxGuesserContinuousRounds * 1;
    presenter.TotalScore += presenterScore;
    turnScores[presenter] = presenterScore;
    
    string presenterResult = $"[결과] {presenter.Nickname} (출제자): 응시자 최고 기록 ({maxGuesserContinuousRounds}라운드 유지) 달성! (+{presenterScore}점, 총 {presenter.TotalScore}점)";
    await BroadcastMessageAsync(presenterResult);
    Console.WriteLine(presenterResult);
    
    await BroadcastMessageAsync("---------------------------------");
    
    // 3. 다음 턴 또는 게임 오버 확인
    currentTurnIndex++;
    if (currentTurnIndex < players.Count)
    {
        // 아직 턴이 남음 -> 다음 턴 진행
        await StartNextTurnAsync();
    }
    else
    {
        // 모든 플레이어가 턴을 마침 -> 게임 오버
        await EndGameAsync();
    }
}

/// <summary>
/// 기획안의 "연속 정답" 규칙을 계산합니다.
/// 응시자가 마지막 라운드로부터 몇 라운드 연속 정답을 유지했는지 반환합니다.
/// (예: "x-x-apple-apple-apple" -> 3라운드 연속 유지)
/// (새로운 점수 규칙 적용됨)
/// </summary>
/// <returns>마지막 라운드로부터 연속 정답을 유지한 라운드 수 (0-5). 못 맞히면 0.</returns>
int CalculateGuesserContinuousRounds(Player guesser, string correctAnswer)
{
    int continuousCount = 0;
    
    // 마지막 라운드(인덱스 4)부터 시작하여 역순으로 검사합니다.
    for (int i = 4; i >= 0; i--)
    {
        // 해당 라운드에 추측을 제출했고, 그 추측이 정답과 같으면
        if (guesser.Guesses.Count > i && guesser.Guesses[i] == correctAnswer)
        {
            continuousCount++; // 연속 카운트 증가
        }
        else
        {
            // 정답이 아니거나, 추측을 제출하지 않았으면 연속이 끊긴 것이므로 반복을 중단합니다.
            break; 
        }
    }
    return continuousCount;
}

/// <summary>
/// 다음 턴을 시작합니다.
/// </summary>
async Task StartNextTurnAsync()
{
    Console.WriteLine($"[게임 로그] 다음 턴({currentTurnIndex + 1})을 시작합니다.");
    await BroadcastMessageAsync($"[서버] 모든 점수 계산이 완료되었습니다. {currentTurnIndex + 1}번째 턴을 시작합니다.");

    // 턴 관련 데이터 초기화
    currentGogaeNumber = 1;
    currentGogaeData.Clear();
    questionAskers.Clear();
    currentReplyIndex = 0;
    
    // (Player의 TotalScore는 초기화하지 않음)
    
    currentGameState = GameState.WaitingForAnswer; 
    await StartTurnAsync(); // 새 출제자와 함께 턴 시작
}

/// <summary>
/// 게임을 종료하고 최종 순위를 발표한 뒤, 로비로 돌아갑니다.
/// </summary>
async Task EndGameAsync()
{
    Console.WriteLine("[게임 로그] 모든 턴이 종료되었습니다. 게임 오버.");
    await BroadcastMessageAsync("[서버] 모든 플레이어가 턴을 마쳤습니다! 게임이 종료되었습니다.");
    await BroadcastMessageAsync("---------- [최종 결과] ----------");

    // 점수에 따라 내림차순 정렬
    var finalRankings = players.OrderByDescending(p => p.TotalScore).ToList();
    
    for (int i = 0; i < finalRankings.Count; i++)
    {
        Player p = finalRankings[i];
        await BroadcastMessageAsync($"[ {i + 1} 위 ] {p.Nickname} (총 {p.TotalScore}점)");
    }
    
    await BroadcastMessageAsync("---------------------------------");
    await BroadcastMessageAsync("[서버] 잠시 후 로비로 돌아갑니다...");
    
    await Task.Delay(3000); // 3초 대기

    // 게임 상태 완전 리셋 (로비로 복귀)
    isGameRunning = false;
    currentGameState = GameState.Lobby;
    currentTurnIndex = 0;
    currentAnswer = "";
    currentGogaeNumber = 1;
    currentGogaeData.Clear();
    questionAskers.Clear();
    
    // 플레이어 데이터도 완전 리셋
    foreach(var p in players)
    {
        p.TotalScore = 0; 
        p.Guesses.Clear();
        p.AvailableChoices.Clear();
        p.ChosenQuestion = null;
    }
    
    await BroadcastMessageAsync("[서버] 로비로 돌아왔습니다. 방장은 /start로 새 게임을 시작할 수 있습니다.");
    Console.WriteLine("[게임 로그] 로비로 복귀.");
}


// --- 유틸리티 함수 ---

/// <summary>
/// 서버에 연결된 모든 플레이어에게 메시지를 전송합니다.
/// </summary>
async Task BroadcastMessageAsync(string message)
{
    byte[] buffer = Encoding.UTF8.GetBytes(message + Environment.NewLine);
    foreach (Player p in players)
    {
        try
        {
            NetworkStream stream = p.Client.GetStream();
            await stream.WriteAsync(buffer, 0, buffer.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error broadcasting to {p.Nickname}: {ex.Message}");
        }
    }
}

/// <summary>
/// 특정 플레이어 1명에게만 1:1 메시지를 전송합니다.
/// </summary>
async Task SendMessageToAsync(Player player, string message)
{
    Console.WriteLine($"Sending to {player.Nickname}: {message}"); // 서버 로그
    byte[] buffer = Encoding.UTF8.GetBytes(message + Environment.NewLine);
    try
    {
        NetworkStream stream = player.Client.GetStream();
        await stream.WriteAsync(buffer, 0, buffer.Length);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error sending to {player.Nickname}: {ex.Message}");
    }
}


// ===================================================
// 게임 로직에서 사용하는 데이터 클래스 및 열거형
// ===================================================

/// <summary>
/// 서버의 현재 상태를 정의하는 '상태 머신'
/// </summary>
enum GameState
{
    Lobby,              // 로비 (게임 시작 전)
    WaitingForAnswer,   // (턴 시작) 출제자의 "정답" 입력 대기
    WaitingForQuestions,// (1고개) 추리자들의 "질문" 입력 대기
    WaitingForReplies,  // (1고개) 출제자의 "답변(y/n)" 입력 대기
    WaitingForChoice,   // (1고개) 추리자들의 "힌트 선택" 입력 대기
    WaitingForGuesses   // (1고개) 추리자들의 "정답 추측" 입력 대기
}

/// <summary>
/// 플레이어 1명의 모든 데이터를 저장하는 클래스
/// </summary>
class Player
{
    /// <summary>
    /// 이 플레이어의 네트워크 연결 정보
    /// </summary>
    public TcpClient Client { get; }
    
    /// <summary>
    /// 플레이어의 닉네임
    /// </summary>
    public string Nickname { get; set; }
    
    /// <summary>
    /// 이 플레이어가 방장(호스트)인지 여부
    /// </summary>
    public bool IsHost { get; } 
    
    /// <summary>
    /// 이 플레이어의 '총 점수'
    /// </summary>
    public int TotalScore { get; set; } = 0;
    
    /// <summary>
    /// (턴 전용) 이 플레이어가 1~5고개 동안 제출한 정답 추측 목록
    /// </summary>
    public List<string> Guesses { get; } = new List<string>();
    
    /// <summary>
    /// (고개 전용) 이 플레이어가 '선택'할 수 있는 다른 플레이어의 질문 목록
    /// </summary>
    public List<Player> AvailableChoices { get; set; } = new List<Player>();
    
    /// <summary>
    /// (고개 전용) 이 플레이어가 '선택'한 다른 플레이어
    /// </summary>
    public Player? ChosenQuestion { get; set; } = null;
    
    public Player(TcpClient client, bool isHost)
    {
        this.Client = client;
        this.IsHost = isHost;
        this.Nickname = "Connecting..."; 
    }
}