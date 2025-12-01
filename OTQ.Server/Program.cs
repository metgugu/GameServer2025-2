using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Linq; 

// 콘솔 입출력 인코딩을 UTF-8로 강제 설정 (한글 깨짐 방지)
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// ==================================================================================
// [전역 변수]
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
    
    // 첫 번째로 들어온 사람(Count==0)이 방장
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

        // [추가됨] 입장 시 현재 인원 알림
        await BroadcastPlayerListAsync();

        if (player.IsHost)
        {
            await SendMessageToAsync(player, "[서버] 당신은 방장입니다. 3~4명이 모이면 '/게임시작'을 입력하세요.");
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
        // -----------------------------------------------------------
        // 플레이어 퇴장 및 게임 강제 종료 로직
        // -----------------------------------------------------------
        players.Remove(player);
        player.Client.Close();
        Console.WriteLine($"Player {player.Nickname} disconnected.");
        
        string leaveMessage = $"[서버] {player.Nickname}님이 퇴장했습니다.";
        await BroadcastMessageAsync(leaveMessage);

        // 1. 게임 진행 중에 나갔다면 -> 게임 강제 종료 및 리셋
        if (isGameRunning)
        {
            await BroadcastMessageAsync("[서버] 🚨 플레이어 이탈로 인해 게임을 강제 종료하고 로비로 돌아갑니다.");
            ResetGameData(); // 게임 데이터 초기화
            
            // [추가됨] 로비 복귀 후 남은 인원 알림
            await BroadcastPlayerListAsync();
        }
        else
        {
            // 게임 중이 아닐 때도 누군가 나가면 남은 인원 갱신
            await BroadcastPlayerListAsync();
        }

        // 2. 나간 사람이 방장이었다면 -> 다음 사람에게 방장 승계
        if (player.IsHost && players.Count > 0)
        {
            Player newHost = players[0];
            newHost.IsHost = true;
            await SendMessageToAsync(newHost, "[서버] 👑 이전 방장이 퇴장하여 당신이 새로운 방장이 되었습니다! (/게임시작 가능)");
            await BroadcastMessageAsync($"[서버] {newHost.Nickname}님이 새로운 방장이 되었습니다.");
            
            // 방장이 바뀌었으니 인원 목록 다시 보여줌 (방장 표시 갱신)
            await BroadcastPlayerListAsync();
        }
    }
}

// ----------------------------------------------------------------------
// [헬퍼 함수] 게임 데이터 리셋 (로비로 복귀)
// ----------------------------------------------------------------------
void ResetGameData()
{
    isGameRunning = false;
    currentGameState = GameState.Lobby;
    currentTurnIndex = 0;
    currentAnswer = "";
    currentGogaeNumber = 1;
    currentGogaeData.Clear();
    questionAskers.Clear();
    currentReplyIndex = 0;

    foreach (var p in players)
    {
        p.TotalScore = 0;
        p.Guesses.Clear();
        p.AvailableChoices.Clear();
        p.ChosenQuestion = null;
    }
}

// ----------------------------------------------------------------------
// [헬퍼 함수] 채팅인지 명령어인지 판별
// ----------------------------------------------------------------------
bool IsCommand(string message, out string content)
{
    if (message.StartsWith("/"))
    {
        content = message.Substring(1).Trim();
        return true;
    }
    content = message;
    return false;
}

// [추가됨] 현재 접속자 목록을 방송하는 함수
async Task BroadcastPlayerListAsync()
{
    if (players.Count == 0) return;

    var names = players.Select(p => p.Nickname + (p.IsHost ? "(방장)" : ""));
    string listMsg = $"[서버] 현재 접속자 ({players.Count}명): {string.Join(", ", names)}";
    await BroadcastMessageAsync(listMsg);
}

// ==================================================================================
// [게임 로직 함수들]
// ==================================================================================

