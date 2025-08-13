using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using Photon;
using Photon.Pun;
using Zorro.Core;
using Random = UnityEngine.Random;
using BepInEx.Logging;
using TMPro;

namespace _​​PeakTeamRace
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.xiaohai.peakteamrace";
        public const string PluginName = "​​PeakTeamRace";
        public const string PluginVersion = "1.0.0";
        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }
        public void Awake()
        {
            Instance = this;
            Log = Logger;
            Logger.LogInfo($"Plugin {PluginName} v {PluginVersion}is loaded!");
            new Harmony("com.xiaohai.peakteamrace").PatchAll();
        }
    }



    public class TeamRaceManager : Singleton<TeamRaceManager>
    {
        public Dictionary<int, int> PlayerTeams { get; private set; } = new Dictionary<int, int>();

        // 存储玩家完成时间（ViewID -> 完成时间）
        public readonly Dictionary<int, float> PlayerFinishTimes = new Dictionary<int, float>();

        // 存储玩家积分（ViewID -> 积分）
        public readonly Dictionary<int, int> PlayerScores = new Dictionary<int, int>();

        // 团队积分（0=红队, 1=蓝队）
        public readonly int[] TeamScores = new int[2];

        // 记录第一名完成时间
        public float FirstPlaceTime { get; private set; } = float.MaxValue;
        public int LocalPlayerTeamId { get; private set; } = -1;

        private PhotonView _photonView;
        private bool _teamsAssigned = false;
        private TeamRaceTimer timer => TeamRaceTimer.Instance;
        public bool isChinese => LocalizedText.CURRENT_LANGUAGE == LocalizedText.Language.SimplifiedChinese;

        // 新添加：团队颜色表示（红队0，蓝队1）
        public readonly Color[] TeamColors = new Color[]
        {
        new Color(1.0f, 0.3f, 0.3f), // 红队
        new Color(0.3f, 0.3f, 1.0f)  // 蓝队
        };
        bool reachedCampfire = false;

        protected override void Awake()
        {
            base.Awake();
            _photonView = GetComponentInChildren<PhotonView>();

            // 确保在所有客户端初始化
            if (_photonView == null)
            {
                _photonView = gameObject.AddComponent<PhotonView>();
                PhotonNetwork.AllocateViewID(_photonView);
            }

            // 确保字典有初始值
            PlayerTeams = new Dictionary<int, int>();
        }
        void Update()
        {
            if (Patch.nextCampfire != null && Character.localCharacter != null)
            {
                // 检查玩家是否到达下一个篝火位置
                float distanceToCampfire = Vector3.Distance(Character.localCharacter.Center, Patch.nextCampfire.transform.position);
                if (distanceToCampfire < 3f && !reachedCampfire)
                {
                    PlayerReachedCampfire();
                }
            }

        }

        public void RandomlyAssignTeams()
        {
            // 只有主机可以分配队伍
            if (!PhotonNetwork.IsMasterClient) return;

            // 清空旧分配
            PlayerTeams.Clear();
            _teamsAssigned = false;

            // 获取所有有效玩家（排除空引用和离场玩家）
            var allPlayers = Character.AllCharacters
                .Where(p => p != null && p.photonView != null)
                .ToList();

            // 单人模式不分配队伍（纯UI显示用）
            if (allPlayers.Count < 2)
            {
                // 单人游戏时分配为红队
                foreach (var player in allPlayers)
                {
                    PlayerTeams[player.photonView.ViewID] = 0;
                }
                _teamsAssigned = true;

            }
            else
            {
                // 创建随机索引列表（Fisher-Yates洗牌）
                var indices = Enumerable.Range(0, allPlayers.Count).ToList();
                System.Random rng = new System.Random();
                indices = indices.OrderBy(x => rng.Next()).ToList();

                // 队伍分配基准点（确保两队人数平衡）
                int teamSplitIndex = allPlayers.Count / 2;

                // 分配队伍
                for (int i = 0; i < indices.Count; i++)
                {
                    int playerId = allPlayers[indices[i]].photonView.ViewID;
                    PlayerTeams[playerId] = (i < teamSplitIndex) ? 0 : 1;
                }
            }


            // 同步给所有客户端
            var serializedData = SerializeTeamsForRPC();
            _photonView.RPC(nameof(RPC_AssignTeams), RpcTarget.All, serializedData);

            // 调试输出
            DebugLogTeamAssignments(allPlayers);
            _teamsAssigned = true;
        }

        // 序列化队伍数据用于RPC传输
        private object[] SerializeTeamsForRPC()
        {
            return new object[]
            {
            PlayerTeams.Count,
            PlayerTeams.Keys.ToArray(),
            PlayerTeams.Values.ToArray()
            };
        }

        [PunRPC]
        void RPC_AssignTeams(object[] data)
        {
            reachedCampfire = false;
            int count = (int)data[0];
            int[] playerIds = (int[])data[1];
            int[] teamIds = (int[])data[2];

            PlayerTeams.Clear();
            for (int i = 0; i < count; i++)
            {
                PlayerTeams[playerIds[i]] = teamIds[i];
            }

            UpdateLocalPlayerTeam();
        }

        // 更新本地玩家队伍ID
        public void UpdateLocalPlayerTeam()
        {
            if (Character.localCharacter != null && Character.localCharacter.photonView != null)
            {
                int viewID = Character.localCharacter.photonView.ViewID;
                if (PlayerTeams.TryGetValue(viewID, out int teamId))
                {
                    LocalPlayerTeamId = teamId;
                    Plugin.Log.LogInfo($"你的队伍ID是: Team{LocalPlayerTeamId} (红队0/蓝队1)");
                    string text = isChinese
                        ? $"你的队伍是: {(teamId == 0 ? "<color=red>红队</color>" : "<color=blue>蓝队</color>")}"
                        : $"Your team is: {(teamId == 0 ? "<color=red>Red Team</color>" : "<color=blue>Blue Team</color>")}";
                    TeamRaceTimer.SetMessage($"<size=40>{text}</size>", 15f);
                    if (!timer.isRunning) // 添加运行状态检查
                    {
                        timer.StartTimer();
                    }
                }
                else
                {
                    Plugin.Log.LogWarning("本地玩家未分配到队伍!");
                }
            }
        }

        // 根据玩家ID获取队伍ID
        public int GetPlayerTeam(int playerViewId)
        {
            return PlayerTeams.TryGetValue(playerViewId, out int teamId) ? teamId : -1;
        }

        // 获取所有队友的ViewID
        public List<int> GetTeammates(int playerViewId)
        {
            if (!PlayerTeams.TryGetValue(playerViewId, out int myTeamId))
                return new List<int>();

            return PlayerTeams
                .Where(pair => pair.Value == myTeamId)
                .Select(pair => pair.Key)
                .ToList();
        }

        // 当玩家到达篝火
        void PlayerReachedCampfire()
        {
            reachedCampfire = true;
            var finishTime = timer.StopTimer();
            _photonView.RPC("RPC_SyncPlayerReachedCampfire", RpcTarget.MasterClient, Character.localCharacter.photonView.ViewID, finishTime);
            string text = isChinese
                ? $"<size=30><color=green>你到达了篝火! 完成时间: {timer.FormatTime(finishTime)}</size></color>"
                : $"<size=30><color=green>You reached the campfire! Finish time: {timer.FormatTime(finishTime)}</size></color>";
            TeamRaceTimer.SetMessage(text, 5f);

        }
        [PunRPC]
        void RPC_SyncPlayerReachedCampfire(int playerViewId, float finishTime)
        {
            if (PhotonNetwork.IsMasterClient)
            {
                // 处理玩家到达篝火的逻辑
                HandleResult(GetCharacterByViewId(playerViewId), finishTime);
            }
        }
        [PunRPC]
        void RPC_SyncResult(object[] data)
        {
            // 1. 解析基础数据
            FirstPlaceTime = (float)data[0];
            TeamScores[0] = (int)data[1];
            TeamScores[1] = (int)data[2];

            // 2. 解析玩家完成时间
            int finishCount = (int)data[3];
            int[] finishPlayerIds = (int[])data[4];
            float[] finishTimes = (float[])data[5];

            PlayerFinishTimes.Clear();
            for (int i = 0; i < finishCount; i++)
            {
                PlayerFinishTimes[finishPlayerIds[i]] = finishTimes[i];
            }

            // 3. 解析玩家积分
            int scoreCount = (int)data[6];
            int[] scorePlayerIds = (int[])data[7];
            int[] scores = (int[])data[8];

            PlayerScores.Clear();
            for (int i = 0; i < scoreCount; i++)
            {
                PlayerScores[scorePlayerIds[i]] = scores[i];
            }

            //4.处理本地结果
            bool localWin = false;
            string extraText = "";
            if ((TeamScores[0] > TeamScores[1] && LocalPlayerTeamId==0)||(TeamScores[0] < TeamScores[1] && LocalPlayerTeamId == 1))
            {
                localWin = true;
            }
            else if (TeamScores[0] == TeamScores[1])
            {
                localWin= true;
            }
            int localScore = PlayerScores.TryGetValue(Character.localCharacter.photonView.ViewID, out int score) ? score : 0;
            if (localWin)
            {
                
                float num = localScore / 100;
                Character.localCharacter.AddExtraStamina(num);
                extraText= isChinese
                    ? $"<size=30><color=green>你赢了! 获得额外耐力: {num*100:F2}%</color></size>"
                    : $"<size=30><color=green>You win! Add extra stamina: {num*100:F2}%</color></size>";
            }
            else
            {
                int randomAffliction = Random.Range(0, 10);
                float num = 1f - (localScore / 100);
                num =Mathf.Clamp(num, 0f, 0.65f); // 确保数值在0.1到1之间
                Character.localCharacter.refs.afflictions.AddStatus((CharacterAfflictions.STATUSTYPE)randomAffliction, num);
                extraText=isChinese
                    ? $"<size=30><color=red>你输了! 获得负面状态: {((CharacterAfflictions.STATUSTYPE)randomAffliction).ToString()}</color></size>"
                    : $"<size=30><color=red>You lose! Get Affliction: {((CharacterAfflictions.STATUSTYPE)randomAffliction).ToString()}</color></size>";
            }

            Plugin.Log.LogInfo($"已同步比赛结果: 第一名时间={FirstPlaceTime:F2}s, 红队={TeamScores[0]}, 蓝队={TeamScores[1]}");
            var resultText = GenerateResultText(extraText);
            ResultUI.ShowResultUI(resultText, 15);
        }
        public void HandleResult(Character player, float finishTime)
        {
            if (!PhotonNetwork.IsMasterClient || player == null) return;
            int playerViewId = player.photonView.ViewID;
            // 记录完成时间
            PlayerFinishTimes[playerViewId] = finishTime;

            // 更新第一名时间
            if (finishTime < FirstPlaceTime)
            {
                FirstPlaceTime = finishTime;
            }

            // 计算并存储个人积分
            int score = CalculatePlayerScore(playerViewId, finishTime);
            PlayerScores[playerViewId] = score;

            // 添加到团队总分
            if (PlayerTeams.TryGetValue(playerViewId, out int teamId))
            {
                TeamScores[teamId] += score;
            }

            Plugin.Log.LogInfo($"玩家{player.characterName}完成! 时间: {timer.FormatTime(finishTime)}, 积分: {score}");

            // 如果所有玩家都完成了，发送结果同步
            if (Patch.nextCampfire.EveryoneInRange(out string text))
            {
                var data = SerializeResultsForRPC();
                _photonView.RPC(nameof(RPC_SyncResult), RpcTarget.All, data);
            }


        }


        private int CalculatePlayerScore(int playerViewId, float finishTime)
        {
            // 1. 获取所有完成玩家的时间
            var allTimes = PlayerFinishTimes.Values.ToList();

            // 2. 如果没有其他玩家完成，默认100分
            if (allTimes.Count == 1) return 100;

            // 3. 计算当前玩家排名
            int rank = 1;
            foreach (var time in allTimes)
            {
                if (time < finishTime) rank++;
            }

            // 4. 基于排名计算分数（等差数列）
            int totalPlayers = allTimes.Count;
            int minScore = 0; // 最后一名分数
            int maxScore = 100; // 第一名分数

            // 等差数列公式：分数 = maxScore - (排名-1) * (maxScore - minScore) / (总人数-1)
            int score = maxScore - (rank - 1) * (maxScore - minScore) / (totalPlayers - 1);

            // 5. 确保分数在0-100范围内
            return Mathf.Clamp(score, minScore, maxScore);
        }

        // 序列化结果数据
        private object[] SerializeResultsForRPC()
        {
            // 准备玩家完成时间数据
            int[] finishPlayerIds = PlayerFinishTimes.Keys.ToArray();
            float[] finishTimes = PlayerFinishTimes.Values.ToArray();

            // 准备玩家积分数据
            int[] scorePlayerIds = PlayerScores.Keys.ToArray();
            int[] scores = PlayerScores.Values.ToArray();

            return new object[]
            {
        FirstPlaceTime,             // 第一名时间
        TeamScores[0],               // 红队积分
        TeamScores[1],               // 蓝队积分
        finishPlayerIds.Length,      // 完成玩家数量
        finishPlayerIds,             // 完成玩家ID数组
        finishTimes,                 // 完成时间数组
        scorePlayerIds.Length,       // 积分玩家数量
        scorePlayerIds,              // 积分玩家ID数组
        scores                       // 积分数组
            };
        }

        Character GetCharacterByViewId(int viewId)
        {
            foreach (Character character in Character.AllCharacters)
            {
                if (character != null && character.photonView != null && character.photonView.ViewID == viewId)
                {
                    return character;
                }
            }
            return null;
        }
        private string GenerateResultText(string extraText)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            string blueWin = TeamScores[1] > TeamScores[0] ? "<size=30>win</size>" : "";
            string redWin = TeamScores[1] < TeamScores[0] ? "<size=30>win</size>" : "";
            string titleText = !isChinese
                ? $"<size=60><color=blue>Team Blue</color>: {TeamScores[1]}</size>{blueWin}   <size=60><color=red>Team Red</color>: {TeamScores[0]}</size>{redWin}"
                : $"<size=60><color=blue>蓝队</color>: {TeamScores[1]}分</size>{blueWin}     <size=60><color=red>红队</color>: {TeamScores[0]}分</size>{redWin}";
            // 团队得分 - 使用大字体和颜色
            sb.AppendLine(titleText);
            sb.AppendLine(); // 空行

            // 表头 - 使用稍小字体
            sb.AppendLine("<size=40>Player            Time              Score</size>");
            sb.AppendLine(); // 空行

            // 获取所有玩家并按时间排序
            var sortedPlayers = PlayerFinishTimes
                .OrderBy(p => p.Value)
                .Select(p => new
                {
                    ViewID = p.Key,
                    Time = p.Value,
                    Score = PlayerScores.TryGetValue(p.Key, out int s) ? s : 0
                })
                .ToList();

            // 添加玩家信息
            for (int i = 0; i < sortedPlayers.Count; i++)
            {
                var player = sortedPlayers[i];
                Character character = GetCharacterByViewId(player.ViewID);
                string playerName = character != null ? character.characterName : "Unknown";

                // 确定玩家颜色（根据队伍）
                string playerColor = PlayerTeams.TryGetValue(player.ViewID, out int teamId) ?
                    (teamId == 0 ? "red" : "blue") : "white";

                // 格式化时间
                string timeStr = TeamRaceTimer.Instance.FormatTime(player.Time);

                // 创建玩家行
                sb.Append($"<size=32>{i + 1}.");

                // 玩家名（带颜色）
                sb.Append($"<color={playerColor}>{playerName}</color></size>");

                // 计算并添加空格对齐
                int namePadding = 15 - playerName.Length; // 确保总宽度15字符
                if (namePadding > 0)
                {
                    sb.Append(new string(' ', namePadding));
                }

                // 时间（右对齐）
                int timePadding = 15 - timeStr.Length; // 确保总宽度12字符
                if (timePadding > 0)
                {
                    sb.Append(new string(' ', timePadding));
                }
                sb.Append(timeStr);

                // 分数（右对齐）
                int scorePadding = 17 - player.Score.ToString().Length; // 确保总宽度10字符
                if (scorePadding > 0)
                {
                    sb.Append(new string(' ', scorePadding));
                }
                sb.Append($"<size=32>{player.Score}</size>");

                sb.AppendLine(); // 换行
            }
            sb.AppendLine(extraText);
            return sb.ToString();
        }


        // 调试输出
        private void DebugLogTeamAssignments(List<Character> players)
        {
            string redTeam = "红队(0): ";
            string blueTeam = "蓝队(1): ";

            foreach (var player in players)
            {
                if (player.photonView == null) continue;

                int viewId = player.photonView.ViewID;
                if (PlayerTeams.TryGetValue(viewId, out int teamId))
                {
                    string playerName = player.characterName;
                    if (string.IsNullOrEmpty(playerName)) playerName = "未命名玩家";

                    if (teamId == 0)
                        redTeam += $"{playerName}({viewId}), ";
                    else
                        blueTeam += $"{playerName}({viewId}), ";
                }
            }

            Plugin.Log.LogInfo(redTeam.TrimEnd(',', ' '));
            Plugin.Log.LogInfo(blueTeam.TrimEnd(',', ' '));
            Plugin.Log.LogInfo($"队伍分配完成! 总计: {players.Count}名玩家");
        }


    }


    public class TeamRaceTimer : MonoBehaviour
    {
        private float messageTimer;
       
        private TMP_Text timerText;
        private float startTime;
        private float pausedTime;
        public bool isRunning;
        private bool isPaused;
        public string message = "";

        public float CurrentTime => isRunning ? (Time.time - startTime) : pausedTime;
        public static TeamRaceTimer Instance { get; private set; }

        private void Start()
        {
            // 1. 查找已有的TMP_Text组件（从复制的UI中）
            timerText = GetComponentInChildren<TMP_Text>();

            // 2. 确保找到文本组件
            if (timerText == null)
            {
                Debug.LogError("无法找到计时器文本组件！");
                enabled = false;
                return;
            }

            // 3. 初始设置
            timerText.text = "00:00.00";
            timerText.alignment = TextAlignmentOptions.Center;
            timerText.richText =true; 
            // 4. 初始隐藏（在团队分配后显示）
            SetVisible(false);
            Instance = this;
        }

        // 开始计时
        public void StartTimer()
        {
            SetVisible(true);

            if (isPaused)
            {
                // 从暂停恢复
                startTime = Time.time - pausedTime;
                isPaused = false;
            }
            else
            {
                // 全新开始
                startTime = Time.time;
                pausedTime = 0f;
            }

            isRunning = true;
        }

        // 暂停计时
        public void PauseTimer()
        {
            if (!isRunning) return;

            pausedTime = CurrentTime;
            isRunning = false;
            isPaused = true;
        }

        // 停止计时并返回最终时间
        public float StopTimer()
        {
            float finalTime = CurrentTime;
            isRunning = false;
            isPaused = false;
            return finalTime;
        }

        // 重置计时器
        public void ResetTimer()
        {
            startTime = Time.time;
            pausedTime = 0f;
            isRunning = false;
            isPaused = false;
            timerText.text = message + "\n\n " + FormatTime(0f);
        }

        // 格式化时间显示
        public string FormatTime(float time)
        {
            int minutes = Mathf.FloorToInt(time / 60f);
            int seconds = Mathf.FloorToInt(time % 60f);
            int milliseconds = Mathf.FloorToInt((time * 100f) % 100f);
            string color =TeamRaceManager.Instance.LocalPlayerTeamId == 0 ? "red" : "blue";
            return $"<size=32><color={color}>{minutes:00}:{seconds:00}.{milliseconds:00}</size></color>";
        }

        // 控制UI可见性
        public void SetVisible(bool visible)
        {
            CanvasGroup group = GetComponent<CanvasGroup>();
            if (group != null)
            {
                group.alpha = visible ? 1f : 0f;
            }
            else
            {
                gameObject.SetActive(visible);
            }
        }

        private void Update()
        {
           
            messageTimer -= Time.deltaTime;
            if (messageTimer <= 0f)
            {
                message = "";
            }
            timerText.text = message + "\n\n" + FormatTime(CurrentTime);
        }

        public static void SetMessage(string msg, float time)
        {
            if (Instance != null)
            {
                Instance.message = msg;
                
                
                Instance.messageTimer = time;
            }
        }
    }

    public class ResultUI : MonoBehaviour
    {
        private static ResultUI _instance;
        public static ResultUI Instance => _instance;

        private TMP_Text _resultText;
        //private CanvasGroup _canvasGroup;
        private float _showTime = 5f;
        private float _timer;
        private bool _isShowing;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;

            _resultText = base.GetComponent<TMP_Text>();
            _resultText.richText = true;
            _resultText.text = "";
            // 初始隐藏

            gameObject.SetActive(true);
        }

        public static void ShowResultUI(string result, float time)
        {
            Instance._showTime = time;
            if (Instance == null) return;

            Instance._resultText.text = result;

            Instance._timer = Instance._showTime;
            Instance._isShowing = true;
        }

        private void Update()
        {
            if (!_isShowing) return;

            _timer -= Time.deltaTime;
            if (_timer <= 0f)
            {
                _resultText.text = ""; // 清空结果文本
                _isShowing = false;
            }
        }
    }

    [HarmonyPatch]
    public static class Patch
    {
        public static Campfire nextCampfire;
        [HarmonyPatch(typeof(GUIManager), "Start")]
        private static class PatchGUIManager
        {
            public static void Postfix(GUIManager __instance)
            {

                InitTimerUI(__instance);

                InitResultUI(__instance);


            }
            static void InitTimerUI(GUIManager __instance)
            {
                //计时器UI的创建和初始化
                // 1. 找到参考的AscentUI（与指南针UI相同方式）
                Transform ascentTransform = __instance.GetComponentInChildren<AscentUI>()?.transform;
                if (ascentTransform == null) return;

                // 2. 复制AscentUI对象（包含所有子组件）
                RectTransform timerTransform = (RectTransform)UnityEngine.Object.Instantiate(
                    ascentTransform,
                    ascentTransform.parent
                );

                // 3. 重命名并移除原有组件
                timerTransform.name = "TeamRaceTimer";
                UnityEngine.Object.Destroy(timerTransform.GetComponent<AscentUI>());

                // 4. 添加计时器组件
                timerTransform.gameObject.AddComponent<TeamRaceTimer>();

                // 5. 调整位置和大小（屏幕右侧居中）
                timerTransform.anchorMin = new Vector2(1f, 0.5f);
                timerTransform.anchorMax = new Vector2(1f, 0.5f);
                timerTransform.pivot = new Vector2(1f, 0.5f);
                timerTransform.anchoredPosition = new Vector2(-50f, 0f); // 右侧留出50px空间
                timerTransform.sizeDelta = new Vector2(200f, 50f); // 宽度200px，高度50px
            }
            static void InitResultUI(GUIManager __instance)
            {
                //结果UI的创建和初始化
                // 找到参考的AscentUI（与指南针UI相同方式）
                Transform ascentTransform = __instance.GetComponentInChildren<AscentUI>()?.transform;
                if (ascentTransform == null) return;

                // 复制AscentUI对象
                Transform resultTransform = UnityEngine.Object.Instantiate(ascentTransform, ascentTransform.parent);
                resultTransform.name = "ResultUI";

                // 移除原有组件
                UnityEngine.Object.Destroy(resultTransform.GetComponent<AscentUI>());

                // 添加结果UI组件
                resultTransform.gameObject.AddComponent<ResultUI>();

                // 调整位置和大小（屏幕中央）
                RectTransform rect = resultTransform.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2(800f, 600f); // 适当大小

                // 设置文本样式
                TMP_Text text = resultTransform.GetComponentInChildren<TMP_Text>();
                if (text != null)
                {
                    text.alignment = TextAlignmentOptions.TopLeft;
                    text.fontSize = 24;
                    text.color = Color.white;
                }
            }
        }

        [HarmonyPatch(typeof(MapHandler))]
        static class PatchMapHandler
        {
            [HarmonyPatch("GoToSegment")]
            public static void Postfix(MapHandler __instance, ref Segment s)
            {
                nextCampfire = __instance.segments[(int)s]._segmentCampfire.gameObject.GetComponentInChildren<Campfire>(true);
                var text = __instance.segments[(int)s]._segmentCampfire == null ? __instance.segments[(int)s]._segmentCampfire.name : "null";
                Plugin.Log.LogInfo($"下一关:{s}, __instance.segments[(int)s]._segmentCampfire={text}");
                if (nextCampfire != null)
                {
                    var dis = Vector3.Distance(nextCampfire.transform.position, Character.localCharacter.transform.position);
                    Plugin.Log.LogInfo($"下一个篝火位置: {nextCampfire.transform.position},距离玩家{dis:F2}M");
                    TeamRaceManager.Instance.RandomlyAssignTeams();
                }
                else
                {
                    Plugin.Log.LogWarning("未找到下一个篝火位置!");


                }

            }


        }
        [HarmonyPatch(typeof(RunManager))]
        static class PatchRunManager
        {
            [HarmonyPatch("Awake")]
            public static void Postfix(RunManager __instance)
            {
                if (__instance.gameObject.GetComponent<TeamRaceManager>() == null)
                {
                    __instance.gameObject.AddComponent<TeamRaceManager>();
                    Plugin.Log.LogInfo("已添加TeamRaceManager组件到RunManager对象上");

                }
            }
        }
        [HarmonyPatch(typeof(MountainProgressHandler))]
        static class PatchMountainProgressHandler
        {
            [HarmonyPatch("CheckAreaAchievement")]
            public static void Prefix(MountainProgressHandler __instance, ref MountainProgressHandler.ProgressPoint pointReached)
            {
                if (pointReached.title.ToLower() != "shore") return;

                var campfires = GameObject.FindObjectsByType<Campfire>(FindObjectsSortMode.None).ToList();
                Plugin.Log.LogInfo($"已经到达海岸!,共找到{campfires.Count}个篝火!");
                foreach (Campfire campfire in campfires)
                {
                    if (nextCampfire == null && campfire.state != Campfire.FireState.Lit && Character.localCharacter != null)
                    {
                        var dis = Vector3.Distance(campfire.transform.position, Character.localCharacter.Center);
                        if (dis > 100)
                        {
                            nextCampfire = campfire;
                            Plugin.Log.LogInfo($"已设置nextCampfire为当前Campfire: {campfire.name}");
                            TeamRaceManager.Instance.RandomlyAssignTeams();
                            break;
                        }

                    }
                }



            }
        }
    }
}
