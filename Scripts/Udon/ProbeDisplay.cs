using TMPro;
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

/// <summary>
/// 計測結果の可視化。TeleportLatencyProbe からの通知で Text を更新する。
/// 全てローカル表示（同期変数なし）。
///
/// 表示エリアは役割で分離:
///   _statusText（上部）: 現在のステート・進捗を簡潔に表示
///   _logText（下部）:    計測結果（テーブル/チャート）を永続表示
///
/// HUD 機能も内包: アクター計測中/レポート収集中に頭部追従する小型テキストを表示。
/// 拒否 HUD / 一時 HUD: 計測中に入ろうとしたプレイヤーや、オブザーバー不足時のフィードバック。
/// StatusBeacon: 筐体上部のランプ（緑=空き, 赤=使用中）。
/// 落下進捗: 5 個のインジケータが順に点灯。
/// 床インジケータ: 待機中は明滅、使用中は赤。
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class ProbeDisplay : UdonSharpBehaviour
{
    [SerializeField] private TeleportLatencyProbe _probe;

    [Header("メインパネル")]
    [SerializeField] private TextMeshProUGUI _statusText;  // 上部: ステータス
    [SerializeField] private TextMeshProUGUI _logText;     // 下部: 結果（永続）

    [Header("HUD（アクター頭部追従）")]
    [SerializeField] private GameObject      _hudRoot;       // HUDPanel GO
    [SerializeField] private TextMeshProUGUI _hudText;
    [SerializeField] private Vector3    _hudOffset = new Vector3(0f, -0.12f, 0.45f);

    [Header("ステータスビーコン")]
    [SerializeField] private Renderer  _statusBeacon;
    [SerializeField] private Material  _beaconGreenMat;
    [SerializeField] private Material  _beaconRedMat;

    [Header("床インジケータ")]
    [SerializeField] private Renderer  _floorIndicator;

    [Header("落下進捗")]
    [SerializeField] private Image[]   _trialIndicators;

    // HUD 参照のランタイム復元フラグ
    private bool _hudResolved = false;
    // 診断ログ用: 前回のステート
    private int _lastDiagState = -999;

    // 一時 HUD 表示タイマー
    private float _tempHudEndTime = 0f;

    // 床インジケータの初期色
    private readonly Color _floorIdleColor = new Color(0.2f, 0.8f, 1f, 0.4f);
    private readonly Color _floorBusyColor = new Color(1f, 0.2f, 0.15f, 0.5f);

    // 落下インジケータの色
    private readonly Color _trialInactiveColor = new Color(0.3f, 0.3f, 0.3f, 1f);
    private readonly Color _trialActiveColor   = new Color(0.1f, 1f, 0.3f, 1f);

    // バーチャート用ブロック文字
    private const char BAR_CHAR = '\u2588'; // █
    private const int  MAX_BAR_LEN = 14;

    private void Update()
    {
        if (_probe == null) return;

        // HUD 参照が CopyProxyToUdon で欠落した場合のフォールバック
        if (!_hudResolved)
        {
            _hudResolved = true;
            if (_hudRoot == null || _hudText == null)
            {
                Transform boothTf = transform.parent;
                if (boothTf != null)
                {
                    if (_hudRoot == null)
                    {
                        Transform hudTf = boothTf.Find("HUDPanel");
                        if (hudTf != null) _hudRoot = hudTf.gameObject;
                    }
                    if (_hudText == null && _hudRoot != null)
                    {
                        Transform textTf = _hudRoot.transform.Find("HUDText");
                        if (textTf != null) _hudText = textTf.GetComponent<TextMeshProUGUI>();
                    }
                }
            }
        }

        // ステート変化時に診断ログ
        int curState = _probe.GetLocalState();
        if (curState != _lastDiagState)
        {
            _lastDiagState = curState;
            Debug.Log($"[LatencyProbe] Display state={curState} "
                    + $"statusText={(_statusText != null)} "
                    + $"hudRoot={(_hudRoot != null)} "
                    + $"hudText={(_hudText != null)}");
        }

        if (_statusText != null)
        {
            UpdateStatus();
        }

        UpdateHUD();
        UpdateBeacon();
        UpdateFloorIndicator();
        UpdateFallIndicators();
    }

    public void OnProbeUpdate()
    {
        if (_probe == null) return;
        if (_statusText != null)
        {
            UpdateStatus();
        }
        UpdateHUD();
        UpdateBeacon();
        UpdateFallIndicators();
    }

    // ================================================================
    // HUD 更新（アクター・頭部追従）
    // ================================================================

    private void UpdateHUD()
    {
        if (_hudRoot == null || _hudText == null) return;

        int state = _probe.GetLocalState();
        bool showActorHud = (state == _probe.StateActorRunning
                          || state == _probe.StateCollecting);

        bool showTempHud = Time.time < _tempHudEndTime;

        _hudRoot.SetActive(showActorHud || showTempHud);

        if (!showActorHud && !showTempHud) return;

        var localPlayer = Networking.LocalPlayer;
        if (!Utilities.IsValid(localPlayer)) return;

        var head = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
        _hudRoot.transform.position = head.position + head.rotation * _hudOffset;
        _hudRoot.transform.rotation = head.rotation;

        if (state == _probe.StateActorRunning)
        {
            int total = _probe.GetTotalFalls();
            if (total <= 1)
            {
                int gate = _probe.GetCurrentGate();
                int numGates = _probe.GetNumGates();
                _hudText.text = $"計測中  ゲート {gate} / {numGates}";
            }
            else
            {
                int fall = _probe.GetCurrentFall();
                _hudText.text = $"計測中  {fall + 1} / {total}";
            }
        }
        else if (state == _probe.StateCollecting)
        {
            int expected = _probe.GetExpectedObserverCount();
            int collected = _probe.GetCollectedObserverCount();
            _hudText.text = $"集計中  {collected} / {expected}";
        }
    }

    public void ShowRejectionHUD()
    {
        ShowTemporaryHUD("計測中です\nしばらくお待ちください");
    }

    public void ShowTemporaryHUD(string message)
    {
        _tempHudEndTime = Time.time + 3f;

        if (_hudRoot != null) _hudRoot.SetActive(true);
        if (_hudText != null) _hudText.text = message;

        var localPlayer = Networking.LocalPlayer;
        if (Utilities.IsValid(localPlayer) && _hudRoot != null)
        {
            var head = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);
            _hudRoot.transform.position = head.position + head.rotation * _hudOffset;
            _hudRoot.transform.rotation = head.rotation;
        }
    }

    // ================================================================
    // ステータスビーコン
    // ================================================================

    private void UpdateBeacon()
    {
        if (_statusBeacon == null || _beaconGreenMat == null || _beaconRedMat == null) return;

        int state = _probe.GetLocalState();
        bool busy = state != _probe.StateIdle
                    || _probe.GetOccupantId() >= 0;

        _statusBeacon.sharedMaterial = busy ? _beaconRedMat : _beaconGreenMat;
    }

    // ================================================================
    // 床インジケータ アニメーション
    // ================================================================

    private void UpdateFloorIndicator()
    {
        if (_floorIndicator == null) return;

        int state = _probe.GetLocalState();
        bool busy = state != _probe.StateIdle
                    || _probe.GetOccupantId() >= 0;

        if (busy)
        {
            _floorIndicator.material.color = _floorBusyColor;
        }
        else
        {
            float alpha = Mathf.Sin(Time.time * 2f) * 0.15f + 0.35f;
            _floorIndicator.material.color = new Color(
                _floorIdleColor.r, _floorIdleColor.g, _floorIdleColor.b, alpha);
        }
    }

    // ================================================================
    // 落下進捗インジケータ
    // ================================================================

    private void UpdateFallIndicators()
    {
        if (_trialIndicators == null) return;

        int state = _probe.GetLocalState();
        int samples = _probe.GetSampleCount();
        int fall = _probe.GetCurrentFall();
        int totalFalls = _probe.GetTotalFalls();

        bool showProgress = state == _probe.StateActorRunning
                         || state == _probe.StateObserverDetect
                         || state == _probe.StateResult
                         || state == _probe.StateCollecting;

        int filled = 0;
        if (totalFalls <= 1)
        {
            // 15×1 モード: ゲート進捗を 5 段階で表示
            int gate = _probe.GetCurrentGate();
            int numGates = _probe.GetNumGates();
            if (numGates > 0 && (state == _probe.StateActorRunning || state == _probe.StateObserverDetect))
                filled = (gate * _trialIndicators.Length) / numGates;
            else if (state == _probe.StateResult || state == _probe.StateCollecting)
                filled = _trialIndicators.Length;
        }
        else
        {
            // 5×5 モード: 落下進捗
            if (state == _probe.StateActorRunning || state == _probe.StateCollecting)
                filled = fall;
            else
                filled = samples;
        }

        for (int i = 0; i < _trialIndicators.Length; i++)
        {
            if (_trialIndicators[i] == null) continue;
            if (showProgress && i < filled)
                _trialIndicators[i].color = _trialActiveColor;
            else
                _trialIndicators[i].color = _trialInactiveColor;
        }
    }

    // ================================================================
    // ステータスエリア更新（上部 — 簡潔なステート表示）
    // ================================================================

    private void UpdateStatus()
    {
        if (_statusText == null) return;

        int state = _probe.GetLocalState();
        string actorName = _probe.GetActorName();
        int fall = _probe.GetCurrentFall();
        int total = _probe.GetTotalFalls();
        int samples = _probe.GetSampleCount();
        float med = _probe.GetMedianLatencyMs();

        // ---- ステータスエリア（上部）: 現在の状態を簡潔に表示 ----
        if (state == _probe.StateIdle)
        {
            if (_probe.GetOccupantId() >= 0)
            {
                _statusText.text = "他のプレイヤーが計測中です";
            }
            else
            {
                _statusText.text = "待機中";
            }
        }
        else if (state == _probe.StateActorRunning)
        {
            if (total <= 1)
            {
                int gate = _probe.GetCurrentGate();
                int numGates = _probe.GetNumGates();
                _statusText.text = $"落下計測中... ゲート {gate} / {numGates}";
            }
            else
            {
                _statusText.text = $"落下計測中... {fall + 1} / {total}";
            }
        }
        else if (state == _probe.StateCollecting)
        {
            int expected = _probe.GetExpectedObserverCount();
            int collected = _probe.GetCollectedObserverCount();
            _statusText.text = $"レポート収集中 {collected} / {expected}";
        }
        else if (state == _probe.StateObserverDetect)
        {
            float latest = _probe.GetLatestSampleMs();
            string latestStr = latest >= 0f ? $"~{latest:F0}ms" : "";
            if (total <= 1)
            {
                int gate = _probe.GetCurrentGate();
                int numGates = _probe.GetNumGates();
                _statusText.text = $"観測中 [{actorName}]  ゲート {gate}/{numGates}  {latestStr}";
            }
            else
            {
                _statusText.text = $"観測中 [{actorName}]  落下 {fall + 1}/{total}  {latestStr}";
            }
        }
        else if (state == _probe.StateResult)
        {
            string medStr = med >= 0f ? $"{med:F0}ms" : "---";
            _statusText.text = $"結果: {medStr}";
            // 結果エリア（下部）に詳細を書き込む
            WriteResultToLogArea(actorName, med, samples);
        }
        else if (state == _probe.StateCooldown)
        {
            string medStr = med >= 0f ? $"{med:F0}ms" : "---";
            _statusText.text = $"結果: {medStr}  (クールダウン中)";
            // 結果エリアは触らない — そのまま維持
        }
    }

    // ================================================================
    // 結果エリア更新（下部 — 永続表示、新しい計測結果のみで上書き）
    // ================================================================

    private void WriteResultToLogArea(string actorName, float med, int samples)
    {
        if (_logText == null) return;

        string resultBlock = "";

        if (_probe.IsActorRole())
        {
            resultBlock = BuildActorResultText(actorName, med);
        }
        else if (samples > 0)
        {
            resultBlock = BuildObserverResultText(actorName, med, samples);
        }

        // 結果 + 履歴ログ
        string history = BuildHistoryText();
        _logText.text = resultBlock + history;
    }

    private string BuildHistoryText()
    {
        int count = _probe.GetLocalLogCount();
        if (count == 0) return "";

        string[] log = _probe.GetLocalLog();
        string text = "\n<size=20><color=#AAAAAA>";
        // 最新のエントリは現在の結果と重複するため、count-2 から古い順に表示
        int start = count >= 2 ? count - 2 : -1;
        if (start < 0) return "";

        text += "--- 過去の計測 ---\n";
        for (int i = start; i >= 0; i--)
            text += log[i] + "\n";

        text += "</color></size>";
        return text;
    }

    // ================================================================
    // アクター結果表示（観測者ごとのバーチャート）
    // ================================================================

    private string BuildActorResultText(string actorName, float finalMedian)
    {
        string medStr = finalMedian >= 0f ? $"{finalMedian:F0} ms" : "---";

        string text = $"<b>[{actorName}]</b> アバター遅延: {medStr}\n\n";

        float[] medians = _probe.GetCollectedMedians();
        bool[] flags = _probe.GetCollectedFlags();
        int expected = _probe.GetExpectedObserverCount();

        // バーチャートのスケール計算
        float maxVal = 0f;
        for (int i = 0; i < expected && i < 5; i++)
        {
            if (flags[i] && medians[i] > maxVal)
                maxVal = medians[i];
        }
        if (maxVal < 1f) maxVal = 1f;

        text += "<size=22>";

        for (int i = 0; i < expected && i < 5; i++)
        {
            string obsName = _probe.GetObserverName(i);

            if (flags[i] && medians[i] >= 0f)
            {
                int barLen = Mathf.CeilToInt((medians[i] / maxVal) * MAX_BAR_LEN);
                if (barLen < 1) barLen = 1;

                string bar = "";
                for (int b = 0; b < barLen; b++)
                    bar += BAR_CHAR;

                bool isMin = Mathf.Abs(medians[i] - finalMedian) < 0.5f;
                string marker = isMin ? " <color=#FFD700>\u2605</color>" : "";
                string barColor = isMin ? "<color=#4FC3F7>" : "<color=#81C784>";

                text += $"  {obsName}\n    {barColor}{bar}</color> {medians[i]:F0}ms{marker}\n";
            }
            else
            {
                text += $"  {obsName}\n    <color=#888888>---</color>\n";
            }
        }

        text += "</size>";
        return text;
    }

    // ================================================================
    // オブザーバー結果表示（ゲート×落下テーブル）
    // ================================================================

    private string BuildObserverResultText(string actorName, float med, int validFalls)
    {
        float[] gateLats = _probe.GetGateLatencies();
        float[] fallMeds = _probe.GetFallMedians();
        int fallCount = _probe.GetFallMedianCount();
        int numGates = _probe.GetNumGates();
        int totalFalls = _probe.GetTotalFalls();

        string medStr = med >= 0f ? $"{med:F0} ms" : "---";
        string text = $"<b>[{actorName}]</b> 観測結果\n\n";

        if (totalFalls <= 1)
        {
            // 15×1 モード: 3行×5列で15ゲートを表示
            text += "<size=20><mspace=0.55em>";
            text += "       G1   G2   G3   G4   G5\n";
            text += "<color=#666666> \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500</color>\n";

            int rows = (numGates + 4) / 5;
            for (int r = 0; r < rows; r++)
            {
                text += $"  #{r + 1} ";
                for (int c = 0; c < 5; c++)
                {
                    int g = r * 5 + c;
                    if (g < numGates && fallCount > 0)
                    {
                        float lat = gateLats[g]; // fall 0 のみ
                        if (lat >= 0f)
                            text += $" {lat,4:F0}";
                        else
                            text += " <color=#666666> ---</color>";
                    }
                    else
                    {
                        text += "     ";
                    }
                }
                text += "\n";
            }

            text += "<color=#666666> \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500</color>\n";
            text += "</mspace></size>";
            text += $"\n 中央値: <color=#4FC3F7>{medStr}</color> (n={numGates})";
        }
        else
        {
            // 5×5 モード: 従来のゲート×落下テーブル
            text += "<size=20><mspace=0.55em>";
            text += "       G1   G2   G3   G4   G5  <color=#FFD700> med</color>\n";
            text += "<color=#666666> \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500</color>\n";

            for (int f = 0; f < totalFalls && f < fallCount; f++)
            {
                text += $"  #{f + 1} ";

                for (int g = 0; g < numGates; g++)
                {
                    float lat = gateLats[f * numGates + g];
                    if (lat >= 0f)
                        text += $" {lat,4:F0}";
                    else
                        text += " <color=#666666> ---</color>";
                }

                // 落下中央値
                if (fallMeds[f] >= 0f)
                    text += $"  <color=#FFD700>{fallMeds[f],4:F0}</color>";
                else
                    text += "  <color=#666666> ---</color>";

                text += "\n";
            }

            // 未完了の落下
            for (int f = fallCount; f < totalFalls; f++)
            {
                text += $"  #{f + 1}  <color=#444444>                          ---</color>\n";
            }

            text += "<color=#666666> \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500</color>\n";
            text += "</mspace></size>";
            text += $"\n 総合中央値: <color=#4FC3F7>{medStr}</color> (n={validFalls})";
        }

        return text;
    }
}