async Task HandleLobbyMessageAsync(Player player, string message)
{
    string command = message.Trim();

    if (command == "/게임시작")
    {
        if (!player.IsHost)
        {
            await SendMessageToAsync(player, "[서버] 방장만 게임을 시작할 수 있습니다.");
        }
        else if (players.Count < 3) 
        {
            await SendMessageToAsync(player, $"[서버] 최소 3명이 필요합니다. (현재 {players.Count}명)");
        }
        else if (players.Count > 4)
        {
            await SendMessageToAsync(player, $"[서버] 최대 4명만 가능합니다. (현재 {players.Count}명)");
        }
        else
        {
            isGameRunning = true; 
            currentGameState = GameState.WaitingForAnswer; 
            currentTurnIndex = 0; 
            
            // 점수 초기화
            foreach(var p in players) { p.TotalScore = 0; }

            string startMessage = $"[서버] 게임을 시작합니다! (총 {players.Count}명)";
            await BroadcastMessageAsync(startMessage);
            
            await StartTurnAsync(); 
        }
    }
    // [추가됨] 인원 확인 명령어
    else if (command == "/인원" || command == "/users")
    {
        var names = players.Select(p => p.Nickname + (p.IsHost ? "(방장)" : ""));
        string listMsg = $"[서버] 현재 접속자 ({players.Count}명): {string.Join(", ", names)}";
        await SendMessageToAsync(player, listMsg);
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
    
    string turnMessage = $"[서버] {currentTurnIndex + 1}번째 턴을 시작합니다. 출제자: [ {questionSetter.Nickname} ]";
    await BroadcastMessageAsync(turnMessage);

    await SendMessageToAsync(questionSetter, "[서버] 당신은 출제자입니다. 정답을 입력할 때 앞에 '/'를 붙여주세요. (예: /사과)");
}

async Task HandleAnswerInputAsync(Player player, string message)
{
    Player questionSetter = players[currentTurnIndex];

    if (player != questionSetter)
    {
        await BroadcastMessageAsync($"[{player.Nickname}]: {message}");
        return;
    }

    if (IsCommand(message, out string content))
    {
        currentAnswer = content;
        Console.WriteLine($"[게임 로그] 정답 설정: {currentAnswer}");

        await SendMessageToAsync(player, $"[서버] 정답이 '{currentAnswer}'(으)로 설정되었습니다.");
        
        currentGogaeNumber = 1; 
        string notice = $"[서버] 정답 설정 완료! {currentGogaeNumber}번째 고개를 시작합니다.";
        await BroadcastMessageAsync(notice);

        currentGogaeData.Clear(); 
        questionAskers.Clear(); 
        
        currentGameState = GameState.WaitingForQuestions; 
        
        string nextStepNotice = "[서버] 출제자를 제외한 플레이어는 질문을 입력해주세요. (명령어: /질문내용)";
        await BroadcastMessageAsync(nextStepNotice);
    }
    else
    {
        await BroadcastMessageAsync($"[{player.Nickname}]: {message}");
        await SendMessageToAsync(player, "(팁: 정답을 설정하려면 '/정답' 처럼 앞에 슬래시를 붙이세요.)");
    }
}

async Task HandleQuestionInputAsync(Player player, string message)
{
    Player questionSetter = players[currentTurnIndex];

    if (player == questionSetter)
    {
        await BroadcastMessageAsync($"[{player.Nickname}]: {message}");
        return;
    }
    
    if (currentGogaeData.ContainsKey(player))
    {
        await BroadcastMessageAsync($"[{player.Nickname}]: {message}");
        return;
    }

    if (IsCommand(message, out string content))
    {
        currentGogaeData.Add(player, (content, "")); 
        questionAskers.Add(player); 
        
        Console.WriteLine($"[게임 로그] {player.Nickname} 질문 등록: {content}");
        await SendMessageToAsync(player, "[서버] 질문이 등록되었습니다.");

        int requiredQuestions = players.Count - 1; 
        int currentQuestions = currentGogaeData.Count;

        if (currentQuestions == requiredQuestions)
        {
            Console.WriteLine("[게임 로그] 모든 질문 수집 완료.");
            await BroadcastMessageAsync("[서버] 모든 질문 등록 완료! 출제자가 답변할 차례입니다.");
            
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
    else
    {
        await BroadcastMessageAsync($"[{player.Nickname}]: {message}");
        await SendMessageToAsync(player, "(팁: 질문을 등록하려면 '/질문내용' 처럼 앞에 슬래시를 붙이세요.)");
    }
}

async Task AskForNextReplyAsync()
{
    Player presenter = players[currentTurnIndex];
    Player asker = questionAskers[currentReplyIndex]; 
    string question = currentGogaeData[asker].Question;

    await SendMessageToAsync(presenter, $"---------- [질문 {currentReplyIndex + 1}/{questionAskers.Count}] ----------");
    await SendMessageToAsync(presenter, $"-> [{asker.Nickname}]: {question}");
    await SendMessageToAsync(presenter, "[서버] 답변을 입력하세요. (명령어: /예 또는 /아니오)");

    string waitMessage = $"[서버] 출제자가 [ {asker.Nickname} ]님의 질문에 답변하는 중입니다...";
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
        await BroadcastMessageAsync($"[{player.Nickname}]: {message}");
        return;
    }

    if (IsCommand(message, out string content))
    {
        string input = content.ToLower(); 
        bool isYes = (input == "예" || input == "y" || input == "yes" || input == "ㅇㅇ");
        bool isNo = (input == "아니오" || input == "아니요" || input == "n" || input == "no" || input == "ㄴㄴ");

        if (!isYes && !isNo)
        {
            await SendMessageToAsync(presenter, "[서버] 잘못된 입력입니다. '/예' 또는 '/아니오'로 답변해주세요.");
            return;
        }
        
        string reply = isYes ? "예" : "아니오"; 
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
            await BroadcastMessageAsync("[서버] 답변 완료! 이제 힌트를 선택할 차례입니다.");
            
            currentGameState = GameState.WaitingForChoice; 
            Player questionSetter = players[currentTurnIndex];
            
            foreach (Player askerPlayer in questionAskers)
            {
                var myData = currentGogaeData[askerPlayer];
                await SendMessageToAsync(askerPlayer, "---------- [힌트 선택] ----------");
                await SendMessageToAsync(askerPlayer, $"[내 질문] [{askerPlayer.Nickname}]: {myData.Question} -> ({myData.Reply})");
                await SendMessageToAsync(askerPlayer, "[서버] 추가로 확인할 질문의 번호를 입력하세요. (명령어: /1, /2 등)");

                var otherAskers = questionAskers.Where(p => p != askerPlayer).ToList();
                askerPlayer.AvailableChoices = otherAskers;
                
                for (int i = 0; i < otherAskers.Count; i++)
                {
                    var otherAsker = otherAskers[i];
                    var otherData = currentGogaeData[otherAsker];
                    await SendMessageToAsync(askerPlayer, $"{i + 1}. [{otherAsker.Nickname}]: {otherData.Question}");
                }
                await SendMessageToAsync(askerPlayer, "---------------------------------");
                
                askerPlayer.ChosenQuestion = null;
            }
            
            await SendMessageToAsync(questionSetter, "[서버] 플레이어들이 힌트를 선택 중입니다...");
        }
    }
    else
    {
        await BroadcastMessageAsync($"[{player.Nickname}]: {message}");
        await SendMessageToAsync(player, "(팁: 답변하려면 '/예' 또는 '/아니오' 처럼 앞에 슬래시를 붙이세요.)");
    }
}

async Task HandleChoiceInputAsync(Player player, string message)
{
    Player presenter = players[currentTurnIndex];

    if (player == presenter || !questionAskers.Contains(player))
    {
        await BroadcastMessageAsync($"[{player.Nickname}]: {message}");
        return;
    }
    
    if (player.ChosenQuestion != null)
    {
        await BroadcastMessageAsync($"[{player.Nickname}]: {message}");
        return;
    }

    if (IsCommand(message, out string content))
    {
        if (!int.TryParse(content, out int choiceIndex) || choiceIndex < 1 || choiceIndex > player.AvailableChoices.Count)
        {
            await SendMessageToAsync(player, $"[서버] 잘못된 번호입니다. 1~{player.AvailableChoices.Count} 사이의 숫자를 '/1' 처럼 입력하세요.");
            return;
        }
        
        Player chosenAsker = player.AvailableChoices[choiceIndex - 1]; 
        player.ChosenQuestion = chosenAsker; 
        
        Console.WriteLine($"[게임 로그] {player.Nickname}가 {chosenAsker.Nickname} 선택");
        await SendMessageToAsync(player, "[서버] 힌트 선택 완료. 대기 중...");

        int requiredChoices = players.Count - 1;
        int currentChoices = questionAskers.Count(p => p.ChosenQuestion != null);

        if (currentChoices == requiredChoices)
        {
            Console.WriteLine("[게임 로그] 힌트 선택 완료.");
            currentGameState = GameState.WaitingForGuesses;
            
            await BroadcastMessageAsync("[서버] 힌트 선택 완료! 정답 유추 단계입니다.");

            foreach (Player askerPlayer in questionAskers)
            {
                var myData = currentGogaeData[askerPlayer];
                Player selectedAsker = askerPlayer.ChosenQuestion!; 
                var chosenData = currentGogaeData[selectedAsker];
                
                await SendMessageToAsync(askerPlayer, "---------- [최종 힌트] ----------");
                await SendMessageToAsync(askerPlayer, $"[내 질문] [{askerPlayer.Nickname}]: {myData.Question} -> ({myData.Reply})");
                await SendMessageToAsync(askerPlayer, $"[선택 질문] [{selectedAsker.Nickname}]: {chosenData.Question} -> ({chosenData.Reply})");
                await SendMessageToAsync(askerPlayer, "---------------------------------");
                
                await SendMessageToAsync(askerPlayer, $"[서버] {currentGogaeNumber}번째 고개의 정답을 추측하세요. (명령어: /정답내용)");
            }
            
            await SendMessageToAsync(presenter, "[서버] 플레이어들이 정답을 추측 중입니다...");
        }
    }
    else
    {
        await BroadcastMessageAsync($"[{player.Nickname}]: {message}");
        await SendMessageToAsync(player, "(팁: 번호를 선택하려면 '/1' 처럼 앞에 슬래시를 붙이세요.)");
    }
}

async Task HandleGuessInputAsync(Player player, string message)
{
    Player presenter = players[currentTurnIndex];

    if (player == presenter || !questionAskers.Contains(player))
    {
        await BroadcastMessageAsync($"[{player.Nickname}]: {message}");
        return;
    }

    if (player.Guesses.Count == currentGogaeNumber)
    {
        await BroadcastMessageAsync($"[{player.Nickname}]: {message}");
        return;
    }
    
    if (player.Guesses.Count != currentGogaeNumber - 1)
    {
        await SendMessageToAsync(player, "[서버] 오류: 기록 불일치.");
        return;
    }

    if (IsCommand(message, out string content))
    {
        player.Guesses.Add(content.ToLower()); 
        Console.WriteLine($"[게임 로그] {player.Nickname} 추측: {content}");
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
                
                string nextStepNotice = "[서버] 질문을 입력해주세요. (명령어: /질문내용)";
                await BroadcastMessageAsync(nextStepNotice);
            }
            else
            {
                Console.WriteLine($"[게임 로그] 5고개 종료.");
                await EndTurnAndCalculateScoresAsync();
            }
        }
    }
    else
    {
        await BroadcastMessageAsync($"[{player.Nickname}]: {message}");
        await SendMessageToAsync(player, "(팁: 정답을 제출하려면 '/정답' 처럼 앞에 슬래시를 붙이세요.)");
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
    }
    
    int presenterScore = maxGuesserContinuousRounds * 1;
    presenter.TotalScore += presenterScore;
    
    string presenterResult = $"[결과] {presenter.Nickname} (출제자): 응시자 최고 기록 {maxGuesserContinuousRounds}라운드! (+{presenterScore}점, 총 {presenter.TotalScore}점)";
    await BroadcastMessageAsync(presenterResult);
    
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
        if (guesser.Guesses.Count > i && guesser.Guesses[i].Trim().ToLower() == correctAnswer.Trim().ToLower())
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
    await BroadcastMessageAsync($"[서버] {currentTurnIndex + 1}번째 턴을 시작합니다.");

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
    await BroadcastMessageAsync("[서버] 로비로 돌아갑니다. /게임시작으로 다시 시작하세요.");
    
    // 게임 리셋 (로비 복귀)
    ResetGameData();
    Console.WriteLine("[게임 로그] 로비 복귀.");
    
    // [추가됨] 게임 종료 후 로비 복귀 시에도 인원 목록 표시
    await BroadcastPlayerListAsync();
}

async Task BroadcastMessageAsync(string message)
{
    byte[] buffer = Encoding.UTF8.GetBytes(message + Environment.NewLine);
    // 컬렉션 복사본을 사용하여 전송 중 players 변경(퇴장)에 안전하게 대비
    foreach (Player p in players.ToList()) 
    {
        try
        {
            if (p.Client.Connected)
            {
                NetworkStream stream = p.Client.GetStream();
                await stream.WriteAsync(buffer, 0, buffer.Length);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error broadcasting to {p.Nickname}: {ex.Message}");
        }
    }
}

async Task SendMessageToAsync(Player player, string message)
{
    byte[] buffer = Encoding.UTF8.GetBytes(message + Environment.NewLine);
    try
    {
        if (player.Client.Connected)
        {
            NetworkStream stream = player.Client.GetStream();
            await stream.WriteAsync(buffer, 0, buffer.Length);
        }
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
    // IsHost를 set 가능하게 변경하여 방장 승계 기능 지원
    public bool IsHost { get; set; } 
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