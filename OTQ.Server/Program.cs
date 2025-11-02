using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Linq; 

// [수정] Windows 터미널의 한글 깨짐 현상을 해결합니다.
Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// --- 전역 변수 ---
List<Player> players = new List<Player>();
bool isGameRunning = false; 
GameState currentGameState = GameState.Lobby; 
int currentTurnIndex = 0; 
string currentAnswer = ""; 
Dictionary<Player, (string Question, string Reply)> currentGogaeData = new();
List<Player> questionAskers = new();
int currentReplyIndex = 0;
int currentGogaeNumber = 1; 
// ---------------

Console.WriteLine("Starting server on 127.0.0.1:9000...");
TcpListener listener = new TcpListener(IPAddress.Parse("127.0.0.1"), 9000);
listener.Start();

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

async Task HandleClientAsync(Player player)
{
    NetworkStream stream = player.Client.GetStream();
    StreamReader reader = new StreamReader(stream, Encoding.UTF8);

    try
    {
        // 1. 닉네임 설정
        string? nickname = await reader.ReadLineAsync();
        if (string.IsNullOrEmpty(nickname))
        {
            throw new Exception("Invalid nickname");
        }
        player.Nickname = nickname;
        
        Console.WriteLine($"Player set nickname: {player.Nickname} (Host: {player.IsHost})");
        
        string joinMessage = $"[서버] {player.Nickname}님이 입장했습니다.";
        Console.WriteLine($"Broadcasting: {joinMessage}"); 
        await BroadcastMessageAsync(joinMessage);

        // 2. 채팅 및 게임 로직 처리 루프
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
        
        // [STEP 7] TODO: 게임 중 퇴장 시 처리 (예: 게임 강제 종료)
    }
}

// ... (HandleLobbyMessageAsync, StartTurnAsync, HandleAnswerInputAsync, HandleQuestionInputAsync, AskForNextReplyAsync, HandleReplyInputAsync, HandleChoiceInputAsync 는 수정 없음) ...
// (가독성을 위해 HandleGuessInputAsync와 신규 함수들만 아래에 작성합니다. 위 함수들은 STEP 6의 코드를 그대로 사용하세요.)


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
            // [STEP 7] 게임 시작 시 모든 플레이어 점수 초기화
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
    
    // [STEP 7] 턴 시작 시 '추측' 기록만 초기화
    foreach(Player p in players)
    {
        p.Guesses.Clear();
        p.AvailableChoices.Clear();
        p.ChosenQuestion = null;
    }
    
    string turnMessage = $"[서버] {currentTurnIndex + 1}번째 턴을 시작합니다. 이번 출제자는 [ {questionSetter.Nickname} ]님입니다.";
    Console.WriteLine($"Broadcasting: {turnMessage}");
    await BroadcastMessageAsync(turnMessage);

    await SendMessageToAsync(questionSetter, "[서버] 당신은 출제자입니다. 정답을 입력하세요 (테스트는 영어로): ");
}

