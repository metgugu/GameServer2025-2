using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Linq; 

// Windows 터미널에서 한글 입력을 올바르게 처리하기 위해 UTF-8을 기본으로 설정합니다.
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// ==================================================================================
// [전역 변수] 서버의 상태 및 데이터 관리
// ==================================================================================

List<Player> players = new List<Player>();
bool isGameRunning = false; 
GameState currentGameState = GameState.Lobby; 
int currentTurnIndex = 0; 
string currentAnswer = ""; 
Dictionary<Player, (string Question, string Reply)> currentGogaeData = new();
List<Player> questionAskers = new();
int currentReplyIndex = 0;
int currentGogaeNumber = 1; 

// ==================================================================================
// [서버 시작]
// ==================================================================================

Console.WriteLine("Starting server...");
TcpListener listener = new TcpListener(IPAddress.Any, 9000);
listener.Start();
Console.WriteLine($"Server started. Listening on {listener.LocalEndpoint}...");

while (true)
{
    TcpClient client = await listener.AcceptTcpClientAsync();

    if (isGameRunning)
    {
        Console.WriteLine("Client tried to connect, but game is running. Rejected.");
        client.Close(); 
        continue;
    }

    Console.WriteLine("Client connected, waiting for nickname...");
    
    Player newPlayer = new Player(client, players.Count == 0); 
    players.Add(newPlayer);
    
    _ = HandleClientAsync(newPlayer);
}

// ==================================================================================
// [클라이언트 핸들러]
// ==================================================================================

