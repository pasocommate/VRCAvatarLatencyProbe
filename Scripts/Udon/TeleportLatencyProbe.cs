using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace PasocomMate.VRCAvatarLatencyProbe
{
    /// <summary>
    /// アバター位置レプリケーション遅延を計測するコアコンポーネント。
    ///
    /// 計測原理（マルチゲート落下方式・マルチオブザーバー）:
    ///   1. アクター（被計測者）がブースでボタンを押すと高所へテレポートされ自由落下する
    ///   2. 落下経路上に等時間間隔（120ms）で配置された5つのゲート（閾値 Y）を
    ///      アクターが通過するたびにサーバー時刻をフレーム間線形補間で算出する
    ///   3. 5ゲート全通過後、通過時刻を同期変数で送信する
    ///   4. 最大5人のオブザーバー（距離が近い順に自動選択）がリモートアバターの
    ///      補間位置で同じゲート通過を検出し、同様にサーバー時刻を算出する
    ///   5. ゲートごとのレイテンシ = オブザーバー通過時刻 − アクター通過時刻
    ///   6. 1落下の結果 = 5ゲートのレイテンシの中央値
    ///   7. 1観測者の結果 = 5落下の中央値
    ///   8. 最終結果 = 全観測者の結果の最小値
    ///
    /// ゲート間隔が120msである理由:
    ///   VRChat はアバター位置を ~100ms 間隔で同期する。gcd(120, 100) = 20 であり、
    ///   5ゲートで 100ms サイクル内の位相を 0, 20, 40, 60, 80ms と均等サンプリングできる。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class TeleportLatencyProbe : UdonSharpBehaviour
    {
        // ================================================================
        // Inspector 参照
        // ================================================================

        [SerializeField] private ProbeDisplay _display;
        [SerializeField] private Transform _fallStart;       // 落下開始位置 (Y = 10)
        [Header("移動速度復元値（VRChat API にゲッターがないため手動設定）")]
        [SerializeField] private float _restoreWalkSpeed    = 2f;
        [SerializeField] private float _restoreRunSpeed     = 4f;
        [SerializeField] private float _restoreStrafeSpeed  = 2f;
        [SerializeField] private float _restoreJumpImpulse  = 3f;
        [Header("トラップドア (地下バリアント用、省略可)")]
        [SerializeField] private Animator _trapdoorAnimator;
        [SerializeField] private GameObject _trapdoorFloor;   // 床コライダー GO（Udon で直接 on/off）
        [SerializeField] private Transform _exitPosition;     // 計測終了後の帰還位置（省略時はテレポートなし）

        // ================================================================
        // 定数
        // ================================================================

        private const int   NUM_FALLS          = 5;
        private const int   NUM_GATES          = 5;
        private const int   MAX_GATES          = 15;  // 15×1 モード用の最大ゲート数
        private const int   MAX_TOTAL_SAMPLES  = 25;  // max(5*5, 15*1)
        private const float FIRST_GATE_FALL_TIME = 0.6f;  // 落下開始からの最初のゲート到達時間 (秒)
        private const float GATE_INTERVAL_SEC  = 0.12f;   // ゲート間の時間間隔 (秒)
        private const float SETTLE_TIME        = 0.5f;    // テレポート後の安定待ち (秒)
        private const float INTER_FALL_DELAY   = 1.5f;    // 落下間の待ち (秒)
        private const float COOLDOWN_SEC       = 5f;
        private const float FALL_TIMEOUT       = 3f;      // 1 落下のタイムアウト (秒、FALLING 開始から)
        private const int   MAX_LOG_LINES      = 10;
        private const int   MAX_OBSERVERS      = 5;
        private const float REPORT_STAGGER_SEC = 2.0f;    // オブザーバー間のレポート間隔
        private const float COLLECT_TIMEOUT    = 14f;      // レポート収集タイムアウト

        // ローカルステート
        private const int STATE_IDLE             = 0;
        private const int STATE_ACTOR_FALLING    = 1;
        private const int STATE_OBSERVER_WATCHING = 2;
        private const int STATE_RESULT           = 3;
        private const int STATE_COOLDOWN         = 4;
        private const int STATE_COLLECTING       = 5; // アクター: オブザーバーレポート収集中

        // 同期フェーズ（アクターが全クライアントへ通知）
        private const int PHASE_IDLE     = 0;
        private const int PHASE_SETTLING = 1; // テレポート直後、安定待ち
        private const int PHASE_FALLING  = 2; // 落下中
        private const int PHASE_CROSSED  = 3; // 全ゲート通過済み (ゲート時刻有効)
        private const int PHASE_DONE     = 4; // 全落下完了

        // ================================================================
        // ローカル状態
        // ================================================================

        private int _localState = STATE_IDLE;

        // --- モード ---
        private int _effectiveGates = NUM_GATES;
        private int _effectiveFalls = NUM_FALLS;

        // --- ゲート高さ (Start で計算) ---
        private float[] _gateHeights = new float[MAX_GATES];
        private bool _gatesComputed = false;

        // --- アクター用 ---
        private int   _currentFall = 0;
        private int   _nextGateIndex = 0;
        private float _actorPrevY = 0f;
        private int   _actorPrevServerMs = 0;
        private bool  _actorMonitoring = false;
        private bool  _waitingForTrapdoorTrigger = false; // 離地検出で扉アニメRPC送信待ち
        private float _fallStartTime = 0f; // タイムアウト用 (Time.time)
        private int[] _actorGateMs = new int[MAX_GATES];

        // --- オブザーバー用 ---
        private int   _actorPlayerId = -1;
        private float _obsPrevY = 0f;
        private int   _obsPrevServerMs = 0;
        private bool  _obsWaitingForCrossing = false;
        private int   _obsNextGateIndex = 0;
        private bool  _obsAllGatesDetected = false;
        private int[] _obsGateMs = new int[MAX_GATES];
        private int   _obsFallIndex = -1;
        private int   _lastSeenPhase = PHASE_IDLE;
        private int   _myObserverSlot = -1;
        private bool  _obsSeenAtTop = false;        // アバターが FallStart 付近に現れた
        private bool  _obsTrapdoorPlayed = false;    // この落下で扉アニメ再生済み

        // オブザーバー結果
        private int     _fallMedianCount = 0;
        private float[] _fallMedians = new float[NUM_FALLS];
        private float[] _gateLatencies = new float[MAX_TOTAL_SAMPLES]; // [fall*_effectiveGates+gate]

        // --- アクター: レポート収集 ---
        private float[] _collectedMedians = new float[MAX_OBSERVERS];
        private int[]   _collectedSamples = new int[MAX_OBSERVERS];
        private bool[]  _collectedFlags   = new bool[MAX_OBSERVERS];
        private int     _expectedObserverCount = 0;
        private int     _collectedCount = 0;
        private float   _collectTimeoutTime = 0f;
        private string[] _observerNames = new string[MAX_OBSERVERS];

        // --- 結果・表示 ---
        private string _actorDisplayName = "";
        private float  _medianMs  = -1f;
        private int    _validSampleCount = 0;
        private bool   _isActorRole = false;

        // --- ローカルログ ---
        private string[] _localLog = new string[MAX_LOG_LINES];
        private int _localLogCount = 0;

        // --- クールダウン ---
        private float _cooldownEndTime = 0f;

        // ================================================================
        // 同期変数
        // ================================================================

        // 落下制御（アクターが所有）
        [UdonSynced] private int _syncedPhase      = PHASE_IDLE;
        [UdonSynced] private int _syncedFallIndex   = 0;

        // ゲート通過時刻（アクターが所有、最大15ゲート分）
        [UdonSynced] private int _syncedActorGateMs0 = 0;
        [UdonSynced] private int _syncedActorGateMs1 = 0;
        [UdonSynced] private int _syncedActorGateMs2 = 0;
        [UdonSynced] private int _syncedActorGateMs3 = 0;
        [UdonSynced] private int _syncedActorGateMs4 = 0;
        [UdonSynced] private int _syncedActorGateMs5 = 0;
        [UdonSynced] private int _syncedActorGateMs6 = 0;
        [UdonSynced] private int _syncedActorGateMs7 = 0;
        [UdonSynced] private int _syncedActorGateMs8 = 0;
        [UdonSynced] private int _syncedActorGateMs9 = 0;
        [UdonSynced] private int _syncedActorGateMs10 = 0;
        [UdonSynced] private int _syncedActorGateMs11 = 0;
        [UdonSynced] private int _syncedActorGateMs12 = 0;
        [UdonSynced] private int _syncedActorGateMs13 = 0;
        [UdonSynced] private int _syncedActorGateMs14 = 0;

        // ゲート数通知（オブザーバーがモードを知るため）
        [UdonSynced] private int _syncedGateCount = NUM_GATES;

        // 排他制御（アクター playerId、-1 = 空き）
        [UdonSynced] private int _syncedOccupantId   = -1;

        // オブザーバー指定 (最大5人、-1 = 未使用)
        [UdonSynced] private int _syncedObsId0 = -1;
        [UdonSynced] private int _syncedObsId1 = -1;
        [UdonSynced] private int _syncedObsId2 = -1;
        [UdonSynced] private int _syncedObsId3 = -1;
        [UdonSynced] private int _syncedObsId4 = -1;

        // 結果配信（オブザーバー or アクターが所有）
        [UdonSynced] private float  _syncedMedianMs   = -1f;
        [UdonSynced] private int    _syncedSampleCnt  =  0;
        [UdonSynced] private string _syncedActorName  = "";
        [UdonSynced] private int    _syncedResultVer  =  0;
        [UdonSynced] private int    _syncedReportSlot = -1;

        // 最後に処理した結果バージョン（重複ログ防止）
        private int _lastSeenResultVer = 0;

        // ================================================================
        // ゲート高さ計算
        // ================================================================

        private void Start()
        {
            ComputeGateHeights();
        }

        private void ComputeGateHeights()
        {
            if (_fallStart == null) return;
            float g = Mathf.Abs(Physics.gravity.y);
            if (g < 0.1f)
            {
                Debug.LogWarning("[LatencyProbe] Physics.gravity.y が 0 に近いため、デフォルト 9.81 を使用");
                g = 9.81f;
            }
            float startY = _fallStart.position.y;
            for (int i = 0; i < _effectiveGates; i++)
            {
                float t = FIRST_GATE_FALL_TIME + i * GATE_INTERVAL_SEC;
                _gateHeights[i] = startY - 0.5f * g * t * t;
            }
            _gatesComputed = true;
            Debug.Log($"[LatencyProbe] ゲート高さ計算完了: {_effectiveGates} gates "
            + $"[{_gateHeights[0]:F2} ... {_gateHeights[_effectiveGates - 1]:F2}]");
        }

        // ================================================================
        // 線形補間ヘルパー
        // ================================================================

        private int InterpolateCrossingMs(
        float prevY, float currY,
        int prevServerMs, int currServerMs,
        float thresholdY)
        {
            float frac = (prevY - thresholdY) / (prevY - currY);
            int deltaMs = currServerMs - prevServerMs;
            return prevServerMs + (int)(frac * deltaMs + 0.5f);
        }

        // ================================================================
        // 同期ゲート時刻ヘルパー
        // ================================================================

        private void WriteSyncedGateTimes()
        {
            _syncedActorGateMs0 = _actorGateMs[0];
            _syncedActorGateMs1 = _actorGateMs[1];
            _syncedActorGateMs2 = _actorGateMs[2];
            _syncedActorGateMs3 = _actorGateMs[3];
            _syncedActorGateMs4 = _actorGateMs[4];
            if (_effectiveGates <= NUM_GATES) return;
            _syncedActorGateMs5  = _actorGateMs[5];
            _syncedActorGateMs6  = _actorGateMs[6];
            _syncedActorGateMs7  = _actorGateMs[7];
            _syncedActorGateMs8  = _actorGateMs[8];
            _syncedActorGateMs9  = _actorGateMs[9];
            _syncedActorGateMs10 = _actorGateMs[10];
            _syncedActorGateMs11 = _actorGateMs[11];
            _syncedActorGateMs12 = _actorGateMs[12];
            _syncedActorGateMs13 = _actorGateMs[13];
            _syncedActorGateMs14 = _actorGateMs[14];
        }

        private int ReadSyncedGateTime(int index)
        {
            switch (index)
            {
                case 0:  return _syncedActorGateMs0;
                case 1:  return _syncedActorGateMs1;
                case 2:  return _syncedActorGateMs2;
                case 3:  return _syncedActorGateMs3;
                case 4:  return _syncedActorGateMs4;
                case 5:  return _syncedActorGateMs5;
                case 6:  return _syncedActorGateMs6;
                case 7:  return _syncedActorGateMs7;
                case 8:  return _syncedActorGateMs8;
                case 9:  return _syncedActorGateMs9;
                case 10: return _syncedActorGateMs10;
                case 11: return _syncedActorGateMs11;
                case 12: return _syncedActorGateMs12;
                case 13: return _syncedActorGateMs13;
                case 14: return _syncedActorGateMs14;
                default: return 0;
            }
        }

        // ================================================================
        // オブザーバー選択
        // ================================================================

        private void SelectNearestObservers()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            Vector3 myPos = localPlayer.GetPosition();

            VRCPlayerApi[] players = new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()];
            VRCPlayerApi.GetPlayers(players);

            int[] nearestIds = new int[MAX_OBSERVERS];
            float[] nearestDists = new float[MAX_OBSERVERS];
            int foundCount = 0;

            for (int i = 0; i < MAX_OBSERVERS; i++)
            {
                nearestIds[i] = -1;
                nearestDists[i] = float.MaxValue;
            }

            for (int i = 0; i < players.Length; i++)
            {
                if (!Utilities.IsValid(players[i])) continue;
                if (players[i].isLocal) continue;

                float dist = Vector3.Distance(myPos, players[i].GetPosition());

                int insertAt = -1;
                for (int j = 0; j < MAX_OBSERVERS; j++)
                {
                    if (dist < nearestDists[j])
                    {
                        insertAt = j;
                        break;
                    }
                }

                if (insertAt >= 0)
                {
                    for (int j = MAX_OBSERVERS - 1; j > insertAt; j--)
                    {
                        nearestIds[j] = nearestIds[j - 1];
                        nearestDists[j] = nearestDists[j - 1];
                    }
                    nearestIds[insertAt] = players[i].playerId;
                    nearestDists[insertAt] = dist;
                    if (foundCount < MAX_OBSERVERS) foundCount++;
                }
            }

            _syncedObsId0 = nearestIds[0];
            _syncedObsId1 = nearestIds[1];
            _syncedObsId2 = nearestIds[2];
            _syncedObsId3 = nearestIds[3];
            _syncedObsId4 = nearestIds[4];
            _expectedObserverCount = foundCount;

            // 観測者名を保存（結果表示用）
            for (int i = 0; i < MAX_OBSERVERS; i++)
            {
                if (nearestIds[i] >= 0)
                {
                    VRCPlayerApi obs = VRCPlayerApi.GetPlayerById(nearestIds[i]);
                    _observerNames[i] = Utilities.IsValid(obs) ? obs.displayName : "?";
                }
                else
                {
                    _observerNames[i] = "";
                }
            }

            Debug.Log($"[LatencyProbe] オブザーバー選択: {foundCount}人 "
            + $"[{_syncedObsId0}, {_syncedObsId1}, {_syncedObsId2}, {_syncedObsId3}, {_syncedObsId4}]");
        }

        private int GetObserverSlot(int playerId)
        {
            if (playerId < 0) return -1;
            if (_syncedObsId0 == playerId) return 0;
            if (_syncedObsId1 == playerId) return 1;
            if (_syncedObsId2 == playerId) return 2;
            if (_syncedObsId3 == playerId) return 3;
            if (_syncedObsId4 == playerId) return 4;
            return -1;
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player)) return;

            if (_localState == STATE_OBSERVER_WATCHING
            && player.playerId == _actorPlayerId)
            {
                FinalizeObservation();
            }
        }

        // ================================================================
        // アクターセッション
        // ================================================================

        private void StartActorSession()
        {
            if (!_gatesComputed) ComputeGateHeights();

            _localState = STATE_ACTOR_FALLING;
            _isActorRole = true;
            _currentFall = 0;
            _actorDisplayName = Networking.LocalPlayer.displayName;

            Networking.SetOwner(Networking.LocalPlayer, gameObject);

            _syncedOccupantId = Networking.LocalPlayer.playerId;

            SelectNearestObservers();

            _collectedCount = 0;
            for (int i = 0; i < MAX_OBSERVERS; i++)
            {
                _collectedMedians[i] = -1f;
                _collectedSamples[i] = 0;
                _collectedFlags[i] = false;
            }

            _syncedReportSlot = -1;
            _syncedMedianMs = -1f;
            _syncedSampleCnt = 0;

            var p = Networking.LocalPlayer;
            p.SetWalkSpeed(0f);
            p.SetRunSpeed(0f);
            p.SetStrafeSpeed(0f);
            p.SetJumpImpulse(0f);

            Debug.Log($"[LatencyProbe] アクターセッション開始 observers={_expectedObserverCount}");
            UpdateDisplay();
            BeginFallSequence();
        }

        private void BeginFallSequence()
        {
            if (_localState != STATE_ACTOR_FALLING) return;

            if (_fallStart == null)
            {
                Debug.LogError("[LatencyProbe] _fallStart が null です。Wire References を実行してください。");
                EndActorSession();
                return;
            }

            // 床を復元してからテレポート（2回目以降の落下で必要）
            if (_trapdoorFloor != null) _trapdoorFloor.SetActive(true);

            Debug.Log($"[LatencyProbe] テレポート先: {_fallStart.position}");
            Networking.LocalPlayer.TeleportTo(
            _fallStart.position,
            _fallStart.rotation
            );

            _syncedPhase = PHASE_SETTLING;
            _syncedFallIndex = _currentFall;
            RequestSerialization();

            _actorMonitoring = false;
            _nextGateIndex = 0;

            UpdateDisplay();

            SendCustomEventDelayedSeconds(nameof(StartFallMonitoring), SETTLE_TIME);
        }

        public void StartFallMonitoring()
        {
            if (_localState != STATE_ACTOR_FALLING) return;
            if (_syncedPhase != PHASE_SETTLING) return;

            // 床を外して落下開始（アクター側のみ）
            if (_trapdoorFloor != null) _trapdoorFloor.SetActive(false);

            // 離地検出で扉アニメRPCを送信する（アバター同期とタイミングを合わせる）
            _waitingForTrapdoorTrigger = (_trapdoorAnimator != null);

            _syncedPhase = PHASE_FALLING;
            RequestSerialization();

            _actorMonitoring = true;
            _nextGateIndex = 0;
            var pos = Networking.LocalPlayer.GetPosition();
            _actorPrevY = pos.y;
            _actorPrevServerMs = Networking.GetServerTimeInMilliseconds();
            _fallStartTime = Time.time;

            for (int i = 0; i < _effectiveGates; i++)
            _actorGateMs[i] = 0;

            Debug.Log($"[LatencyProbe] 落下開始 fall={_currentFall} Y={_actorPrevY:F2}");
        }

        private void ActorUpdate()
        {
            if (!_actorMonitoring) return;

            // 離地を検出したらローカルで扉アニメ再生
            if (_waitingForTrapdoorTrigger && !Networking.LocalPlayer.IsPlayerGrounded())
            {
                _waitingForTrapdoorTrigger = false;
                _trapdoorAnimator.SetTrigger("Play");
                Debug.Log("[LatencyProbe] アクター離地検出 → 扉アニメ再生(ローカル)");
            }

            if (_nextGateIndex >= _effectiveGates) return;

            float currY = Networking.LocalPlayer.GetPosition().y;
            int currServerMs = Networking.GetServerTimeInMilliseconds();

            // 順次ゲート通過検出（1フレームで複数ゲート通過に対応）
            while (_nextGateIndex < _effectiveGates)
            {
                float gateY = _gateHeights[_nextGateIndex];
                if (_actorPrevY >= gateY && currY < gateY)
                {
                    // 通常の通過検出
                    int crossMs = InterpolateCrossingMs(
                    _actorPrevY, currY, _actorPrevServerMs, currServerMs, gateY);
                    _actorGateMs[_nextGateIndex] = crossMs;

                    Debug.Log($"[LatencyProbe] アクターゲート通過 fall={_currentFall} "
                    + $"gate={_nextGateIndex} crossMs={crossMs} Y={currY:F2}");

                    _nextGateIndex++;
                }
                else if (currY < gateY)
                {
                    // すでにゲートより下にいる（スキップ）
                    Debug.LogWarning($"[LatencyProbe] アクターゲートスキップ fall={_currentFall} "
                    + $"gate={_nextGateIndex} prevY={_actorPrevY:F2} currY={currY:F2} gateY={gateY:F2}");
                    _actorGateMs[_nextGateIndex] = currServerMs;
                    _nextGateIndex++;
                }
                else
                {
                    break; // まだゲートに到達していない
                }
            }

            // 全ゲート通過完了
            if (_nextGateIndex >= _effectiveGates)
            {
                _actorMonitoring = false;
                WriteSyncedGateTimes();
                _syncedPhase = PHASE_CROSSED;
                RequestSerialization();

                Debug.Log($"[LatencyProbe] 全ゲート通過完了 fall={_currentFall}");
                SendCustomEventDelayedSeconds(nameof(NextFall), INTER_FALL_DELAY);
                return;
            }

            // タイムアウト
            if (Time.time - _fallStartTime > FALL_TIMEOUT)
            {
                Debug.LogWarning($"[LatencyProbe] fall={_currentFall} タイムアウト "
                + $"(gate {_nextGateIndex}/{_effectiveGates})");
                _actorMonitoring = false;
                SendCustomEventDelayedSeconds(nameof(NextFall), 0.5f);
                return;
            }

            _actorPrevY = currY;
            _actorPrevServerMs = currServerMs;
        }

        public void NextFall()
        {
            if (_localState != STATE_ACTOR_FALLING) return;

            _currentFall++;
            if (_currentFall >= _effectiveFalls)
            {
                _syncedPhase = PHASE_DONE;
                RequestSerialization();

                if (_expectedObserverCount > 0)
                {
                    _localState = STATE_COLLECTING;
                    _collectTimeoutTime = Time.time + COLLECT_TIMEOUT;

                    var p = Networking.LocalPlayer;
                    p.SetWalkSpeed(_restoreWalkSpeed);
                    p.SetRunSpeed(_restoreRunSpeed);
                    p.SetStrafeSpeed(_restoreStrafeSpeed);
                    p.SetJumpImpulse(_restoreJumpImpulse);

                    Debug.Log($"[LatencyProbe] レポート収集開始 expected={_expectedObserverCount}");
                    UpdateDisplay();
                }
                else
                {
                    Debug.Log("[LatencyProbe] オブザーバーなし、セッション終了");
                    EndActorSession();
                }
            }
            else
            {
                BeginFallSequence();
            }
        }

        /// <summary>床コライダー復元 → 帰還テレポート。</summary>
        private void RestoreTrapdoorAndReturn()
        {
            if (_trapdoorFloor != null) _trapdoorFloor.SetActive(true);
            if (_exitPosition != null)
            {
                Networking.LocalPlayer.TeleportTo(
                _exitPosition.position,
                _exitPosition.rotation);
            }
        }

        private void EndActorSession()
        {
            RestoreTrapdoorAndReturn();

            _localState = STATE_COOLDOWN;
            _cooldownEndTime = Time.time + COOLDOWN_SEC;

            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            _syncedOccupantId = -1;
            _syncedPhase = PHASE_IDLE;
            _syncedObsId0 = -1; _syncedObsId1 = -1; _syncedObsId2 = -1;
            _syncedObsId3 = -1; _syncedObsId4 = -1;
            _syncedReportSlot = -1;
            RequestSerialization();

            var p = Networking.LocalPlayer;
            p.SetWalkSpeed(_restoreWalkSpeed);
            p.SetRunSpeed(_restoreRunSpeed);
            p.SetStrafeSpeed(_restoreStrafeSpeed);
            p.SetJumpImpulse(_restoreJumpImpulse);

            Debug.Log("[LatencyProbe] アクターセッション終了");
            UpdateDisplay();
        }

        // ================================================================
        // オブザーバーセッション
        // ================================================================

        private void StartObserverSession(VRCPlayerApi actor)
        {
            if (!_gatesComputed) ComputeGateHeights();

            if (_fallStart == null)
            Debug.LogError("[LatencyProbe] _fallStart が null です。Wire References を実行してください。");

            _localState = STATE_OBSERVER_WATCHING;
            _isActorRole = false;
            _actorPlayerId = actor.playerId;
            _actorDisplayName = actor.displayName;

            _effectiveGates = _syncedGateCount;
            _effectiveFalls = _syncedGateCount >= MAX_GATES ? 1 : NUM_FALLS;
            if (!_gatesComputed) ComputeGateHeights();

            _fallMedianCount = 0;
            _obsAllGatesDetected = false;
            _obsWaitingForCrossing = false;
            _obsSeenAtTop = false;
            _obsTrapdoorPlayed = false;
            _obsNextGateIndex = 0;
            _obsFallIndex = -1;
            _lastSeenPhase = _syncedPhase;

            for (int i = 0; i < NUM_FALLS; i++)
            _fallMedians[i] = -1f;
            for (int i = 0; i < MAX_TOTAL_SAMPLES; i++)
            _gateLatencies[i] = -1f;
            for (int i = 0; i < _effectiveGates; i++)
            _obsGateMs[i] = 0;

            Debug.Log($"[LatencyProbe] オブザーバーセッション開始: {_actorDisplayName} slot={_myObserverSlot}");
            UpdateDisplay();
        }

        private void ObserverUpdate()
        {
            VRCPlayerApi actor = VRCPlayerApi.GetPlayerById(_actorPlayerId);
            if (!Utilities.IsValid(actor))
            {
                FinalizeObservation();
                return;
            }

            // PHASE_DONE 検出
            if (_syncedPhase == PHASE_DONE && _lastSeenPhase != PHASE_DONE)
            {
                _lastSeenPhase = PHASE_DONE;
                FinalizeObservation();
                return;
            }

            if (_syncedPhase != PHASE_FALLING && _syncedPhase != PHASE_CROSSED) return;
            if (_obsAllGatesDetected) return;

            float currY = actor.GetPosition().y;
            int currServerMs = Networking.GetServerTimeInMilliseconds();

            // 扉アニメ: オブザーバーがローカルでアバターの落下開始を検知して再生
            if (_trapdoorAnimator != null && !_obsTrapdoorPlayed && _fallStart != null)
            {
                float topY = _fallStart.position.y;
                if (!_obsSeenAtTop)
                {
                    // アバターが FallStart 付近に現れるのを待つ
                    if (currY > topY - 0.1f)
                    _obsSeenAtTop = true;
                }
                else if (currY < topY - 0.2f)
                {
                    // FallStart から 0.2m 落下 → 扉アニメ再生
                    _obsTrapdoorPlayed = true;
                    _trapdoorAnimator.SetTrigger("Play");
                    Debug.Log($"[LatencyProbe] オブザーバー: アバター落下検知 Y={currY:F2} → 扉アニメ再生(ローカル)");
                }
            }

            if (!_obsWaitingForCrossing)
            {
                // リモートアバターが最初のゲートより上に来るまで待機
                // （テレポートの位置複製が届くまでは地上付近の座標が返る）
                if (currY < _gateHeights[0])
                return;

                _obsPrevY = currY;
                _obsPrevServerMs = currServerMs;
                _obsWaitingForCrossing = true;
                return;
            }

            // 順次ゲート通過検出
            while (_obsNextGateIndex < _effectiveGates)
            {
                float gateY = _gateHeights[_obsNextGateIndex];
                if (_obsPrevY >= gateY && currY < gateY)
                {
                    // 通常の通過検出
                    int crossMs = InterpolateCrossingMs(
                    _obsPrevY, currY, _obsPrevServerMs, currServerMs, gateY);
                    _obsGateMs[_obsNextGateIndex] = crossMs;

                    Debug.Log($"[LatencyProbe] オブザーバーゲート通過検出 fall={_syncedFallIndex} "
                    + $"gate={_obsNextGateIndex} crossMs={crossMs}");

                    _obsNextGateIndex++;
                }
                else if (currY < gateY)
                {
                    // すでにゲートより下にいる（リモートアバターのテレポート後ジャンプ等）
                    Debug.LogWarning($"[LatencyProbe] オブザーバーゲートスキップ fall={_syncedFallIndex} "
                    + $"gate={_obsNextGateIndex} prevY={_obsPrevY:F2} currY={currY:F2} gateY={gateY:F2}");
                    _obsGateMs[_obsNextGateIndex] = 0; // 無効マーク
                    _obsNextGateIndex++;
                }
                else
                {
                    break; // まだゲートに到達していない
                }
            }

            if (_obsNextGateIndex >= _effectiveGates)
            {
                _obsAllGatesDetected = true;
                _obsWaitingForCrossing = false;
                TryRecordFallSample();
                UpdateDisplay();
            }

            _obsPrevY = currY;
            _obsPrevServerMs = currServerMs;
        }

        /// <summary>
        /// オブザーバーの全ゲート検出とアクターのゲート時刻の両方が揃った場合に
        /// 落下サンプルを記録する。OnDeserialization と ObserverUpdate の両方から呼ばれる。
        /// </summary>
        private void TryRecordFallSample()
        {
            if (!_obsAllGatesDetected) return;
            if (_syncedPhase != PHASE_CROSSED) return;
            if (_syncedFallIndex != _obsFallIndex) return;

            // ゲートごとのレイテンシ算出
            float[] gateLats = new float[MAX_GATES];
            int validCount = 0;

            for (int i = 0; i < _effectiveGates; i++)
            {
                int actorMs = ReadSyncedGateTime(i);
                if (actorMs != 0 && _obsGateMs[i] != 0)
                {
                    float lat = (float)(_obsGateMs[i] - actorMs);
                    if (lat >= 0f && lat < 5000f)
                    {
                        gateLats[validCount] = lat;
                        _gateLatencies[_fallMedianCount * _effectiveGates + i] = lat;
                        validCount++;
                    }
                    else
                    {
                        _gateLatencies[_fallMedianCount * _effectiveGates + i] = -1f;
                    }
                }
                else
                {
                    _gateLatencies[_fallMedianCount * _effectiveGates + i] = -1f;
                }
            }

            if (validCount > 0)
            {
                // 挿入ソート
                for (int i = 1; i < validCount; i++)
                {
                    float key = gateLats[i];
                    int j = i - 1;
                    while (j >= 0 && gateLats[j] > key)
                    {
                        gateLats[j + 1] = gateLats[j];
                        j--;
                    }
                    gateLats[j + 1] = key;
                }

                _fallMedians[_fallMedianCount] = Percentile(gateLats, validCount, 50);
                Debug.Log($"[LatencyProbe] 落下サンプル記録 #{_fallMedianCount}: "
                + $"median={_fallMedians[_fallMedianCount]:F1}ms (gates={validCount})");
            }
            else
            {
                _fallMedians[_fallMedianCount] = -1f;
                Debug.LogWarning($"[LatencyProbe] 落下サンプル #{_fallMedianCount}: 有効ゲートなし");
            }

            _fallMedianCount++;
            UpdateDisplay();
        }

        private void FinalizeObservation()
        {
            ComputeResults();

            string line;
            if (_validSampleCount > 0)
            line = $"[{_actorDisplayName}] med~{_medianMs:F0}ms (n={_validSampleCount})";
            else
            line = $"[{_actorDisplayName}] 検出なし";

            AddLogLine(line);

            _localState = STATE_RESULT;
            Debug.Log($"[LatencyProbe] オブザーバーセッション完了: {line}");

            if (_myObserverSlot >= 0)
            {
                float delay = REPORT_STAGGER_SEC + _myObserverSlot * REPORT_STAGGER_SEC;
                Debug.Log($"[LatencyProbe] レポート予約 slot={_myObserverSlot} delay={delay:F1}s");
                SendCustomEventDelayedSeconds(nameof(ReportObserverResult), delay);
            }
            else
            {
                SendCustomEventDelayedSeconds(nameof(TransitionToCooldown), 3f);
            }

            UpdateDisplay();
        }

        public void ReportObserverResult()
        {
            if (_myObserverSlot < 0) return;
            if (_localState != STATE_RESULT) return;

            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            _syncedReportSlot = _myObserverSlot;
            _syncedMedianMs   = _medianMs;
            _syncedSampleCnt  = _validSampleCount;
            RequestSerialization();

            Debug.Log($"[LatencyProbe] オブザーバーレポート送信 slot={_myObserverSlot} "
            + $"median={_medianMs:F0}ms samples={_validSampleCount}");

            _myObserverSlot = -1;
            SendCustomEventDelayedSeconds(nameof(TransitionToCooldown), 3f);
        }

        public void TransitionToCooldown()
        {
            if (_localState != STATE_RESULT) return;
            _localState = STATE_COOLDOWN;
            _cooldownEndTime = Time.time + COOLDOWN_SEC;
            UpdateDisplay();
        }

        // ================================================================
        // レポート収集 (アクター側)
        // ================================================================

        private void ComputeFinalResult()
        {
            if (_localState != STATE_COLLECTING) return;
            RestoreTrapdoorAndReturn();

            float minMedian = -1f;
            int minSamples = 0;
            int validReports = 0;

            for (int i = 0; i < MAX_OBSERVERS; i++)
            {
                if (_collectedFlags[i] && _collectedMedians[i] >= 0f)
                {
                    validReports++;
                    if (minMedian < 0f || _collectedMedians[i] < minMedian)
                    {
                        minMedian = _collectedMedians[i];
                        minSamples = _collectedSamples[i];
                    }
                }
            }

            _medianMs = minMedian;
            _validSampleCount = minSamples;

            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            _syncedReportSlot = -1;
            _syncedMedianMs = minMedian;
            _syncedSampleCnt = minSamples;
            _syncedActorName = _actorDisplayName;
            _syncedResultVer++;
            _lastSeenResultVer = _syncedResultVer;
            _syncedOccupantId = -1;
            _syncedPhase = PHASE_IDLE;
            _syncedObsId0 = -1; _syncedObsId1 = -1; _syncedObsId2 = -1;
            _syncedObsId3 = -1; _syncedObsId4 = -1;
            RequestSerialization();

            string line;
            if (validReports > 0 && minMedian >= 0f)
            line = $"[{_actorDisplayName}] min med~{minMedian:F0}ms ({validReports}人観測)";
            else
            line = $"[{_actorDisplayName}] 検出なし";
            AddLogLine(line);

            Debug.Log($"[LatencyProbe] 最終結果: {line}");

            _localState = STATE_RESULT;
            UpdateDisplay();
            SendCustomEventDelayedSeconds(nameof(TransitionToCooldown), 5f);
        }

        // ================================================================
        // OnDeserialization
        // ================================================================

        public override void OnDeserialization()
        {
            // オブザーバー自動開始
            if (_localState == STATE_IDLE
            && (_syncedPhase == PHASE_SETTLING || _syncedPhase == PHASE_FALLING)
            && _syncedOccupantId >= 0
            && _syncedOccupantId != Networking.LocalPlayer.playerId)
            {
                int slot = GetObserverSlot(Networking.LocalPlayer.playerId);
                if (slot >= 0)
                {
                    _myObserverSlot = slot;
                    VRCPlayerApi actor = VRCPlayerApi.GetPlayerById(_syncedOccupantId);
                    if (Utilities.IsValid(actor))
                    {
                        StartObserverSession(actor);
                    }
                }
            }

            // オブザーバー: フェーズ変化に応答
            if (_localState == STATE_OBSERVER_WATCHING)
            {
                if ((_syncedPhase == PHASE_SETTLING || _syncedPhase == PHASE_FALLING)
                && _syncedFallIndex != _obsFallIndex)
                {
                    // 新しい落下の準備
                    _obsFallIndex = _syncedFallIndex;
                    _obsAllGatesDetected = false;
                    _obsWaitingForCrossing = false;
                    _obsSeenAtTop = false;
                    _obsTrapdoorPlayed = false;
                    _obsNextGateIndex = 0;
                    for (int i = 0; i < _effectiveGates; i++)
                    _obsGateMs[i] = 0;
                    _lastSeenPhase = _syncedPhase;
                    UpdateDisplay();
                }
                else if (_syncedPhase == PHASE_CROSSED)
                {
                    TryRecordFallSample();
                }
                else if (_syncedPhase == PHASE_DONE && _lastSeenPhase != PHASE_DONE)
                {
                    _lastSeenPhase = PHASE_DONE;
                    FinalizeObservation();
                    return;
                }
            }

            // アクター: オブザーバーレポート受信
            if (_localState == STATE_COLLECTING)
            {
                int slot = _syncedReportSlot;
                if (slot >= 0 && slot < MAX_OBSERVERS && !_collectedFlags[slot])
                {
                    _collectedFlags[slot] = true;
                    _collectedMedians[slot] = _syncedMedianMs;
                    _collectedSamples[slot] = _syncedSampleCnt;
                    _collectedCount++;

                    Debug.Log($"[LatencyProbe] アクター: レポート受信 slot={slot} "
                    + $"median={_syncedMedianMs:F0}ms ({_collectedCount}/{_expectedObserverCount})");

                    if (_collectedCount >= _expectedObserverCount)
                    {
                        ComputeFinalResult();
                        return;
                    }
                }
                UpdateDisplay();
                return;
            }

            // 結果同期の受信（非参加者向け）
            if (_syncedResultVer != _lastSeenResultVer)
            {
                _lastSeenResultVer = _syncedResultVer;
                if (_syncedReportSlot < 0)
                {
                    string line;
                    if (_syncedSampleCnt > 0)
                    line = $"[{_syncedActorName}] med~{_syncedMedianMs:F0}ms (n={_syncedSampleCnt})";
                    else
                    line = $"[{_syncedActorName}] 検出なし";
                    AddLogLine(line);
                }
            }
            UpdateDisplay();
        }

        // ================================================================
        // Update
        // ================================================================

        private void Update()
        {
            switch (_localState)
            {
                case STATE_ACTOR_FALLING:
                ActorUpdate();
                break;
                case STATE_OBSERVER_WATCHING:
                ObserverUpdate();
                break;
                case STATE_COLLECTING:
                if (Time.time >= _collectTimeoutTime)
                {
                    Debug.LogWarning($"[LatencyProbe] レポート収集タイムアウト "
                    + $"({_collectedCount}/{_expectedObserverCount})");
                    ComputeFinalResult();
                }
                break;
                case STATE_COOLDOWN:
                if (Time.time >= _cooldownEndTime)
                {
                    _localState = STATE_IDLE;
                    UpdateDisplay();
                }
                break;
            }
        }

        // ================================================================
        // 結果計算
        // ================================================================

        private void ComputeResults()
        {
            // _fallMedians から有効な値を収集
            float[] validMedians = new float[NUM_FALLS];
            int validCount = 0;

            for (int i = 0; i < _fallMedianCount && i < NUM_FALLS; i++)
            {
                if (_fallMedians[i] >= 0f)
                {
                    validMedians[validCount] = _fallMedians[i];
                    validCount++;
                }
            }

            _validSampleCount = validCount;

            if (validCount == 0)
            {
                _medianMs = -1f;
                return;
            }

            // 挿入ソート
            for (int i = 1; i < validCount; i++)
            {
                float key = validMedians[i];
                int j = i - 1;
                while (j >= 0 && validMedians[j] > key)
                {
                    validMedians[j + 1] = validMedians[j];
                    j--;
                }
                validMedians[j + 1] = key;
            }

            _medianMs = Percentile(validMedians, validCount, 50);
        }

        private float Percentile(float[] sorted, int count, int pct)
        {
            if (count <= 0) return -1f;
            if (count == 1) return sorted[0];

            float rank = (pct / 100f) * (count - 1);
            int lower = (int)rank;
            int upper = lower + 1;
            if (upper >= count) upper = count - 1;
            float frac = rank - lower;
            return sorted[lower] + frac * (sorted[upper] - sorted[lower]);
        }

        // ================================================================
        // ログ
        // ================================================================

        private void AddLogLine(string line)
        {
            if (_localLogCount < MAX_LOG_LINES)
            {
                _localLog[_localLogCount++] = line;
            }
            else
            {
                for (int i = 0; i < MAX_LOG_LINES - 1; i++)
                _localLog[i] = _localLog[i + 1];
                _localLog[MAX_LOG_LINES - 1] = line;
            }
        }

        // ================================================================
        // 表示更新
        // ================================================================

        private void UpdateDisplay()
        {
            if (_display != null)
            _display.OnProbeUpdate();
        }

        // ================================================================
        // ProbeDisplay 向けゲッター
        // ================================================================

        public int    GetLocalState()       { return _localState; }
        public string GetActorName()        { return _actorDisplayName; }

        public int GetCurrentFall()
        {
            if (_localState == STATE_ACTOR_FALLING || _localState == STATE_COLLECTING)
            return _currentFall;
            return _syncedFallIndex;
        }

        public int    GetTotalFalls()       { return _effectiveFalls; }
        public int    GetNumGates()         { return _effectiveGates; }
        public int    GetCurrentGate()      { return _isActorRole ? _nextGateIndex : _obsNextGateIndex; }
        public int    GetSampleCount()      { return _fallMedianCount; }
        public float  GetMedianLatencyMs()  { return _medianMs; }

        public float GetLatestSampleMs()
        {
            return _fallMedianCount > 0 ? _fallMedians[_fallMedianCount - 1] : -1f;
        }

        public float[] GetGateLatencies()   { return _gateLatencies; }
        public float[] GetFallMedians()     { return _fallMedians; }
        public int     GetFallMedianCount() { return _fallMedianCount; }

        public float[] GetCollectedMedians() { return _collectedMedians; }
        public bool[]  GetCollectedFlags()   { return _collectedFlags; }

        public string GetObserverName(int slot)
        {
            if (slot < 0 || slot >= MAX_OBSERVERS) return "?";
            return _observerNames[slot];
        }

        public bool   IsActorRole()         { return _isActorRole; }

        public string[] GetLocalLog()       { return _localLog; }
        public int    GetLocalLogCount()    { return _localLogCount; }

        // ボタンゲッター
        public int    GetOccupantId()       { return _syncedOccupantId; }

        // レポート収集ゲッター
        public int    GetExpectedObserverCount()  { return _expectedObserverCount; }
        public int    GetCollectedObserverCount() { return _collectedCount; }

        /// <summary>
        /// セッション開始前のバリデーション。成功時 true を返す。
        /// </summary>
        private bool ValidateAndPrepareSession()
        {
            if (_localState != STATE_IDLE) return false;

            if (_syncedOccupantId >= 0
            && _syncedOccupantId != Networking.LocalPlayer.playerId)
            {
                VRCPlayerApi occupant = VRCPlayerApi.GetPlayerById(_syncedOccupantId);
                if (!Utilities.IsValid(occupant))
                {
                    Networking.SetOwner(Networking.LocalPlayer, gameObject);
                    _syncedOccupantId = -1;
                    _syncedPhase = PHASE_IDLE;
                    RequestSerialization();
                }
                else
                {
                    _display.ShowRejectionHUD();
                    return false;
                }
            }

            int otherPlayerCount = VRCPlayerApi.GetPlayerCount() - 1;
            if (otherPlayerCount <= 0)
            {
                _display.ShowTemporaryHUD("他のプレイヤーが\n必要です");
                return false;
            }

            return true;
        }

        /// <summary>
        /// BoothButton から呼び出される計測開始メソッド（5×5 モード）。
        /// </summary>
        public void OnButtonPress()
        {
            if (!ValidateAndPrepareSession()) return;

            _effectiveGates = NUM_GATES;
            _effectiveFalls = NUM_FALLS;
            _syncedGateCount = NUM_GATES;
            ComputeGateHeights();
            StartActorSession();
        }

        /// <summary>
        /// BoothButton から呼び出される計測開始メソッド（15×1 モード）。
        /// </summary>
        public void OnButtonPressSingleFall()
        {
            if (!ValidateAndPrepareSession()) return;

            _effectiveGates = MAX_GATES;
            _effectiveFalls = 1;
            _syncedGateCount = MAX_GATES;
            ComputeGateHeights();
            StartActorSession();
        }

        // STATE_* 定数公開
        public int StateIdle             { get { return STATE_IDLE; } }
        public int StateActorRunning     { get { return STATE_ACTOR_FALLING; } }
        public int StateObserverDetect   { get { return STATE_OBSERVER_WATCHING; } }
        public int StateResult           { get { return STATE_RESULT; } }
        public int StateCooldown         { get { return STATE_COOLDOWN; } }
        public int StateCollecting       { get { return STATE_COLLECTING; } }
    }
}