async Task HandleAnswerInputAsync(Player player, string message)
{
    Player questionSetter = players[currentTurnIndex];

    if (player == questionSetter)
    {
        currentAnswer = message;
        Console.WriteLine($"[게임 로그] 출제자({questionSetter.Nickname})가 정답 설정: {currentAnswer}");

        await SendMessageToAsync(player, $"[서버] 정답이 '{currentAnswer}'(으)로 설정되었습니다.");
        
        currentGogaeNumber = 1; 
        
        string notice = $"[서버] 출제자가 정답을 설정했습니다. {currentGogaeNumber}번째 고개를 시작합니다.";
        Console.WriteLine($"Broadcasting: {notice}");
        await BroadcastMessageAsync(notice);

        currentGogaeData.Clear(); 
        questionAskers.Clear(); 
        
        currentGameState = GameState.WaitingForQuestions; 
        
        string nextStepNotice = "[서버] 이제부터 출제자를 제외한 모든 플레이어는 '질문'을 입력해주세요. (한 사람당 1개, 영어로)";
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
        Console.WriteLine("[게임 로그] 모든 질문이 수집되었습니다. 답변 단계로 넘어갑니다.");
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
    await SendMessageToAsync(presenter, "[서버] 'y'(예) 또는 'n'(아니오)로 답변하세요: (영어로)");

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

    string input = message.ToLower();
    if (input != "y" && input != "n")
    {
        await SendMessageToAsync(presenter, "[서버] 잘못된 입력입니다. 'y' 또는 'n'으로만 답변해야 합니다: (영어로)");
        return;
    }
    
    string reply = (input == "y") ? "Yes" : "No";
    Player asker = questionAskers[currentReplyIndex]; 
    currentGogaeData[asker] = (currentGogaeData[asker].Question, reply); 
    
    Console.WriteLine($"[게임 로그] 출제자가 {asker.Nickname}의 질문({currentGogaeData[asker].Question})에 '{reply}'로 답변함.");
    await SendMessageToAsync(presenter, $"[서버] '{reply}' (으)로 답변이 저장되었습니다.");

    currentReplyIndex++;

    if (currentReplyIndex < questionAskers.Count)
    {
        await AskForNextReplyAsync();
    }
    else
    {
        Console.WriteLine("[게임 로그] 모든 답변 수집 완료. '답변 선택' 단계로 넘어갑니다.");
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
        await SendMessageToAsync(player, "[서버] 이미 힌트 선택을 완료했습니다. 정답을 입력할 차례를 기다려주세요.");
        return;
    }

    if (!int.TryParse(message, out int choiceIndex) || choiceIndex < 1 || choiceIndex > player.AvailableChoices.Count)
    {
        await SendMessageToAsync(player, $"[서버] 잘못된 입력입니다. 1부터 {player.AvailableChoices.Count} 사이의 숫자를 입력하세요.");
        return;
    }
    
    Player chosenAsker = player.AvailableChoices[choiceIndex - 1]; 
    player.ChosenQuestion = chosenAsker; 
    
    Console.WriteLine($"[게임 로그] {player.Nickname}가 {chosenAsker.Nickname}의 질문을 선택함.");
    await SendMessageToAsync(player, "[서버] 힌트 선택이 완료되었습니다. 다른 플레이어들을 기다립니다...");

    int requiredChoices = players.Count - 1;
    int currentChoices = questionAskers.Count(p => p.ChosenQuestion != null);

    if (currentChoices == requiredChoices)
    {
        Console.WriteLine("[게임 로그] 모든 플레이어가 힌트 선택 완료. 정답 유추 단계로 넘어갑니다.");
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
            await SendMessageToAsync(askerPlayer, $"[서버] {currentGogaeNumber}번째 고개의 정답을 입력하세요. (영어로)");
        }
        
        await SendMessageToAsync(presenter, "[서버] 플레이어들이 정답을 추측 중입니다...");
    }
}