async Task HandleClientAsync(Player player)
{
    NetworkStream stream = player.Client.GetStream();
    StreamReader reader = new StreamReader(stream, Encoding.UTF8);

    try
    {
        string? nickname = await reader.ReadLineAsync();
        if (string.IsNullOrEmpty(nickname)) throw new Exception("Invalid nickname");
        player.Nickname = nickname;
        
        Console.WriteLine($"Player set nickname: {player.Nickname} (Host: {player.IsHost})");
        
        string joinMessage = $"[서버] {player.Nickname}님이 입장했습니다.";
        Console.WriteLine($"Broadcasting: {joinMessage}"); 
        await BroadcastMessageAsync(joinMessage);

        if (player.IsHost)
        {
            await SendMessageToAsync(player, "[서버] 당신은 방장(호스트)입니다. 3~4명이 모이면 /start 를 입력하여 게임을 시작하세요.");
        }
        else
        {
            await SendMessageToAsync(player, "[서버] 대기실에 입장했습니다. 방장이 게임을 시작할 때까지 기다려주세요.");
        }

        while (player.Client.Connected)
        {
            string? message = await reader.ReadLineAsync();
            if (message == null) break; 

            Console.WriteLine($"[{player.Nickname}]: {message}"); 

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
        players.Remove(player);
        player.Client.Close();
        Console.WriteLine($"Player {player.Nickname} disconnected.");
        
        string leaveMessage = $"[서버] {player.Nickname}님이 퇴장했습니다.";
        Console.WriteLine($"Broadcasting: {leaveMessage}"); 
        await BroadcastMessageAsync(leaveMessage);
    }
}

// ==================================================================================
// [게임 로직 함수들]
// ==================================================================================

async Task HandleLobbyMessageAsync(Player player, string message)
{
    if (message.ToLower() == "/start")
    {
        if (!player.IsHost)
        {
            await SendMessageToAsync(player, "[서버] 방장(호스트)만 게임을 시작할 수 있습니다.");
        }
        else if (players.Count < 3) 
        {
            await SendMessageToAsync(player, $"[서버] 최소 3명의 플레이어가 필요합니다. (현재 {players.Count}명)");
        }
        else if (players.Count > 4)
        {
            await SendMessageToAsync(player, $"[서버] 최대 4명의 플레이어만 가능합니다. (현재 {players.Count}명)");
        }
        else
        {
            isGameRunning = true; 
            currentGameState = GameState.WaitingForAnswer; 
            currentTurnIndex = 0; 
            
            foreach(var p in players) { p.TotalScore = 0; }

            string startMessage = $"[서버] 게임을 시작합니다! (총 {players.Count}명)";
            Console.WriteLine($"Broadcasting: {startMessage}");
            await BroadcastMessageAsync(startMessage);
            
            await StartTurnAsync(); 
        }
    }
    else
    {
        string chatMessage = $"[{player.Nickname}]: {message}";
        await BroadcastMessageAsync(chatMessage);
    }
}

async Task StartTurnAsync()
{
    Player questionSetter = players[currentTurnIndex];
    currentAnswer = ""; 
    
    foreach(Player p in players)
    {
        p.Guesses.Clear();
        p.AvailableChoices.Clear();
        p.ChosenQuestion = null;
    }
    
    string turnMessage = $"[서버] {currentTurnIndex + 1}번째 턴을 시작합니다. 이번 출제자는 [ {questionSetter.Nickname} ]님입니다.";
    Console.WriteLine($"Broadcasting: {turnMessage}");
    await BroadcastMessageAsync(turnMessage);

    // [수정됨] 영어 입력 요청 삭제
    await SendMessageToAsync(questionSetter, "[서버] 당신은 출제자입니다. 정답을 입력하세요: ");
}

async Task HandleAnswerInputAsync(Player player, string message)
{
    Player questionSetter = players[currentTurnIndex];

    if (player == questionSetter)
    {
        currentAnswer = message.Trim();
        Console.WriteLine($"[게임 로그] 정답 설정: {currentAnswer}");

        await SendMessageToAsync(player, $"[서버] 정답이 '{currentAnswer}'(으)로 설정되었습니다.");
        
        currentGogaeNumber = 1; 
        
        string notice = $"[서버] 출제자가 정답을 설정했습니다. {currentGogaeNumber}번째 고개를 시작합니다.";
        Console.WriteLine($"Broadcasting: {notice}");
        await BroadcastMessageAsync(notice);

        currentGogaeData.Clear(); 
        questionAskers.Clear(); 
        
        currentGameState = GameState.WaitingForQuestions; 
        
        // [수정됨] 영어 입력 요청 삭제
        string nextStepNotice = "[서버] 이제부터 출제자를 제외한 모든 플레이어는 '질문'을 입력해주세요. (한 사람당 1개)";
        Console.WriteLine($"Broadcasting: {nextStepNotice}");
        await BroadcastMessageAsync(nextStepNotice);
    }
    else
    {
        await SendMessageToAsync(player, "[서버] 지금은 출제자가 정답을 입력할 차례입니다. 잠시만 기다려주세요.");
    }
}

async Task HandleQuestionInputAsync(Player player, string message)
{
    Player questionSetter = players[currentTurnIndex];

    if (player == questionSetter)
    {
        await SendMessageToAsync(player, "[서버] 당신은 출제자입니다. 다른 플레이어들이 질문을 입력할 때까지 기다려주세요.");
        return;
    }
    
    if (currentGogaeData.ContainsKey(player))
    {
        await SendMessageToAsync(player, "[서버] 이미 이번 고개에 질문을 제출했습니다. 답변을 기다려주세요.");
        return;
    }

    currentGogaeData.Add(player, (message, "")); 
    questionAskers.Add(player); 
    
    Console.WriteLine($"[게임 로그] {player.Nickname} 질문 등록: {message}");
    await SendMessageToAsync(player, "[서버] 질문이 등록되었습니다.");

    int requiredQuestions = players.Count - 1; 
    int currentQuestions = currentGogaeData.Count;

    if (currentQuestions == requiredQuestions)
    {
        Console.WriteLine("[게임 로그] 모든 질문 수집 완료.");
        await BroadcastMessageAsync("[서버] 모든 플레이어의 질문이 등록되었습니다. 출제자가 답변할 차례입니다.");
        
        currentReplyIndex = 0; 
        currentGameState = GameState.WaitingForReplies;
        
        await AskForNextReplyAsync(); 
    }
    else
    {
        int remaining = requiredQuestions - currentQuestions;
        await BroadcastMessageAsync($"[서버] 남은 질문: {remaining}개");
    }
}

async Task AskForNextReplyAsync()
{
    Player presenter = players[currentTurnIndex];
    Player asker = questionAskers[currentReplyIndex]; 
    string question = currentGogaeData[asker].Question;

    await SendMessageToAsync(presenter, $"---------- [질문 {currentReplyIndex + 1}/{questionAskers.Count}] ----------");
    await SendMessageToAsync(presenter, $"-> [{asker.Nickname}]: {question}");
    
    // [수정됨] 예/아니오 답변 요청
    await SendMessageToAsync(presenter, "[서버] '예' 또는 '아니오'로 답변하세요.");

    string waitMessage = $"[서버] 출제자가 [ {asker.Nickname} ]님의 질문에 답변하는 중입니다...";
    Console.WriteLine($"Broadcasting: {waitMessage}");
    
    foreach (Player p in players.Where(p => p != presenter))
    {
        await SendMessageToAsync(p, waitMessage);
    }
}

async Task HandleReplyInputAsync(Player player, string message)
{
    Player presenter = players[currentTurnIndex];

    if (player != presenter)
    {
        await SendMessageToAsync(player, "[서버] 지금은 출제자가 질문에 답변하는 중입니다. 잠시만 기다려주세요.");
        return;
    }

    // [수정됨] 한글 '예/아니오' 및 영어 'y/n' 모두 허용 로직
    string input = message.Trim().ToLower(); 
    bool isYes = (input == "예" || input == "y" || input == "yes" || input == "ㅇㅇ");
    bool isNo = (input == "아니오" || input == "아니요" || input == "n" || input == "no" || input == "ㄴㄴ");

    if (!isYes && !isNo)
    {
        await SendMessageToAsync(presenter, "[서버] 잘못된 입력입니다. '예' 또는 '아니오'로 답변해주세요.");
        return;
    }
    
    string reply = isYes ? "예" : "아니오"; // 저장할 땐 통일된 한글로 저장
    Player asker = questionAskers[currentReplyIndex]; 
    currentGogaeData[asker] = (currentGogaeData[asker].Question, reply); 
    
    Console.WriteLine($"[게임 로그] 답변 저장: {reply}");
    await SendMessageToAsync(presenter, $"[서버] '{reply}'(으)로 답변이 저장되었습니다.");

    currentReplyIndex++;

    if (currentReplyIndex < questionAskers.Count)
    {
        await AskForNextReplyAsync();
    }
    else
    {
        Console.WriteLine("[게임 로그] 모든 답변 수집 완료.");
        await BroadcastMessageAsync("[서버] 출제자가 모든 질문에 답변했습니다! 이제 힌트를 선택할 차례입니다.");
        
        currentGameState = GameState.WaitingForChoice; 
        Player questionSetter = players[currentTurnIndex];
        
        foreach (Player askerPlayer in questionAskers)
        {
            var myData = currentGogaeData[askerPlayer];
            await SendMessageToAsync(askerPlayer, "---------- [힌트 선택] ----------");
            await SendMessageToAsync(askerPlayer, $"[내 질문] [{askerPlayer.Nickname}]: {myData.Question} -> ({myData.Reply})");
            await SendMessageToAsync(askerPlayer, "[서버] 아래에서 추가로 확인할 질문 1개를 숫자로 선택하세요.");

            var otherAskers = questionAskers.Where(p => p != askerPlayer).ToList();
            askerPlayer.AvailableChoices = otherAskers;
            
            for (int i = 0; i < otherAskers.Count; i++)
            {
                var otherAsker = otherAskers[i];
                var otherData = currentGogaeData[otherAsker];
                await SendMessageToAsync(askerPlayer, $"{i + 1}. [{otherAsker.Nickname}]: {otherData.Question}");
            }
            await SendMessageToAsync(askerPlayer, "---------------------------------");
            await SendMessageToAsync(askerPlayer, $"숫자(1~{otherAskers.Count})를 입력하세요: ");
            
            askerPlayer.ChosenQuestion = null;
        }
        
        await SendMessageToAsync(questionSetter, "[서버] 플레이어들이 힌트를 선택 중입니다. 잠시만 기다려주세요...");
    }
}

async Task HandleChoiceInputAsync(Player player, string message)
{
    Player presenter = players[currentTurnIndex];

    if (player == presenter || !questionAskers.Contains(player))
    {
        await SendMessageToAsync(player, "[서버] 지금은 힌트를 선택할 차례가 아닙니다.");
        return;
    }
    
    if (player.ChosenQuestion != null)
    {
        await SendMessageToAsync(player, "[서버] 이미 힌트 선택을 완료했습니다.");
        return;
    }

    if (!int.TryParse(message, out int choiceIndex) || choiceIndex < 1 || choiceIndex > player.AvailableChoices.Count)
    {
        await SendMessageToAsync(player, $"[서버] 잘못된 입력입니다. 1부터 {player.AvailableChoices.Count} 사이의 숫자를 입력하세요.");
        return;
    }
    
    Player chosenAsker = player.AvailableChoices[choiceIndex - 1]; 
    player.ChosenQuestion = chosenAsker; 
    
    Console.WriteLine($"[게임 로그] {player.Nickname}가 {chosenAsker.Nickname} 선택");
    await SendMessageToAsync(player, "[서버] 힌트 선택이 완료되었습니다. 대기 중...");

    int requiredChoices = players.Count - 1;
    int currentChoices = questionAskers.Count(p => p.ChosenQuestion != null);

    if (currentChoices == requiredChoices)
    {
        Console.WriteLine("[게임 로그] 힌트 선택 완료.");
        currentGameState = GameState.WaitingForGuesses;
        
        await BroadcastMessageAsync("[서버] 모든 플레이어가 힌트 선택을 완료했습니다!");

        foreach (Player askerPlayer in questionAskers)
        {
            var myData = currentGogaeData[askerPlayer];
            Player selectedAsker = askerPlayer.ChosenQuestion!; 
            var chosenData = currentGogaeData[selectedAsker];
            
            await SendMessageToAsync(askerPlayer, "---------- [최종 힌트] ----------");
            await SendMessageToAsync(askerPlayer, $"[내 질문] [{askerPlayer.Nickname}]: {myData.Question} -> ({myData.Reply})");
            await SendMessageToAsync(askerPlayer, $"[선택 질문] [{selectedAsker.Nickname}]: {chosenData.Question} -> ({chosenData.Reply})");
            await SendMessageToAsync(askerPlayer, "---------------------------------");
            
            // [수정됨] 영어 입력 요청 삭제
            await SendMessageToAsync(askerPlayer, $"[서버] {currentGogaeNumber}번째 고개의 정답을 입력하세요.");
        }
        
        await SendMessageToAsync(presenter, "[서버] 플레이어들이 정답을 추측 중입니다...");
    }
}

async Task HandleGuessInputAsync(Player player, string message)
{
    Player presenter = players[currentTurnIndex];

    if (player == presenter || !questionAskers.Contains(player))
    {
        await SendMessageToAsync(player, "[서버] 지금은 정답을 추측할 차례가 아닙니다.");
        return;
    }

    if (player.Guesses.Count == currentGogaeNumber)
    {
        await SendMessageToAsync(player, "[서버] 이미 정답을 제출했습니다. 대기해주세요.");
        return;
    }
    
    if (player.Guesses.Count != currentGogaeNumber - 1)
    {
        await SendMessageToAsync(player, "[서버] 오류: 이전 고개 기록 없음.");
        return;
    }

    player.Guesses.Add(message.Trim().ToLower());
    Console.WriteLine($"[게임 로그] {player.Nickname} 추측: {message}");
    await SendMessageToAsync(player, "[서버] 정답 추측이 등록되었습니다.");

    int requiredGuesses = players.Count - 1; 
    int currentGuesses = questionAskers.Count(p => p.Guesses.Count == currentGogaeNumber);

    if (currentGuesses == requiredGuesses)
    {
        Console.WriteLine($"[게임 로그] {currentGogaeNumber}고개 종료.");
        
        if (currentGogaeNumber < 5)
        {
            currentGogaeNumber++;
            
            await BroadcastMessageAsync($"[서버] {currentGogaeNumber-1}고개가 종료되었습니다. {currentGogaeNumber}번째 고개를 시작합니다.");
            
            currentGogaeData.Clear(); 
            questionAskers.Clear(); 
            currentReplyIndex = 0;
            foreach(var p in players.Where(p => p != presenter)) { 
                p.AvailableChoices.Clear();
                p.ChosenQuestion = null;
            }
            
            currentGameState = GameState.WaitingForQuestions; 
            
            // [수정됨] 영어 입력 요청 삭제
            string nextStepNotice = "[서버] 이제부터 출제자를 제외한 모든 플레이어는 '질문'을 입력해주세요. (한 사람당 1개)";
            await BroadcastMessageAsync(nextStepNotice);
        }
        else
        {
            Console.WriteLine($"[게임 로그] 5고개 종료.");
            await EndTurnAndCalculateScoresAsync();
        }
    }
}

async Task EndTurnAndCalculateScoresAsync()
{
    await BroadcastMessageAsync("[서버] 5번의 고개가 모두 끝났습니다! 결과를 발표합니다.");
    await BroadcastMessageAsync($"[서버] 이번 턴의 정답은 [ {currentAnswer} ]였습니다!");

    Player presenter = players[currentTurnIndex];
    List<Player> guessers = players.Where(p => p != presenter).ToList();
    
    int maxGuesserContinuousRounds = 0; 

    await BroadcastMessageAsync("---------- [턴 결과] ----------");

    foreach (Player guesser in guessers)
    {
        int continuousRounds = CalculateGuesserContinuousRounds(guesser, currentAnswer.ToLower());
        int score = continuousRounds * 2;
        
        guesser.TotalScore += score;

        string resultMessage;
        if (continuousRounds > 0)
        {
            resultMessage = $"[결과] {guesser.Nickname}: {continuousRounds}라운드 연속 정답! (+{score}점, 총 {guesser.TotalScore}점)";
            if (continuousRounds > maxGuesserContinuousRounds) maxGuesserContinuousRounds = continuousRounds;
        }
        else
        {
            resultMessage = $"[결과] {guesser.Nickname}: 실패 (+0점, 총 {guesser.TotalScore}점)";
        }
        await BroadcastMessageAsync(resultMessage);
        Console.WriteLine(resultMessage);
    }
    
    int presenterScore = maxGuesserContinuousRounds * 1;
    presenter.TotalScore += presenterScore;
    
    string presenterResult = $"[결과] {presenter.Nickname} (출제자): 응시자 최고 기록 {maxGuesserContinuousRounds}라운드! (+{presenterScore}점, 총 {presenter.TotalScore}점)";
    await BroadcastMessageAsync(presenterResult);
    Console.WriteLine(presenterResult);
    
    await BroadcastMessageAsync("---------------------------------");
    
    currentTurnIndex++;
    if (currentTurnIndex < players.Count)
    {
        await StartNextTurnAsync();
    }
    else
    {
        await EndGameAsync();
    }
}

int CalculateGuesserContinuousRounds(Player guesser, string correctAnswer)
{
    int continuousCount = 0;
    for (int i = 4; i >= 0; i--)
    {
        if (guesser.Guesses.Count > i && guesser.Guesses[i] == correctAnswer)
        {
            continuousCount++; 
        }
        else
        {
            break; 
        }
    }
    return continuousCount;
}

async Task StartNextTurnAsync()
{
    Console.WriteLine($"[게임 로그] 다음 턴({currentTurnIndex + 1}) 시작.");
    await BroadcastMessageAsync($"[서버] 모든 점수 계산이 완료되었습니다. {currentTurnIndex + 1}번째 턴을 시작합니다.");

    currentGogaeNumber = 1;
    currentGogaeData.Clear();
    questionAskers.Clear();
    currentReplyIndex = 0;
    
    currentGameState = GameState.WaitingForAnswer; 
    await StartTurnAsync(); 
}

async Task EndGameAsync()
{
    Console.WriteLine("[게임 로그] 게임 오버.");
    await BroadcastMessageAsync("[서버] 모든 턴이 끝났습니다! 최종 결과 발표!");
    await BroadcastMessageAsync("---------- [최종 결과] ----------");

    var finalRankings = players.OrderByDescending(p => p.TotalScore).ToList();
    
    for (int i = 0; i < finalRankings.Count; i++)
    {
        Player p = finalRankings[i];
        await BroadcastMessageAsync($"[ {i + 1} 위 ] {p.Nickname} (총 {p.TotalScore}점)");
    }
    
    await BroadcastMessageAsync("---------------------------------");
    await BroadcastMessageAsync("[서버] 3초 후 로비로 돌아갑니다...");
    
    await Task.Delay(3000); 

    isGameRunning = false;
    currentGameState = GameState.Lobby;
    currentTurnIndex = 0;
    currentAnswer = "";
    currentGogaeNumber = 1;
    currentGogaeData.Clear();
    questionAskers.Clear();
    
    foreach(var p in players)
    {
        p.TotalScore = 0; 
        p.Guesses.Clear();
        p.AvailableChoices.Clear();
        p.ChosenQuestion = null;
    }
    
    await BroadcastMessageAsync("[서버] 로비로 돌아왔습니다. /start로 다시 시작하세요.");
    Console.WriteLine("[게임 로그] 로비 복귀.");
}

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

async Task SendMessageToAsync(Player player, string message)
{
    Console.WriteLine($"Sending to {player.Nickname}: {message}"); 
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

enum GameState
{
    Lobby,
    WaitingForAnswer,
    WaitingForQuestions,
    WaitingForReplies,
    WaitingForChoice,
    WaitingForGuesses
}

class Player
{
    public TcpClient Client { get; }
    public string Nickname { get; set; }
    public bool IsHost { get; } 
    public int TotalScore { get; set; } = 0;
    public List<string> Guesses { get; } = new List<string>();
    public List<Player> AvailableChoices { get; set; } = new List<Player>();
    public Player? ChosenQuestion { get; set; } = null;
    
    public Player(TcpClient client, bool isHost)
    {
        this.Client = client;
        this.IsHost = isHost;
        this.Nickname = "Connecting..."; 
    }
}