// [STEP 7] HandleGuessInputAsync 수정
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
        await SendMessageToAsync(player, "[서버] 이미 이번 고개에 정답을 제출했습니다. 다음 고개를 기다려주세요.");
        return;
    }
    
    if (player.Guesses.Count != currentGogaeNumber - 1)
    {
        await SendMessageToAsync(player, "[서버] 오류: 이전 고개의 정답 기록이 없습니다.");
        return;
    }

    // [STEP 7] 정답 비교 시 대소문자 무시 (영문 테스트용)
    player.Guesses.Add(message.ToLower());
    Console.WriteLine($"[게임 로그] {player.Nickname} {currentGogaeNumber}고개 추측: {message.ToLower()}");
    await SendMessageToAsync(player, "[서버] 정답 추측이 등록되었습니다.");

    int requiredGuesses = players.Count - 1; 
    int currentGuesses = questionAskers.Count(p => p.Guesses.Count == currentGogaeNumber);

    if (currentGuesses == requiredGuesses)
    {
        Console.WriteLine($"[게임 로그] {currentGogaeNumber}고개의 모든 추측이 수집되었습니다.");
        
        if (currentGogaeNumber < 5)
        {
            // 6-A. 다음 고개로
            currentGogaeNumber++;
            
            await BroadcastMessageAsync($"[서버] {currentGogaeNumber-1}고개가 종료되었습니다. {currentGogaeNumber}번째 고개를 시작합니다.");
            
            currentGogaeData.Clear(); 
            questionAskers.Clear(); 
            currentReplyIndex = 0;
            // [STEP 7] 버그 수정: questionAskers는 비어있으므로, 모든 '추리자'를 순회해야 함
            foreach(var p in players.Where(p => p != presenter)) {
                p.AvailableChoices.Clear();
                p.ChosenQuestion = null;
            }
            
            currentGameState = GameState.WaitingForQuestions; 
            
            string nextStepNotice = "[서버] 이제부터 출제자를 제외한 모든 플레이어는 '질문'을 입력해주세요. (한 사람당 1개, 영어로)";
            await BroadcastMessageAsync(nextStepNotice);
        }
        else
        {
            // 6-B. 5고개 완료! -> 턴 종료 및 점수 계산
            Console.WriteLine($"[게임 로그] 5고개가 모두 종료되었습니다. 턴을 종료합니다.");
            
            // [STEP 7] 임시 로직을 삭제하고, 점수 계산 함수 호출
            await EndTurnAndCalculateScoresAsync();
        }
    }
}


// ===================================================
// [STEP 7] 신규 함수들 (점수 계산 및 턴/게임 종료)
// ===================================================

async Task EndTurnAndCalculateScoresAsync()
{
    await BroadcastMessageAsync("[서버] 5번의 고개가 모두 끝났습니다! 턴을 종료하고 점수를 계산합니다.");
    await BroadcastMessageAsync($"[서버] 이번 턴의 정답은 [ {currentAnswer} ]였습니다!");

    Player presenter = players[currentTurnIndex];
    List<Player> guessers = players.Where(p => p != presenter).ToList();
    
    Dictionary<Player, int> turnScores = new Dictionary<Player, int>();
    int bestGogae = 0; // 출제자 점수 계산용 (0 = 아무도 못 맞힘)

    await BroadcastMessageAsync("---------- [턴 결과] ----------");

    // 1. 추리자 점수 계산
    foreach (Player guesser in guessers)
    {
        int correctGogae = CalculateGuesserScore(guesser, currentAnswer.ToLower());
        int score = GetScoreFromGogae(correctGogae);
        
        guesser.TotalScore += score;
        turnScores[guesser] = score;

        string resultMessage;
        if (correctGogae > 0)
        {
            resultMessage = $"[결과] {guesser.Nickname}: {correctGogae}고개에서 정답! (+{score}점, 총 {guesser.TotalScore}점)";
            // 출제자 점수를 위한 최고 기록 갱신
            if (correctGogae < bestGogae || bestGogae == 0)
            {
                bestGogae = correctGogae;
            }
        }
        else
        {
            resultMessage = $"[결과] {guesser.Nickname}: 정답 맞추기 실패 (+0점, 총 {guesser.TotalScore}점)";
        }
        await BroadcastMessageAsync(resultMessage);
        Console.WriteLine(resultMessage);
    }
    
    // 2. 출제자 점수 계산 (기획안: 빨리 맞힐수록 점수)
    // 여기서는 "추리자 중 가장 좋은 점수"를 출제자도 동일하게 획득
    int presenterScore = GetScoreFromGogae(bestGogae);
    presenter.TotalScore += presenterScore;
    turnScores[presenter] = presenterScore;
    
    string presenterResult = $"[결과] {presenter.Nickname} (출제자): 점수 획득! (+{presenterScore}점, 총 {presenter.TotalScore}점)";
    await BroadcastMessageAsync(presenterResult);
    Console.WriteLine(presenterResult);
    
    await BroadcastMessageAsync("---------------------------------");
    
    // 3. 다음 턴 또는 게임 오버
    currentTurnIndex++;
    if (currentTurnIndex < players.Count)
    {
        // 3-A. 다음 턴 진행
        await StartNextTurnAsync();
    }
    else
    {
        // 3-B. 게임 오버
        await EndGameAsync();
    }
}

// [STEP 7] 기획안의 "연속 정답" 점수 계산 로직
int CalculateGuesserScore(Player guesser, string correctAnswer)
{
    // 예: ["x", "x", "apple", "apple", "apple"] (정답: "apple")
    int firstCorrectGogae = 0; // 0 = 맞힌 적 없음
    
    // 5고개(index 4)부터 1고개(index 0)까지 역순으로 검사
    for (int i = 4; i >= 0; i--)
    {
        if (guesser.Guesses.Count > i && guesser.Guesses[i] == correctAnswer)
        {
            // 정답과 일치하면, "연속"이므로 '최초 정답 고개'를 갱신
            firstCorrectGogae = i + 1; // (1-based gogae number)
        }
        else
        {
            // "연속"이 끊김! (예: "apple"이 아닌 "x"를 만남)
            break; 
        }
    }
    // 예: 5, 4고개는 맞고 3고개가 틀림 -> "apple", "apple", "x"
    // i=4 ("apple") -> firstCorrectGogae = 5
    // i=3 ("apple") -> firstCorrectGogae = 4
    // i=2 ("x")     -> break;
    // 최종 반환: 4 (4고개부터 맞힌 것으로 간주)
    return firstCorrectGogae;
}

// [STEP 7] 고개 번호에 따른 점수 반환
int GetScoreFromGogae(int gogae)
{
    switch (gogae)
    {
        case 1: return 50; // 1고개
        case 2: return 40; // 2고개
        case 3: return 30; // 3고개
        case 4: return 20; // 4고개
        case 5: return 10; // 5고개
        default: return 0; // 못 맞힘 (0)
    }
}

// [STEP 7] 다음 턴을 시작하기 위한 준비
async Task StartNextTurnAsync()
{
    Console.WriteLine($"[게임 로그] 다음 턴({currentTurnIndex + 1})을 시작합니다.");
    await BroadcastMessageAsync($"[서버] 모든 점수 계산이 완료되었습니다. {currentTurnIndex + 1}번째 턴을 시작합니다.");

    // 턴 관련 데이터 초기화
    currentGogaeNumber = 1;
    currentGogaeData.Clear();
    questionAskers.Clear();
    currentReplyIndex = 0;
    
    // 플레이어의 '고개' 데이터 초기화 (점수는 유지)
    foreach(var p in players) 
    {
        p.Guesses.Clear();
        p.AvailableChoices.Clear();
        p.ChosenQuestion = null;
    }
    
    currentGameState = GameState.WaitingForAnswer; 
    await StartTurnAsync(); // 새 출제자와 함께 턴 시작
}

// [STEP 7] 게임 오버 처리
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

    // 게임 상태 완전 리셋
    isGameRunning = false;
    currentGameState = GameState.Lobby;
    currentTurnIndex = 0;
    currentAnswer = "";
    currentGogaeNumber = 1;
    currentGogaeData.Clear();
    questionAskers.Clear();
    
    foreach(var p in players)
    {
        p.TotalScore = 0; // 점수도 리셋
        p.Guesses.Clear();
        p.AvailableChoices.Clear();
        p.ChosenQuestion = null;
    }
    
    await BroadcastMessageAsync("[서버] 로비로 돌아왔습니다. 방장은 /start로 새 게임을 시작할 수 있습니다.");
    Console.WriteLine("[게임 로그] 로비로 복귀.");
}


// --- 유틸리티 함수 (수정 없음) ---
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


// ===================================================
// 형식 선언(enum, class)은 파일 맨 아래에 둡니다.
// ===================================================

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
    public List<string> Guesses { get; } = new List<string>();
    
    public List<Player> AvailableChoices { get; set; } = new List<Player>();
    public Player? ChosenQuestion { get; set; } = null;
    
    // [STEP 7] 플레이어의 총 점수
    public int TotalScore { get; set; } = 0;
    
    public Player(TcpClient client, bool isHost)
    {
        this.Client = client;
        this.IsHost = isHost;
        this.Nickname = "Connecting..."; 
    }
}